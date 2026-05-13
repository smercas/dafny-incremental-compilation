#nullable enable
using Microsoft.Dafny;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Numerics;

namespace DafnyCore.IncrementalCompilation {
  public abstract class BaseProtectToProveApplySuffix : ApplySuffix, ICloneable<BaseProtectToProveApplySuffix> {

    protected static readonly Expression PlaceholderScope = new SeqDisplayExpr(SourceOrigin.NoToken, []);
    protected static readonly Expression PlaceholderId = new LiteralExpr(SourceOrigin.NoToken);

    public new BaseProtectToProveApplySuffix Clone(Cloner cloner) => throw new InvalidOperationException("`ProtectToProveApplySuffix` logic does not allow for cloning");

    protected BaseProtectToProveApplySuffix(Expression e, Protector protector, ProtectorFunctions.ProtectorFunction function, BigInteger? id = null) : base(e.Origin, null, function.ToExprDotName(), [
        new(null, e.WithProtections(protector)),
      new(null, new StringLiteralExpr(SourceOrigin.NoToken, e.ToString(), false)),
      new(null, PlaceholderScope),
      new(null, id is not null ? new LiteralExpr(SourceOrigin.NoToken, id) : PlaceholderId),
    ], Token.NoToken
    ) {
      Contract.Ensures(IsValidPreResolve);
    }
    public bool IsValidPreResolve => Bindings.ArgumentBindings is [_, _, { Actual: SeqDisplayExpr { Elements: [] } }, _];
    internal void AddScopeArgs(INewOrOldResolver resolver, ResolutionContext context) {
      Contract.Requires(IsValidPreResolve);
      //static List<T?> getThingsFromScope<T>(Scope<T> s) where T : class =>
      //    (s.GetType()
      //      .GetField("things", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
      //      .GetValue(s) as List<T?>)!;
      //Seq.Elements.AddRange(getThingsFromScope(resolver.Scope).IgnoreNulls().Distinct().Select(v => {
      //  var e = VariableNameWrappedIn_ProtectScope_Call(v.Name);
      //  var ns = (e.Bindings.ArgumentBindings.First(b => b.Actual is NameSegment _ns && _ns.Name == v.Name).Actual as NameSegment)!;

      //  var id = new IdentifierExpr(SourceOrigin.NoToken, v);
      //  ns.ResolvedExpression = id;
      //  ns.Type = id.Type.UseInternalSynonym();
      //  return e;
      //}));
      Bindings.ArgumentBindings.First(ab => ReferenceEquals(ab.Actual, PlaceholderScope)).Actual = new SeqDisplayExpr(SourceOrigin.NoToken, [.. resolver.ScopeArgsFrom(context)]);
      //System.Console.WriteLine('[' + string.Join(", ", Seq.Elements) + ']');
    }

