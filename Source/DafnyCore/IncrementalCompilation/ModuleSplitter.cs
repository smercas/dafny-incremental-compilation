#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Boogie;
using Microsoft.Dafny;

namespace DafnyCore.IncrementalCompilation {
  public class ModuleSplitter(DafnyOptions dafnyOptions) {
    private DafnyOptions DafnyOptions => dafnyOptions;
    public LiteralModuleDecl Split(Microsoft.Dafny.Program p) {
      Contract.Requires(p.DefaultModuleDef.SourceDecls.NoneAreOfType<ModuleExportDecl>()); // parser doesn't allow export decls in root module
      static LiteralModuleDecl MakeNewModuleWithOldRootStuff(ModuleSplitter self, Microsoft.Dafny.Program p) {
        var def = new ModuleDefinition(
          p.DefaultModuleDef.Origin,
          Constants.Name.ToNameNodeWithVirtualToken(),
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
        private LiteralModuleDecl CreateModule(DafnyOptions dafnyOptions, ModuleDefinition enclosingModule) {
          var trace = Trace(EnclosingDecl);
          var refinementTargetParts = trace.Select(d => d.Name);
          IEnumerable<string> enclosingDeclName = [];
          if (EnclosingDecl is not DefaultClassDecl) { enclosingDeclName = [ EnclosingDecl.Name ]; }
          var def = new ModuleDefinition(
            MemberDecl.Origin,
            $"{string.Join("_", [.. refinementTargetParts, .. enclosingDeclName])}_{MemberDecl.Name}".ToNameNodeWithVirtualToken(),
            [],
            ModuleKindEnum.Abstract,
            new(ImplementationKind.Refinement, new([.. refinementTargetParts.Select(Microsoft.Dafny.Util.ToNameNodeWithVirtualToken)])),
            enclosingModule,
            null,
            []
          );
          var decl = new LiteralModuleDecl(dafnyOptions, def, enclosingModule, Guid.NewGuid());
          return decl;
        }
        protected abstract void AlterOriginalMemberDecl();
        protected abstract M CreateDuplicateOfOriginalMemberDecl();
        public override TopLevelDecl Process(DafnyOptions dafnyOptions, DefaultModuleDefinition root) {
          var new_decl = CreateModule(dafnyOptions, root);
          var new_member = CreateDuplicateOfOriginalMemberDecl();
          new_member.NameNode = $"{Constants.Name}_{new_member.NameNode}".ToNameNodeWithVirtualToken();
          switch (EnclosingDecl) {
            case DefaultClassDecl: {
              new_member.EnclosingClass = new_decl.ModuleDef.DefaultClass!;
              new_decl.ModuleDef.DefaultClass!.Members.Add(new_member);
              AlterOriginalMemberDecl();
              new_decl.ModuleDef.DefaultClass!.SetMembersBeforeResolution();
            } break;
            case ClassDecl or TraitDecl or DatatypeDecl or NewtypeDecl or AbstractTypeDecl: {
              TopLevelDecl new_enclosing_class = EnclosingDecl switch {
                ClassDecl => new ClassDecl(EnclosingDecl.Origin, EnclosingDecl.NameNode.Clone(new()), null, [.. EnclosingDecl.TypeArgs], new_decl.ModuleDef, [new_member], [], true),
                TraitDecl => new TraitDecl(EnclosingDecl.Origin, EnclosingDecl.NameNode.Clone(new()), new_decl.ModuleDef, [.. EnclosingDecl.TypeArgs], [new_member], null, true, []),
                DatatypeDecl => EnclosingDecl switch {
                  CoDatatypeDecl => new CoDatatypeDecl(EnclosingDecl.Origin, EnclosingDecl.NameNode.Clone(new()), new_decl.ModuleDef, [.. EnclosingDecl.TypeArgs], [], [], [new_member], null, true),
                  IndDatatypeDecl indDatatypeDecl => indDatatypeDecl switch {
                    TupleTypeDecl => throw new UnreachableException("`TupleTypeDecl` should only be present in the `System` module"),
                    _ when indDatatypeDecl.IsExactly() => new IndDatatypeDecl(EnclosingDecl.Origin, EnclosingDecl.NameNode.Clone(new()), new_decl.ModuleDef, [.. EnclosingDecl.TypeArgs], [], [], [new_member], null, true),
                    _ => throw new UnreachableException(),
                  },
                  _ => throw new UnreachableException(),
                },
                NewtypeDecl => new NewtypeDecl(EnclosingDecl.Origin, EnclosingDecl.NameNode.Clone(new()), [.. EnclosingDecl.TypeArgs], new_decl.ModuleDef, null, SubsetTypeDecl.WKind.CompiledZero, null, [], [new_member], null, true),
                AbstractTypeDecl { Characteristics: var characteristics } => new AbstractTypeDecl(EnclosingDecl.Origin, EnclosingDecl.NameNode.Clone(new()), new_decl.ModuleDef, new TypeParameterCharacteristics(characteristics.EqualitySupport, characteristics.AutoInit, characteristics.ContainsNoReferenceTypes) { SourceOrigin = characteristics.SourceOrigin }, [.. EnclosingDecl.TypeArgs], [], [new_member], null, true),
                _ => throw new UnreachableException(),
              };
              new_member.EnclosingClass = new_enclosing_class;
              new_decl.ModuleDef.SourceDecls.Add(new_enclosing_class);
              AlterOriginalMemberDecl();
            } break;

          }
          return new_decl;
        }
      }
      public class FromConstantField<E>(E enclosingDecl, ConstantField constantField) : FromMemberDecl<E, ConstantField>(enclosingDecl, constantField) where E : TopLevelDeclWithMembers {
        protected override ConstantField CreateDuplicateOfOriginalMemberDecl() => throw new NotImplementedException();
        protected override void AlterOriginalMemberDecl() => throw new NotImplementedException();
      }
      public class FromMethodOrFunction<E>(E enclosingDecl, MethodOrFunction methodOrFunction, IReadOnlyDictionary<AttributedExpression, IReadOnlySet<AttributesAccessor>> attrInContract, IReadOnlySet<AttributesAccessor> attrInBody) : FromMemberDecl<E, MethodOrFunction>(enclosingDecl, methodOrFunction) where E : TopLevelDeclWithMembers {
        private IReadOnlyDictionary<AttributedExpression, IReadOnlySet<AttributesAccessor>> AttrInContract { get; } = attrInContract;
        private IReadOnlySet<AttributesAccessor> AttrInBody { get; } = attrInBody;
        protected override MethodOrFunction CreateDuplicateOfOriginalMemberDecl() => MemberDecl.WithProtections(new());

        protected override void AlterOriginalMemberDecl() {
          foreach (var acc in Microsoft.Dafny.Util.Concat(AttrInContract.Values.SelectMany(v => v), AttrInBody)) {
            (acc.Attributes, _) = Attributes.WithoutFirstOccurenceOf(acc.Attributes, Constants.AttributeName);
          }
          foreach (var acc in Microsoft.Dafny.Util.Concat(
            AttrInContract.Keys.SelectMany(k => ContainingAttr(k, Constants.ImmediateAttributeName)), // {:ipm_now} can't be found in an ensures clause that doesn't have {:ipm}
            MemberDecl switch {
              Microsoft.Dafny.Function f => ContainingAttr(f.Body!, Constants.ImmediateAttributeName),
              MethodOrConstructor m => ContainingAttr(m.Body!, Constants.ImmediateAttributeName),
              _ => throw new UnreachableException(),
            }
          )) {
            (acc.Attributes, _) = Attributes.WithoutFirstOccurenceOf(acc.Attributes, Constants.ImmediateAttributeName);
          }
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
    private static IEnumerable<RefiningModuleGenerator> Split(LiteralModuleDecl lmd) {
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
          case TopLevelDeclWithMembers wm when wm is ClassDecl or TraitDecl or DatatypeDecl or NewtypeDecl or AbstractTypeDecl:
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
    private static IEnumerable<RefiningModuleGenerator> Split(IteratorDecl id) {
      if (id.Body is null) { yield break; }
      yield break;
      yield return new RefiningModuleGenerator.FromIteratorDecl(id);
    }
    private static IEnumerable<RefiningModuleGenerator> Split(SubsetTypeDecl std) {
      if (std.Witness is null) { yield break; } // ??? maybe constraint also plays a role here?
      yield break;
      yield return new RefiningModuleGenerator.FromSubSetTypeDecl(std);
    }
    public class VerificationExclusionaryOrigin(IOrigin o) : IOrigin {
      public bool IncludesRange => o.IncludesRange;
      public Uri Uri => o.Uri;
      public TokenRange? EntireRange => o.EntireRange;
      public TokenRange ReportingRange => o.ReportingRange;
      public bool IsCopy => true;
      public bool IsSourceToken => o.IsSourceToken;
      public int kind { get => o.kind; set => o.kind = value; }
      public int pos { get => o.pos; set => o.pos = value; }
      public int col { get => o.col; set => o.col = value; }
      public int line { get => o.line; set => o.line = value; }
      public string val { get => o.val; set => o.val = value; }
      public bool IsValid => o.IsValid;
      public int CompareTo(IToken? other) => o.CompareTo(other);
      public bool IsInherited(ModuleDefinition m) => o.IsInherited(m);
    }
    private static IEnumerable<RefiningModuleGenerator> Split<E>(E dcd) where E : TopLevelDeclWithMembers {
      foreach (var member in dcd.Members) {
        switch (member) {
          case ConstantField { Rhs: var e and not null, Attributes: var attrs } cf when HasAttr(attrs, Constants.AttributeName) || ContainingAttr(e, Constants.AttributeName).Any():
            yield return new RefiningModuleGenerator.FromConstantField<E>(dcd, cf);
            break;
          case ConstantField: break;
          case Field: break;
          case MethodOrFunction m_or_f:
            //var contractWithAttr = Microsoft.Dafny.Util.Concat(m_or_f.Req.Where(HasAttr), m_or_f.Ens.Where(HasAttr)).ToImmutableHashSet();
            var attrInContract = m_or_f.Ens.SelectWhere(ens => {
              var attrs = ContainingAttr(ens, Constants.AttributeName).ToImmutableHashSet() as IReadOnlySet<AttributesAccessor>;
              return (attrs.Count != 0, (ens, attrs));
            }).ToImmutableDictionary(p => p.ens, p => p.attrs);
            switch (m_or_f) {
              case Microsoft.Dafny.Function { Body: not null } f
                  when ContainingAttr(f, Constants.AttributeName).ToImmutableHashSet() is var assertsWithAttr && (!attrInContract.IsEmpty || !assertsWithAttr.IsEmpty):
                yield return new RefiningModuleGenerator.FromMethodOrFunction<E>(dcd, f, attrInContract, assertsWithAttr);
                break;
              case Microsoft.Dafny.Function: break;
              case MethodOrConstructor { Body: not null } m_or_c when
                  ContainingAttr(m_or_c, Constants.AttributeName).ToImmutableHashSet() is var assertsWithAttr && (!attrInContract.IsEmpty || !assertsWithAttr.IsEmpty):
                yield return m_or_c switch {
                  Method m => new RefiningModuleGenerator.FromMethodOrFunction<E>(dcd, m, attrInContract, assertsWithAttr),
                  Constructor c => new RefiningModuleGenerator.FromMethodOrFunction<E>(dcd, c, attrInContract, assertsWithAttr),
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
        if (member is ICanVerify) { member.SetOrigin(new VerificationExclusionaryOrigin(member.Origin)); }
      }
    }
    public static bool HasAttr(Attributes? attrs, string attr) => Attributes.Contains(attrs, attr);
    public static IEnumerable<AttributesAccessor> ContainingAttr(AttributedExpression ae, string attr) {
      if (HasAttr(ae.Attributes, attr)) { yield return new AttributesAccessor(ae); }
      foreach (var aa in ContainingAttr(ae.E, attr)) { yield return aa; }
    }
    public static IEnumerable<AttributesAccessor> ContainingAttr(Expression e, string attr) => e.PreResolveRecursiveSubStatements().OfType<AssertStmt>().Where(assertStmt => HasAttr(assertStmt.Attributes, attr)).Select(s => new AttributesAccessor(s));

    public static IEnumerable<AttributesAccessor> ContainingAttr(Statement s, string attr) => s.PreResolveRecursiveSubStatements().OfType<AssertStmt>().Where(assertStmt => HasAttr(assertStmt.Attributes, attr)).Select(s => new AttributesAccessor(s));

    public static IEnumerable<AttributesAccessor> ContainingAttr(MethodOrFunction m_or_f, string attr) =>
      Microsoft.Dafny.Util.Concat(
        Microsoft.Dafny.Util.Concat(
          Microsoft.Dafny.Util.Concat(m_or_f.Req, m_or_f.Ens).Select(static ae => ae.E),
          Microsoft.Dafny.Util.Concat([m_or_f.Reads], m_or_f switch { MethodOrConstructor m_or_c => [m_or_c.Mod], Microsoft.Dafny.Function => [], _ => throw new UnreachableException(), })
            .SelectMany(static s => s.Expressions ?? []).Select(static fe => fe.OriginalExpression),
          m_or_f.Decreases.Expressions ?? []
        ).SelectMany(static e => e.PreResolveRecursiveSubStatements()),
        m_or_f switch {
          MethodOrConstructor { Body: not null } m_or_c => m_or_c.Body.PreResolveRecursiveSubStatements(),
          Microsoft.Dafny.Function { Body: not null } f => f.Body.PreResolveRecursiveSubStatements(),
          _ => throw new UnreachableException(),
        } ?? []
      ).OfType<AssertStmt>().Where(assertStmt => HasAttr(assertStmt.Attributes, attr)).Select(s => new AttributesAccessor(s));
    public class AttributesAccessor {
      private Func<Attributes?> get { get; }
      private Action<Attributes?> set { get; }
      public AttributesAccessor(Statement s) {
        get = () => s.Attributes;
        set = (value) => s.Attributes = value;
      }
      public AttributesAccessor(AttributedExpression e) {
        get = () => e.Attributes;
        set = (value) => e.Attributes = value;
      }
      public Attributes? Attributes { get => get(); set => set(value); }
    }
  }
}
