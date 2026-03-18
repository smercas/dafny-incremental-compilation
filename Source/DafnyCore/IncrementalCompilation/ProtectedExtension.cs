#nullable enable
using Microsoft.Dafny;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Numerics;
using System.Threading;
using static DafnyCore.IncrementalCompilation.ProtectorFunctions;
using static Microsoft.Dafny.CalcStmt;

namespace DafnyCore.IncrementalCompilation {
  internal static class ProtectedExtension { // FOR THE LOVE OF GOD LET'S KEEP THIS BEFORE THE RESOLUTION
    #region protection context
    private static AsyncLocal<Stack<(Lazy<MemberDecl>, Stack<Lazy<IAttributeBearingDeclaration>>)>> AsyncLocalContext { get; } = new();
    private static Stack<(Lazy<MemberDecl>, Stack<Lazy<IAttributeBearingDeclaration>>)> Context => AsyncLocalContext.Value ??= new();
    private static AsyncLocal<bool> AsyncLocalProtectLhsInContainsBinaryExpressions { get; } = new() { Value = true };
    private static bool ProtectLhsInContainsBinaryExpressions {
      get => AsyncLocalProtectLhsInContainsBinaryExpressions.Value;
      set => AsyncLocalProtectLhsInContainsBinaryExpressions.Value = value;
    }
    public static T WithProtectionOnIdentifierExpressionsInContainsBinaryExpressionsDisabled<T>(Func<T> f) {
      (var old, ProtectLhsInContainsBinaryExpressions) = (ProtectLhsInContainsBinaryExpressions, false);
      var r = f();
      ProtectLhsInContainsBinaryExpressions = old;
      return r;
    }
    private class Box<T> {
      public T? Value { get; set; } = default;
    }
    public static T WithMemberAdditionalContext<T>(Func<T> f) where T : MemberDecl {
      var r = new Box<T>();
      Context.Push((new(() => r.Value!), []));
      r.Value = f();
      Context.Pop();
      return r.Value;
    }

    public static T WithAttributeAdditionalContext<T>(Func<T> f) where T : IAttributeBearingDeclaration {
      var r = new Box<T>();
      Context.Peek().Item2.Push(new(() => r.Value!));
      r.Value = f();
      Context.Peek().Item2.Pop();
      return r.Value;
    }
    // if `Last` isn't called with something in both stacks, geniunely what're we doing?
    public static (Lazy<MemberDecl>, Stack<Lazy<IAttributeBearingDeclaration>>) MostRecentContext => Context.Peek();
    #endregion
    private static UnreachableException CannotAppearBeforeResolution<T>(this T o) where T : notnull => new($"{o} (of type `{typeof(T).Name}`) can't appear before resolution");
    private static Cloner cloner { get; } = new();
    private static IOrigin Clone(this IOrigin o) => cloner.Origin(o);
    [return: NotNullIfNotNull(nameof(a))] private static Attributes? Clone(this Attributes? a) => cloner.CloneAttributes(a);
    [return: NotNullIfNotNull(nameof(t))] private static Microsoft.Dafny.Type? Clone(this Microsoft.Dafny.Type? t) => cloner.CloneType(t);
    [return: NotNullIfNotNull(nameof(tp))] private static TypeParameter? Clone(this TypeParameter? tp) => cloner.CloneTypeParam(tp);
    [return: NotNullIfNotNull(nameof(t))] private static AttributedToken? Clone(this AttributedToken? t) => cloner.AttributedTok(t);
    [return: NotNullIfNotNull(nameof(e))] private static E? Clone<E>(this E? e) where E : Expression => cloner.CloneExpr(e) as E;
    private static List<Label> Clone(this List<Label> labels) => labels.ConvertAllWhere(static label => (label.Name is not null, label.Clone()));
    private static Label Clone(this Label l) => l switch {
      AssertLabel al => al.Clone(),
      _ when l.IsExactly() => new Label(l.Tok.Clone(), l.Name),
      _ => throw new UnreachableException(),
    };
    #region Label
    private static AssertLabel Clone(this AssertLabel l) => new(l.Tok.Clone(), l.Name);
    #endregion
    private static Name Clone(this Name n) => new(cloner, n);
    private static E Clone<E>(this ICloneable<E> e) => e.Clone(cloner);

    #region other
    public static VT AsProtected<VT>(this VT vt) where VT : IVariable => (VT)(vt switch { // idk why the generic was necessary but I'm not the kind to ask questions
      NonglobalVariable ngv => ngv switch {
        BoundVar bv => bv switch {
          QuantifiedVar qv => qv.AsProtected() as IVariable, // cast determines type of switch expr at large & thus satisfies the compiler
          _ => bv.AsProtected(),
        },
        Formal f => f switch {
          ImplicitFormal i => throw i.CannotAppearBeforeResolution(),
          _ => f.AsProtected(),
        },
        _ => throw new UnreachableException(),
      },
      LocalVariable lv => lv.AsProtected(),
      _ => throw new UnreachableException(),
    });
    #region IVariable
    public static BoundVar AsProtected(this BoundVar var) => new(var.Origin.Clone(), var.NameNode.Clone(), var.SyntacticType.Clone(), var.IsGhost);
    public static QuantifiedVar AsProtected(this QuantifiedVar var) => new(var.Origin.Clone(), var.NameNode.Clone(), var.SyntacticType.Clone(), var.Domain?.AsProtected(), var.Range?.AsProtected());

    // for `Formal` and `LocalVariable`, the `localField` field (ouch, my brain) is not cloned; might be a bug
    public static Formal AsProtected(this Formal var) => new(var.Origin.Clone(), var.NameNode.Clone(), var.SyntacticType.Clone(), var.InParam, var.IsGhost, var.DefaultValue?.AsProtected(), var.Attributes?.Clone(), var.IsOld, var.IsNameOnly, var.IsOlder, var.NameForCompilation);
    public static LocalVariable AsProtected(this LocalVariable var) => new(var.Origin.Clone(), var.Name, var.SyntacticType.Clone(), var.IsGhost);
    #endregion

    public static CasePattern<VT> AsProtected<VT>(this CasePattern<VT> pat) where VT : IVariable => new(pat.Origin.Clone(), pat.Id, pat.Var is null ? pat.Var : pat.Var.AsProtected(), pat.Arguments?.ConvertAll(AsProtected));

    public static ExtendedPattern AsProtected(this ExtendedPattern pat) => pat switch {
      DisjunctivePattern dp => new DisjunctivePattern(dp.Origin.Clone(), dp.Alternatives.ConvertAll(AsProtected), dp.IsGhost),
      IdPattern ip => new IdPattern(ip.Origin.Clone(), ip.Id, ip.SyntacticType?.Clone(), ip.Arguments?.ConvertAll(AsProtected), ip.IsGhost, ip.HasParenthesis),
      LitPattern lp => new LitPattern(lp.Origin.Clone(), lp.OrigLit.AsProtected(), lp.IsGhost),
      _ => throw new UnreachableException(),
    };

