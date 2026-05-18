#nullable enable
using Microsoft.Dafny;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Numerics;
using static Microsoft.Dafny.BoogieGenerator;

namespace DafnyCore.IncrementalCompilation;
public static class ProtectorFunctions {

  public static NameSegment getIdentityFromProtectFunctionCall(ApplySuffix call) => (call.Bindings.ArgumentBindings[0].Actual as NameSegment)!;
  public abstract class ProtectorFunction {
    public string Name => Function.Name;
    public abstract Function Function { get; }

    public sealed class NewProtect : ProtectorFunction {
      private static Function functionFrom(string name) {
        var typeVar = "T".ToTypeParameter();
        return ProtectorFunctionBase(
          typeArgs: [typeVar,],
          name: name,
          signature: ([
            ("x", typeVar).ToFormal(),
            ("name", StringType()).ToFormal(),
          ], new BoolType()),
          body: new LiteralExpr(
            origin: SourceOrigin.NoToken,
            value: true
          )
        );
      }
      public override Function Function { get; } = functionFrom("_protect");

      public ApplySuffix InvocationFrom(Expression expression) => new(expression.Origin, null, this.ToExprDotName(), [
        new(null, expression),
        new(null, new StringLiteralExpr(SourceOrigin.NoToken, expression.ToString(), false)),
      ], Token.NoToken);
      public ApplySuffix InvocationFrom(string varname) => new(SourceOrigin.NoToken, null, this.ToExprDotName(), [
        new(null, new NameSegment(SourceOrigin.NoToken, varname, null)),
        new(null, new StringLiteralExpr(SourceOrigin.NoToken, varname, false)),
      ], Token.NoToken);
    }
    public sealed class OldProtect : ProtectorFunction {
      private static Function functionFrom(string name) {
        var typeVar = "T".ToTypeParameter();
        return IdentityOf(
          typeArgs: [typeVar,],
          name: name,
          signature: (
            ("x", typeVar).ToFormal(), [
            ("name", StringType()).ToFormal(),
            ]
          )
        );
      }
      public override Function Function { get; } = functionFrom("_oldProtect");

      public ApplySuffix InvocationFrom(Expression expression) => new(expression.Origin, null, this.ToExprDotName(), [
        new(null, expression),
        new(null, new StringLiteralExpr(SourceOrigin.NoToken, expression.ToString(), false)),
      ], Token.NoToken);
    }
    public sealed class ProtectScope : ProtectorFunction {
      private static Function functionFrom(string name) {
        var typeVar = "T".ToTypeParameter();
        return ProtectorFunctionBase(
          typeArgs: [typeVar,],
          name: name,
          signature: ([
            ("x", typeVar).ToFormal(),
            ("name", StringType()).ToFormal(),
          ], new BoolType()),
          body: new LiteralExpr(
            origin: SourceOrigin.NoToken,
            value: true
          )
        );
      }
      public override Function Function { get; } = functionFrom("_protectScope");

      public ApplySuffix InvocationFrom(string varname) => new(SourceOrigin.NoToken, null, this.ToExprDotName(), [
        new(null, new NameSegment(SourceOrigin.NoToken, varname, null)),
        new(null, new StringLiteralExpr(SourceOrigin.NoToken, varname, false)),
      ], Token.NoToken);
    }
    public interface IChangeContextDependant {
      ApplySuffix InvocationFrom(Expression expression, Protector protector);
    }
    public sealed class ProtectToProve : ProtectorFunction, IChangeContextDependant {
      private static Function functionFrom(string name) {
        var typeVar = "T".ToTypeParameter();
        return IdentityOf(
          typeArgs: [typeVar,],
          name: name,
          signature: (
            ("x", typeVar).ToFormal(), [
            ("name", StringType()).ToFormal(),
              ("scope", new SeqType(new BoolType())).ToFormal(),
              ("id", new IntType()).ToFormal(),
            ]
          )
        );
      }
      public override Function Function { get; } = functionFrom("_protectToProve");

      public ApplySuffix InvocationFrom(Expression expression, Protector protector) =>
        new ProtectToProveApplySuffix(expression, protector);
    }
    public sealed class ProtectToProveInv : ProtectorFunction, IChangeContextDependant {
      private static Function functionFrom(string name) {
        return IdentityOf(
          typeArgs: [],
          name: name,
          signature: (
            ("x", new BoolType()).ToFormal(), [
            ("name", StringType()).ToFormal(),
              ("scope", new SeqType(new BoolType())).ToFormal(),
              ("id", new IntType()).ToFormal(),
            ]
          )
        );
      }
      public override Function Function { get; } = functionFrom("_protectToProveInv");

