using DafnyCore;
using Microsoft.Dafny.LanguageServer.Language;
using Microsoft.Dafny.LanguageServer.Language.Symbols;
using Microsoft.Dafny.LanguageServer.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OmniSharp.Extensions.LanguageServer.Server;
using Serilog;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Dafny.LanguageServer {
  public class LanguageServer : IServer<LanguageServer> {
    public static string ServerKind => "Language";

    public static IEnumerable<Option> Options => new Option[] {
        BoogieOptionBag.NoVerify,
        ProjectManager.Verification,
        GhostStateDiagnosticCollector.GhostIndicators,
        GutterIconAndHoverVerificationDetailsManager.LineVerificationStatus,
        VerifySnapshots,
        DafnyLangSymbolResolver.CachingType,
        ProjectManager.UpdateThrottling,
        CachingProjectFileOpener.ProjectFileCacheExpiry,
        DeveloperOptionBag.SplitPrint,
        DeveloperOptionBag.PassivePrint,
        DeveloperOptionBag.BoogiePrint,
        InternalDocstringRewritersPluginConfiguration.UseJavadocLikeDocstringRewriterOption,
        LegacySignatureAndCompletionTable.MigrateSignatureAndCompletionTable
      }.Concat(DafnyCommands.VerificationOptions).
      Concat(DafnyCommands.ResolverOptions);

    public static readonly Option<uint> VerifySnapshots = new("--cache-verification", @"
(experimental)
0 - do not use any verification result caching (default)
1 - use the basic verification result caching
2 - use the more advanced verification result caching
3 - use the more advanced caching and report errors according
  to the new source locations for errors and their
  related locations
".TrimStart()) {
      ArgumentHelpName = "level"
    };

    public static void ConfigureDafnyOptionsForServer(DafnyOptions dafnyOptions) {
      dafnyOptions.Set(Snippets.ShowSnippets, true);
    }

    public static async Task Start(DafnyOptions dafnyOptions) {
      var configuration = IServer<LanguageServer>.CreateConfiguration();
      IServer<LanguageServer>.InitializeLogger(dafnyOptions, configuration);

      dafnyOptions = new DafnyOptions(dafnyOptions, true);
      try {
        Action? shutdownServer = null;
        var server = await OmniSharp.Extensions.LanguageServer.Server.LanguageServer.From(
          options => options
            .WithServices(s => s.AddSingleton(dafnyOptions))
            .WithInput(Console.OpenStandardInput())
            .WithOutput(Console.OpenStandardOutput())
            .ConfigureConfiguration(builder => builder.AddConfiguration(configuration))
            .ConfigureLogging(IServer<LanguageServer>.SetupLogging)
            .WithUnhandledExceptionHandler(IServer<LanguageServer>.LogException)
            // ReSharper disable once AccessToModifiedClosure
            .WithDafnyLanguageServer(dafnyOptions, () => shutdownServer!())
        );
        // Prevent any other parts of the language server to actually write to standard output.
        await using var logWriter = new IServer<LanguageServer>.LogWriter();
        Console.SetOut(logWriter);
        shutdownServer = () => server.ForcefulShutdown();
        await server.WaitForExit;
      } finally {
        await Log.CloseAndFlushAsync();
      }
    }
  }
}
