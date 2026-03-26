#nullable enable

using DafnyCore.IncrementalCompilation;
using System.Collections.Generic;

namespace Microsoft.Dafny;

public class MultiSetFormingExpr : Expression, ICloneable<MultiSetFormingExpr> {
  [Peer]
  public Expression E;

  public MultiSetFormingExpr(Cloner cloner, MultiSetFormingExpr original) : base(cloner, original) {
    E = cloner.CloneExpr(original.E);
  }

  [SyntaxConstructor]
  [Captured]
  public MultiSetFormingExpr(IOrigin origin, Expression e)
    : base(origin) {
    Cce.Owner.AssignSame(this, e);
    E = e;
  }

  public override IEnumerable<Expression> SubExpressions {
    get { yield return E; }
  }

  public MultiSetFormingExpr Clone(Cloner cloner) {
    return new MultiSetFormingExpr(cloner, this);
  }

  protected MultiSetFormingExpr(Protector protector, MultiSetFormingExpr original) : base(protector, original) {
    E = original.E.WithProtections(protector);
  }
  public override MultiSetFormingExpr WithProtections(Protector protector) => new(protector, this);
}