    public static ActualBindings AsProtected(this ActualBindings bs) => new(bs.ArgumentBindings.ConvertAll(b => new ActualBinding(b.FormalParameterName?.Clone(), b.Actual.AsProtected())));

    public static AssignmentRhs AsProtected(this AssignmentRhs rhs) => rhs switch {
      ExprRhs e => e.AsProtected(),
      HavocRhs h => h.AsProtected(),
      TypeRhs t => t switch {
        AllocateArray aa => aa.AsProtected(),
        AllocateClass ac => ac.AsProtected(),
        _ => throw new UnreachableException(),
      },
      _ => throw new UnreachableException(),
    };
    #region AssignmentRhs
    public static ExprRhs AsProtected(this ExprRhs rhs) => new(rhs.Origin.Clone(), rhs.Expr.AsProtected(), rhs.Attributes.Clone());
    public static HavocRhs AsProtected(this HavocRhs rhs) => new(rhs.Origin.Clone());// { Attributes = rhs.Attributes.Clone() };
    public static AllocateArray AsProtected(this AllocateArray rhs) => (rhs.InitDisplay, rhs.ExplicitType, rhs.ElementInit) switch {
      (not null, not null, null) => new(rhs.Origin.Clone(), rhs.ExplicitType.Clone(), rhs.ArrayDimensions[0].AsProtected(), rhs.InitDisplay!.ConvertAll(AsProtected), rhs.Attributes.Clone()),
      (null, _, not null) => new(rhs.Origin.Clone(), rhs.ExplicitType.Clone(), rhs.ArrayDimensions.ConvertAll(AsProtected), rhs.ElementInit!.AsProtected(), rhs.Attributes.Clone()),
      _ => throw new UnreachableException(),
    };
    public static AllocateClass AsProtected(this AllocateClass rhs) => new(rhs.Origin.Clone(), rhs.Path.Clone(), rhs.Bindings?.AsProtected(), rhs.Attributes.Clone());
    #endregion

    public enum AEKind { Ensures };
    public static AttributedExpression AsProtected(this AttributedExpression e, AEKind? kind = null) {
      static AttributedExpression CreateFrom(AttributedExpression e, Expression inner) => new(inner, e.Label?.Clone(), e.Attributes.Clone());
      return kind switch {
        AEKind.Ensures when Attributes.Contains(e.Attributes, Constants.AttributeName) => WithAttributeAdditionalContext(() =>
          CreateFrom(e, e.E.WrappedWith(ProtectToProve with {
            ChangeContext = new ProtectToProveApplySuffix.ChangeContext(MostRecentContext),
          }))
        ),
        null or AEKind.Ensures => CreateFrom(e, e.E.AsProtected()),
        _ => throw new UnreachableException(),
      };
    }

    public static Specification<FrameExpression> AsProtected(this Specification<FrameExpression> spec) => new(spec.Expressions?.ConvertAll(AsProtected), spec.Attributes.Clone());
    public static Specification<Expression> AsProtected(this Specification<Expression> spec) => new(spec.Expressions?.ConvertAll(AsProtected), spec.Attributes.Clone());

    public static FrameExpression AsProtected(this FrameExpression e) => new(e.Origin.Clone(), e.OriginalExpression.AsProtected(), e.FieldName);

    public static CalcOp AsProtected(this CalcOp o) => o switch {
      BinaryCalcOp b => new BinaryCalcOp(b.Op),
      TernaryCalcOp t => new TernaryCalcOp(t.Index.AsProtected()),
      _ => throw new NotSupportedException(),
    };

    public static GuardedAlternative AsProtected(this GuardedAlternative a) => new(a.Origin.Clone(), a.IsBindingGuard, a.Guard.AsProtected(), a.Body.ConvertAll(AsProtected), a.Attributes.Clone());

    public static Expression AsProtectedFiniteSetOrMapComprehensionRange(this Expression range) {
      if (range is BinaryExpr { Op: BinaryExpr.Opcode.And } binaryExpr) {
        // domain
        switch (binaryExpr.E1) {
          case BinaryExpr { Op: BinaryExpr.Opcode.In, E0: IdentifierExpr, E1.Origin: QuantifiedVariableDomainOrigin } when ReferenceEquals(binaryExpr.Origin, binaryExpr.E1.Origin):
            // custom cloner?
            return binaryExpr.Clone();
          case { Origin: QuantifiedVariableRangeOrigin } when ReferenceEquals(binaryExpr.Origin, binaryExpr.E1.Origin.Center):
            // protect normally?
            return binaryExpr.Clone();
          default: throw new UnreachableException();
        }
        {
          return new BinaryExpr(range.Origin, BinaryExpr.Opcode.And,
            binaryExpr.E0.AsProtectedFiniteSetOrMapComprehensionRange(),
            WithProtectionOnIdentifierExpressionsInContainsBinaryExpressionsDisabled(() => binaryExpr.E1.AsProtected())
          );
        }
      }
      return range.Clone();
      //return WithProtectionOnIdentifierExpressionsInContainsBinaryExpressionsDisabled(() => range.AsProtected());
    }

    #endregion

