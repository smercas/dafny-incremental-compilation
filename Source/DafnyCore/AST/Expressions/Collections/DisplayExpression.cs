#nullable enable

using DafnyCore.IncrementalCompilation;
using System.Collections.Generic;

namespace Microsoft.Dafny;

public abstract class DisplayExpression : Expression {
  public List<Expression> Elements;

  protected DisplayExpression(Cloner cloner, DisplayExpression original) : base(cloner, original) {
    Elements = original.Elements.ConvertAll(cloner.CloneExpr);
  }

  [SyntaxConstructor]
  public DisplayExpression(IOrigin origin, List<Expression> elements)
    : base(origin) {
    Elements = elements;
  }

  public override IEnumerable<Expression> SubExpressions => Elements;

  protected DisplayExpression(Protector protector, DisplayExpression original) : base(protector, original) {
    Elements = original.Elements.ConvertAll(e => e.WithProtections(protector));
  }
  public abstract override DisplayExpression WithProtections(Protector protector);
}