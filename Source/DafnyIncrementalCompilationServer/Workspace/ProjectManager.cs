using Microsoft.Boogie;
using Microsoft.Dafny.LanguageServer.Language.Symbols;
using Microsoft.Dafny.LanguageServer.Workspace;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Reactive.Disposables;
using System.Text;
using System.Threading.Tasks;
using Util = Microsoft.Dafny.Util;

namespace Microsoft.Dafny.IncrementalCompilation.Workspace;

public delegate ProjectManager CreateProjectManager(
  int stackSize,
  DafnyProject project
);

public class ProjectManager : IDisposable {
  private readonly DafnyOptions options;
  public IncCompModification? Modification {
    get => options.Modification();
    set => options.Modification(value);
  }
  public DafnyProject Project => options.DafnyProject;
  private Compilation compilation;
  public Compilation Compilation {
    get => compilation;
    private set {
      compilation.Dispose();
      compilation = value;
      entryPoints = null;
    }
  }
  private Task<HashSet<ICanVerify>?>? entryPoints = null;
  public Task<IReadOnlySet<ICanVerify>?> EntryPoints {
    get {
      entryPoints ??= ComputeInitialEntryPoints();
      return entryPoints.Then(
        static s => s.ApplyIfNotNull(
          static s => s as IReadOnlySet<ICanVerify>
        )
      );
    }
  }

  private async Task<HashSet<ICanVerify>?> ComputeInitialEntryPoints() =>
    (await Compilation.ParsedProgram).ApplyIfNotNull(CollectInitialEntryPointsOf)?.ToHashSet();

  private delegate Compilation CreateCompilation(CompilationInput? input = null);
  private readonly CreateCompilation createCompilation;
  private readonly ILogger<ProjectManager> logger;
  private int version = 0;

  #region Boogie Execution Engine
  private readonly TaskScheduler scheduler;
  private readonly VerificationResultCache verificationCache = new();
  private ExecutionEngine? boogieExecutionEngine = null;
  public ExecutionEngine BoogieExecutionEngine {
    get {
      if (options.Get(LanguageServer.Workspace.ProjectManager.ReuseSolvers)) {
        boogieExecutionEngine ??= new ExecutionEngine(options, verificationCache, scheduler);
      } else {
        boogieExecutionEngine?.Dispose();
        boogieExecutionEngine = new ExecutionEngine(options, verificationCache, scheduler);
      }
      return boogieExecutionEngine;
    }
  }
  #endregion
  private IEnumerable<ICanVerify> CollectInitialEntryPointsOf(Program program) => CollectInitialEntryPointsOf(program.DefaultModuleDef);
  private IEnumerable<ICanVerify> CollectInitialEntryPointsOf(ModuleDefinition def) {
    foreach (var prefixSubmodule in def.PrefixNamedModules.Select(pm => pm.Module.ModuleDef)) {
      foreach (var entryPointsOfSubModule in CollectInitialEntryPointsOf(prefixSubmodule)) {
        yield return entryPointsOfSubModule;
      }
    }
    if (def.DefaultClass is not null) {
      foreach (var member in def.DefaultClass.Members.OfType<ICanVerify>().Where(cv => cv.ShouldVerify)) {
        yield return member;
      }
    }
    
    foreach (var decl in def.SourceDecls) {
      switch (decl) {
        case LiteralModuleDecl { ModuleDef: var submodule }:
          foreach (var entryPointsOfSubModule in CollectInitialEntryPointsOf(submodule)) {
            yield return entryPointsOfSubModule;
          }
          break;
        case ModuleDecl:
          break; // AbstractModuleDecl, AliasModuleDecl and ModuleExportDecl
        case TopLevelDeclWithMembers { Members: var members } t and (ClassLikeDecl or DatatypeDecl or NewtypeDecl or AbstractTypeDecl):
          switch (t) {
            case NewtypeDecl nd: // inplied by last
              Contract.Assert(nd.ShouldVerify == true);
              yield return nd;
              break;
            case IteratorDecl id: // inplied by last
              Contract.Assert(id.ShouldVerify == true);
              yield return id;
              break;
            case ICanVerify { ShouldVerify: true } cv:
              logger.LogError("Unhandled case of valid ICanVerify ({})", decl.GetType().Name);
              yield return cv;
              break;
          }
          foreach (var member in members.OfType<ICanVerify>().Where(cv => cv.ShouldVerify)) {
            yield return member;
          }
          break;
        case TypeSynonymDecl { ShouldVerify: true } tsd:
          yield return tsd;
          break;
        case ICanVerify { ShouldVerify: true } cv:
          logger.LogError("Unhandled case of valid ICanVerify ({})", decl.GetType().Name);
          yield return cv;
          break;
        case DefaultClassDecl:
        case TypeParameter:
        case AmbiguousTopLevelDecl:
        case InternalTypeSynonymDecl:
        case NonNullTypeDecl:
        case ValuetypeDecl:
        default:
          throw new UnreachableException();
      }
    }
  }
  public ProjectManager(
    DafnyOptions serverOptions,
    int stackSize,
    DafnyProject project,
    ILogger<ProjectManager> logger,
    Dafny.CreateCompilation createCompilation
  ) {
    static DafnyOptions DetermineProjectOptions(DafnyProject project, DafnyOptions serverOptions) {
      var result = new DafnyOptions(serverOptions);

      foreach (var (option, value) in Server.Options.SelectWhere(o => (project.TryGetValue(o, out var v), (o, v)))) {
        result.Options.OptionArguments[option] = value;
        result.ApplyBinding(option);
      }

      if (result.SolverIdentifier == "Z3") {
        result.SolverVersion = null;
      }

      result.DafnyProject = project;
      result.ApplyDefaultOptionsWithoutSettingsDefault();

      return result;
    }
    options = DetermineProjectOptions(project, serverOptions);
    this.logger = logger;
    scheduler = CustomStackSizePoolTaskScheduler.Create(stackSize, options.VcsCores);
    this.createCompilation = (input) => createCompilation(BoogieExecutionEngine, input ?? new CompilationInput(options, version, Project));
    Modification = null;
    compilation = this.createCompilation();
    Compilation.Start();
    // collect entry points here in a id to entry point mapping
  }

  public void ApplyModification(IncCompModification modification) {
    Modification = modification;
    StartNewCompilation();
  }

  public void StartNewCompilation() {
    //version += 1;
    Compilation = createCompilation(/*new CompilationInput(options, version, Project)*/);
    Compilation.Start();
  }

  public bool IsDisposed { get; private set; }
  public void Dispose() {
    GC.SuppressFinalize(this);
    IsDisposed = true;
    boogieExecutionEngine?.Dispose();
    Compilation.Dispose();
  }
}