    #region Statement
    public static Statement AsProtected(this Statement s) => s switch {
      NestedMatchStmt p => p.AsProtected(),
      ConcreteAssignStatement p => p.AsProtected(),
      SingleAssignStmt p => p.AsProtected(),
      VarDeclPattern p => p.AsProtected(),
      VarDeclStmt p => p.AsProtected(),
      BreakOrContinueStmt p => p.AsProtected(),
      ForallStmt p => p.AsProtected(),
      CallStmt p => throw p.CannotAppearBeforeResolution(),
      PrintStmt p => p.AsProtected(),
      ProduceStmt p => p switch {
        ReturnStmt pp => pp.AsProtected(),
        YieldStmt pp => pp.AsProtected(),
        _ => throw new UnreachableException(),
      },
      TryRecoverStatement p => p.AsProtected(),
      HideRevealStmt p => p.AsProtected(),
      ModifyStmt p => p.AsProtected(),
      PredicateStmt p => p switch {
        AssertStmt pp => pp.AsProtected(),
        AssumeStmt pp => pp.AsProtected(),
        ExpectStmt pp => pp.AsProtected(),
        _ => throw new UnreachableException(),
      },
      BlockByProofStmt p => p.AsProtected(),
      CalcStmt p => p.AsProtected(),
      MatchStmt p => throw p.CannotAppearBeforeResolution(),
      SkeletonStatement p => p.AsProtected(),
      LabeledStatement p => p switch {
        AlternativeStmt pp => pp.AsProtected(),
        LoopStmt pp => pp switch {
          AlternativeLoopStmt ppp => ppp.AsProtected(),
          OneBodyLoopStmt ppp => ppp switch {
            ForLoopStmt pppp => pppp.AsProtected(),
            WhileStmt pppp => pppp switch {
              RefinedWhileStmt ppppp => throw ppppp.CannotAppearBeforeResolution(),
              _ when pppp.IsExactly() => pppp.AsProtected(),
              _ => throw new UnreachableException(),
            },
            _ => throw new UnreachableException(),
          },
          _ => throw new UnreachableException(),
        },
        IfStmt pp => pp.AsProtected(),
        BlockLikeStmt pp => pp.AsProtected(),
        _ when p.IsExactly() => p.AsProtected(),
        _ => throw new UnreachableException(),
      },
      _ => throw new UnreachableException(),
    };
    public static NestedMatchStmt AsProtected(this NestedMatchStmt s) => new(s.Origin.Clone(), s.Source.AsProtected(), s.Cases.ConvertAll(static c => new NestedMatchCaseStmt(c.Origin.Clone(), c.Pat.AsProtected(), c.Body.ConvertAll(AsProtected), c.Attributes.Clone())), s.UsesOptionalBraces, s.Attributes.Clone());
    public static ConcreteAssignStatement AsProtected(this ConcreteAssignStatement s) => s switch {
      AssignOrReturnStmt a => a.AsProtected(),
      AssignStatement a => a.AsProtected(),
      AssignSuchThatStmt a => a.AsProtected(),
      _ => throw new UnreachableException(),
    };
    #region ConcreteAssignStatement
    public static AssignOrReturnStmt AsProtected(this AssignOrReturnStmt s) => new(s.Origin.Clone(), s.Lhss.ConvertAll(Clone), s.Rhs.AsProtected(), s.KeywordToken.Clone(), s.Rhss.ConvertAll(AsProtected));
    public static AssignStatement AsProtected(this AssignStatement s) => new(s.Origin.Clone(), s.Lhss.ConvertAll<Expression>(Clone), s.Rhss.ConvertAll(AsProtected), s.CanMutateKnownState, s.Attributes.Clone());
    public static AssignSuchThatStmt AsProtected(this AssignSuchThatStmt s) => new(s.Origin.Clone(), s.Lhss.ConvertAll(Clone), s.Expr.AsProtected(), s.AssumeToken.Clone(), s.Attributes.Clone());
    #endregion

    public static SingleAssignStmt AsProtected(this SingleAssignStmt s) => new(s.Origin.Clone(), s.Lhs.AsProtected(), s.Rhs.AsProtected());
    public static VarDeclPattern AsProtected(this VarDeclPattern s) => new(s.Origin.Clone(), s.LHS.AsProtected(), s.RHS.AsProtected(), s.HasGhostModifier);
    public static VarDeclStmt AsProtected(this VarDeclStmt s) => new(s.Origin.Clone(), s.Locals.ConvertAll(AsProtected), s.Assign?.ApplyIfNotNull(AsProtected), s.Attributes?.Clone());
    public static BreakOrContinueStmt AsProtected(this BreakOrContinueStmt s) => new(s.Origin.Clone(), s.TargetLabel?.Clone(), s.BreakAndContinueCount, s.IsContinue, s.Attributes.Clone());
    public static ForallStmt AsProtected(this ForallStmt s) => new(s.Origin.Clone(), s.BoundVars.ConvertAll(AsProtected), s.Attributes.Clone(), s.Range.AsProtected(), s.Ens.ConvertAll(static e => e.AsProtected()), s.Body.AsProtected());
    public static PrintStmt AsProtected(this PrintStmt s) => new(s.Origin.Clone(), s.Args.ConvertAll(AsProtected), s.Attributes.Clone());
    #region ProduceStmt
    public static ReturnStmt AsProtected(this ReturnStmt s) => new(s.Origin.Clone(), s.Rhss?.ConvertAll(AsProtected), s.Attributes.Clone()) { ReverifyPost = s.ReverifyPost }; // ReverifyPost is done in the cloner but not necessary, since it can only be assigned after the protections are done
    public static YieldStmt AsProtected(this YieldStmt s) => new(s.Origin.Clone(), s.Rhss?.ConvertAll(AsProtected));
    #endregion
    public static TryRecoverStatement AsProtected(this TryRecoverStatement s) {
      Console.WriteLine("TryRecoverStatement shouldn't be present in the AST");
      return new(s.TryBody.AsProtected(), s.HaltMessageVar.AsProtected(), s.RecoverBody.AsProtected());
    }
    public static HideRevealStmt AsProtected(this HideRevealStmt s) => new(s.Origin.Clone(), s.Exprs?.ConvertAll(AsProtected), s.Mode, s.Attributes.Clone());
    public static ModifyStmt AsProtected(this ModifyStmt s) => new(s.Origin.Clone(), s.Mod.Expressions?.ConvertAll(AsProtected), s.Mod.Attributes.Clone(), s.Body.AsProtected());
    #region PredicateStmt
    private static AssertStmt AsProtected(this AssertStmt a) {
      static AssertStmt CreateFrom(AssertStmt s, Expression? e = null) => new(s.Origin.Clone(), e ?? s.Expr.AsProtected(), s.Label?.Clone(), s.Attributes.Clone());
      var attributeName = Constants.AttributeName;
      var immediateAttributeName = Constants.ImmediateAttributeName;
      if (Attributes.Contains(a.Attributes, attributeName)) {
        //Console.WriteLine("Protecting to prove assertion " + a.Expr.ToString());
        return WithAttributeAdditionalContext(() => CreateFrom(a, a.Expr.WrappedWith(ProtectToProve with {
          ChangeContext = new ProtectToProveApplySuffix.ChangeContext(MostRecentContext),
        })));
      }
      if (Attributes.Find(a.Attributes, immediateAttributeName) is { } attr) {
        if (attr is { Args: [] }) { attr.Args.Add(new LiteralExpr(SourceOrigin.NoToken, 0)); } // temporary bcs frontend doesn't use {:ipm_now 0} yet
        if (attr is not { Args: [var arg] }) { throw new Exception($"the {{:{immediateAttributeName}}} attribute requires an argument"); }
        if (arg is not LiteralExpr { Value: BigInteger entryPoint }) { throw new Exception($"{{:{immediateAttributeName}}}'s argument needs to be a natural number"); }
        return CreateFrom(a, a.Expr.WrappedWith(ProtectToProveImmediate with { EntryPoint = entryPoint }));
      }
      //Console.WriteLine($"assert statement: {a.Expr}");
      return CreateFrom(a);
    }
    public static AssumeStmt AsProtected(this AssumeStmt s) => new(s.Origin.Clone(), s.Expr.AsProtected(), s.Attributes.Clone());
    public static ExpectStmt AsProtected(this ExpectStmt s) => new(s.Origin.Clone(), s.Expr.AsProtected(), s.Message.Clone(), s.Attributes.Clone());
    #endregion
    public static BlockByProofStmt AsProtected(this BlockByProofStmt s) {
      static BlockByProofStmt CreateFrom(BlockByProofStmt s, Statement body) => new(s.Origin.Clone(), s.Proof.AsProtected(), body, s.Attributes.Clone());
      return s.Body is AssertStmt ? WithAttributeAdditionalContext(() => CreateFrom(s, s.Body.AsProtected())) : CreateFrom(s, s.Body.AsProtected());
    }

