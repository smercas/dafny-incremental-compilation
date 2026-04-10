#nullable enable
using Microsoft.Dafny;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using static Microsoft.Dafny.Change;

namespace DafnyCore.IncrementalCompilation {
  public class ProtectToProveApplySuffix : ApplySuffix, ICloneable<ProtectToProveApplySuffix> {
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
        switch (Changes[idx].WF, attributeBearingDeclaration) {
          case (AssertWFChange assertWFChange, [AssertStmt assertStmt, ..]):
            assertWFChange.Update(memberDecl, assertStmt);
            break;
          case (EnsuresWFChange ensuresWFChange, [AttributedExpression attributedExpression, ..]):
            ensuresWFChange.Update(memberDecl, attributedExpression);
            break;
          default: throw new UnreachableException(); //IPMTODO: from prior debugging, these were hit
        }
        switch (Changes[idx].ProofHint, attributeBearingDeclaration, memberDecl) {
          case (AssertWithByProofHintChange assertWithByProofHintChange, [AssertStmt assertStmt, BlockByProofStmt blockByProofStmt, ..], _) when ReferenceEquals(blockByProofStmt.Body, assertStmt):
            assertWithByProofHintChange.Update(memberDecl, blockByProofStmt);
            break;
          case (AssertWithoutByProofHintChange assertWithoutByProofHintChange, [AssertStmt assertStmt, ..], _):
            assertWithoutByProofHintChange.Update(memberDecl, assertStmt);
            break;
          case (FunctionEnsuresProofHintChange functionEnsuresProofHintChange, [AttributedExpression attributedExpression, ..], Function function):
            functionEnsuresProofHintChange.Update(function, attributedExpression);
            break;
          case (MethodOrConstructorEnsuresProofHintChange methodOrConstructorEnsuresProofHintChange, [AttributedExpression attributedExpression, ..], MethodOrConstructor methodOrConstructor):
            methodOrConstructorEnsuresProofHintChange.Update(methodOrConstructor, attributedExpression);
            break;
          default: throw new UnreachableException(); //IPMTODO: from prior debugging, these were hit
        }
      }
      ResetChangedModulesAndMembersLazy();
    }
    private static SortedSet<ProtectToProveApplySuffix> instances { get; set; } = new(Comparer);
    public static IReadOnlySet<ProtectToProveApplySuffix> Instances => instances;


    public sealed record ChangeContext(Lazy<MemberDecl> MemberDecl, IReadOnlyList<Lazy<IAttributeBearingDeclaration>> AttributeBearingDeclarations) {
      public ChangeContext((Lazy<MemberDecl>, Stack<Lazy<IAttributeBearingDeclaration>>) ctx) : this(ctx.Item1, [.. ctx.Item2]) { }
      public (MemberDecl MemberDecl, IReadOnlyList<IAttributeBearingDeclaration> AttributeBearingDeclarations) Evaluated =>
        (MemberDecl.Value, AttributeBearingDeclarations.ConvertAll(static abd => abd.Value));
    }
    private static Dictionary<ProtectToProveApplySuffix, ChangeContext> ChangeContexts { get; set; } = [];
    // these change objects can be computed once and updated with relevant information
    private static Lazy<List<(Change<WF>, Change<ProofHint>)>> changesLazy { get; } = new(() => [.. Instances.Select(i => ChangeContexts[i].Evaluated).Select(cc => (
      cc.AttributeBearingDeclarations switch {
        [AssertStmt assertStmt, ..] => new AssertWFChange(cc.MemberDecl, assertStmt) as Change<WF>,
        [AttributedExpression attributedExpression, ..] => new EnsuresWFChange(cc.MemberDecl, attributedExpression),
        _ => throw new NotImplementedException(), //IPMTODO: from prior debugging, these were hit
      },
      (cc.AttributeBearingDeclarations, cc.MemberDecl) switch {
        ([AssertStmt assertStmt, BlockByProofStmt blockByProofStmt, ..], _) when ReferenceEquals(blockByProofStmt.Body, assertStmt) => new AssertWithByProofHintChange(cc.MemberDecl, blockByProofStmt) as Change<ProofHint>,
        ([AssertStmt assertStmt, ..], _) => new AssertWithoutByProofHintChange(cc.MemberDecl, assertStmt),
        ([AttributedExpression attributedExpression, ..], Function function) => new FunctionEnsuresProofHintChange(function, attributedExpression),
        ([AttributedExpression attributedExpression, ..], MethodOrConstructor methodOrConstructor) => new MethodOrConstructorEnsuresProofHintChange(methodOrConstructor),
        (_, _) => throw new NotImplementedException(), //IPMTODO: from prior debugging, these were hit
      }
    ))]);
    public static IEnumerable<(string?, string?)> ChangeTexts {
      set {
        var changedModulesNeedToBeReset = false;
        foreach (var (changePair, (WFText, ProofHintText)) in Changes.Zip(value.ExtendWith(() => (null, null)))) {
          var prev = (changePair.WF.IsEmptyChange, changePair.ProofHint.IsEmptyChange);
          changePair.WF.Text = WFText;
          changePair.ProofHint.Text = ProofHintText;
          changedModulesNeedToBeReset |= prev != (changePair.WF.IsEmptyChange, changePair.ProofHint.IsEmptyChange);
        }
        if (changedModulesNeedToBeReset) { ResetChangedModulesAndMembersLazy(); }
      }
    }
    public const int ChangeTypesCount = 2;
    public static IReadOnlyList<(Change<WF> WF, Change<ProofHint> ProofHint)> Changes => changesLazy.Value;
    private static IEnumerable<Change> ChangesFlattener((Change<WF> WF, Change<ProofHint> ProofHint) changePair) { yield return changePair.WF; yield return changePair.ProofHint; }
    // since `Changes` are constructed once, `ChangesFlattened` can also be constructed once
    private static Lazy<IReadOnlyList<Change>> changesFlattened { get; } = new(() => [.. Changes.SelectMany(ChangesFlattener)]);
    public static IReadOnlyList<Change> ChangesFlattened => changesFlattened.Value;
    // since computing `changedModulesLazy` depends on many aspects that can change from run to run, the lazy instance needs to be reset when said aspects change
    private static void ResetChangedModulesAndMembersLazy() {
      changedModulesLazy = new(() => ChangesFlattened.Where(static c => !c.IsEmptyChange).Select(static c => c.AffectedModuleDecl).ToImmutableHashSet());
      changedMembersLazy = new(() => ChangesFlattened.Where(static c => !c.IsEmptyChange).OfType<IChangeToMemberDecl>().Select(static c => c.MemberDecl).ToImmutableHashSet());
    }
    private static Lazy<IReadOnlySet<ModuleDecl>> changedModulesLazy { get; set; } = new();
    public static IReadOnlySet<ModuleDecl> ChangedModules => changedModulesLazy.Value;
    private static Lazy<IReadOnlySet<MemberDecl>> changedMembersLazy { get; set; } = new();
    public static IReadOnlySet<MemberDecl> ChangedMembers => changedMembersLazy.Value;

    private static readonly Expression PlaceholderScope = new SeqDisplayExpr(SourceOrigin.NoToken, []);
    private static readonly Expression PlaceholderId = new LiteralExpr(SourceOrigin.NoToken);

    public ProtectToProveApplySuffix(Cloner cloner, ProtectToProveApplySuffix original) : base(cloner, original) {
      throw new UnreachableException("not sure if it can be reached, I sincerely hope it can't");
    }
    public new ProtectToProveApplySuffix Clone(Cloner cloner) => new(cloner, this);

    public ProtectToProveApplySuffix(Expression e, Protector protector, ChangeContext changeContext) : base(e.Origin, null, ProtectorFunctions.ProtectToProve.ToExprDotName(), [
        new(null, e.WithProtections(protector)),
      new(null, new StringLiteralExpr(SourceOrigin.NoToken, e.ToString(), false)),
      new(null, PlaceholderScope),
      new(null, PlaceholderId),
    ], Token.NoToken
    ) {
      Contract.Ensures(IsValidPreResolve);
      instances.Add(this);
      ChangeContexts[this] = changeContext;
    }
    public ProtectToProveApplySuffix(Expression e, Protector protector, BigInteger immediateOrder) : base(e.Origin, null, ProtectorFunctions.ProtectToProveImmediate.ToExprDotName(), [
        new(null, e.WithProtections(protector)),
      new(null, new StringLiteralExpr(SourceOrigin.NoToken, e.ToString(), false)),
      new(null, PlaceholderScope),
      new(null, new LiteralExpr(SourceOrigin.NoToken, immediateOrder)),
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

    public override ProtectToProveApplySuffix WithProtections(Protector protector) => throw new UnreachableException($"Due to the nature of the protection applied over the AST, no part of the AST should be processed more than once; this expression signals that a part of the AST ({this}) is to be processed at least twice");
  }
}
