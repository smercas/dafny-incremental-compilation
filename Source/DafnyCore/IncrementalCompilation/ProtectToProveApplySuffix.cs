using Microsoft.Dafny;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace DafnyCore.IncrementalCompilation {
  internal class ProtectToProveApplySuffix : ApplySuffix, ICloneable<ProtectToProveApplySuffix> {
    private static Comparer<ProtectToProveApplySuffix> comparer { get; } = Comparer<ProtectToProveApplySuffix>.Create((l, r) => {
      int cmp = string.Compare(l.Origin.Uri.AbsoluteUri, r.Origin.Uri.AbsoluteUri);
      if (cmp != 0) { return cmp; }
      cmp = l.Origin.line.CompareTo(r.Origin.line);
      if (cmp != 0) { return cmp; }
      ;
      return l.Origin.col.CompareTo(r.Origin.col);
    });
    public static void ResetInstances() {
      instances = new(comparer);
    }
    public static void AssignEntryPoints() {
      foreach (var (instance, idx) in instances.Indexed()) {
        instance.Bindings.ArgumentBindings.First(ab => ReferenceEquals(ab.Actual, PlaceholderId)).Actual = new LiteralExpr(SourceOrigin.NoToken, new BigInteger(idx));
      }
    }
    private static SortedSet<ProtectToProveApplySuffix> instances { get; set; } = new(comparer);
    public static IReadOnlySet<ProtectToProveApplySuffix> Instances => instances;

    private static readonly Expression PlaceholderScope = new SeqDisplayExpr(SourceOrigin.NoToken, []);
    private static readonly Expression PlaceholderId = new LiteralExpr(SourceOrigin.NoToken);

    public ProtectToProveApplySuffix(Cloner cloner, ProtectToProveApplySuffix original) : base(cloner, original) {
      throw new UnreachableException("not sure if it can be reached, I sincerely hope it can't");
    }
    ProtectToProveApplySuffix ICloneable<ProtectToProveApplySuffix>.Clone(Cloner cloner) => new(cloner, this);

    [SyntaxConstructor]
    public ProtectToProveApplySuffix(Expression e) : base(e.Origin, null, ProtectorFunctions.ProtectToProve.ToExprDotName(), [
        new(null, e.AsProtected()),
        new(null, new StringLiteralExpr(SourceOrigin.NoToken, e.ToString(), false)),
        new(null, PlaceholderScope),
        new(null, PlaceholderId),
      ], Token.NoToken
    ) {
      Contract.Ensures(IsValidPreResolve);
      instances.Add(this);
    }
    public ProtectToProveApplySuffix(Expression e, BigInteger immediateOrder) : base(e.Origin, null, ProtectorFunctions.ProtectToProveImmediate.ToExprDotName(), [
        new(null, e.AsProtected()),
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
  }
}