    public static CalcStmt AsProtected(this CalcStmt s) {
      List<Expression> lines;
      switch (s.Lines.Count) {
        case < 2:
        case >= 2 when s.Lines[^2] != s.Lines[^1]: {
            lines = s.Lines.ConvertAll(AsProtected);
          }
          break;
        case >= 2: {
            lines = new(s.Lines.Count);
            lines.AddRange(s.Lines.SkipLast(1).Select(AsProtected).WithTheLastElementRepeated());
          }
          break;
      }
      return new(s.Origin.Clone(), s.UserSuppliedOp.AsProtected(), lines, s.Hints.ConvertAll(AsProtected), s.StepOps.ConvertAll(AsProtected), s.Attributes.Clone());
    }
    public static SkeletonStatement AsProtected(this SkeletonStatement s) => (s.S, s.ConditionEllipsis, s.BodyEllipsis) switch { // not sure what the object invariant, so I assumed based on usade throughout the codebase
      (null, null, null) => new(s.Origin.Clone()),
      (not null, not null, not null) => new(s.S.AsProtected(), s.ConditionEllipsis.Clone(), s.BodyEllipsis.Clone()),
      _ => throw new UnreachableException(),
    };
    public static LabeledStatement AsProtected(this LabeledStatement s) => new(s.Origin.Clone(), s.Labels.Clone(), s.Attributes.Clone());
    public static AlternativeStmt AsProtected(this AlternativeStmt s) => new(s.Origin.Clone(), s.Labels.Clone(), s.Alternatives.ConvertAll(AsProtected), s.UsesOptionalBraces, s.Attributes.Clone());
    public static AlternativeLoopStmt AsProtected(this AlternativeLoopStmt s) => new(s.Origin.Clone(), s.Invariants.ConvertAll(static i => i.AsProtected()), s.Decreases.AsProtected(), s.Mod.AsProtected(), s.Alternatives.ConvertAll(AsProtected), s.UsesOptionalBraces, s.Labels.Clone(), s.Attributes.Clone());
    public static ForLoopStmt AsProtected(this ForLoopStmt s) => new(s.Origin.Clone(), s.LoopIndex.AsProtected(), s.Start.AsProtected(), s.End?.AsProtected(), s.GoingUp, s.Invariants.ConvertAll(static i => i.AsProtected()), s.Decreases.AsProtected(), s.Mod.AsProtected(), s.Body?.AsProtected(), s.Labels.Clone(), s.Attributes.Clone());
    public static WhileStmt AsProtected(this WhileStmt s) => new(s.Origin.Clone(), s.Guard?.AsProtected()!, s.Invariants.ConvertAll(static i => i.AsProtected()), s.Decreases.AsProtected(), s.Mod.AsProtected(), s.Body?.AsProtected()!);
    public static IfStmt AsProtected(this IfStmt s) => new(s.Origin.Clone(), s.IsBindingGuard, s.Guard?.AsProtected(), s.Thn.AsProtected(), s.Els?.AsProtected());
    public static BlockLikeStmt AsProtected(this BlockLikeStmt s) => s switch {
      DividedBlockStmt db => db.AsProtected(),
      BlockStmt b => b switch {
        OpaqueBlock ob => ob.AsProtected(),
        _ when b.IsExactly() => b.AsProtected(),
        _ => throw new UnreachableException(),
      },
      _ => throw new UnreachableException(),
    };
    #region BlockLikeStmt
    public static DividedBlockStmt AsProtected(this DividedBlockStmt s) => new(s.Origin.Clone(), s.BodyInit.ConvertAll(AsProtected), s.SeparatorTok?.Clone(), s.BodyProper.ConvertAll(AsProtected), s.Labels.Clone(), s.Attributes.Clone());
    public static BlockStmt AsProtected(this BlockStmt s) => new(s.Origin.Clone(), s.Body.ConvertAll(AsProtected), s.Labels.Clone(), s.Attributes.Clone());
    public static OpaqueBlock AsProtected(this OpaqueBlock s) => new(s.Origin.Clone(), s.Body.ConvertAll(AsProtected), s.Ensures.ConvertAll(static e => e.AsProtected()), s.Modifies.AsProtected(), s.Labels.Clone(), s.Attributes.Clone());
    #endregion
    #endregion

