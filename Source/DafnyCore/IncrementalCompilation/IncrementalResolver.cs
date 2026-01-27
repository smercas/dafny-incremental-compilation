#nullable enable
using DafnyCore.IncrementalCompilation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
  public override Task Resolve(CancellationToken cancellationToken) {
    Type.ResetScopes();

    Type.EnableScopes();

    var moduleWithOldRootStuff = new ModuleSplitter(Options).Split(Program);
    AddProtectorsModule();
    new ProtectorsImporter(Options).ImportIn(moduleWithOldRootStuff);

    // For the formatter, we ensure we take snapshots of the PrefixNamedModules and topleveldecls
    Program.DefaultModuleDef.PreResolveSnapshotForFormatter();

    // Changing modules at this stage without changing their CloneId doesn't break resolution caching,
    // because ResolvedPrefixNamedModules end up in the dependencies of a module so they change its hash anyways
    Program.DefaultModuleDef.ProcessPrefixNamedModules();

    var startingErrorCount = Reporter.ErrorCount;
    ComputeModuleDependencyGraph(Program, out var moduleDeclarationPointers);

    if (Reporter.ErrorCount != startingErrorCount) {
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

    ResolveSortedDecls(moduleDeclarationPointers, cancellationToken);

    if (Reporter.ErrorCount != startingErrorCount) {
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
  protected void AddProtectorsModule() {
    var def = new ModuleDefinition(SourceOrigin.NoToken, new(ProtectorFunctions.ContainingModuleName), [], ModuleKindEnum.Concrete, null, Program.DefaultModuleDef, null, []);
    var decl = new LiteralModuleDecl(Options, def, Program.DefaultModuleDef, Guid.NewGuid());
    def.DefaultClass!.Members.AddRange(ProtectorFunctions.All.Select(pf => pf.Function));
    def.DefaultClass!.SetMembersBeforeResolution();
    Program.DefaultModuleDef.SourceDecls.Insert(0, decl);
  }
  protected abstract void ResolveSystemModule();
  protected abstract void ResolveSortedDecls(Dictionary<ModuleDecl, Action<ModuleDecl>> moduleDeclarationPointers, CancellationToken cancellationToken);
  protected new void ProcessDeclarationResolutionResult(
    Dictionary<ModuleDecl, Action<ModuleDecl>> moduleDeclarationPointers,
    ModuleDecl decl,
    ModuleResolutionResult moduleResolutionResult
  ) {
    Cache.ModuleDeclResolutionResults[decl] = moduleResolutionResult;
    base.ProcessDeclarationResolutionResult(moduleDeclarationPointers, decl, moduleResolutionResult);
  }
  protected ModuleResolutionResult ResolveModuleDeclaration(ModuleDecl curr) => ResolveModuleDeclaration(Program.Compilation, curr);
}

// a lot of copy paste from the original ProgramResolver, will be fixed later
public class InitialIncrementalResolver(Program program) : IncrementalResolver(program) {
  public override ResolutionCache Cache { get; protected set; } = new ResolutionCache();

  protected override void ResolveSystemModule() {
    Cache = Cache with {
      SystemModuleManager = Program.SystemModuleManager,
      SystemClassMembers = base.ResolveSystemModule(Program),
    };
  }
  protected override void ResolveSortedDecls(Dictionary<ModuleDecl, Action<ModuleDecl>> moduleDeclarationPointers, CancellationToken cancellationToken) {
    foreach (var decl in Cache.SortedDecls) {
      cancellationToken.ThrowIfCancellationRequested();
      var moduleResolutionResult = ResolveModuleDeclaration(decl);
      ProcessDeclarationResolutionResult(moduleDeclarationPointers, decl, moduleResolutionResult);
    }
  }
}

public class SubsequentIncrementalResolver(Program program, ResolutionCache prevCache, IncCompModification? modification) : IncrementalResolver(program) {
  public override ResolutionCache Cache { get; protected set; } = new ResolutionCache();
  public ResolutionCache PrevCache { get; private init; } = prevCache;
  public IncCompModification? Modification { get; private init; } = modification;

  #region Secondary Constructors
  public SubsequentIncrementalResolver(
    Program program, IncrementalResolver prevIncResolver, IncCompModification? modification
  ) : this(program, prevIncResolver.Cache, modification) { }
  #endregion

  protected override void ResolveSystemModule() {
    Cache = Cache with {
      SystemModuleManager = PrevCache.SystemModuleManager,
      SystemClassMembers = PrevCache.SystemClassMembers,
    };
    Program.SystemModuleManager = PrevCache.SystemModuleManager;
  }
  protected override void ResolveSortedDecls(Dictionary<ModuleDecl, Action<ModuleDecl>> moduleDeclarationPointers, CancellationToken cancellationToken) {
    Contract.Requires(Cache.SortedDecls.Count() == PrevCache.SortedDecls.Count());
    // req clause for memberwise equality / equivalence, not `FullDafnyName` equality
    Contract.Requires(Contract.ForAll(Cache.SortedDecls.Zip(PrevCache.SortedDecls), pair => { var (c, p) = pair; return c.FullDafnyName == p.FullDafnyName; } ));
    if (Modification is not ModificationToModuleDeclaration { AffectedModuleDecl: var amd } mtmd) {
      // for now, as a default case, if module doesn't affect a moduleDecl, we do resolution normally
      // this branch is equivalent to `InitialIncrementalResolver.ResolveSortedDecls`
      foreach (var decl in Cache.SortedDecls) {
        cancellationToken.ThrowIfCancellationRequested();
        var moduleResolutionResult = ResolveModuleDeclaration(decl);
        ProcessDeclarationResolutionResult(moduleDeclarationPointers, decl, moduleResolutionResult);
      }
      return;
    }

    void GenericResolution((ModuleDecl, ModuleDecl) decls, Func<ModuleDecl, ModuleDecl, ModuleResolutionResult> resolve) {
      var (curr, prev) = decls;

      cancellationToken.ThrowIfCancellationRequested();
      var moduleResolutionResult = resolve(curr, prev);
      ProcessDeclarationResolutionResult(moduleDeclarationPointers, curr, moduleResolutionResult);
    }
    
    void UseCache((ModuleDecl, ModuleDecl) decls) => GenericResolution(decls, (curr, prev) => PrevCache.ModuleDeclResolutionResults[prev]);
    
    bool IsAffectedModuleDecl((ModuleDecl _, ModuleDecl Prev) decls) => ReferenceEquals(amd.Old, decls.Prev);
    
    void ResolveAffectedModuleDecl((ModuleDecl, ModuleDecl) decls) => GenericResolution(decls, (curr, prev) => {
      amd.NewlyProcessed = curr;
      return new ModuleResolver(this, curr.Options).ResolveModuleDeclaration(Program.Compilation, curr/*, prev*/);
    });
    
    IEnumerable<ModuleDecl> RecursiveDependenciesOf(ModuleDecl m) {
      Contract.Requires(dependencies.FindVertex(m) is not null);
      var v = dependencies.FindVertex(m)!;
      foreach (var dep in v.Successors) {
        yield return dep.N;
        foreach (var transDep in RecursiveDependenciesOf(dep.N)) {
          yield return transDep;
        }
      }
    }
    void ResolveAfterAffectedModuleDecl((ModuleDecl, ModuleDecl) decls) =>
      GenericResolution(decls, (curr, prev) => RecursiveDependenciesOf(curr).Contains(amd.NewlyProcessed) switch {
        true => ResolveModuleDeclaration(curr), // this `ModuleDecl` depends (directly or indirectly) on the modified one, so we have to resolve it normally
        false => PrevCache.ModuleDeclResolutionResults[prev], // this `ModuleDecl` is unaffected, so we can use the previous resolution result
      });

    Cache.SortedDecls.Zip(PrevCache.SortedDecls).ForEachInPhases(
      UseCache, (IsAffectedModuleDecl, ResolveAffectedModuleDecl, ResolveAfterAffectedModuleDecl)
    );
  }
}
