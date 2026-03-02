#nullable enable
using Microsoft.Dafny;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using static DafnyCore.IncrementalCompilation.ProtectorFunctions;

namespace DafnyCore.IncrementalCompilation {
  internal static class ProtectedExtension { // FOR THE LOVE OF GOD LET'S KEEP THIS BEFORE THE RESOLUTION
    private static Expression? CustomAssertExprProtection(AssertStmt a) {
      var attributeName = ModuleSplitter.AttributeName;
      if (Attributes.Contains(a.Attributes, attributeName)) {
        //Console.WriteLine("Protecting to prove assertion " + a.Expr.ToString());
        return a.Expr.WrappedWith(ProtectToProve);
      }
      if (Attributes.Find(a.Attributes, attributeName + "_now") is { } attr) {
        if (attr is { Args: [] }) { attr.Args.Add(new LiteralExpr(SourceOrigin.NoToken, 0)); } // temporary bcs frontend doesn't use {:ipm_now 0} yet
        if (attr is not { Args: [var arg] }) { throw new Exception($"the {{:{attributeName}_now}} attribute requires an argument"); }
        if (arg is not LiteralExpr { Value: BigInteger entryPoint }) { throw new Exception($"{{:{attributeName}_now}}'s argument needs to be a natural number"); }
        return a.Expr.WrappedWith(ProtectToProveImmediate with { EntryPoint = entryPoint });
      }
      //Console.WriteLine($"assert statement: {a.Expr}");
      return null;
    }
    private static UnreachableException CanOnlyAppearDuringResolution<T>(T o) where T : notnull => new($"{o.GetType().Name} can only appear during resolution");
    private static Cloner cloner { get; } = new();
    private static IOrigin Clone(this IOrigin o) => cloner.Origin(o);
    [return: NotNullIfNotNull(nameof(a))] private static Attributes? Clone(this Attributes? a) => cloner.CloneAttributes(a);
    [return: NotNullIfNotNull(nameof(t))] private static Microsoft.Dafny.Type? Clone(this Microsoft.Dafny.Type? t) => cloner.CloneType(t);
    [return: NotNullIfNotNull(nameof(tp))] private static TypeParameter? Clone(this TypeParameter? tp) => cloner.CloneTypeParam(tp);
    [return: NotNullIfNotNull(nameof(t))] private static AttributedToken? Clone(this AttributedToken? t) => cloner.AttributedTok(t);
    [return: NotNullIfNotNull(nameof(e))] private static E? Clone<E>(this E? e) where E : Expression => cloner.CloneExpr(e) as E;
    private static Label Clone(this Label l) => l switch {
      AssertLabel al => al.Clone(),
      _ when l.GetType() == typeof(Label) => new(l.Tok.Clone(), l.Name),
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
          ImplicitFormal i => throw CanOnlyAppearDuringResolution(i),
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


    public static AttributedExpression AsProtected(this AttributedExpression e) => new(e.E.AsProtected(), e.Label?.Clone(), e.Attributes.Clone());
    public static AttributedExpression AsProtectedEns(this AttributedExpression e) => new(Attributes.Contains(e.Attributes, ModuleSplitter.AttributeName) ? e.E.WrappedWith(ProtectToProve) : e.E.AsProtected(), e.Label?.Clone(), e.Attributes.Clone());

    public static Specification<FrameExpression> AsProtected(this Specification<FrameExpression> spec) => new(spec.Expressions?.ConvertAll(AsProtected), spec.Attributes.Clone());
    public static Specification<Expression> AsProtected(this Specification<Expression> spec) => new(spec.Expressions?.ConvertAll(AsProtected), spec.Attributes.Clone());
    public static FrameExpression AsProtected(this FrameExpression e) => new(e.Origin.Clone(), e.OriginalExpression.AsProtected(), e.FieldName);

    #endregion

    #region Statement
    public static Statement AsProtected(this Statement s) => s switch {
      #region done
      NestedMatchStmt p => p.AsProtected(),
      ConcreteAssignStatement p => p.AsProtected(),
      SingleAssignStmt p => p.AsProtected(),
      VarDeclPattern p => p.AsProtected(),
      VarDeclStmt p => p.AsProtected(),
      BreakOrContinueStmt p => p.AsProtected(),
      ForallStmt p => p.AsProtected(),
      CallStmt p => throw CanOnlyAppearDuringResolution(p),
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
        #endregion
        AssertStmt pp => pp.AsProtected(),
        AssumeStmt pp => pp.AsProtected(),
        ExpectStmt pp => pp.AsProtected(),
        _ => throw new UnreachableException(),
      },
      #region todo
      BlockByProofStmt p => p.AsProtected(),
      CalcStmt p => p.AsProtected(),
      MatchStmt p => p.AsProtected(),
      SkeletonStatement p => p.AsProtected(),
      LabeledStatement p => p switch {
        AlternativeStmt pp => pp.AsProtected(),
        LoopStmt pp => pp switch {
          AlternativeLoopStmt ppp => ppp.AsProtected(),
          OneBodyLoopStmt ppp => ppp switch {
            ForLoopStmt pppp => pppp.AsProtected(),
            WhileStmt pppp => pppp switch {
              RefinedWhileStmt ppppp => ppppp.AsProtected(),
              _ => throw new UnreachableException(),
            },
            _ => throw new UnreachableException(),
          },
          _ => throw new UnreachableException(),
        },
        IfStmt pp => pp.AsProtected(),
        BlockLikeStmt pp => pp.AsProtected(),
        _ => throw new UnreachableException(),
      },
      #endregion
      _ => throw new UnreachableException(),
    };
    public static NestedMatchStmt AsProtected(this NestedMatchStmt s) => new(
      s.Origin.Clone(), s.Source.AsProtected(), s.Cases.ConvertAll(c => new NestedMatchCaseStmt(c.Origin.Clone(), c.Pat.AsProtected(), c.Body.ConvertAll(AsProtected), c.Attributes.Clone())), s.UsesOptionalBraces, s.Attributes.Clone()
    );
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
    public static ForallStmt AsProtected(this ForallStmt s) => new(s.Origin.Clone(), s.BoundVars.ConvertAll(AsProtected), s.Attributes.Clone(), s.Range.AsProtected(), s.Ens.ConvertAll(AsProtected), s.Body.AsProtected());
    public static PrintStmt AsProtected(this PrintStmt s) => new(s.Origin.Clone(), s.Args.ConvertAll(AsProtected), s.Attributes.Clone());
    public static ReturnStmt AsProtected(this ReturnStmt s) => new(s.Origin.Clone(), s.Rhss?.ConvertAll(AsProtected), s.Attributes.Clone()) { ReverifyPost = s.ReverifyPost }; // ReverifyPost is done in the cloner but not necessary, since it can only be assigned after the protections are done
    public static YieldStmt AsProtected(this YieldStmt s) => new(s.Origin.Clone(), s.Rhss?.ConvertAll(AsProtected));
    public static TryRecoverStatement AsProtected(this TryRecoverStatement s) {
      Console.WriteLine("TryRecoverStatement shouldn't be present in the AST");
      return new(s.TryBody.AsProtected(), s.HaltMessageVar.AsProtected(), s.RecoverBody.AsProtected());
    }
    public static HideRevealStmt AsProtected(this HideRevealStmt s) => new(s.Origin.Clone(), s.Exprs?.ConvertAll(AsProtected), s.Mode, s.Attributes.Clone());
    public static ModifyStmt AsProtected(this ModifyStmt s) => new(s.Origin.Clone(), s.Mod.Expressions?.ConvertAll(AsProtected), s.Mod.Attributes.Clone(), s.Body.AsProtected());
    public static AssertStmt AsProtected(this AssertStmt s) => new(s.Origin.Clone(), CustomAssertExprProtection(s) ?? s.Expr.AsProtected(), s.Label?.Clone(), s.Attributes.Clone());
    public static AssumeStmt AsProtected(this AssumeStmt s) => new(s.Origin.Clone(), s.Expr.AsProtected(), s.Attributes.Clone());
    public static ExpectStmt AsProtected(this ExpectStmt s) => new(s.Origin.Clone(), s.Expr.AsProtected(), s.Message.Clone(), s.Attributes.Clone());
    // after many other things
    public static BlockLikeStmt AsProtected(this BlockLikeStmt s) => s switch {
      DividedBlockStmt db => db.AsProtected(),
      BlockStmt b => b switch {
        OpaqueBlock ob => ob.AsProtected(),
        _ when b.GetType() == typeof(BlockStmt) => b.AsProtected(),
        _ => throw new UnreachableException(),
      },
      _ => throw new UnreachableException(),
    };
    public static DividedBlockStmt AsProtected(this DividedBlockStmt s) => new(s.Origin.Clone(), s.BodyInit.ConvertAll(AsProtected), s.SeparatorTok?.Clone(), s.BodyProper.ConvertAll(AsProtected), s.Labels.ConvertAllWhere(l => (l.Name is not null, new Label(l.Tok.Clone(), l.Name))), s.Attributes.Clone());
    public static BlockStmt AsProtected(this BlockStmt s) => new(s.Origin.Clone(), s.Body.ConvertAll(AsProtected), s.Labels.ConvertAllWhere(l => (l.Name is not null, new Label(l.Tok.Clone(), l.Name))), s.Attributes.Clone());
    public static OpaqueBlock AsProtected(this OpaqueBlock s) => null!;

