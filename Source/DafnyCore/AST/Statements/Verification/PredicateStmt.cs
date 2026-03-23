#nullable enable

using DafnyCore.IncrementalCompilation;
using System.Diagnostics.Contracts;

namespace Microsoft.Dafny;

public abstract class PredicateStmt : Statement, ICanResolveNewAndOld {
  public Expression Expr;

  protected PredicateStmt(Cloner cloner, PredicateStmt original) : base(cloner, original) {
    Expr = cloner.CloneExpr(original.Expr);
  }

  [SyntaxConstructor]
  protected PredicateStmt(IOrigin origin, Expression expr, Attributes? attributes = null)
    : base(origin, attributes) {
    this.Expr = expr;
  }

  protected PredicateStmt(IOrigin origin, Expression expr)
    : this(origin, expr, null) {
    this.Expr = expr;
  }

  public override void GenResolve(INewOrOldResolver resolver, ResolutionContext context) {
    base.GenResolve(resolver, context);
    resolver.ResolveExpression(Expr, context);// follows from postcondition of ResolveExpression
    resolver.ConstrainTypeExprBool(Expr, "condition is expected to be of type bool, but is {0}");
  }

  protected PredicateStmt(Protector protector, PredicateStmt original, Expression? expr = null) : base(protector, original) {
    Expr = expr ?? original.Expr.WithProtections(protector);
  }
  public abstract override PredicateStmt WithProtections(Protector protector);
}