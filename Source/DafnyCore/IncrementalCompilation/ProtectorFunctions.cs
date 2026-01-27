#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FormalConstructionArgs = (string Name, Microsoft.Dafny.NodeWithOrigin Type);
using System.Diagnostics;
using Microsoft.Dafny;

namespace DafnyCore.IncrementalCompilation;
public static class ProtectorFunctions {
  static ProtectorFunctions() {
    Protect =        new("_protect",        null!); Protect =         Protect         with { Function = protectFunction(),        };
    ProtectScope =   new("_protectScope",   null!); ProtectScope =    ProtectScope    with { Function = protectScopeFunction(),   };
    ProtectToProve = new("_protectToProve", null!); ProtectToProve =  ProtectToProve  with { Function = protectToProveFunction(), };
    All = [Protect, ProtectScope, ProtectToProve];
  }

  private static Function protectFunction() {
    var typeVar = "T".ToTypeParameter();
    return IdentityOf(
      typeArgs: [typeVar,],
      name: Protect.Name,
      signature: (
        ("x", typeVar), [
        ("name", StringType()),
      ], typeVar.ToType())
    );
  }
  private static Function protectScopeFunction() {
    var typeVar = "T".ToTypeParameter();
    return ProtectorFunctionBase(
      typeArgs: [typeVar,],
      name: ProtectScope.Name,
      signature: ([
        ("x", typeVar),
        ("name", StringType()),
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
      signature: (
        ("x", typeVar), [
        ("name", StringType()),
        ("scope", new SeqType(new BoolType())),
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
    if (ReferenceEquals(protectorFunction, ProtectToProve)) {
      return new ProtectToProveApplySuffix(expression);
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

  public sealed record ProtectorFunction(string Name, Function Function);

  public static readonly ProtectorFunction Protect;
  public static readonly ProtectorFunction ProtectScope;
  public static readonly ProtectorFunction ProtectToProve;
  public static readonly ICollection<ProtectorFunction> All;
  public static readonly string ContainingModuleName = "_protectors";

  private static Function ProtectorFunctionBase(List<TypeParameter> typeArgs, string name, (List<FormalConstructionArgs> args, Microsoft.Dafny.Type result) signature, Expression body) => new(
    origin: new Token(),
    // can't use SourceOrigin.NoToken because ref. eq. to it
    // is used to ensure that DefaultModuleDefinitions are verified;
    // I do NOT like that piece of code (:
    nameNode: new(name),
    hasStaticKeyword: false,
    isGhost: true,
    isOpaque: true,
    typeArgs: typeArgs,
    ins: signature.args.ConvertAll(fca => (fca.Name, fca.Type switch {
      Microsoft.Dafny.Type t => t,
      TypeParameter tp => new UserDefinedType(tp),
      _ => throw new ArgumentException("not `Type` or `TypeParameter`"),
    }).ToFormal()),
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

  private static Function IdentityOf(List<TypeParameter> typeArgs, string name, (FormalConstructionArgs identity, Microsoft.Dafny.Type result) signature) =>
    IdentityOf(typeArgs, name, ([]                      , signature.identity, []                     , signature.result));
  private static Function IdentityOf(List<TypeParameter> typeArgs, string name, (List<FormalConstructionArgs> beforeIdentity, FormalConstructionArgs identity, Microsoft.Dafny.Type result) signature) =>
    IdentityOf(typeArgs, name, (signature.beforeIdentity, signature.identity, []                     , signature.result));
  private static Function IdentityOf(List<TypeParameter> typeArgs, string name, (FormalConstructionArgs identity, List<FormalConstructionArgs> afterIdentity, Microsoft.Dafny.Type result) signature) =>
    IdentityOf(typeArgs, name, ([]                      , signature.identity, signature.afterIdentity, signature.result));
  private static Function IdentityOf(List<TypeParameter> typeArgs, string name, (List<FormalConstructionArgs> beforeIdentity, FormalConstructionArgs identity, List<FormalConstructionArgs> afterIdentity, Microsoft.Dafny.Type result) signature) =>
    ProtectorFunctionBase(typeArgs, name, ([.. signature.beforeIdentity, signature.identity, .. signature.afterIdentity,], signature.result), signature.identity.Name.ToFunctionBody());

  private static Microsoft.Dafny.Type StringType() => new UserDefinedType(origin: SourceOrigin.NoToken, name: "string", optTypeArgs: null);

  private static Expression ToFunctionBody(this string name) => new NameSegment(SourceOrigin.NoToken, name: name, null);
  private static Formal ToFormal(this (string Name, Microsoft.Dafny.Type Type) t) => new(
    origin: SourceOrigin.NoToken,
    nameNode: new(t.Name),
    syntacticType: t.Type,
    inParam: true,
    isGhost: false,
    defaultValue: null,
    attributes: null,
    isOld: false,
    isNameOnly: false,
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
  private static Name ToName(this string name) => new(name);
  public static ExprDotName ToExprDotName(this ProtectorFunction pf) => new(SourceOrigin.NoToken, ContainingModuleName.ToNameSegment(), pf.Name.ToName(), null);
}
