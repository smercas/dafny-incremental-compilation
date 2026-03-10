#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using Microsoft.Dafny;
using System.Numerics;
using static DafnyCore.IncrementalCompilation.ProtectToProveApplySuffix;

namespace DafnyCore.IncrementalCompilation;
public static class ProtectorFunctions {
  static ProtectorFunctions() {
    Protect = new("_protect", null!); Protect = Protect with { Function = protectFunction(), };
    ProtectScope = new("_protectScope", null!); ProtectScope = ProtectScope with { Function = protectScopeFunction(), };
    ProtectToProve = new("_protectToProve", null!, null!); ProtectToProve = ProtectToProve with { Function = protectToProveFunction(), };
    ProtectToProveImmediate = new("_protectToProveImmediate", null!, BigInteger.Zero); ProtectToProveImmediate = ProtectToProveImmediate with { Function = protectToProveImmediateFunction(), };
    All = [Protect, ProtectScope, ProtectToProve, ProtectToProveImmediate];
  }

  private static Function protectFunction() {
    var typeVar = "T".ToTypeParameter();
    return IdentityOf(
      typeArgs: [typeVar,],
      name: Protect.Name,
      signature: (
        ("x", typeVar).ToFormal(), [
        ("name", StringType()).ToFormal(),
      ], typeVar.ToType())
    );
  }
  private static Function protectScopeFunction() {
    var typeVar = "T".ToTypeParameter();
    return ProtectorFunctionBase(
      typeArgs: [typeVar,],
      name: ProtectScope.Name,
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
  private static Function protectToProveFunction() {
    var typeVar = "T".ToTypeParameter();
    return IdentityOf(
      typeArgs: [typeVar,],
      name: ProtectToProve.Name,
      signature: (("x", typeVar).ToFormal(), [
        ("name", StringType()).ToFormal(),
        ("scope", new SeqType(new BoolType())).ToFormal(),
        ("id", new IntType()).ToFormal(),
      ], typeVar.ToType())
    );
  }
  private static Function protectToProveImmediateFunction() {
    var typeVar = "T".ToTypeParameter();
    return IdentityOf(
      typeArgs: [typeVar,],
      name: ProtectToProveImmediate.Name,
      signature: (("x", typeVar).ToFormal(), [
        ("name", StringType()).ToFormal(),
        ("scope", new SeqType(new BoolType())).ToFormal(),
        ("id", new IntType()).ToFormal(),
      ], typeVar.ToType())
    );
  }
  public static ApplySuffix WrappedWith(this Expression expression, ProtectorFunction protectorFunction) {
    if (ReferenceEquals(protectorFunction, Protect)) {
      return new(expression.Origin, null, Protect.ToExprDotName(), [
        new(null, expression),
        new(null, new StringLiteralExpr(SourceOrigin.NoToken, expression.ToString(), false)),
      ], Token.NoToken);
    }
    if (protectorFunction is ProtectorFunction.WithContext { ChangeContext: var changeContext }) {
      return new ProtectToProveApplySuffix(expression, changeContext);
    }
    if (protectorFunction is ProtectorFunction.WithEntryPoint { EntryPoint: var entryPoint }) {
      return new ProtectToProveApplySuffix(expression, entryPoint);
    }
    throw new ArgumentException("\"protectorFunction\" needs to be either `_protect` or `_protectToProve`");
  }
  public static ApplySuffix WrappedWith(this string varname, ProtectorFunction protectorFunction) {
    if (ReferenceEquals(protectorFunction, ProtectScope)) {
      return new(SourceOrigin.NoToken, null, ProtectScope.ToExprDotName(), [
        new(null, new NameSegment(SourceOrigin.NoToken, varname, null)),
        new(null, new StringLiteralExpr(SourceOrigin.NoToken, varname, false)),
      ], Token.NoToken);
    }
    throw new ArgumentException("\"protectorFunction\" needs to be `_protectScope`");
  }

  public record ProtectorFunction(string Name, Function Function) {
    public sealed record WithEntryPoint(string Name, Function Function, BigInteger EntryPoint) : ProtectorFunction(Name, Function);
    public sealed record WithContext(string Name, Function Function, ChangeContext ChangeContext) : ProtectorFunction(Name, Function);
  }

  public static ProtectorFunction Protect { get; }
  public static ProtectorFunction ProtectScope { get; }
  public static ProtectorFunction.WithContext ProtectToProve { get; }
  public static ProtectorFunction.WithEntryPoint ProtectToProveImmediate { get; }
  public static ICollection<ProtectorFunction> All { get; }
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

  private static Function IdentityOf(List<TypeParameter> typeArgs, string name, (Formal identity, Microsoft.Dafny.Type result) signature) =>
    IdentityOf(typeArgs, name, ([], signature.identity, [], signature.result));
  private static Function IdentityOf(List<TypeParameter> typeArgs, string name, (List<Formal> beforeIdentity, Formal identity, Microsoft.Dafny.Type result) signature) =>
    IdentityOf(typeArgs, name, (signature.beforeIdentity, signature.identity, [], signature.result));
  private static Function IdentityOf(List<TypeParameter> typeArgs, string name, (Formal identity, List<Formal> afterIdentity, Microsoft.Dafny.Type result) signature) =>
    IdentityOf(typeArgs, name, ([], signature.identity, signature.afterIdentity, signature.result));
  private static Function IdentityOf(List<TypeParameter> typeArgs, string name, (List<Formal> beforeIdentity, Formal identity, List<Formal> afterIdentity, Microsoft.Dafny.Type result) signature) =>
    ProtectorFunctionBase(typeArgs, name, ([.. signature.beforeIdentity, signature.identity, .. signature.afterIdentity,], signature.result), signature.identity.Name.ToFunctionBody());

  private static Microsoft.Dafny.Type StringType() => new UserDefinedType(origin: SourceOrigin.NoToken, name: "string", optTypeArgs: null);

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
  private static Microsoft.Dafny.Type ToType(this TypeParameter tp) => new UserDefinedType(tp);

  private static NameSegment ToNameSegment(this string name) => new(SourceOrigin.NoToken, name, null);
  public static ExprDotName ToExprDotName(this ProtectorFunction pf) => new(SourceOrigin.NoToken, ContainingModuleName.ToNameSegment(), pf.Name.ToNameNodeWithVirtualToken(), null);
}
