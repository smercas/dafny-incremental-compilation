#nullable enable
using DafnyCore.IncrementalCompilation;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;

namespace Microsoft.Dafny;

[SyntaxBaseType(null)]
public class AttributedExpression : NodeWithOrigin, IAttributeBearingDeclaration, IProtectable<AttributedExpression> {
  public Expression E;
  public AssertLabel? Label;

  [ContractInvariantMethod]
  void ObjectInvariant() {
    Contract.Invariant(E != null);
  }

  public Attributes? Attributes { get; set; }

  string IAttributeBearingDeclaration.WhatKind => "expression";

  public override IOrigin Origin => E.Origin;

  public bool HasAttributes() {
    return Attributes != null;
  }

  public AttributedExpression(Expression e)
    : this(e, null) {
    Contract.Requires(e != null);
  }

  public AttributedExpression(Expression e, Attributes? attributes) : this(e, null, attributes) {
  }

  [SyntaxConstructor]
  public AttributedExpression(Expression e, AssertLabel? label, Attributes? attributes) : base(e.Origin) {
    E = e;
    Label = label;
    Attributes = attributes;
  }

  public enum Kind { Ensures };
  public AttributedExpression WithProtections(Protector protector) => WithProtections(protector, null);
  public AttributedExpression WithProtections(Protector protector, Kind? kind) {
    AttributedExpression CreateFrom(Expression E) => new(E, Label.ApplyIfNotNull(protector.Clone), protector.Clone(Attributes));
    return kind switch {
      Kind.Ensures when Attributes.Contains(Attributes, Constants.AttributeName) =>
        protector.WithAttributeAdditionalContext(() => CreateFrom(
          E.WrappedWith(ProtectorFunctions.ProtectToProve with {
            ChangeContext = new ProtectToProveApplySuffix.ChangeContext(protector.MostRecentContext),
          })
        )),
      Kind.Ensures or null => CreateFrom(E.WithProtections(protector)),
      _ => throw new UnreachableException(),
    };
  }

  public void AddCustomizedErrorMessage(IOrigin tok, string s) {
    var args = new List<Expression>() { new StringLiteralExpr(tok, s, true) };
    IOrigin openBrace = tok;
    IOrigin closeBrace = new Token(tok.line, tok.col + 7 + s.Length + 1); // where 7 = length(":error ")
    this.Attributes = new UserSuppliedAttributes(tok, openBrace, closeBrace, args, this.Attributes);
  }

  public override IEnumerable<INode> Children =>
    Attributes.AsEnumerable().Concat<Node>(
      new List<Node>() { E });

  public override IEnumerable<INode> PreResolveChildren => Children;
}