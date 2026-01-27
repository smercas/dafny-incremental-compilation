using Microsoft.Boogie;
using Microsoft.Dafny.LanguageServer.Workspace;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Dafny.IncrementalCompilation.Workspace {
  public class GeneralManager { // TODO: fold into projectManager if redundant and when you can figure out how
    private const int StackSize = 10 * 1024 * 1024;

    private readonly ILogger<GeneralManager> logger;
    private readonly ProjectManager manager;
    private readonly FileSystem fileSystem;

    public GeneralManager(
      ProjectManager manager,
      FileSystem fileSystem,
      ILogger<GeneralManager> logger
    ) {
      this.manager = manager;
      this.fileSystem = fileSystem;
      this.logger = logger;
    }
    public static async Task<GeneralManager> Create(
      FileSystem fileSystem,
      DafnyOptions serverOptions,
      CreateProjectManager createProjectManager,
      ILogger<GeneralManager> logger
    ) {
      var firstFile = serverOptions.CliRootSourceUris.First();
      // TODO: figure out if `Token.Ide` is ok for `ProjectFileOpener` init
      var project = serverOptions.DafnyProject ??
                    await new ProjectFileOpener(fileSystem, Token.Ide).TryFindProject(firstFile) ??
                    ProjectManagerDatabase.ImplicitProject(firstFile); // fuck if I know; TODO: find out
      return new(createProjectManager(StackSize, project), fileSystem, logger);
    }
    public void ApplyModification(IncCompModification modification) {
      fileSystem.ApplyModification(modification);
      manager.ApplyModification(modification);
    }
  }
}