    public override BaseProtectToProveApplySuffix WithProtections(Protector protector) => throw new UnreachableException($"Due to the nature of the protection applied over the AST, no part of the AST should be processed more than once; this expression signals that a part of the AST ({this}) is to be processed at least twice");
  }
  public class ProtectToProveApplySuffix : BaseProtectToProveApplySuffix {
    public static Comparer<ProtectToProveApplySuffix> Comparer { get; } = Comparer<ProtectToProveApplySuffix>.Create((l, r) => {
      int cmp = string.Compare(l.Origin.Uri.AbsoluteUri, r.Origin.Uri.AbsoluteUri);
      if (cmp != 0) { return cmp; }
      cmp = l.Origin.line.CompareTo(r.Origin.line);
      if (cmp != 0) { return cmp; }
      ;
      return l.Origin.col.CompareTo(r.Origin.col);
    });
    public static void ResetInstances() {
      instances = new(Comparer);
      ChangeContexts = [];
    }
    public static void AssignEntryPoints() {
      foreach (var (instance, idx) in instances.Indexed()) {
        instance.Bindings.ArgumentBindings.First(ab => ReferenceEquals(ab.Actual, PlaceholderId)).Actual = new LiteralExpr(SourceOrigin.NoToken, new BigInteger(idx));
        var (memberDecl, attributeBearingDeclaration) = ChangeContexts[instance].Evaluated;

        foreach (var baseChange in ChangesPerEntryPoint[idx]) {
          switch (baseChange, attributeBearingDeclaration, memberDecl) {
            case (AssertWFChange change, [AssertStmt assertStmt, ..], _):
              change.Update(memberDecl); break;
            case (InvariantWFChange change, [AttributedExpression invariant, LoopStmt loopStmt, ..], _) when loopStmt.Invariants.Contains(invariant):
              change.Update(memberDecl); break;
            case (EnsuresWFChange change, [AttributedExpression ensuresClause, ForallStmt forallStmt, ..], _) when forallStmt.Ens.Contains(ensuresClause):
              change.Update(memberDecl); break;
            case (EnsuresWFChange change, [AttributedExpression ensuresClause, OpaqueBlock opaqueBlock, ..], _) when opaqueBlock.Ensures.Contains(ensuresClause):
              change.Update(memberDecl); break;
            case (EnsuresWFChange change, [AttributedExpression ensuresClause], MethodOrFunction mof) when mof.Ens.Contains(ensuresClause):
              change.Update(memberDecl); break;
            case (AssertWithByProofHintChange change, [AssertStmt assertStmt, BlockByProofStmt blockByProofStmt, ..], _) when ReferenceEquals(blockByProofStmt.Body, assertStmt):
              change.Update(memberDecl); break;
            case (AssertWithoutByProofHintChange change, [AssertStmt assertStmt, ..], _):
              change.Update(memberDecl); break;
            case (InvariantInitialProofChange change, [AttributedExpression invariant, LoopStmt loopStmt, ..], _) when loopStmt.Invariants.Contains(invariant):
              change.Update(memberDecl); break;
            case (BodylessLoopInvariantMaintainProofChange change, [AttributedExpression invariant, OneBodyLoopStmt loopStmt, ..], _) when loopStmt.Invariants.Contains(invariant):
              change.Update(memberDecl); break;
            case (OneBodyLoopInvariantMaintainProofChange change, [AttributedExpression invariant, OneBodyLoopStmt loopStmt, ..], _) when loopStmt.Invariants.Contains(invariant):
              change.Update(memberDecl); break;
            case (AlternativeLoopInvariantMaintainProofChange change, [AttributedExpression invariant, AlternativeLoopStmt loopStmt, ..], _) when loopStmt.Invariants.Contains(invariant):
              change.Update(memberDecl); break;
            case (EnsuresBodylessForallStatementProofHintChange change, [AttributedExpression ensuresClause, ForallStmt forallStmt, ..], _) when forallStmt.Ens.Contains(ensuresClause):
              change.Update(memberDecl); break;
            case (EnsuresForallStatementWithBodyProofHintChange change, [AttributedExpression ensuresClause, ForallStmt forallStmt, ..], _) when forallStmt.Ens.Contains(ensuresClause):
              change.Update(memberDecl); break;
            case (EnsuresOpaqueBlockProofHintChange change, [AttributedExpression ensuresClause, OpaqueBlock opaqueBlock, ..], _) when opaqueBlock.Ensures.Contains(ensuresClause):
              change.Update(memberDecl); break;
            case (FunctionEnsuresProofHintChange change, [AttributedExpression ensuresClause], Function function) when function.Ens.Contains(ensuresClause):
              change.Update(function); break;
            case (BodylessMethodOrConstructorEnsuresProofHintChange change, [AttributedExpression ensuresClause], MethodOrConstructor methodOrConstructor) when methodOrConstructor.Ens.Contains(ensuresClause):
              change.Update(methodOrConstructor); break;
            case (MethodOrConstructorWithBodyEnsuresProofHintChange change, [AttributedExpression ensuresClause], MethodOrConstructor methodOrConstructor) when methodOrConstructor.Ens.Contains(ensuresClause):
              change.Update(methodOrConstructor); break;
            default: throw new NotImplementedException();
          }
        }
      }
    }
    private static SortedSet<ProtectToProveApplySuffix> instances { get; set; } = new(Comparer);
    public static IReadOnlySet<ProtectToProveApplySuffix> Instances => instances;


