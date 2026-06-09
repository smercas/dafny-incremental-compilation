#nullable enable
using Dafny;
using DafnyCore.IncrementalCompilation;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Dafny;

#region Resolution Cache
public sealed record ResolutionCache(
  IEnumerable<ModuleDecl> SortedDecls,
  Dictionary<TopLevelDeclWithMembers, Dictionary<string, MemberDecl>> SystemClassMembers,
  SystemModuleManager SystemModuleManager,
  Dictionary<ModuleDecl, ModuleResolutionResult> ModuleDeclResolutionResults
) {
  public ResolutionCache() : this(null!, null!, null!, []) {
  }
}
#endregion

public abstract class IncrementalResolver(Program program) : ProgramResolver(program) {
  public abstract ResolutionCache Cache { get; protected set; }
  protected virtual void onError() { }
  public override Task Resolve(CancellationToken cancellationToken) {
    Type.ResetScopes();

    Type.EnableScopes();

    ProtectToProveApplySuffix.ResetInstances();
    var moduleWithOldRootStuff = new ModuleSplitter(Options).Split(Program);
    ProtectToProveApplySuffix.AssignEntryPoints();
    new Protector().Protect(moduleWithOldRootStuff);
    AddProtectorsModule();
    new ProtectorsImporter(Options).ImportIn(moduleWithOldRootStuff); // importing in this module transfers the imports to all the other modules

    // For the formatter, we ensure we take snapshots of the PrefixNamedModules and topleveldecls
    Program.DefaultModuleDef.PreResolveSnapshotForFormatter();

    // Changing modules at this stage without changing their CloneId doesn't break resolution caching,
    // because ResolvedPrefixNamedModules end up in the dependencies of a module so they change its hash anyways
    Program.DefaultModuleDef.ProcessPrefixNamedModules();

    var startingErrorCount = Reporter.ErrorCount;
    ComputeModuleDependencyGraph(Program, out var moduleDeclarationPointers);

    if (Reporter.ErrorCount != startingErrorCount) {
      onError();
      return Task.CompletedTask;
    }

    Cache = Cache with {
      SortedDecls = dependencies.TopologicallySortedComponents(),
    };
    Program.ModuleSigs = new();

    SetHeights(Cache.SortedDecls);

    ResolveSystemModule();
    foreach (var moduleClassMembers in Cache.SystemClassMembers) {
      classMembers[moduleClassMembers.Key] = moduleClassMembers.Value;
    }

    var rewriters = RewriterCollection.GetRewriters(Reporter, Program);

    var compilation = Program.Compilation;
    foreach (var rewriter in rewriters) {
      cancellationToken.ThrowIfCancellationRequested();
      rewriter.PreResolve(Program);
    }

    ResolveSortedDecls(moduleDeclarationPointers, Cache.SortedDecls, cancellationToken);

    if (Reporter.ErrorCount != startingErrorCount) {
      onError();
      return Task.CompletedTask;
    }

    Type.DisableScopes();

    InstantiateReplaceableModules(Program);
    CheckDuplicateModuleNames(Program);

    foreach (var rewriter in rewriters) {
      cancellationToken.ThrowIfCancellationRequested();
      rewriter.PostResolve(Program);
    }
    return Task.CompletedTask;
  }
  private void AddProtectorsModule() {
    var def = new ModuleDefinition(SourceOrigin.NoToken, new(ProtectorFunctions.ContainingModuleName), [], ModuleKindEnum.Concrete, null, Program.DefaultModuleDef, null, []);
    var decl = new LiteralModuleDecl(Options, def, Program.DefaultModuleDef, Guid.NewGuid());
    def.DefaultClass!.Members.AddRange(ProtectorFunctions.All.Select(pf => pf.Function));
    def.DefaultClass!.SetMembersBeforeResolution();
    foreach (var arity in ProtectorFunctions.All.Select(pf => pf.Function.Ins.Count).Distinct()) {
      SystemModuleManager.CreateArrowTypeDecl(arity);
    }
    Program.DefaultModuleDef.SourceDecls.Insert(0, decl);
  }
  protected abstract void ResolveSystemModule();
  protected abstract void ResolveSortedDecls(Dictionary<ModuleDecl, Action<ModuleDecl>> moduleDeclarationPointers, IEnumerable<ModuleDecl> sortedDecls, CancellationToken cancellationToken);
  protected new void ProcessDeclarationResolutionResult(
    Dictionary<ModuleDecl, Action<ModuleDecl>> moduleDeclarationPointers,
    ModuleDecl decl,
    ModuleResolutionResult moduleResolutionResult
  ) {
    Cache.ModuleDeclResolutionResults[decl] = moduleResolutionResult;
    base.ProcessDeclarationResolutionResult(moduleDeclarationPointers, decl, moduleResolutionResult);
  }
  protected ModuleResolutionResult ResolveModuleDeclaration(ModuleDecl curr) => ResolveModuleDeclaration(Program.Compilation, curr);
  protected ModuleResolutionResult ResolveModuleDeclaration(ModuleDecl curr, ModuleDecl prev) => new ModuleResolver(this, curr.Options).ResolveModuleDeclaration(Program.Compilation, curr/*, prev*/);
}