      public ApplySuffix InvocationFrom(Expression expression, Protector protector) =>
        new ProtectToProveInvApplySuffix(expression, protector);
    }
    public sealed class ProtectToProveImmediate : ProtectorFunction {
      private static Function functionFrom(string name) {
        var typeVar = "T".ToTypeParameter();
        return IdentityOf(
          typeArgs: [typeVar,],
          name: name,
          signature: (
            ("x", typeVar).ToFormal(), [
            ("name", StringType()).ToFormal(),
              ("scope", new SeqType(new BoolType())).ToFormal(),
              ("id", new IntType()).ToFormal(),
            ]
          )
        );
      }
      public override Function Function { get; } = functionFrom("_protectToProveImmediate");

      public ApplySuffix InvocationFrom(Expression expression, Protector protector, BigInteger entryPoint) =>
        new ProtectToProveImmediateApplySuffix(expression, protector, entryPoint);
    }
    public sealed class ProtectToProveWF : ProtectorFunction {
      private static Function functionFrom(string name) {
        return IdentityOf(
          typeArgs: [],
          name: name,
          signature: (
            ("x", new BoolType()).ToFormal(), [
            ("name", StringType()).ToFormal(),
              ("scope", new SeqType(new BoolType())).ToFormal(),
              ("id", new IntType()).ToFormal(),
            ]
          )
        );
      }
      public override Function Function { get; } = functionFrom("_protectToProveWF");

      public Microsoft.Boogie.Expr InvocationFrom(Microsoft.Boogie.Expr wfCheck, Expression causeOfWfCheck, ProtectToProveApplySuffix protectToProveExpr, ExpressionTranslator etran, DafnyOptions options, ProofObligationDescription desc) =>
        InvocationFrom(wfCheck, etran.GetToken(causeOfWfCheck), protectToProveExpr, etran, options, desc);
      public Microsoft.Boogie.Expr InvocationFrom(Microsoft.Boogie.Expr wfCheck, IOrigin tok, ProtectToProveApplySuffix protectToProveExpr, ExpressionTranslator etran, DafnyOptions options, ProofObligationDescription desc) {
        Contract.Requires(wfCheck.Type == Microsoft.Boogie.Type.Bool);
        var resolvedProtectToProveExpr = (protectToProveExpr.ResolvedExpression as FunctionCallExpr)!;
        List<(Microsoft.Boogie.Expr e, Type dt)> args = [
          (wfCheck, Type.Bool),
          (etran.TranslateString(desc.GetAssertedExpr(options).ToString()), resolvedProtectToProveExpr.Args[1].Type),
          .. resolvedProtectToProveExpr.Args.Skip(2).Select(a => (etran.TrExpr(a.Resolved), a.Type))];
        return etran.BoogieGenerator.CondApplyUnbox(tok, new Microsoft.Boogie.NAryExpr(tok, new Microsoft.Boogie.FunctionCall(new Microsoft.Boogie.IdentifierExpr(tok, Function.FullSanitizedName, Microsoft.Boogie.Type.Bool)), [
          etran.BoogieGenerator.GetRevealConstant(Function),
          .. args.Zip(Function.Ins.Select(i => i.Type)).Select(a => etran.BoogieGenerator.AdaptBoxing(tok, a.First.e, a.First.dt, a.Second)),
        ]), Function.ResultType, Type.Bool);
      }
    }
    public sealed class ProtectFinishedInv : ProtectorFunction {
      private static Function functionFrom(string name) {
        return IdentityOf(
          typeArgs: [],
          name: name,
          signature: (
            ("x", new BoolType()).ToFormal(), [
            ("name", StringType()).ToFormal(),
              ("scope", new SeqType(new BoolType())).ToFormal(),
              ("id", new IntType()).ToFormal(),
            ]
          )
        );
      }
      public override Function Function { get; } = functionFrom("_protectFinishedInv");

      public Microsoft.Boogie.Expr InvocationFrom(ProtectToProveApplySuffix protectToProveInvExpr, ExpressionTranslator etran) {
        var resolvedProtectToProveInvExpr = (protectToProveInvExpr.ResolvedExpression as FunctionCallExpr)!;
        var tok = resolvedProtectToProveInvExpr.Origin;
        List<(Microsoft.Boogie.Expr e, Type dt)> args = [.. resolvedProtectToProveInvExpr.Args.Select(a => (etran.TrExpr(a.Resolved), a.Type))];
        return etran.BoogieGenerator.CondApplyUnbox(tok, new Microsoft.Boogie.NAryExpr(tok, new Microsoft.Boogie.FunctionCall(new Microsoft.Boogie.IdentifierExpr(tok, Function.FullSanitizedName, Microsoft.Boogie.Type.Bool)), [
          etran.BoogieGenerator.GetRevealConstant(Function),
          .. args.Zip(Function.Ins.Select(i => i.Type)).Select(a => etran.BoogieGenerator.AdaptBoxing(tok, a.First.e, a.First.dt, a.Second)),
        ]), Function.ResultType, Type.Bool);
      }
    }
  }