    public sealed record ChangeContext(Lazy<MemberDecl> MemberDecl, IReadOnlyList<Lazy<IAttributeBearingDeclaration>> AttributeBearingDeclarations) {
      public ChangeContext((Lazy<MemberDecl>, Stack<Lazy<IAttributeBearingDeclaration>>) ctx) : this(ctx.Item1, [.. ctx.Item2]) { }
      public (MemberDecl MemberDecl, IReadOnlyList<IAttributeBearingDeclaration> AttributeBearingDeclarations) Evaluated =>
        (MemberDecl.Value, AttributeBearingDeclarations.ConvertAll(static abd => abd.Value));
    }
    private static Dictionary<ProtectToProveApplySuffix, ChangeContext> ChangeContexts { get; set; } = [];
    // these baseChange objects can be computed once and updated with relevant information
    private static IEnumerable<Change> changesFromContext(MemberDecl memberDecl, IReadOnlyList<IAttributeBearingDeclaration> declsWithAttributes) {
      yield return (declsWithAttributes, memberDecl) switch {
        ([AssertStmt assertStmt, ..], _) => new AssertWFChange(memberDecl, assertStmt),
        ([AttributedExpression invariant, LoopStmt loopStmt, ..], _) when loopStmt.Invariants.Contains(invariant) => new InvariantWFChange(memberDecl, invariant),
        ([AttributedExpression ensuresClause, ForallStmt forallStmt, ..], _) when forallStmt.Ens.Contains(ensuresClause) => new EnsuresWFChange(memberDecl, ensuresClause),
        ([AttributedExpression ensuresClause, OpaqueBlock opaqueBlock, ..], _) when opaqueBlock.Ensures.Contains(ensuresClause) => new EnsuresWFChange(memberDecl, ensuresClause),
        ([AttributedExpression ensuresClause], MethodOrFunction mof) when mof.Ens.Contains(ensuresClause) => new EnsuresWFChange(memberDecl, ensuresClause),
        (_, _) => throw new NotImplementedException(), //IPMTODO: from prior debugging, these were hit
      };
      foreach (var change in (declsWithAttributes, memberDecl) switch {
        ([AssertStmt assertStmt, BlockByProofStmt blockByProofStmt, ..], _) when ReferenceEquals(blockByProofStmt.Body, assertStmt) =>
          new List<Change> { new AssertWithByProofHintChange(memberDecl, blockByProofStmt) },
        ([AssertStmt assertStmt, ..], _) => [new AssertWithoutByProofHintChange(memberDecl, assertStmt)],
        ([AttributedExpression invariant, LoopStmt loopStmt, ..], _) when loopStmt.Invariants.Contains(invariant) => [new InvariantInitialProofChange(memberDecl, loopStmt), ..loopStmt switch {
          OneBodyLoopStmt { Body: null } oneBodyLoopStmt => new List<Change> { new BodylessLoopInvariantMaintainProofChange(memberDecl, oneBodyLoopStmt) } as IEnumerable<Change>,
          OneBodyLoopStmt { Body: not null } oneBodyLoopStmt => [new OneBodyLoopInvariantMaintainProofChange(memberDecl, oneBodyLoopStmt)],
          AlternativeLoopStmt alternativeLoopStmt => alternativeLoopStmt.Alternatives.Select((a, i) => new AlternativeLoopInvariantMaintainProofChange(memberDecl, alternativeLoopStmt, i)),
          _ => throw new UnreachableException(),
        }],
        ([AttributedExpression ensuresClause, ForallStmt forallStmt, ..], _) when forallStmt.Ens.Contains(ensuresClause) => [forallStmt.Body switch {
          null => new EnsuresBodylessForallStatementProofHintChange(memberDecl, forallStmt),
          not null => new EnsuresForallStatementWithBodyProofHintChange(memberDecl, forallStmt),
        }],
        ([AttributedExpression ensuresClause, OpaqueBlock opaqueBlock, ..], _) when opaqueBlock.Ensures.Contains(ensuresClause) => [new EnsuresOpaqueBlockProofHintChange(memberDecl, opaqueBlock)],
        ([AttributedExpression ensuresClause], Function function) when function.Ens.Contains(ensuresClause) => [new FunctionEnsuresProofHintChange(function, ensuresClause)],
        ([AttributedExpression ensuresClause], MethodOrConstructor methodOrConstructor) when methodOrConstructor.Ens.Contains(ensuresClause) => [methodOrConstructor.Body switch {
          null => new BodylessMethodOrConstructorEnsuresProofHintChange(methodOrConstructor),
          not null => new MethodOrConstructorWithBodyEnsuresProofHintChange(methodOrConstructor),
        }],
        (_, _) => throw new NotImplementedException(), //IPMTODO: from prior debugging, these were hit
      }) { yield return change; }
    }
    public class NoMatch(string text) : Exception($"{text} doesn't match any of the supported changesGrouped imput patterns");
    public static void ModifyChangesWith(string text) {
      var matched = Patterns.Parse(text);
      if (matched is null) { throw new NoMatch(text); }
      var change = matched.MatchChange();
      change.Text = matched.Text;
    }
    public static void EmptyAllChanges() {
      foreach (var change in ChangesFlattened) {
        change.Text = null;
      }
    }
    public static IEnumerable<(Uri Uri, OmniSharp.Extensions.LanguageServer.Protocol.Models.Range Range, string Text)> AggregatedNonEmptyChanges { get {
      foreach (var changesGrouped in ChangesFlattened.Where(static c => !c.IsEmptyChange).GroupBy(c => c switch {
        BodylessLoopInvariantMaintainProofChange imp => imp.Range,
        EnsuresBodylessForallStatementProofHintChange feph => feph.Range,
        BodylessMethodOrConstructorEnsuresProofHintChange meph => meph.Range,
        _ => new object(),
      })) {
        yield return (changesGrouped.Key, changesGrouped.First()) switch {
          (OmniSharp.Extensions.LanguageServer.Protocol.Models.Range range, BodylessLoopInvariantMaintainProofChange imp) => (imp.Uri, range, $"{{ {string.Join(' ', changesGrouped.Select(c => c.Text))} }}"),
          (OmniSharp.Extensions.LanguageServer.Protocol.Models.Range range, EnsuresBodylessForallStatementProofHintChange feph) => (feph.Uri, range, $"{{ {string.Join(' ', changesGrouped.Select(c => c.Text))} }}"),
          (OmniSharp.Extensions.LanguageServer.Protocol.Models.Range range, BodylessMethodOrConstructorEnsuresProofHintChange meph) => (meph.Uri, range, $"{{ {string.Join(' ', changesGrouped.Select(c => c.Text))} }}"),
          // yes, the first three cases could be merged, IPMTODO: see if there's any point in keeping them separate
          (object _, var onlyChange) => (onlyChange.Uri, onlyChange.Range, onlyChange.Text!),
        };
      }
    } }