    #region Expression
    public static Expression AsProtected(this Expression e) => e switch {
      ApplyExpr p => throw p.CannotAppearBeforeResolution(),
      FunctionCallExpr p => throw p.CannotAppearBeforeResolution(),
      MemberSelectExpr p => throw p.CannotAppearBeforeResolution(),
      MultiSelectExpr p => p.AsProtected(),
      SeqSelectExpr p => p.AsProtected(),
      ThisExpr p => p switch {
        ImplicitThisExpr pp => pp switch {
          ImplicitThisExprConstructorCall ppp => throw ppp.CannotAppearBeforeResolution(),
          _ when pp.IsExactly() => pp.AsProtected(),
          _ => throw new UnreachableException(),
        },
        _ when p.IsExactly() => p.AsProtected(),
        _ => throw new UnreachableException(),
      },
      DisplayExpression p => p switch {
        SeqDisplayExpr pp => pp.AsProtected(),
        SetDisplayExpr pp => pp.AsProtected(),
        MultiSetDisplayExpr pp => pp.AsProtected(),
        _ => throw new UnreachableException(),
      },
      MapDisplayExpr p => p.AsProtected(),
      MultiSetFormingExpr p => p.AsProtected(),
      SeqConstructionExpr p => p.AsProtected(),
      SeqUpdateExpr p => p.AsProtected(),
      ComprehensionExpr p => p switch {
        LambdaExpr pp => pp.AsProtected(),
        MapComprehension pp => pp.AsProtected(),
        SetComprehension pp => pp.AsProtected(),
        QuantifierExpr pp => pp switch {
          ForallExpr ppp => ppp.AsProtected(),
          ExistsExpr ppp => ppp.AsProtected(),
          _ => throw new UnreachableException(),
        },
        _ => throw new UnreachableException(),
      },
      ITEExpr p => p.AsProtected(),
      NestedMatchExpr p => p.AsProtected(),
      TernaryExpr p => p.AsProtected(),
      DatatypeValue p => p.AsProtected(),
      FieldLocation p => throw p.CannotAppearBeforeResolution(),
      IndexFieldLocation p => throw p.CannotAppearBeforeResolution(),
      LocalsObjectExpression p => p.AsProtected(),
      OldExpr p => p.AsProtected(),
      UnchangedExpr p => p.AsProtected(),
      WildcardExpr p => p.AsProtected(),
      BinaryExpr p => p.AsProtected(),
      DecreasesToExpr p => p.AsProtected(),
      UnaryExpr p => p switch {
        UnaryOpExpr pp => pp switch {
          FreshExpr ppp => ppp.AsProtected(),
          _ when pp.IsExactly() => pp.AsProtected(),
          _ => throw new UnreachableException(),
        },
        TypeUnaryExpr pp => pp switch {
          ConversionExpr ppp => ppp.AsProtected(),
          TypeTestExpr ppp => ppp.AsProtected(),
          _ => throw new UnreachableException(),
        },
        _ => throw new UnreachableException(),
      },
      BoxingCastExpr p => throw p.CannotAppearBeforeResolution(),
      UnboxingCastExpr p => throw p.CannotAppearBeforeResolution(),
      IdentifierExpr p => p switch {
        AutoGhostIdentifierExpr pp => pp.AsProtected(),
        ImplicitIdentifierExpr pp => pp.AsProtected(),
        _ when p.IsExactly() => p.AsProtected(),
        _ => throw new UnreachableException(),
      },
      LetExpr p => p switch {
        BoogieGenerator.SubstLetExpr pp => throw pp.CannotAppearBeforeResolution(),
        _ when p.IsExactly() => p.AsProtected(),
        _ => throw new UnreachableException(),
      },
      ResolverIdentifierExpr p => throw p.CannotAppearBeforeResolution(),
      ConcreteSyntaxExpression p => p switch {
        NameSegment pp => pp.AsProtected(),
        SuffixExpr pp => pp switch {
          ApplySuffix ppp => ppp switch {
            ProtectToProveApplySuffix pppp => throw new UnreachableException($"Due to the nature of the protection applied over the AST, no part of the AST should be processed more than once; this expression signals that a part of the AST {pppp} is to be processed at least twice"),
            _ when ppp.IsExactly() => ppp.AsProtected(),
            _ => throw new UnreachableException(),
          },
          ExprDotName ppp => ppp.AsProtected(),
          FieldLocationExpression ppp => ppp.AsProtected(),
          IndexFieldLocationExpression ppp => ppp.AsProtected(),
          _ => throw new UnreachableException(),
        },
        DatatypeUpdateExpr pp => pp.AsProtected(),
        ChainingExpression pp => pp.AsProtected(),
        ParensExpression pp => pp switch {
          AutoGeneratedExpression ppp => throw ppp.CannotAppearBeforeResolution(),
          _ when pp.IsExactly() => pp.AsProtected(),
          _ => throw new UnreachableException(),
        },
        LetOrFailExpr pp => pp.AsProtected(),
        DefaultValueExpression pp => pp switch {
          DefaultValueExpressionType ppp => throw ppp.CannotAppearBeforeResolution(),
          DefaultValueExpressionPreType ppp => throw ppp.CannotAppearBeforeResolution(),
          _ => throw new UnreachableException(),
        },
        NegationExpression pp => pp.AsProtected(),
        _ => throw new UnreachableException(),
      },
      LiteralExpr p => p switch {
        StaticReceiverExpr pp => throw pp.CannotAppearBeforeResolution(),
        CharLiteralExpr pp => pp.AsProtected(),
        StringLiteralExpr pp => pp.AsProtected(),
        DecimalLiteralExpr pp => pp.AsProtected(),
        _ when p.IsExactly() => p.AsProtected(),
        _ => throw new UnreachableException(),
      },
      StmtExpr p => p.AsProtected(),
      MatchExpr p => throw p.CannotAppearBeforeResolution(),
      BoogieGenerator.BoogieWrapper p => throw p.CannotAppearBeforeResolution(),
      BoogieGenerator.BoogieFunctionCall p => throw p.CannotAppearBeforeResolution(),
      _ => throw new UnreachableException(),
    };
    public static MultiSelectExpr AsProtected(this MultiSelectExpr e) => new(e.Origin.Clone(), e.Array.AsProtected(), e.Indices.ConvertAll(AsProtected));
    public static SeqSelectExpr AsProtected(this SeqSelectExpr e) => new(e.Origin.Clone(), e.SelectOne, e.Seq.AsProtected(), e.E0?.AsProtected(), e.E1?.AsProtected(), e.CloseParen);
    public static ApplySuffix AsProtected(this ThisExpr e) => e.Clone().WrappedWith(ProtectorFunctions.OldProtect);
    public static ApplySuffix AsProtected(this ImplicitThisExpr e) => e.Clone().WrappedWith(ProtectorFunctions.OldProtect); // TODO: check what this is
    public static SeqDisplayExpr AsProtected(this SeqDisplayExpr e) => new(e.Origin.Clone(), e.Elements.ConvertAll(AsProtected));
    public static SetDisplayExpr AsProtected(this SetDisplayExpr e) => new(e.Origin.Clone(), e.Finite, e.Elements.ConvertAll(AsProtected));
    public static MultiSetDisplayExpr AsProtected(this MultiSetDisplayExpr e) => new(e.Origin.Clone(), e.Elements.ConvertAll(AsProtected));
    public static MapDisplayExpr AsProtected(this MapDisplayExpr e) => new(e.Origin.Clone(), e.Finite, e.Elements.ConvertAll(static entry => new MapDisplayEntry(entry.A.AsProtected(), entry.B.AsProtected())));
    public static MultiSetFormingExpr AsProtected(this MultiSetFormingExpr e) => new(e.Origin.Clone(), e.E.Clone()); // TODO: check what this is
    public static SeqConstructionExpr AsProtected(this SeqConstructionExpr e) => new(e.Origin.Clone(), e.ExplicitElementType.Clone(), e.N.AsProtected(), e.Initializer.AsProtected());
    public static SeqUpdateExpr AsProtected(this SeqUpdateExpr e) => new(e.Origin.Clone(), e.Seq.AsProtected(), e.Index.AsProtected(), e.Value.AsProtected());
    public static LambdaExpr AsProtected(this LambdaExpr e) => new(e.Origin.Clone(), e.BoundVars.ConvertAll(AsProtected), e.Range?.AsProtected(), e.Reads.AsProtected(), e.Term.AsProtected(), e.Attributes.Clone());
    public static MapComprehension AsProtected(this MapComprehension e) => new(e.Origin.Clone(), e.Finite, e.BoundVars.ConvertAll(AsProtected), e.Range?.AsProtectedFiniteSetOrMapComprehensionRange()!, e.TermLeft?.AsProtected(), e.Term.AsProtected(), e.Attributes.Clone());
    public static SetComprehension AsProtected(this SetComprehension e) => new(e.Origin.Clone(), e.Finite, e.BoundVars.ConvertAll(AsProtected), e.Range?.AsProtectedFiniteSetOrMapComprehensionRange()!, e.Term.AsProtected(), e.Attributes.Clone()) { TermIsImplicit = e.TermIsImplicit };
    public static ForallExpr AsProtected(this ForallExpr e) => new(e.Origin.Clone(), e.BoundVars.ConvertAll(AsProtected), e.Range?.AsProtected(), e.Term.AsProtected(), e.Attributes.Clone());
    public static ExistsExpr AsProtected(this ExistsExpr e) => new(e.Origin.Clone(), e.BoundVars.ConvertAll(AsProtected), e.Range?.AsProtected(), e.Term.AsProtected(), e.Attributes.Clone());
    public static ITEExpr AsProtected(this ITEExpr e) => new(e.Origin.Clone(), e.IsBindingGuard, e.Test.AsProtected(), e.Thn.AsProtected(), e.Els.AsProtected());
    public static NestedMatchExpr AsProtected(this NestedMatchExpr e) => new(e.Origin.Clone(), e.Source.AsProtected(), e.Cases.ConvertAll(static c => new NestedMatchCaseExpr(c.Origin.Clone(), c.Pat.AsProtected(), c.Body.AsProtected(), c.Attributes.Clone())), e.UsesOptionalBraces, e.Attributes.Clone());
    public static TernaryExpr AsProtected(this TernaryExpr e) => new(e.Origin.Clone(), e.Op, e.E0.AsProtected(), e.E1.AsProtected(), e.E2.AsProtected());
    public static ApplySuffix AsProtected(this DatatypeValue e) => e.Clone().WrappedWith(ProtectorFunctions.OldProtect);
    public static LocalsObjectExpression AsProtected(this LocalsObjectExpression e) => new(e.Origin.Clone());
    public static OldExpr AsProtected(this OldExpr e) => new(e.Origin.Clone(), e.Expr.AsProtected(), e.At);
    public static UnchangedExpr AsProtected(this UnchangedExpr e) => new(e.Origin.Clone(), e.Frame.ConvertAll(AsProtected), e.At);
    public static WildcardExpr AsProtected(this WildcardExpr e) => new(e.Origin.Clone());
    public static BinaryExpr AsProtected(this BinaryExpr e) => new(e.Origin.Clone(), e.Op, (ProtectLhsInContainsBinaryExpressions || e is not { Op: BinaryExpr.Opcode.In, E0: IdentifierExpr or NameSegment }) switch {
      true => e.E0.AsProtected(),
      false => e.E0.Clone(),
    }, e.E1.AsProtected());
    public static DecreasesToExpr AsProtected(this DecreasesToExpr e) => new(e.Origin.Clone(), e.OldExpressions.ConvertAll(AsProtected), e.NewExpressions.ConvertAll(AsProtected), e.AllowNoChange);
    public static UnaryOpExpr AsProtected(this UnaryOpExpr e) {
      Contract.Requires(e.Op is UnaryOpExpr.Opcode.Not or UnaryOpExpr.Opcode.Allocated or UnaryOpExpr.Opcode.Cardinality or UnaryOpExpr.Opcode.Assigned);
      return new(e.Origin.Clone(), e.Op, e.E.AsProtected());
    }
    public static FreshExpr AsProtected(this FreshExpr e) {
      Contract.Requires(e.Op is UnaryOpExpr.Opcode.Fresh);
      return new(e.Origin.Clone(), e.E.AsProtected(), e.At);
    }
    public static ConversionExpr AsProtected(this ConversionExpr e) => new(e.Origin.Clone(), e.E.AsProtected(), e.ToType.Clone());//, e.messagePrefix);
    public static TypeTestExpr AsProtected(this TypeTestExpr e) => new(e.Origin.Clone(), e.E.AsProtected(), e.ToType.Clone());
    public static ApplySuffix AsProtected(this IdentifierExpr e) => e.Clone().WrappedWith(ProtectorFunctions.OldProtect);
    public static LetExpr AsProtected(this LetExpr e) => new(e.Origin.Clone(), e.LHSs.ConvertAll(AsProtected), e.RHSs.ConvertAll(AsProtected), e.Body.AsProtected(), e.Exact, e.Attributes.Clone());
    public static ApplySuffix AsProtected(this NameSegment e) => e.Clone().WrappedWith(ProtectorFunctions.OldProtect);
    // not sure if anything needs to be done for backtick tokens, you'll find out I guess
    // there are both cases in which `Lhs` should be protected and in which it shouldn't be protected
    // maybe the solution would be deferred protection aka both a clone and a protected clone are made and, during resolution, we see if the protected clone is valid
    // that would require doing the resolution on multiple versions of an `ApplySuffix` AND changing the way some operations are done on `ApplySuffix` objects
    // for now, we'll only protect lambda expressions, which seems pretty much harmless
    //public static ApplySuffix AsProtected(this ApplySuffix e) => new(e.Origin.Clone(), e.AtTok?.Clone(), e.Lhs.AsProtected(), e.Bindings.ArgumentBindings.ConvertAll(static ab => new ActualBinding(ab.FormalParameterName?.Clone(), ab.Actual.AsProtected(), ab.IsGhost)), e.CloseParen);
    public static ApplySuffix AsProtected(this ApplySuffix e) => new(e.Origin.Clone(), e.AtTok?.Clone(), e.Lhs is ParensExpression { E: LambdaExpr } ? e.Lhs.AsProtected() : e.Lhs.Clone(), new ActualBindings(
      e.Bindings.ArgumentBindings.ConvertAll(static ab => new ActualBinding(ab.FormalParameterName?.Clone(), ab.Actual.AsProtected(), ab.IsGhost))
    ), e.CloseParen);

