using Microsoft.Dafny;
using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace DafnyCore.IncrementalCompilation {
  internal class ProtectToProveApplySuffix : ApplySuffix, ICloneable<ProtectToProveApplySuffix> {
    private static Comparer<ProtectToProveApplySuffix> comparer = Comparer<ProtectToProveApplySuffix>.Create((l, r) => {
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
        instance.Actuals.OfType<LiteralExpr>().First(a => a.Value is BigInteger i && i == BigInteger.Zero).Value = new BigInteger(idx);
      }
    }
    private static SortedSet<ProtectToProveApplySuffix> instances { get; set; } = new(comparer);
    public static IReadOnlySet<ProtectToProveApplySuffix> Instances => instances;
    ProtectToProveApplySuffix ICloneable<ProtectToProveApplySuffix>.Clone(Cloner cloner) => new(cloner, this);
    public ProtectToProveApplySuffix(Cloner cloner, ProtectToProveApplySuffix original) : base(cloner, original) { }
    [SyntaxConstructor]
    public ProtectToProveApplySuffix(Expression e) : base(e.Origin, null, ProtectorFunctions.ProtectToProve.ToExprDotName(), [
      new(null, new LiteralExpr(SourceOrigin.NoToken, 0)),
      new(null, e.AsProtected()),
      new(null, new StringLiteralExpr(SourceOrigin.NoToken, e.ToString(), false)),
      new(null, new SeqDisplayExpr(SourceOrigin.NoToken, [])),
    ], Token.NoToken) {
      Contract.Ensures(IsValidPreResolve);
      instances.Add(this);
    }
    private IEnumerable<Expression> Actuals => Bindings.ArgumentBindings.Select(ab => ab.Actual);
    public bool IsValidPreResolve => Bindings.ArgumentBindings is [{ }, { }, { Actual: SeqDisplayExpr { Elements: [] } }];
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
      Actuals.OfType<SeqDisplayExpr>().First().Elements.AddRange(resolver.ScopeArgsFrom(context));
      //System.Console.WriteLine('[' + string.Join(", ", Seq.Elements) + ']');
    }
  }
}