    private static Lazy<List<List<Change>>> changesPerEntryPointLazy { get; } = new(() => [.. Instances.Select(i => ChangeContexts[i].Evaluated).Select(cc => changesFromContext(cc.MemberDecl, cc.AttributeBearingDeclarations).ToList())]);
    public static IReadOnlyList<IReadOnlyList<Change>> ChangesPerEntryPoint => changesPerEntryPointLazy.Value;
    private static Lazy<IReadOnlyList<Change>> changesFlattenedLazy { get; } = new(() => [.. ChangesPerEntryPoint.SelectMany(i => i)]);
    public static IEnumerable<Change> ChangesFlattened => changesFlattenedLazy.Value;
    public static IReadOnlySet<ModuleDecl> ChangedModules => ChangesFlattened.Where(static c => !c.IsEmptyChange).Select(static c => c.AffectedModuleDecl).ToImmutableHashSet();
    public static IReadOnlySet<MemberDecl> ChangedMembers => ChangesFlattened.Where(static c => !c.IsEmptyChange).OfType<IChangeToMemberDecl>().Select(static c => c.MemberDecl).ToImmutableHashSet();
    protected ProtectToProveApplySuffix(Expression e, Protector protector, ProtectorFunctions.ProtectorFunction protectorFunction) : base(e, protector, protectorFunction) {
      instances.Add(this);
      ChangeContexts[this] = new ChangeContext(protector.MostRecentContext);
    }
    public ProtectToProveApplySuffix(Expression e, Protector protector) : this(e, protector, ProtectorFunctions.ProtectToProve) {}
  }
  public class ProtectToProveInvApplySuffix(Expression e, Protector protector) : ProtectToProveApplySuffix(e, protector, ProtectorFunctions.ProtectToProveInv) {}
  public class ProtectToProveImmediateApplySuffix(Expression e, Protector protector, BigInteger id) : BaseProtectToProveApplySuffix(e, protector, ProtectorFunctions.ProtectToProveImmediate, id) { }
}