    public static ExprDotName AsProtected(this ExprDotName e) => new(e.Origin.Clone(), e.Lhs.AsProtected(), e.SuffixNameNode.Clone(), e.OptTypeArguments?.ConvertAll<Microsoft.Dafny.Type>(Clone));
    public static FieldLocationExpression AsProtected(this FieldLocationExpression e) => new(e.Lhs.AsProtected(), e.Backtick, e.Name.Clone());
    public static IndexFieldLocationExpression AsProtected(this IndexFieldLocationExpression e) => new(e.Lhs.AsProtected(), e.OpenParen, e.Indices.ConvertAll(AsProtected), e.CloseParen);
    public static DatatypeUpdateExpr AsProtected(this DatatypeUpdateExpr e) => new(e.Origin.Clone(), e.Root.AsProtected(), e.Updates.ConvertAll(static t => Tuple.Create(t.Item1, t.Item2, t.Item3.AsProtected())));
    public static ChainingExpression AsProtected(this ChainingExpression e) => new(e.Origin.Clone(), e.Operands.ConvertAll(AsProtected), e.Operators, e.OperatorLocs.ConvertAll(Clone), e.PrefixLimits.ConvertAll(static e => e?.AsProtected())); // TODO: shallow-copy Operators?
    public static ParensExpression AsProtected(this ParensExpression e) => new(e.Origin.Clone(), e.E.AsProtected());
    public static LetOrFailExpr AsProtected(this LetOrFailExpr e) => new(e.Origin.Clone(), e.Lhs?.AsProtected(), e.Rhs.AsProtected(), e.Body.AsProtected());
    public static NegationExpression AsProtected(this NegationExpression e) => new(e.Origin.Clone(), e.E.AsProtected());
    public static LiteralExpr AsProtected(this LiteralExpr e) => e.Clone();
    public static StmtExpr AsProtected(this StmtExpr e) => new(e.Origin.Clone(), e.S.AsProtected(), e.E.AsProtected());
    #endregion

