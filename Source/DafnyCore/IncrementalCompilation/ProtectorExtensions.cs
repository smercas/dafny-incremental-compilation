using Microsoft.Boogie;
using Microsoft.Dafny;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DafnyCore.IncrementalCompilation {
  public static class ProtectorExtensions {
    public static bool isWildcardName(this string s) => s.StartsWith("_v") && int.TryParse(s[2..], out _);
    public static AttributedExpression ToProtectClause(this IVariable v) => v.Name.ToProtectClause();
    public static AttributedExpression ToProtectClause(this Expression e) =>
      ToProtectClauseCore(ProtectorFunctions.NewProtect.InvocationFrom(e));
    public static AttributedExpression ToProtectClause(this string s) =>
      ToProtectClauseCore(ProtectorFunctions.NewProtect.InvocationFrom(s));
    private static AttributedExpression ToProtectClauseCore(ApplySuffix e) => new(e, null, null); // maybe add label?

    public static AssertStmt ToProtectAssertion(this IVariable v) => v.Name.ToProtectAssertion();
    public static AssertStmt ToProtectAssertion(this Expression e) =>
      ToProtectAssertionCore(ProtectorFunctions.NewProtect.InvocationFrom(e));
    public static AssertStmt ToProtectAssertion(this string s) =>
      ToProtectAssertionCore(ProtectorFunctions.NewProtect.InvocationFrom(s));
    private static AssertStmt ToProtectAssertionCore(ApplySuffix e) => new(SourceOrigin.NoToken, e, null, null); // maybe add label?

    public static UnreachableException CannotAppearBeforeResolution<T>(this T o) where T : notnull => new($"{o} (of type `{typeof(T).Name}`) can't appear before resolution"); // IPMTODO: rename after you remove the old protection
    public static Specification<Expression> WithProtections(this Specification<Expression> spec, Protector protector) =>
      new(spec.Expressions?.ConvertAll(e => e.WithProtections(protector)), protector.Clone(spec.Attributes));
    public static Specification<FrameExpression> WithProtections(this Specification<FrameExpression> spec, Protector protector) =>
      new(spec.Expressions?.ConvertAll(fe => fe.WithProtections(protector)), protector.Clone(spec.Attributes));
    private static IEnumerable<Statement> WithProtectionsAsSeparateStatements(this Statement s, Protector protector) {
      yield return s.WithProtections(protector);
      switch (s) {
        case VarDeclStmt { Assign: not AssignSuchThatStmt, Assign.Lhss: var newvars }:
          foreach (var newvar in newvars) {
            yield return protector.Clone(newvar).ToProtectAssertion();
          }
          break;
        case VarDeclPattern { LocalVars: var unfiltered }:
          foreach (var newvar in unfiltered.Where(nv => !nv.Name.isWildcardName())) {
            yield return newvar.WithProtections(protector).ToProtectAssertion();
          }
          break;
      }
    }
    public static IEnumerable<Statement> WithProtections(this IEnumerable<Statement> ss, Protector protector) => ss.SelectMany(s => s.WithProtectionsAsSeparateStatements(protector));

    public static StmtExpr WithPrependedAssertions(this Expression e, AssertStmt first, params AssertStmt[] secondUntilLast) => e.WithPrependedAssertions([first, .. secondUntilLast]);
    // enumerable must have at least one element
    public static StmtExpr WithPrependedAssertions(this Expression e, IEnumerable<AssertStmt> ss) {
      Contract.Requires(ss.Any());
      return ss.Reverse().AggregateAs(e, (prev, s) => new StmtExpr(prev.Origin, s, prev));
    }
    // Weaker version of `WithPrependedAssertions` that doesn't guarantee that the result is a `StmtExpr`, but can accept being passed no elements
    public static Expression WithPrependedAssertionsIfAny(this Expression e, params AssertStmt[] ss) => e.WithPrependedAssertionsIfAny(ss as IEnumerable<AssertStmt>);
    // Weaker version of `WithPrependedAssertions` that doesn't guarantee that the result is a `StmtExpr`, but can accept an empty enumerable
    public static Expression WithPrependedAssertionsIfAny(this Expression e, IEnumerable<AssertStmt> ss) {
      Contract.Ensures(!ss.Any() || Contract.Result<Expression>() is StmtExpr);
      var l = ss.ToList();
      if (l.Count == 0) { return e; }
      return l.Reversed().Aggregate(e, (prev, s) => new StmtExpr(prev.Origin, s, prev));
    }

    public static BinaryExpr WithPrependedExpressions(this Expression e, Expression first, params Expression[] secondUntilLast) => e.WithPrependedExpressions([first, .. secondUntilLast]);
    // enumerable must have at least one element
    public static BinaryExpr WithPrependedExpressions(this Expression e, IEnumerable<Expression> es) {
      Contract.Requires(es.Any());
      return es.Reverse().AggregateAs(e.WrapWithParensIfNecessary() as Expression, (prev, s) => new BinaryExpr(prev.Origin, BinaryExpr.Opcode.And, s, prev)); // IPMTODO: check if this is ok
    }
    // Weaker version of `WithPrependedExpressions` that doesn't guarantee that the result is a `BinaryExpr`, but can accept being passed no elements
    public static Expression WithPrependedExpressionsIfAny(this Expression e, params Expression[] es) => e.WithPrependedExpressionsIfAny(es as IEnumerable<Expression>);
    // Weaker version of `WithPrependedExpressions` that doesn't guarantee that the result is a `BinaryExpr`, but can accept an empty enumerable
    public static Expression WithPrependedExpressionsIfAny(this Expression e, IEnumerable<Expression> es) {
      Contract.Ensures(!es.Any() || Contract.Result<Expression>() is StmtExpr);
      var l = es.ToList();
      if (l.Count == 0) { return e; }
      return l.Reversed().Aggregate(e.WrapWithParensIfNecessary() as Expression, (prev, s) => new BinaryExpr(prev.Origin, BinaryExpr.Opcode.And, s, prev));
    }
    private static ParensExpression WrapWithParensIfNecessary(this Expression e) => e switch {
      ParensExpression pe => pe,
      _ => new ParensExpression(e.Origin, e),
    };
    public static BinaryExpr ConjBinaryExprFrom(Expression first, params Expression[] secondUntilLast) => secondUntilLast.Reverse().AggregateAs(first, (prev, s) => new BinaryExpr(prev.Origin, BinaryExpr.Opcode.And, s, prev));
    public static BinaryExpr ConjBinaryExprFrom(IEnumerable<Expression> es) {
      var l = es.ToList();
      if (l.Count == 0) { throw new ArgumentException($"can't make a conjunction out of an empty list"); }
      return l.SkipLast(1).Reverse().AggregateAs(l[^1], (prev, s) => new BinaryExpr(prev.Origin, BinaryExpr.Opcode.And, s, prev));
    }
  }
}
