using Microsoft.Dafny;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DafnyCore.IncrementalCompilation {
  public static class ProtectorExtensions {
    public static bool isWildcardName(this string s) => s.StartsWith("_v") && int.TryParse(s[2..], out _);
    public static AttributedExpression ToProtectClause(this IVariable e) => e.Name.ToProtectClause();
    public static AttributedExpression ToProtectClause(this Expression e) =>
      ToProtectClauseCore(e.WrappedWith(ProtectorFunctions.NewProtect));
    public static AttributedExpression ToProtectClause(this string e) =>
      ToProtectClauseCore(e.WrappedWith(ProtectorFunctions.NewProtect));
    private static AttributedExpression ToProtectClauseCore(ApplySuffix e) => new(e, null, null); // maybe add label?

    public static AssertStmt ToProtectAssertion(this IVariable e) => e.Name.ToProtectAssertion();
    public static AssertStmt ToProtectAssertion(this Expression e) =>
      ToProtectAssertionCore(e.WrappedWith(ProtectorFunctions.NewProtect));
    public static AssertStmt ToProtectAssertion(this string e) =>
      ToProtectAssertionCore(e.WrappedWith(ProtectorFunctions.NewProtect));
    private static AssertStmt ToProtectAssertionCore(ApplySuffix e) => new(SourceOrigin.NoToken, e, null, null); // maybe add label?

    public static UnreachableException NewCannotAppearBeforeResolution<T>(this T o) where T : notnull => new($"{o} (of type `{typeof(T).Name}`) can't appear before resolution"); // IPMTODO: rename after you remove the old protection
    public static Specification<Expression> WithProtections(this Specification<Expression> spec, Protector protector) =>
      new(spec.Expressions?.ConvertAll(e => e.WithProtections(protector)), protector.Clone(spec.Attributes));
    public static Specification<FrameExpression> WithProtections(this Specification<FrameExpression> spec, Protector protector) =>
      new(spec.Expressions?.ConvertAll(fe => fe.WithProtections(protector)), protector.Clone(spec.Attributes));
    private static IEnumerable<Statement> WithProtectionsAsSeparateStatements(this Statement s, Protector protector) {
      yield return s.WithProtections(protector);
      switch (s) {
        case VarDeclStmt { Assign.Lhss: var newvars }:
          foreach (var newvar in newvars) {
            yield return newvar.WithProtections(protector).ToProtectAssertion();
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
  }
}
