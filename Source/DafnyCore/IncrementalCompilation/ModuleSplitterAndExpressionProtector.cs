#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Boogie;
using Microsoft.Dafny;

namespace DafnyCore.IncrementalCompilation {
  internal class ModuleSplitterAndExpressionProtector(DafnyOptions dafnyOptions) {
    private DafnyOptions DafnyOptions => dafnyOptions;
    public static readonly string Name = "_IPM";
    public static readonly string AttributeName = "ipm";
    public LiteralModuleDecl SplitAndProtect(Microsoft.Dafny.Program p) {
      ProtectToProveApplySuffix.ResetInstances();
      Contract.Requires(p.DefaultModuleDef.SourceDecls.NoneAreOfType<ModuleExportDecl>()); // parser doesn't allow export decls in root module
      static LiteralModuleDecl MakeNewModuleWithOldRootStuff(ModuleSplitterAndExpressionProtector self, Microsoft.Dafny.Program p) {
        var def = new ModuleDefinition(
          p.DefaultModuleDef.Origin,
          Name.ToNameNodeWithVirtualToken(),
          [],
          ModuleKindEnum.Abstract,
          null,
          p.DefaultModuleDef,
          null,
          [.. p.DefaultModuleDef.SourceDecls]
        );
        def.DefaultClass!.Members.AddRange(p.DefaultModuleDef.DefaultClass!.Members);
        def.DefaultClass.SetMembersBeforeResolution();
        var decl = new LiteralModuleDecl(self.DafnyOptions, def, p.DefaultModuleDef, Guid.NewGuid());
        foreach (var sd in def.SourceDecls) {
          Contract.Assert(sd is not ModuleExportDecl);

          sd.EnclosingModuleDefinition = def;
          if (sd is LiteralModuleDecl lmd) { lmd.ModuleDef.EnclosingModule = def; }
        }
        return decl;
      }
      var moduleWithOldRootStuff = MakeNewModuleWithOldRootStuff(this, p);

      p.DefaultModuleDef.SourceDecls.Clear();
      p.DefaultModuleDef.SourceDecls.Add(moduleWithOldRootStuff);

      p.DefaultModuleDef.DefaultClass!.Members.Clear();
      p.DefaultModuleDef.DefaultClass.SetMembersBeforeResolution();

      foreach (var g in SplitAndProtect(moduleWithOldRootStuff)) {
        p.DefaultModuleDef.SourceDecls.Add(g.Process(DafnyOptions, p.DefaultModuleDef));
      }
      ProtectToProveApplySuffix.AssignEntryPoints();
      return moduleWithOldRootStuff;
    }
    #region helper processing classes
    private abstract class RefiningModuleGenerator {
      public abstract TopLevelDecl Process(DafnyOptions dafnyOptions, DefaultModuleDefinition root);
      private static List<TopLevelDecl> Trace(TopLevelDecl topLevelDecl) {
        List<TopLevelDecl> r = [topLevelDecl];
        while (true) {
          var e = r[^1].EnclosingModuleDefinition?.EnclosingLiteralModuleDecl;
          if (e is null) { break; }
          r.Add(e);
        }
        r = r[1..^1];
        r.Reverse();
        return r;
      }
      public abstract class FromMemberDecl<E, M> : RefiningModuleGenerator where E : TopLevelDeclWithMembers where M : MemberDecl {
        public FromMemberDecl(E enclosingDecl, M memberDecl) {
          MemberDecl = memberDecl;
          EnclosingDecl = enclosingDecl;
        }
        protected M MemberDecl { get; }
        protected E EnclosingDecl { get; }
        protected ModuleDefinition OriginalModuleDef => EnclosingDecl.EnclosingModuleDefinition;
        protected LiteralModuleDecl OriginalModuleDecl => OriginalModuleDef.EnclosingLiteralModuleDecl!;
        private LiteralModuleDecl CreateModule(DafnyOptions dafnyOptions, DefaultModuleDefinition root) {
          var trace = Trace(EnclosingDecl);
          var refinement_target_parts = trace.Select(d => d.Name);
          if (EnclosingDecl is not DefaultClassDecl) { refinement_target_parts = refinement_target_parts.Append(EnclosingDecl.Name); }
          var def = new ModuleDefinition(
            OriginalModuleDef.Origin,
            $"{string.Join("_", refinement_target_parts)}_{MemberDecl.Name}".ToNameNodeWithVirtualToken(),
            [],
            ModuleKindEnum.Abstract,
            new(ImplementationKind.Refinement, new([.. refinement_target_parts.Select(Microsoft.Dafny.Util.ToNameNodeWithVirtualToken)])),
            root,
            null,
            []
          );
          var decl = new LiteralModuleDecl(dafnyOptions, def, root, Guid.NewGuid());
          return decl;
        }
        protected abstract M CreateDuplicateOfMemberDecl();
        public override TopLevelDecl Process(DafnyOptions dafnyOptions, DefaultModuleDefinition root) {
          var new_decl = CreateModule(dafnyOptions, root);
          switch (EnclosingDecl) {
            case DefaultClassDecl:
              new_decl.ModuleDef.DefaultClass!.Members.Add(CreateDuplicateOfMemberDecl());
              new_decl.ModuleDef.DefaultClass!.SetMembersBeforeResolution();
              break;
          }
          return new_decl;
        }
      }
      public class FromConstantField<E>(E enclosingDecl, ConstantField constantField) : FromMemberDecl<E, ConstantField>(enclosingDecl, constantField) where E : TopLevelDeclWithMembers {
        protected override ConstantField CreateDuplicateOfMemberDecl() {
          var ncf = new ConstantField(new(), MemberDecl);
          ncf.NameNode = new($"{Name}_{ncf.NameNode}");
          return ncf;
        }
      }
      public class FromMethodOrFunction<E, M>(E enclosingDecl, M methodOrFunction, IReadOnlySet<AttributedExpression> contractWithAttr, IReadOnlySet<AssertStmt> assertsWithAttr) : FromMemberDecl<E, M>(enclosingDecl, methodOrFunction) where E : TopLevelDeclWithMembers where M : MethodOrFunction {
        private IReadOnlySet<AttributedExpression> ContractWithAttr { get; } = contractWithAttr;
        private IReadOnlySet<AssertStmt> AssertsWithAttr { get; } = assertsWithAttr;
        private void AlterOriginalMemberDecl() {
          foreach (var attributedExpression in ContractWithAttr) {
            (attributedExpression.Attributes, _) = Attributes.WithoutFirstOccurenceOf(attributedExpression.Attributes, AttributeName);
          }
          foreach (var assert in AssertsWithAttr) {
            (assert.Attributes, _) = Attributes.WithoutFirstOccurenceOf(assert.Attributes, AttributeName);
          }
        }
        private void ProtectDuplicate(M mof) {
          static FrameExpression ReplacedFrameExpression(FrameExpression rf) => new(rf.Origin, rf.OriginalExpression.AsProtected(), rf.FieldName);
          static void ModifyAssert(AssertStmt a) {
            if (Attributes.Contains(a.Attributes, AttributeName)) {
              //Console.WriteLine("Protecting to prove assertion " + a.Expr.ToString());
              a.Expr = a.Expr.WrappedWith(ProtectorFunctions.ProtectToProve);
            } else if (Attributes.Contains(a.Attributes, AttributeName + "_now")) {
              a.Expr = a.Expr.WrappedWith(ProtectorFunctions.ProtectToProve with { EntryPoint = false });
            } else {
              a.Expr = a.Expr.AsProtected();
              //Console.WriteLine($"assert statement: {a.Expr}");
            }
          }
          foreach (var arg in mof.Ins.Where(arg => arg.DefaultValue is not null)) {
            arg.DefaultValue = arg.DefaultValue!.AsProtected();
          }
          foreach (var req in mof.Req) {
            req.E = req.E.AsProtected();
          }
          foreach (var ens in mof.Ens) {
            if (Attributes.Contains(ens.Attributes, AttributeName)) {
              ens.E = ens.E.WrappedWith(ProtectorFunctions.ProtectToProve);
            } else {
              ens.E = ens.E.AsProtected();
            }
          }
          mof.Decreases.Expressions?.ModifyAllInPlace(ProtectedExtension.AsProtected);
          mof.Reads.Expressions?.ModifyAllInPlace(ReplacedFrameExpression);
          switch (mof) {
            case Microsoft.Dafny.Function { Body: not null } f:
              f.Body.PreResolveRecursiveSubStatements().OfType<AssertStmt>().ForEach(ModifyAssert);
              break;
            case Microsoft.Dafny.Function: break;
            case MethodOrConstructor { Body: not null } m:
              m.Body.Body.SelectMany(s => s.PreResolveRecursiveSubStatements()).OfType<AssertStmt>().ForEach(ModifyAssert);
              m.Mod.Expressions?.ModifyAllInPlace(ReplacedFrameExpression);
              break;
            case MethodOrConstructor: break;
            default: throw new UnreachableException();
          }
        }
        protected override M CreateDuplicateOfMemberDecl() {
          M result = null!;
          switch (MemberDecl) {
            case PrefixPredicate or SpecialFunction:
              throw new UnreachableException();
            case GreatestPredicate or LeastPredicate or TwoStatePredicate or TwoStateFunction:
              throw new NotImplementedException("only functions, predicates and lemmas are allowed");
            case Predicate p:
              var np = new Cloner().CloneFunction(p);
              np.NameNode = new($"{Name}_{np.NameNode}");
              result = (np as M)!;
              break;
            case Microsoft.Dafny.Function f:
              var nf = new Cloner().CloneFunction(f);
              nf.NameNode = new($"{Name}_{nf.NameNode}");
              result = (nf as M)!;
              break;
            case PrefixLemma:
              throw new UnreachableException();
            case GreatestLemma or LeastLemma or TwoStateLemma or Constructor:
              throw new NotImplementedException("only functions, predicates and lemmas are allowed");
            case Lemma l:
              var nl = new Lemma(new(), l);
              nl.NameNode = new($"{Name}_{nl.NameNode}");
              result = (nl as M)!;
              break;
            case Method:
              throw new NotImplementedException("only functions, predicates and lemmas are allowed");
            default:
              throw new UnreachableException();
          }
          AlterOriginalMemberDecl();
          ProtectDuplicate(result);
          return result;
        }
      }
      public class FromIteratorDecl(IteratorDecl iteratorDecl) : RefiningModuleGenerator {
        protected readonly IteratorDecl IteratorDecl = iteratorDecl;
        public override TopLevelDecl Process(DafnyOptions dafnyOptions, DefaultModuleDefinition root) {
          return null!;
        }
      }
      public class FromSubSetTypeDecl(SubsetTypeDecl subsetTypeDecl) : RefiningModuleGenerator {
        protected readonly SubsetTypeDecl SubsetTypeDecl = subsetTypeDecl;
        public override TopLevelDecl Process(DafnyOptions dafnyOptions, DefaultModuleDefinition root) {
          return null!;
        }
      }
    }