    #endregion

    #region Expression
    public static Expression AsProtected(this Expression e) => e switch {
      ApplyExpr p => p.AsProtected(),
      FunctionCallExpr p => p.AsProtected(),
      MemberSelectExpr p => p.AsProtected(),
      MultiSelectExpr p => p.AsProtected(),
      SeqSelectExpr p => p.AsProtected(),
      ThisExpr p => p switch {
        ImplicitThisExpr pp => pp switch {
          ImplicitThisExprConstructorCall ppp => ppp.AsProtected(),
          _ => pp.AsProtected(),
        },
        _ => p.AsProtected(),
      },
      DisplayExpression p => p switch {
        SeqDisplayExpr pp => pp.AsProtected(),
        SetDisplayExpr pp => pp.AsProtected(),
        MultiSetDisplayExpr pp => pp.AsProtected(),
        _ => p.AsProtected(),
      },
      MapDisplayExpr p => p.AsProtected(),
      MultiSetFormingExpr p => p.AsProtected(),
      SeqConstructionExpr p => p.AsProtected(),
      SeqUpdateExpr p => p.AsProtected(),
      ComprehensionExpr p => p switch {
        LambdaExpr pp => pp.AsProtected(),
        MapComprehension pp => pp.AsProtected(),
        QuantifierExpr pp => pp switch {
          ForallExpr ppp => ppp.AsProtected(),
          ExistsExpr ppp => ppp.AsProtected(),
          _ => pp.AsProtected(),
        },
        SetComprehension pp => pp.AsProtected(),
        _ => p.AsProtected(),
      },
      ITEExpr p => p.AsProtected(),
      NestedMatchExpr p => p.AsProtected(),
      TernaryExpr p => p.AsProtected(),
      DatatypeValue p => p.AsProtected(),
      FieldLocation p => p.AsProtected(),
      IndexFieldLocation p => p.AsProtected(),
      LocalsObjectExpression p => p.AsProtected(),
      OldExpr p => p.AsProtected(),
      UnchangedExpr p => p.AsProtected(),
      WildcardExpr p => p.AsProtected(),
      BinaryExpr p => p.AsProtected(),
      DecreasesToExpr p => p.AsProtected(),
      UnaryExpr p => p switch {
        UnaryOpExpr pp => pp switch {
          FreshExpr ppp => ppp.AsProtected(),
          _ => pp.AsProtected(),
        },
        TypeUnaryExpr pp => pp switch {
          ConversionExpr ppp => ppp.AsProtected(),
          TypeTestExpr ppp => ppp.AsProtected(),
          _ => pp.AsProtected(),
        },
        _ => p.AsProtected(),
      },
      BoxingCastExpr p => p.AsProtected(),
      UnboxingCastExpr p => p.AsProtected(),
      IdentifierExpr p => p switch {
        AutoGhostIdentifierExpr pp => pp.AsProtected(),
        ImplicitIdentifierExpr pp => pp.AsProtected(),
        _ => p.AsProtected(),
      },
      LetExpr p => p switch {
        BoogieGenerator.SubstLetExpr pp => pp.AsProtected(),
        _ => p.AsProtected(),
      },
      ResolverIdentifierExpr p => p.AsProtected(),
      ConcreteSyntaxExpression p => p switch {
        NameSegment pp => pp.AsProtected(),
        SuffixExpr pp => pp switch {
          ApplySuffix ppp => ppp switch {
            ProtectToProveApplySuffix pppp => pppp.AsProtected(),
            _ => ppp.AsProtected(),
          },
          ExprDotName ppp => ppp.AsProtected(),
          FieldLocationExpression ppp => ppp.AsProtected(),
          IndexFieldLocationExpression ppp => ppp.AsProtected(),
          _ => pp.AsProtected(),
        },
        DatatypeUpdateExpr pp => pp.AsProtected(),
        ChainingExpression pp => pp.AsProtected(),
        ParensExpression pp => pp switch {
          AutoGeneratedExpression ppp => ppp.AsProtected(),
          _ => pp.AsProtected(),
        },
        LetOrFailExpr pp => pp.AsProtected(),
        DefaultValueExpression pp => pp switch { // geniunely unreachable???
          DefaultValueExpressionType ppp => ppp.AsProtected(),
          DefaultValueExpressionPreType ppp => ppp.AsProtected(),
          _ => pp.AsProtected(),
        },
        NegationExpression pp => pp.AsProtected(),
        _ => p.AsProtected(),
      },
      LiteralExpr p => p switch {
        StaticReceiverExpr pp => pp.AsProtected(),
        CharLiteralExpr pp => pp.AsProtected(),
        StringLiteralExpr pp => pp.AsProtected(),
        _ => p.AsProtected(),
      },
      StmtExpr p => p.AsProtected(),
      MatchExpr p => p.AsProtected(),
      BoogieGenerator.BoogieWrapper p => p.AsProtected(),
      BoogieGenerator.BoogieFunctionCall p => p.AsProtected(),
      _ => throw new UnreachableException(),
    };
    private static T NotImplemented<T>(T e) where T : notnull {
      Console.WriteLine($"Dafny IPM: {e.GetType().Name} not yet implemented.");
      return e;
    }
    public static StaticReceiverExpr AsProtected(this StaticReceiverExpr e) => e.Clone();
    public static LiteralExpr AsProtected(this LiteralExpr e) => e.Clone();
    public static ProtectToProveApplySuffix AsProtected(this ProtectToProveApplySuffix e) => e; // no need to make a clone, since they're another kind of protection that also clones its original
    public static ApplySuffix AsProtected(this ThisExpr e) => e.Clone().WrappedWith(ProtectorFunctions.Protect);
    public static ApplySuffix AsProtected(this IdentifierExpr e) => e.Clone().WrappedWith(ProtectorFunctions.Protect);
    public static ApplySuffix AsProtected(this DatatypeValue e) => e.Clone().WrappedWith(ProtectorFunctions.Protect);
    public static ApplySuffix AsProtected(this NameSegment e) => e.Clone().WrappedWith(ProtectorFunctions.Protect);
    public static UnaryOpExpr AsProtected(this UnaryOpExpr e) {
      // `UnaryOpExpr.Opcode.Lit` used in translation, this method is called before translation
      Contract.Assert(e.Op is not UnaryOpExpr.Opcode.Lit);
      Contract.Assert(e.Op is UnaryOpExpr.Opcode.Not or
        UnaryOpExpr.Opcode.Cardinality or
        UnaryOpExpr.Opcode.Allocated or
        UnaryOpExpr.Opcode.Assigned || e is FreshExpr { Op: UnaryOpExpr.Opcode.Fresh });
      return new(e.Origin.Clone(), e.Op, e.E.AsProtected());
    }
    public static SeqSelectExpr AsProtected(this SeqSelectExpr e) => new(
      e.Origin.Clone(), e.SelectOne, e.Seq.AsProtected(), e.E0.ApplyIfNotNull(AsProtected), e.E1.ApplyIfNotNull(AsProtected), e.CloseParen
    );
    public static SeqDisplayExpr AsProtected(this SeqDisplayExpr e) => new(e.Origin.Clone(), e.Elements.ConvertAll(AsProtected));
    public static SetDisplayExpr AsProtected(this SetDisplayExpr e) => new(e.Origin.Clone(), e.Finite, e.Elements.ConvertAll(AsProtected));
    public static MultiSetDisplayExpr AsProtected(this MultiSetDisplayExpr e) => new(e.Origin.Clone(), e.Elements.ConvertAll(AsProtected));
    public static MapDisplayExpr AsProtected(this MapDisplayExpr e) => new(
      e.Origin.Clone(), e.Finite, e.Elements.ConvertAll(entry => new MapDisplayEntry(entry.A.AsProtected(), entry.B.AsProtected()))
    );
    public static SeqConstructionExpr AsProtected(this SeqConstructionExpr e) => new(
      e.Origin.Clone(), e.ExplicitElementType.ApplyIfNotNull(cloner.CloneType), e.N.AsProtected(), e.Initializer.AsProtected()
    );
    public static SeqUpdateExpr AsProtected(this SeqUpdateExpr e) => new(e.Origin.Clone(), e.Seq.AsProtected(), e.Index.AsProtected(), e.Value.AsProtected());
    public static BinaryExpr AsProtected(this BinaryExpr e) => new(e.Origin.Clone(), e.Op, e.E0.AsProtected(), e.E1.AsProtected());
    public static ChainingExpression AsProtected(this ChainingExpression e) {
      if (e.PrefixLimits.Any(l => l is not null)) { // why?
        return NotImplemented(e);
      }
      return new(e.Origin.Clone(), e.Operands.ConvertAll(AsProtected), [.. e.Operators], e.OperatorLocs.ConvertAll(Clone), [.. e.PrefixLimits]);
    }
    public static ParensExpression AsProtected(this ParensExpression e) => new(e.Origin.Clone(), e.E.AsProtected());
    public static DefaultValueExpression AsProtected(this DefaultValueExpression e) {
      Contract.Assert(e.WasResolved());
      Contract.Assert(e.Resolved is not null);
      return NotImplemented(e);
    }
    public static LetExpr AsProtected(this LetExpr e) => new(
      e.Origin.Clone(), e.LHSs.ConvertAll(cloner.CloneCasePattern), e.RHSs.ConvertAll(AsProtected), e.Body.AsProtected(), e.Exact, e.Attributes.Clone()
    );
    public static ApplySuffix AsProtected(this ApplySuffix e) => new(
      e.Origin.Clone(), e.AtTok.ApplyIfNotNull(Clone), e.Lhs.AsProtected(), e.Bindings.ArgumentBindings.ConvertAll(ab => new ActualBinding(ab.FormalParameterName, ab.Actual.AsProtected(), ab.IsGhost)), e.CloseParen
    );
    public static StmtExpr AsProtected(this StmtExpr e) {
      Statement ReplaceExprInStatement(Statement s) => s switch { // TODO: complete everything here, only PredicateStmt is handled properly
        PredicateStmt stmt => stmt switch {
          AssertStmt assert => new AssertStmt(assert.Origin.Clone(), assert.Expr.AsProtected(), assert.Label is null ? null : new AssertLabel(assert.Label.Tok, assert.Label.Name), assert.Attributes.Clone()),
          AssumeStmt assume => new AssumeStmt(assume.Origin.Clone(), assume.Expr.AsProtected(), assume.Attributes.Clone()),
          ExpectStmt expect => new ExpectStmt(expect.Origin.Clone(), expect.Expr.AsProtected(), cloner.CloneExpr(expect.Message), expect.Attributes.Clone()), // Protect `expect.Message`?
          _ => throw new Cce.UnreachableException(),
        },
        CalcStmt stmt => stmt,
        ForallStmt stmt => stmt,     // one could wrap a `forall` expression around the `ensures` clause, but "true" is conservative and much simpler :)
        HideRevealStmt stmt => stmt, // one could use the definition axiom or the referenced labeled assertions, but "true" is conservative and much simpler :)
        AssignStatement stmt => stmt,// one could use the postcondition of the method, suitably instantiated, but "true" is conservative and much simpler :)
        BlockByProofStmt stmt => stmt,
        _ => throw new Cce.UnreachableException(),  // unexpected statement
      };
      return new(e.Origin.Clone(), ReplaceExprInStatement(e.S), e.E.AsProtected());
    }
    #endregion

