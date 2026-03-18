using Microsoft.Dafny;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DafnyCore.IncrementalCompilation {
  public static class ProtectorExtensions {
    public static UnreachableException NewCannotAppearBeforeResolution<T>(this T o) where T : notnull => new($"{o} (of type `{typeof(T).Name}`) can't appear before resolution"); // TODO: rename after you remove the old protection
    public static Specification<Expression> WithProtections(this Specification<Expression> spec, Protector protector) =>
      new(spec.Expressions?.ConvertAll(e => e.WithProtections(protector)), protector.Clone(spec.Attributes));
    public static Specification<FrameExpression> WithProtections(this Specification<FrameExpression> spec, Protector protector) =>
      new(spec.Expressions?.ConvertAll(fe => fe.WithProtections(protector)), protector.Clone(spec.Attributes));
    private static IEnumerable<Statement> WithProtectionsAsSeparateStatements(this Statement s, Protector protector) {
      yield return s.WithProtections(protector);
    }
    public static IEnumerable<Statement> WithProtections(this IEnumerable<Statement> ss, Protector protector) => ss.SelectMany(s => s.WithProtectionsAsSeparateStatements(protector));
  }
}
