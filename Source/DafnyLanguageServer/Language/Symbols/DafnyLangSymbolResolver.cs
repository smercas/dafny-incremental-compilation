using System;
using System.CommandLine;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Dafny.LanguageServer.Workspace;
using System.Diagnostics.Contracts;
using Namotion.Reflection;
using System.Linq;

namespace Microsoft.Dafny.LanguageServer.Language.Symbols {
  /// <summary>
  /// Symbol resolver that utilizes dafny-lang to resolve the symbols.
  /// </summary>
  /// <remarks>
  /// dafny-lang makes use of static members and assembly loading. Since thread-safety of this is not guaranteed,
  /// this resolver serializes all invocations.
  /// </remarks>
  public class DafnyLangSymbolResolver : ISymbolResolver {
    // TODO: bring this in line with the language at large or rewrite how modifications are passed
    public static readonly Option<IncCompModification?> IncrementalCompilationModification = new("--incremental-compilation-modification", () => null, 
      "compile based on previous compilation results and a provided modification") {
      IsHidden = true
    };

    public static readonly Option<bool> UseCaching = new("--use-caching", () => true,
      "Use caching to speed up analysis done by the Dafny IDE after each text edit.") {
      IsHidden = true
    };

    private readonly ILogger logger;
    private readonly ILogger<CachingResolver> innerLogger;
    private readonly SemaphoreSlim resolverMutex = new(1);
    private readonly TelemetryPublisherBase telemetryPublisher;

    public DafnyLangSymbolResolver(ILogger<DafnyLangSymbolResolver> logger, ILogger<CachingResolver> innerLogger, TelemetryPublisherBase telemetryPublisher) {
      this.logger = logger;
      this.innerLogger = innerLogger;
      this.telemetryPublisher = telemetryPublisher;
    }

    private readonly ResolutionCache resolutionCache = new();
    public async Task ResolveSymbols(Compilation compilation, Program program, CancellationToken cancellationToken) {
      // TODO The resolution requires mutual exclusion since it sets static variables of classes like Microsoft.Dafny.Type.
      //      Although, the variables are marked "ThreadStatic" - thus it might not be necessary. But there might be
      //      other classes as well.
      await resolverMutex.WaitAsync(cancellationToken);
      try {
        await RunDafnyResolver(compilation, program, cancellationToken);
      }
      finally {
        resolverMutex.Release();
      }
    }

    //for some reason this doesn't get preserved like the cache does, so we'll make it static for now
    static private ProgramResolver? resolver;

    private async Task RunDafnyResolver(Compilation compilation, Program program, CancellationToken cancellationToken) {
      var beforeResolution = DateTime.Now;
      try {
        if (program.Options.Options.OptionArguments.ContainsKey(IncrementalCompilationModification)) {
          Contract.Assert(resolver is null or IncrementalResolver);
          resolver = (resolver is null) switch {
            true => new InitialIncrementalResolver(program),
            false => new SubsequentIncrementalResolver(program, (resolver as IncrementalResolver)!, program.Options.Get(IncrementalCompilationModification)),
          };
        } else if (program.Options.Get(UseCaching)) {
          resolver = new CachingResolver(program, innerLogger, telemetryPublisher, resolutionCache);
        } else {
          resolver = new ProgramResolver(program);
        }
        await resolver.Resolve(cancellationToken);
        if (compilation.HasErrors) {
          logger.LogDebug($"encountered errors while resolving {compilation.Project.Uri}");
        }
      }
      finally {
        telemetryPublisher.PublishTime("Resolution", compilation.Project.Uri.ToString(), DateTime.Now - beforeResolution);
      }
    }
  }
}
