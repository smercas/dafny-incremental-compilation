#nullable enable

using DafnyCore.IncrementalCompilation;

namespace Microsoft.Dafny;

public abstract class NestedMatchCase : NodeWithOrigin, IProtectable<NestedMatchCase> {
  public ExtendedPattern Pat;

  [SyntaxConstructor]
  protected NestedMatchCase(IOrigin origin, ExtendedPattern pat) : base(origin) {
    Pat = pat;
  }

  public void CheckLinearNestedMatchCase(Type type, ResolutionContext resolutionContext, ModuleResolver resolver) {
    Pat.CheckLinearExtendedPattern(type, resolutionContext, resolver);
  }

  public int PrependedProtectionsCount { get; protected set; }
  public static bool ProtectionFilter(ModuleResolver resolver, Statement s) => !resolver.moduleInfo.Ctors.ContainsKey(ProtectorFunctions.getIdentityFromProtectFunctionCall(((s as AssertStmt)!.Expr as ApplySuffix)!).Name);
  protected NestedMatchCase(Protector protector, NestedMatchCase original) : base(protector, original) {
    Pat = original.Pat.WithProtections(protector);
  }
  public abstract NestedMatchCase WithProtections(Protector protector);
}