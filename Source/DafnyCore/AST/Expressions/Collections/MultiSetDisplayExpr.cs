#nullable enable

using DafnyCore.IncrementalCompilation;
using System.Collections.Generic;
using System.Diagnostics.Contracts;

namespace Microsoft.Dafny;

public class MultiSetDisplayExpr : DisplayExpression, ICloneable<MultiSetDisplayExpr> {
  public MultiSetDisplayExpr(Cloner cloner, MultiSetDisplayExpr original) : base(cloner, original) {
  }

  [SyntaxConstructor]
  public MultiSetDisplayExpr(IOrigin origin, List<Expression> elements) : base(origin, elements) {
  }

  public MultiSetDisplayExpr Clone(Cloner cloner) {
    return new MultiSetDisplayExpr(cloner, this);
  }

  protected MultiSetDisplayExpr(Protector protector, MultiSetDisplayExpr original) : base(protector, original) { }
  public override MultiSetDisplayExpr WithProtections(Protector protector) => new(protector, this);
}