  public static ProtectorFunction.NewProtect NewProtect { get; } = new();
  public static ProtectorFunction.OldProtect OldProtect { get; } = new();
  public static ProtectorFunction.ProtectScope ProtectScope { get; } = new();
  public static ProtectorFunction.ProtectToProve ProtectToProve { get; } = new();
  public static ProtectorFunction.ProtectToProveInv ProtectToProveInv { get; } = new();
  public static ProtectorFunction.ProtectToProveImmediate ProtectToProveImmediate { get; } = new();
  public static ProtectorFunction.ProtectToProveWF ProtectToProveWF { get; } = new();
  public static ProtectorFunction.ProtectFinishedInv ProtectFinishedInv { get; } = new();
  public static ICollection<ProtectorFunction> All { get; } = [NewProtect, OldProtect, ProtectScope, ProtectToProve, ProtectToProveInv, ProtectToProveImmediate, ProtectToProveWF, ProtectFinishedInv];
  public static string ContainingModuleName { get; } = "_protectors";

  private static Function ProtectorFunctionBase(List<TypeParameter> typeArgs, string name, (List<Formal> args, Microsoft.Dafny.Type result) signature, Expression body) => new(
    origin: new Token(),
    // can't use SourceOrigin.NoToken because ref. eq. to it
    // is used to ensure that DefaultModuleDefinitions are verified;
    // I do NOT like that piece of code (:
    nameNode: new(name),
    hasStaticKeyword: false,
    isGhost: false,
    isOpaque: true,
    typeArgs: typeArgs,
    ins: signature.args,
    result: null,
    resultType: signature.result,
    req: [],
    reads: new(),
    ens: [],
    decreases: new(),
    body: body,
    byMethodTok: null, byMethodBody: null,
    attributes: new(
      name: "auto_generated", args: [],
      prev: null
    ),
    signatureEllipsis: null
  );

  private static Function IdentityOf(List<TypeParameter> typeArgs, string name, Formal identity) =>
    IdentityOf(typeArgs, name, ([], identity, []));
  private static Function IdentityOf(List<TypeParameter> typeArgs, string name, (List<Formal> beforeIdentity, Formal identity) signature) =>
    IdentityOf(typeArgs, name, (signature.beforeIdentity, signature.identity, []));
  private static Function IdentityOf(List<TypeParameter> typeArgs, string name, (Formal identity, List<Formal> afterIdentity) signature) =>
    IdentityOf(typeArgs, name, ([], signature.identity, signature.afterIdentity));
  private static Function IdentityOf(List<TypeParameter> typeArgs, string name, (List<Formal> beforeIdentity, Formal identity, List<Formal> afterIdentity) signature) =>
    ProtectorFunctionBase(typeArgs, name, ([.. signature.beforeIdentity, signature.identity, .. signature.afterIdentity,], signature.identity.Type), signature.identity.Name.ToFunctionBody());

  private static Type StringType() => new UserDefinedType(origin: SourceOrigin.NoToken, name: "string", optTypeArgs: null);

  private static Expression ToFunctionBody(this string name) => new NameSegment(SourceOrigin.NoToken, name: name, null);
  private static Formal ToFormal(this (string Name, TypeParameter TypeParameter) t, bool isGhost = false, Expression? defaultValue = null, bool isNameOnly = false) =>
    (t.Name, t.TypeParameter.ToType()).ToFormal(isGhost, defaultValue, isNameOnly);

  private static Formal ToFormal(this (string Name, Microsoft.Dafny.Type Type) t, bool isGhost = false, Expression? defaultValue = null, bool isNameOnly = false) => new(
    origin: SourceOrigin.NoToken,
    nameNode: t.Name.ToNameNodeWithVirtualToken(),
    syntacticType: t.Type,
    inParam: true,
    isGhost: isGhost,
    defaultValue: defaultValue,
    attributes: null,
    isOld: false,
    isNameOnly: isNameOnly,
    isOlder: false,
    nameForCompilation: null
  );
  private static TypeParameter ToTypeParameter(this string name) => new(
    origin: SourceOrigin.NoToken,
    nameNode: new(name),
    varianceSyntax: TPVarianceSyntax.NonVariant_Strict,
    characteristics: TypeParameterCharacteristics.Default(),
    typeBounds: [],
    attributes: null
  );
  private static Type ToType(this TypeParameter tp) => new UserDefinedType(tp);

  private static NameSegment ToNameSegment(this string name) => new(SourceOrigin.NoToken, name, null);
  public static ExprDotName ToExprDotName(this ProtectorFunction pf) => new(SourceOrigin.NoToken, ContainingModuleName.ToNameSegment(), pf.Name.ToNameNodeWithVirtualToken(), null);
}