    #region Method, Constructor and Function
    private static Name ToProtectedName(this Name name) => $"{ModuleSplitter.Name}_{name}".ToNameNodeWithVirtualToken();
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

    public static Constructor AsProtected(this Constructor c) => new(
      c.Origin.Clone(),
      c.NameNode.ToProtectedName(),
      c.IsGhost,
      c.TypeArgs.ConvertAll<TypeParameter>(Clone),
      c.Ins.ConvertAll(AsProtected),
      c.Req.ConvertAll(AsProtected), c.Reads.AsProtected(), c.Mod.AsProtected(), c.Ens.ConvertAll(AsProtectedEns),
      c.Decreases.AsProtected(),
      c.Body?.AsProtected(),
      c.Attributes.Clone(), c.SignatureEllipsis?.Clone()
    );
    public static Method AsProtected(this Method m) => m switch {
      Lemma l => l.AsProtected(),
      TwoStateLemma l => l.AsProtected(),
      PrefixLemma l => throw CanOnlyAppearDuringResolution(l),
      ExtremeLemma l => l.AsProtected(),
      _ when m.GetType() == typeof(Method) => new(
        m.Origin.Clone(),
        m.NameNode.ToProtectedName(),
        m.Attributes.Clone(),
        m.HasStaticKeyword, m.IsGhost,
        m.TypeArgs.ConvertAll<TypeParameter>(Clone),
        m.Ins.ConvertAll(AsProtected),
        m.Req.ConvertAll(AsProtected), m.Ens.ConvertAll(AsProtectedEns),
        m.Reads.AsProtected(), m.Decreases.AsProtected(),
        m.Outs.ConvertAll(AsProtected), m.Mod.AsProtected(),
        m.Body?.AsProtected(), m.SignatureEllipsis?.Clone(),
        m.IsByMethod
      ),
      _ => throw new UnreachableException(),
    };
    public static Lemma AsProtected(this Lemma l) => new(
      l.Origin.Clone(),
      l.NameNode.ToProtectedName(),
      l.HasStaticKeyword,
      l.TypeArgs.ConvertAll<TypeParameter>(Clone),
      l.Ins.ConvertAll(AsProtected), l.Outs.ConvertAll(AsProtected),
      l.Req.ConvertAll(AsProtected), l.Reads.AsProtected(), l.Mod.AsProtected(), l.Ens.ConvertAll(AsProtectedEns),
      l.Decreases.AsProtected(), l.Body!.AsProtected(), l.Attributes.Clone(), l.SignatureEllipsis?.Clone()
    );
    public static TwoStateLemma AsProtected(this TwoStateLemma l) => new(
      l.Origin.Clone(),
      l.NameNode.ToProtectedName(),
      l.HasStaticKeyword,
      l.TypeArgs.ConvertAll<TypeParameter>(Clone),
      l.Ins.ConvertAll(AsProtected), l.Outs.ConvertAll(AsProtected),
      l.Req.ConvertAll(AsProtected), l.Reads.AsProtected(), l.Mod.AsProtected(), l.Ens.ConvertAll(AsProtectedEns),
      l.Decreases.AsProtected(), l.Body!.AsProtected(), l.Attributes.Clone(), l.SignatureEllipsis?.Clone()
    );
    public static ExtremeLemma AsProtected(this ExtremeLemma l) => l switch {
      GreatestLemma gl => gl.AsProtected(),
      LeastLemma ll => ll.AsProtected(),
      _ => throw new UnreachableException(),
    };
    public static GreatestLemma AsProtected(this GreatestLemma l) => new(
      l.Origin.Clone(),
      l.NameNode.ToProtectedName(),
      l.HasStaticKeyword,
      l.TypeOfK, l.TypeArgs.ConvertAll<TypeParameter>(Clone),
      l.Ins.ConvertAll(AsProtected), l.Outs.ConvertAll(AsProtected),
      l.Req.ConvertAll(AsProtected), l.Reads.AsProtected(), l.Mod.AsProtected(), l.Ens.ConvertAll(AsProtectedEns),
      l.Decreases.AsProtected(), l.Body!.AsProtected(), l.Attributes.Clone(), l.SignatureEllipsis?.Clone()
    );
    public static LeastLemma AsProtected(this LeastLemma l) => new(
      l.Origin.Clone(),
      l.NameNode.ToProtectedName(),
      l.HasStaticKeyword,
      l.TypeOfK, l.TypeArgs.ConvertAll<TypeParameter>(Clone),
      l.Ins.ConvertAll(AsProtected), l.Outs.ConvertAll(AsProtected),
      l.Req.ConvertAll(AsProtected), l.Reads.AsProtected(), l.Mod.AsProtected(), l.Ens.ConvertAll(AsProtectedEns),
      l.Decreases.AsProtected(), l.Body!.AsProtected(), l.Attributes.Clone(), l.SignatureEllipsis?.Clone()
    );

