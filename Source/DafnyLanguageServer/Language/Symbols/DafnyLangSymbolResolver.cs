using System;
using System.CommandLine;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Dafny.LanguageServer.Workspace;
using System.Diagnostics.Contracts;
using Namotion.Reflection;
using System.Linq;
using System.Diagnostics;

namespace Microsoft.Dafny.LanguageServer.Language.Symbols {
  /// <summary>
  /// Symbol resolver that utilizes dafny-lang to resolve the symbols.
  /// </summary>
  /// <remarks>
  /// dafny-lang makes use of static members and assembly loading. Since thread-safety of this is not guaranteed,
  /// this resolver serializes all invocations.
  /// </remarks>
  public class DafnyLangSymbolResolver : ISymbolResolver {
    public abstract record CachingMode {
      public sealed record None : CachingMode;
      public sealed record HashBased : CachingMode;
      public sealed record Incremental(IncCompModification? Modification) : CachingMode;
      public static CachingMode Default() => new None();
    }
    public static readonly Option<CachingMode> CachingType = new(
      name: "--caching-type",
      description: "Caching mode: none, hash, or incremental.",
      parseArgument: result => {
        var token = result.Tokens[0].Value;
        if (token.ToLowerInvariant() is not ("none" or "hash" or "incremental")) {
          result.ErrorMessage = $"Invalid caching mode '{token}' (expected: none, hash or incremental)";
          return CachingMode.Default();
        }
        return token.ToLowerInvariant() switch {
          "none" => new CachingMode.None(),
          "hash" => new CachingMode.HashBased(),
          "incremental" => new CachingMode.Incremental(null),
          _ => throw new UnreachableException()
        };
      }
    ) {
      Arity = ArgumentArity.ExactlyOne,
      IsHidden = true
    };
    static DafnyLangSymbolResolver() {
      CachingType.SetDefaultValueFactory(static () => new CachingMode.HashBased()); // same semantics as before
    }

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
    static private ProgramResolver? resolver = null;
    //private ProgramResolver? resolver = null;

    private async Task RunDafnyResolver(Compilation compilation, Program program, CancellationToken cancellationToken) {
      var beforeResolution = DateTime.Now;
      try {
        resolver = program.Options.Get(CachingType) switch {
          CachingMode.None => new ProgramResolver(program),
          CachingMode.HashBased => new CachingResolver(program, innerLogger, telemetryPublisher, resolutionCache),
          CachingMode.Incremental when resolver is null => new InitialIncrementalResolver(program),
          CachingMode.Incremental(var m) when resolver is IncrementalResolver incrementalResolver =>
            new SubsequentIncrementalResolver(program, incrementalResolver!, m),
          _ => throw new UnreachableException(),
        };
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