    #endregion
    private IEnumerable<RefiningModuleGenerator> SplitAndProtect(LiteralModuleDecl lmd) {
      Contract.Requires(lmd.ModuleDef.ModuleKind is (ModuleKindEnum.Abstract or ModuleKindEnum.Concrete),
        $"module must be either abstract or concrete, but is {lmd.ModuleDef.ModuleKind}");
      lmd.ModuleDef.ModuleKind = ModuleKindEnum.Abstract;
      foreach (var e in SplitAndProtect(lmd.ModuleDef.DefaultClass!)) { yield return e; }

      foreach (var prefix_lmd in lmd.ModuleDef.PrefixNamedModules.Select(pnm => pnm.Module)) {
        foreach (var e in SplitAndProtect(prefix_lmd)) { yield return e; }
      }
      foreach (var sd in lmd.ModuleDef.SourceDecls) {
        switch (sd) {
          case LiteralModuleDecl inner_lmd:
            foreach (var e in SplitAndProtect(inner_lmd)) { yield return e; }
            break;
          case ModuleExportDecl or AbstractModuleDecl or AliasModuleDecl:
            break; // nothing to be done on imports or exports
          case ModuleDecl: throw new UnreachableException();
          case IteratorDecl id:
            foreach (var e in SplitAndProtect(id)) { yield return e; }
            break;
          case TopLevelDeclWithMembers wm when wm is (ClassDecl or TraitDecl or DatatypeDecl or NewtypeDecl or AbstractTypeDecl):
            foreach (var e in SplitAndProtect(wm)) { yield return e; }
            break;
          case SubsetTypeDecl tsd:
            foreach (var e in SplitAndProtect(tsd)) { yield return e; }
            break;
          case ConcreteTypeSynonymDecl: break; // these are simply bare aliases
          default: throw new UnreachableException();
        }
      }
    }
    private IEnumerable<RefiningModuleGenerator> SplitAndProtect(IteratorDecl id) {
      if (id.Body is null) { yield break; }
      yield break;
      yield return new RefiningModuleGenerator.FromIteratorDecl(id);
    }
    private IEnumerable<RefiningModuleGenerator> SplitAndProtect(SubsetTypeDecl std) {
      if (std.Witness is null) { yield break; } // ??? maybe constraint also plays a role here?
      yield break;
      yield return new RefiningModuleGenerator.FromSubSetTypeDecl(std);
    }
    private IEnumerable<RefiningModuleGenerator> SplitAndProtect<E>(E dcd) where E : TopLevelDeclWithMembers {
      bool canHaveConstructors = dcd is ClassDecl or TraitDecl;
      foreach (var member in dcd.Members) {
        switch (member) {
          case ConstantField { Rhs: var e and not null, Attributes: var attrs } cf when HasAttr(attrs) || HasAttr(e):
            yield return new RefiningModuleGenerator.FromConstantField<E>(dcd, cf);
            break;
          case ConstantField: break;
          case Field: break;
          case MethodOrFunction m_or_f:
            //var contractWithAttr = Microsoft.Dafny.Util.Concat(m_or_f.Req.Where(HasAttr), m_or_f.Ens.Where(HasAttr)).ToImmutableHashSet();
            var contractWithAttr = m_or_f.Ens.Where(HasAttr).ToImmutableHashSet();
            switch (m_or_f) {
              case Microsoft.Dafny.Function { Body: not null } f
                  when AssertsContainingAttr(f.Body).ToImmutableHashSet() is var assertsWithAttr && (!contractWithAttr.IsEmpty || !assertsWithAttr.IsEmpty):
                yield return new RefiningModuleGenerator.FromMethodOrFunction<E, Microsoft.Dafny.Function>(dcd, f, contractWithAttr, assertsWithAttr);
                break;
              case Microsoft.Dafny.Function: break;
              case MethodOrConstructor { Body: not null } m_or_c when
                  m_or_c.Body.Body.SelectMany(AssertsContainingAttr).ToImmutableHashSet() is var assertsWithAttr && (!contractWithAttr.IsEmpty || !assertsWithAttr.IsEmpty):
                yield return m_or_c switch {
                  Method m => new RefiningModuleGenerator.FromMethodOrFunction<E, Method>(dcd, m, contractWithAttr, assertsWithAttr),
                  Constructor c => new RefiningModuleGenerator.FromMethodOrFunction<E, Constructor>(dcd, c, contractWithAttr, assertsWithAttr),
                  _ => throw new UnreachableException(),
                };
                break;
              case MethodOrConstructor: break;
              default: throw new UnreachableException();
            }
            break;
          default:
            throw new UnreachableException();
        }
      }
    }
    public static bool HasAttr(Attributes? attrs) => Attributes.Contains(attrs, AttributeName);
    public static IEnumerable<AssertStmt> AssertsContainingAttr(Expression e) => e.PreResolveRecursiveSubStatements().OfType<AssertStmt>().Where(assertStmt => HasAttr(assertStmt.Attributes));
    public static bool HasAttr(Expression e) => AssertsContainingAttr(e).Any();
    public static IEnumerable<AssertStmt> AssertsContainingAttr(Statement s) => s.PreResolveRecursiveSubStatements().OfType<AssertStmt>().Where(assertStmt => HasAttr(assertStmt.Attributes));
    public static bool HasAttr(Statement s) => AssertsContainingAttr(s).Any();
    public static bool HasAttr(AttributedExpression ae) => HasAttr(ae.Attributes) || HasAttr(ae.E);
  }
}