    public static Function AsProtected(this Function f) => f switch {
      Predicate p => p.AsProtected(),
      TwoStateFunction tsf => tsf.AsProtected(),
      PrefixPredicate pp => throw CanOnlyAppearDuringResolution(pp),
      SpecialFunction sf => throw CanOnlyAppearDuringResolution(sf), // during default module resolution, still after the point where this would happen
      ExtremePredicate ep => ep.AsProtected(),
      _ when f.GetType() == typeof(Function) => new(
        f.Origin.Clone(),
        f.NameNode.ToProtectedName(),
        f.HasStaticKeyword, f.IsGhost, f.IsOpaque,
        f.TypeArgs.ConvertAll<TypeParameter>(Clone),
        f.Ins.ConvertAll(AsProtected),
        f.Result?.AsProtected(), f.ResultType.Clone(),
        f.Req.ConvertAll(AsProtected), f.Reads.AsProtected(), f.Ens.ConvertAll(AsProtectedEns), f.Decreases.AsProtected(),
        f.Body?.AsProtected(),
        f.ByMethodTok?.Clone(), f.ByMethodBody?.AsProtected(),
        f.Attributes.Clone(), f.SignatureEllipsis?.Clone()
      ),
      _ => throw new UnreachableException(),
    };
    public static Predicate AsProtected(this Predicate p) => new(
      p.Origin.Clone(),
      p.NameNode.ToProtectedName(),
      p.HasStaticKeyword, p.IsGhost, p.IsOpaque,
      p.TypeArgs.ConvertAll<TypeParameter>(Clone),
      p.Ins.ConvertAll(AsProtected),
      p.Result?.AsProtected(),
      p.Req.ConvertAll(AsProtected), p.Reads.AsProtected(), p.Ens.ConvertAll(AsProtectedEns), p.Decreases.AsProtected(),
      p.Body?.AsProtected(), p.BodyOrigin,
      p.ByMethodTok?.Clone(), p.ByMethodBody?.AsProtected(),
      p.Attributes.Clone(), p.SignatureEllipsis?.Clone()
    );
    public static TwoStateFunction AsProtected(this TwoStateFunction f) => f switch {
      TwoStatePredicate p => p.AsProtected(),
      _ when f.GetType() == typeof(TwoStateFunction) => new TwoStateFunction(
        f.Origin.Clone(),
        f.NameNode.ToProtectedName(),
        f.HasStaticKeyword, f.IsOpaque,
        f.TypeArgs.ConvertAll<TypeParameter>(Clone),
        f.Ins.ConvertAll(AsProtected),
        f.Result?.AsProtected(), f.ResultType.Clone(),
        f.Req.ConvertAll(AsProtected), f.Reads.AsProtected(), f.Ens.ConvertAll(AsProtectedEns), f.Decreases.AsProtected(),
        f.Body?.AsProtected(),
        f.Attributes.Clone(), f.SignatureEllipsis?.Clone()
      ),
      _ => throw new UnreachableException(),
    };
    public static TwoStatePredicate AsProtected(this TwoStatePredicate p) => new(
      p.Origin.Clone(),
      p.NameNode.ToProtectedName(),
      p.HasStaticKeyword, p.IsOpaque,
      p.TypeArgs.ConvertAll<TypeParameter>(Clone),
      p.Ins.ConvertAll(AsProtected),
      p.Result?.AsProtected(),
      p.Req.ConvertAll(AsProtected), p.Reads.AsProtected(), p.Ens.ConvertAll(AsProtectedEns), p.Decreases.AsProtected(),
      p.Body?.AsProtected(),
      p.Attributes.Clone(), p.SignatureEllipsis?.Clone()
    );
    public static ExtremePredicate AsProtected(this ExtremePredicate p) => p switch {
      GreatestPredicate gp => gp.AsProtected(),
      LeastPredicate lp => lp.AsProtected(),
      _ => throw new UnreachableException(),
    };
    public static GreatestPredicate AsProtected(this GreatestPredicate p) => new(
      p.Origin.Clone(),
      p.NameNode.ToProtectedName(),
      p.HasStaticKeyword, p.IsOpaque,
      p.TypeOfK, p.TypeArgs.ConvertAll<TypeParameter>(Clone),
      p.Ins.ConvertAll(AsProtected),
      p.Result?.AsProtected(),
      p.Req.ConvertAll(AsProtected), p.Reads.AsProtected(), p.Ens.ConvertAll(AsProtectedEns),
      p.Body!.AsProtected(),
      p.Attributes.Clone(), p.SignatureEllipsis?.Clone()
    );
    public static LeastPredicate AsProtected(this LeastPredicate p) => new(
      p.Origin.Clone(),
      p.NameNode.ToProtectedName(),
      p.HasStaticKeyword, p.IsOpaque,
      p.TypeOfK, p.TypeArgs.ConvertAll<TypeParameter>(Clone),
      p.Ins.ConvertAll(AsProtected),
      p.Result?.AsProtected(),
      p.Req.ConvertAll(AsProtected), p.Reads.AsProtected(), p.Ens.ConvertAll(AsProtectedEns),
      p.Body!.AsProtected(),
      p.Attributes.Clone(), p.SignatureEllipsis?.Clone()
    );
    #endregion
    public static void Protect(this LiteralModuleDecl decl) {
      decl.ModuleDef.DefaultClass!.Protect();
      foreach (var module in decl.ModuleDef.PrefixNamedModules.Select(m => m.Module)) {
        module.Protect();
      }
      foreach (var sd in decl.ModuleDef.SourceDecls) {
        switch (sd) {
          case LiteralModuleDecl inner_lmd: inner_lmd.Protect(); break;
          case ModuleExportDecl or AbstractModuleDecl or AliasModuleDecl: break; // nothing to be done on imports or exports
          case ModuleDecl: throw new UnreachableException();
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
      decl.Members = decl.Members.ConvertAll(m => m is MethodOrFunction mof ? mof.AsProtected() : m);
      decl.SetMembersBeforeResolution();
    }
  }
}
