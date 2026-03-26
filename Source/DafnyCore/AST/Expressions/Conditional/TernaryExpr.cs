#nullable enable

using DafnyCore.IncrementalCompilation;
using System.Collections.Generic;
using System.Diagnostics.Contracts;

namespace Microsoft.Dafny;

public class TernaryExpr : Expression, ICloneable<TernaryExpr> {
  public Opcode Op;
  public Expression E0;
  public Expression E1;
  public Expression E2;
  public enum Opcode { PrefixEqOp, PrefixNeqOp }

  public TernaryExpr(Cloner cloner, TernaryExpr original) : base(cloner, original) {
    Op = original.Op;
    E0 = cloner.CloneExpr(original.E0);
    E1 = cloner.CloneExpr(original.E1);
    E2 = cloner.CloneExpr(original.E2);
  }

  [SyntaxConstructor]
  public TernaryExpr(IOrigin origin, Opcode op, Expression e0, Expression e1, Expression e2)
    : base(origin) {
    Op = op;
    E0 = e0;
    E1 = e1;
    E2 = e2;
  }

  public override IEnumerable<Expression> SubExpressions {
    get {
      yield return E0;
      yield return E1;
      yield return E2;
    }
  }

  public TernaryExpr Clone(Cloner cloner) {
    return new TernaryExpr(cloner, this);
  }

  protected TernaryExpr(Protector protector, TernaryExpr original) : base(protector, original) {
    Op = original.Op;
    E0 = original.E0.WithProtections(protector);
    E1 = original.E1.WithProtections(protector);
    E2 = original.E2.WithProtections(protector);
  }
  public override TernaryExpr WithProtections(Protector protector) => new(protector, this);
}