    #region Method, Constructor and Function
    public static MethodOrFunction AsProtected(this MethodOrFunction mof) => mof switch {
      MethodOrConstructor m => m.AsProtected(),
      Function f => f.AsProtected(),
      _ => throw new UnreachableException(),
    };
    public static MethodOrConstructor AsProtected(this MethodOrConstructor moc) => moc switch {
      Method m => m.AsProtected(),
      Constructor c => c.AsProtected(),
      _ => throw new UnreachableException(),
    };

    public static Constructor AsProtected(this Constructor c) => WithMemberAdditionalContext(() => new Constructor(
      c.Origin.Clone(),
      c.NameNode.Clone(),
      c.IsGhost,
      c.TypeArgs.ConvertAll<TypeParameter>(Clone),
      c.Ins.ConvertAll(AsProtected),
      c.Req.ConvertAll(static e => e.AsProtected()), c.Reads.AsProtected(), c.Mod.AsProtected(), c.Ens.ConvertAll(static e => e.AsProtected(AEKind.Ensures)),
      c.Decreases.AsProtected(),
      c.Body?.AsProtected(),
      c.Attributes.Clone(), c.SignatureEllipsis?.Clone()
    ));

    public static Method AsProtected(this Method m) => m switch {
      Lemma l => l.AsProtected(),
      TwoStateLemma l => l.AsProtected(),
      PrefixLemma l => throw CannotAppearBeforeResolution(l),
      ExtremeLemma l => l.AsProtected(),
      _ when m.IsExactly() => WithMemberAdditionalContext(() => new Method(
        m.Origin.Clone(),
        m.NameNode.Clone(),
        m.Attributes.Clone(),
        m.HasStaticKeyword, m.IsGhost,
        m.TypeArgs.ConvertAll<TypeParameter>(Clone),
        m.Ins.ConvertAll(AsProtected),
        m.Req.ConvertAll(static e => e.AsProtected()), m.Ens.ConvertAll(static e => e.AsProtected(AEKind.Ensures)),
        m.Reads.AsProtected(), m.Decreases.AsProtected(),
        m.Outs.ConvertAll(AsProtected), m.Mod.AsProtected(),
        m.Body?.AsProtected(), m.SignatureEllipsis?.Clone(),
        m.IsByMethod
      )),
      _ => throw new UnreachableException(),
    };
    public static Lemma AsProtected(this Lemma l) => WithMemberAdditionalContext(() => new Lemma(
      l.Origin.Clone(),
      l.NameNode.Clone(),
      l.HasStaticKeyword,
      l.TypeArgs.ConvertAll<TypeParameter>(Clone),
      l.Ins.ConvertAll(AsProtected), l.Outs.ConvertAll(AsProtected),
      l.Req.ConvertAll(static e => e.AsProtected()), l.Reads.AsProtected(), l.Mod.AsProtected(), l.Ens.ConvertAll(static e => e.AsProtected(AEKind.Ensures)),
      l.Decreases.AsProtected(), l.Body!.AsProtected(), l.Attributes.Clone(), l.SignatureEllipsis?.Clone()
    ));
    public static TwoStateLemma AsProtected(this TwoStateLemma l) => WithMemberAdditionalContext(() => new TwoStateLemma(
      l.Origin.Clone(),
      l.NameNode.Clone(),
      l.HasStaticKeyword,
      l.TypeArgs.ConvertAll<TypeParameter>(Clone),
      l.Ins.ConvertAll(AsProtected), l.Outs.ConvertAll(AsProtected),
      l.Req.ConvertAll(static e => e.AsProtected()), l.Reads.AsProtected(), l.Mod.AsProtected(), l.Ens.ConvertAll(static e => e.AsProtected(AEKind.Ensures)),
      l.Decreases.AsProtected(), l.Body!.AsProtected(), l.Attributes.Clone()!, l.SignatureEllipsis?.Clone()!
    ));
    public static ExtremeLemma AsProtected(this ExtremeLemma l) => l switch {
      GreatestLemma gl => gl.AsProtected(),
      LeastLemma ll => ll.AsProtected(),
      _ => throw new UnreachableException(),
    };
    public static GreatestLemma AsProtected(this GreatestLemma l) => WithMemberAdditionalContext(() => new GreatestLemma(
      l.Origin.Clone(),
      l.NameNode.Clone(),
      l.HasStaticKeyword,
      l.TypeOfK, l.TypeArgs.ConvertAll<TypeParameter>(Clone),
      l.Ins.ConvertAll(AsProtected), l.Outs.ConvertAll(AsProtected),
      l.Req.ConvertAll(static e => e.AsProtected()), l.Reads.AsProtected(), l.Mod.AsProtected(), l.Ens.ConvertAll(static e => e.AsProtected(AEKind.Ensures)),
      l.Decreases.AsProtected(), l.Body!.AsProtected(), l.Attributes.Clone(), l.SignatureEllipsis?.Clone()
    ));
    public static LeastLemma AsProtected(this LeastLemma l) => WithMemberAdditionalContext(() => new LeastLemma(
      l.Origin.Clone(),
      l.NameNode.Clone(),
      l.HasStaticKeyword,
      l.TypeOfK, l.TypeArgs.ConvertAll<TypeParameter>(Clone),
      l.Ins.ConvertAll(AsProtected), l.Outs.ConvertAll(AsProtected),
      l.Req.ConvertAll(static e => e.AsProtected()), l.Reads.AsProtected(), l.Mod.AsProtected(), l.Ens.ConvertAll(static e => e.AsProtected(AEKind.Ensures)),
      l.Decreases.AsProtected(), l.Body!.AsProtected(), l.Attributes.Clone()!, l.SignatureEllipsis?.Clone()!
    ));

