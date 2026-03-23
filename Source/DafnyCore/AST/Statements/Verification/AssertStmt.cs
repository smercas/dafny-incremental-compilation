#nullable enable
using DafnyCore.IncrementalCompilation;
using Microsoft.Dafny.Auditor;
using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Numerics;

namespace Microsoft.Dafny;

public class AssertStmt : PredicateStmt, ICloneable<AssertStmt>, ICanFormat {
  public AssertLabel? Label;

  public AssertStmt Clone(Cloner cloner) {
    return new AssertStmt(cloner, this);
  }

  public AssertStmt(Cloner cloner, AssertStmt original) : base(cloner, original) {
    Label = original.Label == null ? null : new AssertLabel(cloner.Origin(original.Label.Tok), original.Label.Name);
  }

  public static AssertStmt CreateErrorAssert(INode node, string message, Expression? guard = null) {
    var errorMessage = new StringLiteralExpr(node.Origin, message, true);
    errorMessage.Type = new SeqType(Type.Char);
    var attr = new Attributes("error", [errorMessage], null);
    guard ??= Expression.CreateBoolLiteral(node.Origin, false);
    var assertFalse = new AssertStmt(node.Origin, guard, null, attr);
    assertFalse.IsGhost = true;
    return assertFalse;
  }

  [SyntaxConstructor]
  public AssertStmt(IOrigin origin, Expression expr, AssertLabel? label, Attributes? attributes)
    : base(origin, expr, attributes) {
    Contract.Requires(origin != null);
    Contract.Requires(expr != null);
    Label = label;
  }

  public void AddCustomizedErrorMessage(IOrigin tok, string s) {
    var args = new List<Expression>() { new StringLiteralExpr(tok, s, true) };
    var openBrace = tok;
    var closeBrace = new Token(tok.line, tok.col + 7 + s.Length + 1); // where 7 = length(":error ")
    this.Attributes = new UserSuppliedAttributes(tok, openBrace, closeBrace, args, this.Attributes);
  }

  public override IEnumerable<Expression> SpecificationSubExpressions {
    get {
      foreach (var e in base.SpecificationSubExpressions) { yield return e; }
      yield return Expr;
    }
  }

  public override IEnumerable<Assumption> Assumptions(Declaration decl) {
    if (this.HasUserAttribute("only", out _)) {
      yield return new Assumption(decl, Origin, AssumptionDescription.AssertOnly);
    }
  }

  public bool SetIndent(int indentBefore, TokenNewIndentCollector formatter) {
    return formatter.SetIndentAssertLikeStatement(this, indentBefore);
  }

  public override void GenResolve(INewOrOldResolver resolver, ResolutionContext context) {
    if (Label != null) {
      if (resolver.DominatingStatementLabels.Find(Label.Name) != null) {
        resolver.Reporter.Error(MessageSource.Resolver, Label.Tok, "assert label shadows a dominating label");
      } else {
        var rr = resolver.DominatingStatementLabels.Push(Label.Name, Label);
        Contract.Assert(rr == Scope<Label>.PushResult.Success); // since we just checked for duplicates, we expect the Push to succeed
      }
    }

    if (Attributes.Find(Attributes, "only") is { } attribute) {
      resolver.Reporter.Warning(MessageSource.Verifier, ResolutionErrors.ErrorId.r_assert_only_assumes_others.ToString(), attribute!.Origin,
        "Assertion with {:only} temporarily transforms other assertions into assumptions");
      if (attribute.Args.Count >= 1
          && attribute.Args[0] is LiteralExpr { Value: string value }
          && value != "before" && value != "after") {
        resolver.Reporter.Warning(MessageSource.Verifier, ResolutionErrors.ErrorId.r_assert_only_before_after.ToString(), attribute.Args[0].Origin,
          "{:only} only accepts \"before\" or \"after\" as an optional argument");
      }
    }

    base.GenResolve(resolver, context);
  }

  public bool HasAssertOnlyAttribute(out AssertOnlyKind assertOnlyKind) {
    assertOnlyKind = AssertOnlyKind.Single;
    if (!this.HasUserAttribute("only", out var attribute)) {
      return false;
    }

    if (attribute!.Args.Count != 1 || attribute.Args[0] is not LiteralExpr { Value: var value }) {
      return true;
    }

    assertOnlyKind = value switch {
      "before" => AssertOnlyKind.Before,
      "after" => AssertOnlyKind.After,
      _ => assertOnlyKind
    };

    return true;
  }

  public enum AssertOnlyKind {
    Before,
    After,
    Single
  }

  public override void ResolveGhostness(ModuleResolver resolver, ErrorReporter reporter, bool mustBeErasable,
    ICodeContext codeContext,
    string? proofContext, bool allowAssumptionVariables, bool inConstructorInitializationPhase) {
    IsGhost = true;
  }

  protected AssertStmt(Protector protector, AssertStmt original, Expression? expr = null) : base(protector, original, expr) {
    Label = original.Label.ApplyIfNotNull(protector.Clone);
  }
  public override AssertStmt WithProtections(Protector protector) {
    var attributeName = Constants.AttributeName;
    var immediateAttributeName = Constants.ImmediateAttributeName;
    if (Attributes.Contains(Attributes, Constants.AttributeName)) {
      //Console.WriteLine("Protecting to prove assertion " + a.Expr.ToString());
      return protector.WithAttributeAdditionalContext(() => new AssertStmt(protector, this, Expr.WrappedWith(ProtectorFunctions.ProtectToProve with {
        ChangeContext = new ProtectToProveApplySuffix.ChangeContext(protector.MostRecentContext),
      })));
    }
    if (Attributes.Find(Attributes, immediateAttributeName) is { } attr) {
      if (attr is { Args: [] }) { attr.Args.Add(new LiteralExpr(SourceOrigin.NoToken, 0)); } // temporary bcs frontend doesn't use {:ipm_now 0} yet
      if (attr is not { Args: [var arg] }) { throw new Exception($"the {{:{immediateAttributeName}}} attribute requires an argument"); }
      if (arg is not LiteralExpr { Value: BigInteger entryPoint }) { throw new Exception($"{{:{immediateAttributeName}}}'s argument needs to be a natural number"); }
      return new(protector, this, Expr.WrappedWith(ProtectorFunctions.ProtectToProveImmediate with { EntryPoint = entryPoint }));
    }
    //Console.WriteLine($"assert statement: {a.Expr}");
    return new(protector, this);
  }
}

