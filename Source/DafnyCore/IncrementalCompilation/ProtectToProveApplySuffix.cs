using Microsoft.Dafny;
using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DafnyCore.IncrementalCompilation {
  internal class ProtectToProveApplySuffix : ApplySuffix, ICloneable<ProtectToProveApplySuffix> {
    ProtectToProveApplySuffix ICloneable<ProtectToProveApplySuffix>.Clone(Cloner cloner) => new(cloner, this);
    public ProtectToProveApplySuffix(Cloner cloner, ProtectToProveApplySuffix original) : base(cloner, original) { }
    [SyntaxConstructor]
    public ProtectToProveApplySuffix(Expression e) : base(e.Origin, null, ProtectorFunctions.ProtectToProve.ToExprDotName(), [
      new(null, e),
      //new(null, ExprReplacer.ReplaceExpr(e)),
      new(null, new StringLiteralExpr(SourceOrigin.NoToken, e.ToString(), false)),
      new(null, new SeqDisplayExpr(SourceOrigin.NoToken, [])),
    ], Token.NoToken) {
      Contract.Ensures(IsValidPreResolve);
    }
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
      (Bindings.ArgumentBindings[2].Actual as SeqDisplayExpr).Elements.AddRange(resolver.ScopeArgsFrom(context));
      //System.Console.WriteLine('[' + string.Join(", ", Seq.Elements) + ']');
    }
  }
}
