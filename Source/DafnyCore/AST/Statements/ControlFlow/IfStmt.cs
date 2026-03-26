#nullable enable
using DafnyCore.IncrementalCompilation;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;

namespace Microsoft.Dafny;

public class IfStmt : LabeledStatement, ICloneable<IfStmt>, ICanFormat {
  public bool IsBindingGuard;
  public Expression? Guard;
  public BlockStmt Thn;
  public Statement? Els;
  [ContractInvariantMethod]
  void ObjectInvariant() {
    Contract.Invariant(!IsBindingGuard || (Guard is ExistsExpr && ((ExistsExpr)Guard).Range == null));
    Contract.Invariant(Thn != null);
    Contract.Invariant(Els == null || Els is BlockStmt || Els is IfStmt || Els is SkeletonStatement);
  }

  public new IfStmt Clone(Cloner cloner) {
    return new IfStmt(cloner, this);
  }

  public IfStmt(Cloner cloner, IfStmt original) : base(cloner, original) {
    IsBindingGuard = original.IsBindingGuard;
    Guard = cloner.CloneExpr(original.Guard);
    Thn = cloner.CloneBlockStmt(original.Thn);
    Els = cloner.CloneStmt(original.Els, false);
  }

  public IfStmt(IOrigin origin, bool isBindingGuard, Expression? guard, BlockStmt thn, Statement? els)
    : base(origin, []) {
    Contract.Requires(origin != null);
    Contract.Requires(!isBindingGuard || (guard is ExistsExpr && ((ExistsExpr)guard).Range == null));
    Contract.Requires(els == null || els is BlockStmt || els is IfStmt || els is SkeletonStatement);
    IsBindingGuard = isBindingGuard;
    Guard = guard;
    Thn = thn;
    Els = els;
  }

  [SyntaxConstructor]
  public IfStmt(IOrigin origin, bool isBindingGuard, Expression? guard, BlockStmt thn, Statement? els,
    List<Label> labels, Attributes? attributes)
    : base(origin, labels, attributes) {
    Contract.Requires(origin != null);
    Contract.Requires(!isBindingGuard || (guard is ExistsExpr && ((ExistsExpr)guard).Range == null));
    Contract.Requires(els == null || els is BlockStmt || els is IfStmt || els is SkeletonStatement);
    IsBindingGuard = isBindingGuard;
    Guard = guard;
    Thn = thn;
    Els = els;
  }
  public override IEnumerable<Statement> SubStatements {
    get {
      yield return Thn;
      if (Els != null) {
        yield return Els;
      }
    }
  }
  public override IEnumerable<Expression> NonSpecificationSubExpressions {
    get {
      foreach (var e in base.NonSpecificationSubExpressions) { yield return e; }
      if (Guard != null) {
        yield return Guard;
      }
    }
  }

  public bool SetIndent(int indentBefore, TokenNewIndentCollector formatter) {
    foreach (var token in OwnedTokens) {
      if (formatter.SetIndentLabelTokens(token, indentBefore)) {
        continue;
      }
      switch (token.val) {
        case "if": {
            formatter.SetOpeningIndentedRegion(token, indentBefore);
            formatter.Visit(Guard, indentBefore);
            formatter.SetIndentBody(Thn, indentBefore);
            break;
          }
        case "else": {
            formatter.SetKeywordWithoutSurroundingIndentation(token, indentBefore);
            formatter.SetIndentBody(Els, indentBefore);
            break;
          }
      }
    }
    return false;
  }

  public void Resolve(INewOrOldResolver resolver, ResolutionContext resolutionContext) {
    if (Guard != null) {
      if (!resolutionContext.IsGhost && IsBindingGuard && resolver.Options.ForbidNondeterminism) {
        resolver.Reporter.Error(MessageSource.Resolver, GeneratorErrors.ErrorId.c_binding_if_forbidden, Origin, "binding if statement forbidden by the --enforce-determinism option");
      }
      resolver.ResolveExpression(Guard, resolutionContext);
      resolver.ConstrainTypeExprBool(Guard, "condition is expected to be of type bool, but is {0}");
    } else {
      if (!resolutionContext.IsGhost && resolver.Options.ForbidNondeterminism) {
        resolver.Reporter.Error(MessageSource.Resolver, GeneratorErrors.ErrorId.c_nondeterministic_if_forbidden, Origin, "nondeterministic if statement forbidden by the --enforce-determinism option");
      }
    }

    resolver.Scope.PushMarker();
    if (IsBindingGuard) {
      var exists = (ExistsExpr)Guard!;
      foreach (var v in exists.BoundVars) {
        resolver.ScopePushAndReport(resolver.Scope, v.Name, v, v.Origin, "bound-variable");
      }
    }
    resolver.DominatingStatementLabels.PushMarker();
    resolver.ResolveBlockStatement(Thn, resolutionContext);
    resolver.DominatingStatementLabels.PopMarker();
    resolver.Scope.PopMarker();

    if (Els != null) {
      resolver.DominatingStatementLabels.PushMarker();
      resolver.ResolveStatement(Els, resolutionContext);
      resolver.DominatingStatementLabels.PopMarker();
    }
  }

  public override void ResolveGhostness(ModuleResolver resolver, ErrorReporter reporter, bool mustBeErasable,
    ICodeContext codeContext, string? proofContext,
    bool allowAssumptionVariables, bool inConstructorInitializationPhase) {
    IsGhost = mustBeErasable || (Guard != null && ExpressionTester.UsesSpecFeatures(Guard));
    if (!mustBeErasable && IsGhost) {
      reporter.Info(MessageSource.Resolver, Origin, "ghost if");
    }
    Thn.ResolveGhostness(resolver, reporter, IsGhost, codeContext, proofContext, allowAssumptionVariables, inConstructorInitializationPhase);
    if (Els != null) {
      Els.ResolveGhostness(resolver, reporter, IsGhost, codeContext, proofContext, allowAssumptionVariables, inConstructorInitializationPhase);
    }
    // if both branches were all ghost, then we can mark the enclosing statement as ghost as well
    IsGhost = IsGhost || (Thn.IsGhost && (Els == null || Els.IsGhost));
    if (!IsGhost && Guard != null) {
      // If there were features in the guard that are treated differently in ghost and non-ghost
      // contexts, make sure they get treated for non-ghost use.
      ExpressionTester.CheckIsCompilable(resolver, reporter, Guard, codeContext);
    }
  }

  protected IfStmt(Protector protector, IfStmt original) : base(protector, original) {
    IsBindingGuard = original.IsBindingGuard;
    Guard = original.Guard?.WithProtections(protector);
    Thn = IsBindingGuard switch {
      true => original.Thn.WithProtections(protector, (Guard as ComprehensionExpr)!.BoundVars.Select(bv => bv.ToProtectAssertion())), // determined from parser that `Guard` is an `ExistsExpr`, hope this doesn't change
      false => original.Thn.WithProtections(protector),
    };
    Els = original.Els?.WithProtections(protector);
  }
  public override IfStmt WithProtections(Protector protector) => new(protector, this);
}