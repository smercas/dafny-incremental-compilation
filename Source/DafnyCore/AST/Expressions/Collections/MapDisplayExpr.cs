#nullable enable

using DafnyCore.IncrementalCompilation;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.Dafny;

public class MapDisplayExpr : Expression, ICanFormat, ICloneable<MapDisplayExpr> {
  public bool Finite;
  public List<MapDisplayEntry> Elements;

  public MapDisplayExpr(Cloner cloner, MapDisplayExpr original) : base(cloner, original) {
    Finite = original.Finite;
    Elements = original.Elements.Select(p => new MapDisplayEntry(cloner.CloneExpr(p.A), cloner.CloneExpr(p.B))).ToList();
  }

  [SyntaxConstructor]
  public MapDisplayExpr(IOrigin origin, bool finite, List<MapDisplayEntry> elements)
    : base(origin) {
    Finite = finite;
    Elements = elements;
  }
  public override IEnumerable<Expression> SubExpressions {
    get {
      foreach (var ep in Elements) {
        yield return ep.A;
        yield return ep.B;
      }
    }
  }

  public bool SetIndent(int indentBefore, TokenNewIndentCollector formatter) {
    return formatter.SetIndentParensExpression(indentBefore, OwnedTokens);
  }

  public MapDisplayExpr Clone(Cloner cloner) {
    return new MapDisplayExpr(cloner, this);
  }

  protected MapDisplayExpr(Protector protector, MapDisplayExpr original) : base(protector, original) {
    Finite = original.Finite;
    Elements = original.Elements.ConvertAll(e => e.WithProtections(protector));
  }
  public override MapDisplayExpr WithProtections(Protector protector) => new(protector, this);
}

public class MapDisplayEntry : IProtectable<MapDisplayEntry> {
  public Expression A, B;

  [SyntaxConstructor]
  public MapDisplayEntry(Expression a, Expression b) {
    A = a;
    B = b;
  }

  protected MapDisplayEntry(Protector protector, MapDisplayEntry original) {
    A = original.A.WithProtections(protector);
    B = original.B.WithProtections(protector);
  }
  public MapDisplayEntry WithProtections(Protector protector) => new(protector, this);
}