    public static Function AsProtected(this Function f) => f switch {
      Predicate p => p.AsProtected(),
      TwoStateFunction tsf => tsf.AsProtected(),
      PrefixPredicate pp => throw pp.CannotAppearBeforeResolution(),
      SpecialFunction sf => throw sf.CannotAppearBeforeResolution(), // during default module resolution, still after the point where this would happen
      ExtremePredicate ep => ep.AsProtected(),
      _ when f.IsExactly() => WithMemberAdditionalContext(() => new Function(
        f.Origin.Clone(),
        f.NameNode.Clone(),
        f.HasStaticKeyword, f.IsGhost, f.IsOpaque,
        f.TypeArgs.ConvertAll<TypeParameter>(Clone),
        f.Ins.ConvertAll(AsProtected),
        f.Result?.AsProtected(), f.ResultType.Clone(),
        f.Req.ConvertAll(static e => e.AsProtected()), f.Reads.AsProtected(), f.Ens.ConvertAll(static e => e.AsProtected(AEKind.Ensures)), f.Decreases.AsProtected(),
        f.Body?.AsProtected(),
        f.ByMethodTok?.Clone(), f.ByMethodBody?.AsProtected(),
        f.Attributes.Clone(), f.SignatureEllipsis?.Clone()
      )),
      _ => throw new UnreachableException(),
    };
    public static Predicate AsProtected(this Predicate p) => WithMemberAdditionalContext(() => new Predicate(
      p.Origin.Clone(),
      p.NameNode.Clone(),
      p.HasStaticKeyword, p.IsGhost, p.IsOpaque,
      p.TypeArgs.ConvertAll<TypeParameter>(Clone),
      p.Ins.ConvertAll(AsProtected),
      p.Result?.AsProtected(),
      p.Req.ConvertAll(static e => e.AsProtected()), p.Reads.AsProtected(), p.Ens.ConvertAll(static e => e.AsProtected(AEKind.Ensures)), p.Decreases.AsProtected(),
      p.Body?.AsProtected(), p.BodyOrigin,
      p.ByMethodTok?.Clone(), p.ByMethodBody?.AsProtected(),
      p.Attributes.Clone(), p.SignatureEllipsis?.Clone()
    ));
    public static TwoStateFunction AsProtected(this TwoStateFunction f) => f switch {
      TwoStatePredicate p => p.AsProtected(),
      _ when f.IsExactly() => WithMemberAdditionalContext(() => new TwoStateFunction(
        f.Origin.Clone(),
        f.NameNode.Clone(),
        f.HasStaticKeyword, f.IsOpaque,
        f.TypeArgs.ConvertAll<TypeParameter>(Clone),
        f.Ins.ConvertAll(AsProtected),
        f.Result?.AsProtected(), f.ResultType.Clone(),
        f.Req.ConvertAll(static e => e.AsProtected()), f.Reads.AsProtected(), f.Ens.ConvertAll(static e => e.AsProtected(AEKind.Ensures)), f.Decreases.AsProtected(),
        f.Body?.AsProtected(),
        f.Attributes.Clone(), f.SignatureEllipsis?.Clone()
      )),
      _ => throw new UnreachableException(),
    };
    public static TwoStatePredicate AsProtected(this TwoStatePredicate p) => WithMemberAdditionalContext(() => new TwoStatePredicate(
      p.Origin.Clone(),
      p.NameNode.Clone(),
      p.HasStaticKeyword, p.IsOpaque,
      p.TypeArgs.ConvertAll<TypeParameter>(Clone),
      p.Ins.ConvertAll(AsProtected),
      p.Result?.AsProtected(),
      p.Req.ConvertAll(static e => e.AsProtected()), p.Reads.AsProtected(), p.Ens.ConvertAll(static e => e.AsProtected(AEKind.Ensures)), p.Decreases.AsProtected(),
      p.Body?.AsProtected(),
      p.Attributes.Clone(), p.SignatureEllipsis?.Clone()
    ));
    public static ExtremePredicate AsProtected(this ExtremePredicate p) => p switch {
      GreatestPredicate gp => gp.AsProtected(),
      LeastPredicate lp => lp.AsProtected(),
      _ => throw new UnreachableException(),
    };
    public static GreatestPredicate AsProtected(this GreatestPredicate p) => WithMemberAdditionalContext(() => new GreatestPredicate(
      p.Origin.Clone(),
      p.NameNode.Clone(),
      p.HasStaticKeyword, p.IsOpaque,
      p.TypeOfK, p.TypeArgs.ConvertAll<TypeParameter>(Clone),
      p.Ins.ConvertAll(AsProtected),
      p.Result?.AsProtected(),
      p.Req.ConvertAll(static e => e.AsProtected()), p.Reads.AsProtected(), p.Ens.ConvertAll(static e => e.AsProtected(AEKind.Ensures)),
      p.Body!.AsProtected(),
      p.Attributes.Clone(), p.SignatureEllipsis?.Clone()
    ));
    public static LeastPredicate AsProtected(this LeastPredicate p) => WithMemberAdditionalContext(() => new LeastPredicate(
      p.Origin.Clone(),
      p.NameNode.Clone(),
      p.HasStaticKeyword, p.IsOpaque,
      p.TypeOfK, p.TypeArgs.ConvertAll<TypeParameter>(Clone),
      p.Ins.ConvertAll(AsProtected),
      p.Result?.AsProtected()!,
      p.Req.ConvertAll(static e => e.AsProtected()), p.Reads.AsProtected(), p.Ens.ConvertAll(static e => e.AsProtected(AEKind.Ensures)),
      p.Body!.AsProtected(),
      p.Attributes.Clone()!, p.SignatureEllipsis?.Clone()!
    ));
    #endregion
    public static void Protect(this LiteralModuleDecl decl) {
      decl.ModuleDef.DefaultClass!.Protect();
      foreach (var module in decl.ModuleDef.PrefixNamedModules.Select(m => m.Module)) {
        module.Protect();
      }
      foreach (var sd in decl.ModuleDef.SourceDecls) {
        switch (sd) {
          case LiteralModuleDecl inner_lmd:
            inner_lmd.Protect();
            break;
          case ModuleExportDecl or AbstractModuleDecl or AliasModuleDecl:
            break; // nothing to be done on imports or exports
          case ModuleDecl:
            throw new UnreachableException();
          case IteratorDecl id:
            throw new NotImplementedException();
            break;
          case TopLevelDeclWithMembers wm when wm is (ClassDecl or TraitDecl or DatatypeDecl or NewtypeDecl or AbstractTypeDecl):
            wm.Protect();
            break;
          case SubsetTypeDecl tsd:
            throw new NotImplementedException();
            break;
          case ConcreteTypeSynonymDecl:
            break; // these are simply bare aliases
          default:
            throw new UnreachableException();
        }
      }
    }
    public static void Protect(this TopLevelDeclWithMembers decl) {
      decl.Members = decl.Members.ConvertAll(m => m is MethodOrFunction mof ? mof.WithProtections(new()) : m);
      decl.SetMembersBeforeResolution();
    }
  }
}
