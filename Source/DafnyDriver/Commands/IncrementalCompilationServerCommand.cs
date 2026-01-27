using Microsoft.Dafny.LanguageServer.Language.Symbols;
using System.CommandLine;

namespace Microsoft.Dafny.IncrementalCompilation;

public class IncrementalCompilationServerCommand {
  public static readonly Command Instance = Create();

  private IncrementalCompilationServerCommand() {
  }

  static IncrementalCompilationServerCommand() {
    //OptionRegistry.RegisterOption(DafnyLangSymbolResolver.CachingType, OptionScope.Cli);
    //OptionRegistry.RegisterOption(LanguageServer.Workspace.ProjectManager.ReuseSolvers, OptionScope.Cli);
    // // not sure if these need to be included, since the language server already registers them
  }

  private static Command Create() {
    var result = new Command("inc-comp-server", "Start the Dafny incremental compilation server");
    result.Add(DafnyCommands.FilesArgument);
    foreach (var option in Server.Options) {
      result.Add(option);
    }
    DafnyNewCli.SetHandlerUsingDafnyOptionsContinuation(result, static async (options, context) => {
      options.Set(DafnyFile.DoNotVerifyDependencies, true);
      Server.ConfigureDafnyOptionsForServer(options);
      await Server.Start(options);
      return 0;
    });
    return result;
  }

}
