#nullable enable

using DafnyCore.IncrementalCompilation;
using System.Collections.Generic;
using System.Diagnostics.Contracts;

namespace Microsoft.Dafny;

public class SeqConstructionExpr : Expression, ICloneable<SeqConstructionExpr> {
  public Type? ExplicitElementType;
  public Expression N;
  public Expression Initializer;

  public SeqConstructionExpr(Cloner cloner, SeqConstructionExpr original) : base(cloner, original) {
    var elemType = original.ExplicitElementType == null ? null : cloner.CloneType(original.ExplicitElementType);
    N = cloner.CloneExpr(original.N);
    Initializer = cloner.CloneExpr(original.Initializer);
    ExplicitElementType = elemType;
  }

  [SyntaxConstructor]
  public SeqConstructionExpr(IOrigin origin, Type? explicitElementType, Expression n, Expression initializer)
    : base(origin) {
    ExplicitElementType = explicitElementType;
    N = n;
    Initializer = initializer;
  }
  public override IEnumerable<Expression> SubExpressions {
    get {
      yield return N;
      yield return Initializer;
    }
  }

  public override IEnumerable<Type> ComponentTypes {
    get {
      if (ExplicitElementType != null) {
        yield return ExplicitElementType;
      }
    }
  }

  public SeqConstructionExpr Clone(Cloner cloner) {
    return new SeqConstructionExpr(cloner, this);
  }

  protected SeqConstructionExpr(Protector protector, SeqConstructionExpr original) : base(protector, original) {
    ExplicitElementType = original.ExplicitElementType.ApplyIfNotNull(protector.Clone);
    N = original.N.WithProtections(protector);
    Initializer = original.Initializer.WithProtections(protector);
  }
  public override SeqConstructionExpr WithProtections(Protector protector) => new(protector, this);
}