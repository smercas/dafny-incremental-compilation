#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Boogie;
using Microsoft.Dafny;

namespace DafnyCore.IncrementalCompilation {
  internal class ModuleSplitter(DafnyOptions dafnyOptions) {
    private DafnyOptions DafnyOptions => dafnyOptions;
    public static readonly string Name = "_IPM";
    public static readonly string AttributeName = "ipm";
    public LiteralModuleDecl Split(Microsoft.Dafny.Program p) {
      Contract.Requires(p.DefaultModuleDef.SourceDecls.NoneAreOfType<ModuleExportDecl>()); // parser doesn't allow export decls in root module
      static LiteralModuleDecl MakeNewModuleWithOldRootStuff(ModuleSplitter self, Microsoft.Dafny.Program p) {
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

      foreach (var g in Split(moduleWithOldRootStuff)) {
        p.DefaultModuleDef.SourceDecls.Add(g.Process(DafnyOptions, p.DefaultModuleDef));
      }
      return moduleWithOldRootStuff;
    }
    #region helper processing classes
    private abstract class RefiningModuleGenerator {
      public abstract TopLevelDecl Process(DafnyOptions dafnyOptions, DefaultModuleDefinition root);
      private static List<TopLevelDecl> Trace(TopLevelDecl topLevelDecl) {
        List<TopLevelDecl> r = [ topLevelDecl ];
        while (true) {
          var e = r[^1].EnclosingModuleDefinition?.EnclosingLiteralModuleDecl;
          if (e is null) { break; }
          r.Add(e);
        }
        r = r[1..^1];
        r.Reverse();
        return r;
      }
      public class FromMemberDecl<E, M>(E enclosingDecl, M memberDecl): RefiningModuleGenerator where E: TopLevelDeclWithMembers where M: MemberDecl {
        protected readonly M MemberDecl = memberDecl;
        protected E EnclosingDecl => enclosingDecl;
        protected ModuleDefinition OriginalModuleDef => EnclosingDecl.EnclosingModuleDefinition;
        protected LiteralModuleDecl OriginalModuleDecl => OriginalModuleDef.EnclosingLiteralModuleDecl!;
        private LiteralModuleDecl CreateModule(DafnyOptions dafnyOptions, DefaultModuleDefinition root) {
          var trace = Trace(EnclosingDecl);
          var refinement_target_parts = trace.Select(d => d.Name);
          if (enclosingDecl is not DefaultClassDecl) { refinement_target_parts = refinement_target_parts.Append(EnclosingDecl.Name); }
          var def = new ModuleDefinition(
            OriginalModuleDef.Origin,
            $"{string.Join("_", refinement_target_parts)}_{MemberDecl.Name}".ToNameNodeWithVirtualToken(),
            [],
            ModuleKindEnum.Abstract,
            new(ImplementationKind.Refinement, new([..refinement_target_parts.Select(Microsoft.Dafny.Util.ToNameNodeWithVirtualToken)])),
            root,
            null,
            []
          );
          var decl = new LiteralModuleDecl(dafnyOptions, def, root, Guid.NewGuid());
          return decl;
        }
        private M CreateDuplicateOfMemberDecl() {
          // radd {:verify false} to original member decl?
          switch (MemberDecl) {
            case ConstantField cf:
              var ncf = new ConstantField(new(), cf);
              ncf.NameNode = new($"{Name}_{ncf.NameNode}");
              return (ncf as M)!;
            case PrefixPredicate or SpecialFunction: throw new UnreachableException();
            case GreatestPredicate or LeastPredicate or TwoStatePredicate or TwoStateFunction:
              throw new NotImplementedException("only functions, predicates and lemmas are allowed");
            case Predicate p:
              var np = new Cloner().CloneFunction(p);
              np.NameNode = new($"{Name}_{np.NameNode}");
              return (np as M)!;
            case Microsoft.Dafny.Function f:
              var nf = new Cloner().CloneFunction(f);
              nf.NameNode = new($"{Name}_{nf.NameNode}");
              return (nf as M)!;
            case PrefixLemma: throw new UnreachableException();
            case GreatestLemma or LeastLemma or TwoStateLemma or Constructor:
              throw new NotImplementedException("only functions, predicates and lemmas are allowed");
            case Lemma l:
              var nl = new Lemma(new(), l);
              nl.NameNode = new($"{Name}_{nl.NameNode}");
              return (nl as M)!;
            case Method:
              throw new NotImplementedException("only functions, predicates and lemmas are allowed");
            default: throw new UnreachableException();
          }
        }
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
      public class FromIteratorDecl(IteratorDecl iteratorDecl): RefiningModuleGenerator {
        protected readonly IteratorDecl IteratorDecl = iteratorDecl;
        public override TopLevelDecl Process(DafnyOptions dafnyOptions, DefaultModuleDefinition root) {
          return null!;
        }
      }
      public class FromSubSetTypeDecl(SubsetTypeDecl subsetTypeDecl): RefiningModuleGenerator {
        protected readonly SubsetTypeDecl SubsetTypeDecl = subsetTypeDecl;
        public override TopLevelDecl Process(DafnyOptions dafnyOptions, DefaultModuleDefinition root) {
          return null!;
        }
      }
    }

    #endregion
    private IEnumerable<RefiningModuleGenerator> Split(LiteralModuleDecl lmd) {
      Contract.Requires(lmd.ModuleDef.ModuleKind is (ModuleKindEnum.Abstract or ModuleKindEnum.Concrete),
        $"module must be either abstract or concrete, but is {lmd.ModuleDef.ModuleKind}");
      lmd.ModuleDef.ModuleKind = ModuleKindEnum.Abstract;
      foreach (var e in Split(lmd.ModuleDef.DefaultClass!)) { yield return e; }

      foreach (var prefix_lmd in lmd.ModuleDef.PrefixNamedModules.Select(pnm => pnm.Module)) {
        foreach (var e in Split(prefix_lmd)) { yield return e; }
      }
      foreach (var sd in lmd.ModuleDef.SourceDecls) {
        switch (sd) {
          case LiteralModuleDecl inner_lmd:
            foreach (var e in Split(inner_lmd)) { yield return e; }
            break;
          case ModuleExportDecl or AbstractModuleDecl or AliasModuleDecl:
            break; // nothing to be done on imports or exports
          case ModuleDecl: throw new UnreachableException();
          case IteratorDecl id:
            foreach (var e in Split(id)) { yield return e; }
            break;
          case TopLevelDeclWithMembers wm when wm is (ClassDecl or TraitDecl or DatatypeDecl or NewtypeDecl or AbstractTypeDecl):
            foreach (var e in Split(wm)) { yield return e; }
            break;
          case SubsetTypeDecl tsd:
            foreach (var e in Split(tsd)) { yield return e; }
            break;
          case ConcreteTypeSynonymDecl: break; // these are simply bare aliases
          default: throw new UnreachableException();
        }
      }
    }
    private IEnumerable<RefiningModuleGenerator> Split(IteratorDecl id) {
      if (id.Body is null) { yield break; }
      yield return new RefiningModuleGenerator.FromIteratorDecl(id);
    }
    private IEnumerable<RefiningModuleGenerator> Split(SubsetTypeDecl std) {
      if (std.Witness is null) { yield break; } // ??? maybe constraint also plays a role here?
      yield return new RefiningModuleGenerator.FromSubSetTypeDecl(std);
    }
    private IEnumerable<RefiningModuleGenerator> Split<E>(E dcd) where E: TopLevelDeclWithMembers {
      bool canHaveConstructors = dcd is ClassDecl or TraitDecl;
      foreach (var member in dcd.Members) {
        switch (member) {
          case ConstantField { Rhs: var e and not null, Attributes: var attrs } cf when HasAttr(attrs) || HasAttr(e):
            yield return new RefiningModuleGenerator.FromMemberDecl<E, ConstantField>(dcd, cf);
            break;
          case ConstantField: break;
          case Field: break;
          case MethodOrFunction m_or_f:
            var attr_in_contract = m_or_f.Req.Any(HasAttr) || m_or_f.Ens.Any(HasAttr);
            switch (m_or_f) {
              case Microsoft.Dafny.Function { Body: not null } f when attr_in_contract || HasAttr(f.Body):
                yield return new RefiningModuleGenerator.FromMemberDecl<E, Microsoft.Dafny.Function>(dcd, f);
                break;
              case Microsoft.Dafny.Function: break;
              case MethodOrConstructor { Body: not null } m_or_c when attr_in_contract || m_or_c.Body.Body.Any(HasAttr):
                yield return m_or_c switch {
                  Method m => new RefiningModuleGenerator.FromMemberDecl<E, Method>(dcd, m),
                  Constructor c => new RefiningModuleGenerator.FromMemberDecl<E, Constructor>(dcd, c),
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
    public static bool HasAttr(Expression e) => e.PreResolveSubStatements().OfType<AssertStmt>().Any(assertStmt => HasAttr(assertStmt.Attributes));
    public static bool HasAttr(Statement s) => s.PreResolveSubStatements().OfType<AssertStmt>().Any(assertStmt => HasAttr(assertStmt.Attributes));
    public static bool HasAttr(AttributedExpression ae) => HasAttr(ae.Attributes) || HasAttr(ae.E);
  }
}
