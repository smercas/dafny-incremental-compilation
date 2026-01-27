using Microsoft.Dafny.LanguageServer;
using Microsoft.Dafny.LanguageServer.Language.Symbols;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using OmniSharp.Extensions.JsonRpc;
using Serilog;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Diagnostics.Contracts;
using System.Threading.Tasks;

namespace Microsoft.Dafny.IncrementalCompilation {
  public class Server : IServer<Server> {
    public static string ServerKind => "IncrementalCompilation";

    public static IEnumerable<Option> Options => new Option[] {
        DafnyLangSymbolResolver.CachingType,
        LanguageServer.Workspace.ProjectManager.ReuseSolvers,
        DeveloperOptionBag.SplitPrint,
        DeveloperOptionBag.PassivePrint,
        DeveloperOptionBag.BoogiePrint,
      }.Concat(DafnyCommands.VerificationOptions).
      Concat(DafnyCommands.ResolverOptions);

    // TODO: remove this when static virtual works in this case
    public static void ConfigureDafnyOptionsForServer(DafnyOptions dafnyOptions) => IServer<Server>.ConfigureDafnyOptionsForServer(dafnyOptions);

    public static async Task Start(DafnyOptions dafnyOptions) {
      var configuration = IServer<Server>.CreateConfiguration();
      IServer<Server>.InitializeLogger(dafnyOptions, configuration);

      dafnyOptions = new DafnyOptions(dafnyOptions, true);

      try {
        var host = Host.CreateDefaultBuilder()
          .ConfigureServices(services => {
            services
              .AddJsonRpcServer(options => options
                .WithServices(s => s.AddSingleton(dafnyOptions))
                .WithInput(Console.OpenStandardInput())
                .WithOutput(Console.OpenStandardOutput())
                .WithDafnyLanguage()
                .WithDafnyWorkspace()
                .WithUnhandledExceptionHandler(IServer<Server>.LogException)
                .WithHandler<EchoHandler>()
              )
              .AddHostedService<Startup>();

            //services.AddHostedService<JsonRpcBootstrapper>();
          })
          .ConfigureAppConfiguration(builder => builder.AddConfiguration(configuration))
          .ConfigureLogging(IServer<Server>.SetupLogging)
          .Build();
        await using var logWriter = new IServer<Server>.LogWriter();
        Console.SetOut(logWriter);
        await host.RunAsync();
      } finally {
        await Log.CloseAndFlushAsync();
      }
    }
  }
}
