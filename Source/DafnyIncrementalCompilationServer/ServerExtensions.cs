using Microsoft.Dafny.IncrementalCompilation.Plugins;
using Microsoft.Dafny.IncrementalCompilation.Workspace;
using Microsoft.Dafny.LanguageServer;
using Microsoft.Dafny.LanguageServer.Handlers;
using Microsoft.Dafny.LanguageServer.Handlers.Custom;
using Microsoft.Dafny.LanguageServer.Language;
using Microsoft.Dafny.LanguageServer.Language.Symbols;
using Microsoft.Dafny.LanguageServer.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Server;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Dafny.IncrementalCompilation {

  public static class ServerExtensions {
    public static JsonRpcServerOptions WithDafnyLanguage(this JsonRpcServerOptions options) {
      return options.WithServices(services => services.WithDafnyLanguage());
    }
    private static IServiceCollection WithDafnyLanguage(this IServiceCollection services) {
      return services
        .AddSingleton<IDafnyParser>(serviceProvider => new DafnyLangParser(
          serviceProvider.GetRequiredService<DafnyOptions>(),
          serviceProvider.GetRequiredService<IFileSystem>(),
          serviceProvider.GetRequiredService<TelemetryPublisherBase>(),
          serviceProvider.GetRequiredService<ILogger<DafnyLangParser>>(),
          serviceProvider.GetRequiredService<ILogger<CachingParser>>()
        ))
        .AddSingleton<ISymbolResolver, DafnyLangSymbolResolver>()
        .AddSingleton<IProgramVerifier>(serviceProvider => new DafnyProgramVerifier(
          serviceProvider.GetRequiredService<ILogger<DafnyProgramVerifier>>()
        ))
        .AddSingleton<CreateCompilation>(serviceProvider => (engine, compilation) => new Compilation(
          serviceProvider.GetRequiredService<ILogger<Compilation>>(),
          serviceProvider.GetRequiredService<IFileSystem>(),
          serviceProvider.GetRequiredService<ITextDocumentLoader>(),
          serviceProvider.GetRequiredService<IProgramVerifier>(),
          engine, compilation
        ));
    }

    public static JsonRpcServerOptions WithDafnyWorkspace(this JsonRpcServerOptions options) {
      return options.WithServices(services => services.WithDafnyWorkspace());
    }
    public static IServiceCollection WithDafnyWorkspace(this IServiceCollection services) {
      return services
        .AddSingleton<Workspace.CreateProjectManager>(serviceProvider => (stackSize, project) => new Workspace.ProjectManager(
          serviceProvider.GetRequiredService<DafnyOptions>(),
          stackSize,
          project,
          serviceProvider.GetRequiredService<ILogger<Workspace.ProjectManager>>(),
          serviceProvider.GetRequiredService<CreateCompilation>()
        ))
        .AddSingleton<FileSystem>(serviceProvider => new(
          serviceProvider.GetRequiredService<ILogger<FileSystem>>(),
          serviceProvider.GetRequiredService<DafnyOptions>().CliRootSourceUris.AsReadOnly()
        ))
        .AddSingleton<ITextDocumentLoader, TextDocumentLoader>()
        .AddSingleton<TelemetryPublisherBase, IncrementalCompilationTelemetryPublisher>();
    }

    // TODO: change to property when updating to C#14
    public static IncCompModification? Modification(this DafnyOptions dafnyOptions) =>
    (dafnyOptions.Get(DafnyLangSymbolResolver.CachingType) as DafnyLangSymbolResolver.CachingMode.Incremental)!.Modification;
    public static void Modification(this DafnyOptions dafnyOptions, IncCompModification? modification) =>
      dafnyOptions.Set(DafnyLangSymbolResolver.CachingType, new DafnyLangSymbolResolver.CachingMode.Incremental(modification));

  }
}