// a lot of copy paste from the original ProgramResolver, will be fixed later
public class InitialIncrementalResolver(Program program) : IncrementalResolver(program) {
  public override ResolutionCache Cache { get; protected set; } = new ResolutionCache();

  protected override void onError() => Reporter.Error(MessageSource.Resolver, "", SourceOrigin.NoToken, "initial resolution can't have resolution errors, since it's the basis of subsequent resolution runs");
  protected override void ResolveSystemModule() {
    Cache = Cache with {
      SystemModuleManager = Program.SystemModuleManager,
      SystemClassMembers = base.ResolveSystemModule(Program),
    };
  }
  protected override void ResolveSortedDecls(Dictionary<ModuleDecl, Action<ModuleDecl>> moduleDeclarationPointers, IEnumerable<ModuleDecl> sortedDecls, CancellationToken cancellationToken) {
    foreach (var decl in sortedDecls) {
      cancellationToken.ThrowIfCancellationRequested();
      var moduleResolutionResult = ResolveModuleDeclaration(decl);
      ProcessDeclarationResolutionResult(moduleDeclarationPointers, decl, moduleResolutionResult);
    }
  }
}

public class SubsequentIncrementalResolver(Program program, ResolutionCache prevCache) : IncrementalResolver(program) {
  public override ResolutionCache Cache { get; protected set; } = new ResolutionCache();
  public ResolutionCache PrevCache { get; private init; } = prevCache;

  #region Secondary Constructors
  public SubsequentIncrementalResolver(Program program, IncrementalResolver prevIncResolver) : this(program, prevIncResolver.Cache) { }
  #endregion

  protected override void onError() => Cache = PrevCache;
  protected override void ResolveSystemModule() {
    Cache = Cache with {
      SystemModuleManager = PrevCache.SystemModuleManager,
      SystemClassMembers = PrevCache.SystemClassMembers,
    };
    Program.SystemModuleManager = PrevCache.SystemModuleManager;
  }
  protected override void ResolveSortedDecls(Dictionary<ModuleDecl, Action<ModuleDecl>> moduleDeclarationPointers, IEnumerable<ModuleDecl> sortedDecls, CancellationToken cancellationToken) {
    Contract.Requires(sortedDecls.Count() == PrevCache.SortedDecls.Count());
    // req clause for memberwise equality / equivalence, not `FullDafnyName` equality
    Contract.Requires(Contract.ForAll(sortedDecls.Zip(PrevCache.SortedDecls), pair => { var (c, p) = pair; return c.FullDafnyName == p.FullDafnyName; }));

    if (ProtectToProveApplySuffix.ChangesFlattened.All(c => c.IsEmptyChange)) {
      // as a default case, if no changes, we do resolution normally
      // this branch is equivalent to `InitialIncrementalResolver.ResolveSortedDecls`
      foreach (var decl in sortedDecls) {
        cancellationToken.ThrowIfCancellationRequested();
        var moduleResolutionResult = ResolveModuleDeclaration(decl);
        ProcessDeclarationResolutionResult(moduleDeclarationPointers, decl, moduleResolutionResult);
      }
      return;
    }

    IEnumerable<ModuleDecl> RecursiveDependantsOf(ModuleDecl m) {
      Contract.Requires(dependencies.FindVertex(m) is not null);
      var v = dependencies.FindVertex(m);
      var immediatePredecessors = dependencies.GetVertices().SelectWhere(ppv => (ppv.Successors.Contains(v), ppv.N));
      foreach (var pred in immediatePredecessors) {
        yield return pred;
        foreach (var trans in RecursiveDependantsOf(pred)) { yield return trans; }
      }
    }
    var dependants = ProtectToProveApplySuffix.ChangedModules.SelectMany(RecursiveDependantsOf).ToImmutableHashSet();

    void GenericResolution((ModuleDecl, ModuleDecl) decls, Func<ModuleDecl, ModuleDecl, ModuleResolutionResult> resolve) {
      var (curr, prev) = decls;

      cancellationToken.ThrowIfCancellationRequested();
      var moduleResolutionResult = resolve(curr, prev);
      ProcessDeclarationResolutionResult(moduleDeclarationPointers, curr, moduleResolutionResult);
    }

    void UseCache((ModuleDecl, ModuleDecl) decls) => GenericResolution(decls, (curr, prev) => PrevCache.ModuleDeclResolutionResults[prev]);

    bool IsAffectedModuleDecl((ModuleDecl Curr, ModuleDecl _) decls) => ProtectToProveApplySuffix.ChangedModules.Contains(decls.Curr);

    void ResolveFirstAffectedModuleDecl((ModuleDecl, ModuleDecl) decls) => GenericResolution(decls, ResolveModuleDeclaration);

    void ResolveAfterFirstAffectedModuleDecl((ModuleDecl, ModuleDecl) decls) =>
      GenericResolution(decls, (curr, prev) => {
        if (dependants.Contains(curr)) { return ResolveModuleDeclaration(curr); }
        if (IsAffectedModuleDecl(decls)) { return ResolveModuleDeclaration(curr, prev); }
        return PrevCache.ModuleDeclResolutionResults[prev];
      });

    sortedDecls.Zip(PrevCache.SortedDecls).ForEachInPhases(
      UseCache, (IsAffectedModuleDecl, ResolveFirstAffectedModuleDecl, ResolveAfterFirstAffectedModuleDecl)
    );
  }
}
