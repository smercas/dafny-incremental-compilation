using Microsoft.Dafny;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Boogie;

namespace DafnyCore.IncrementalCompilation {
  internal class ProtectorsImporter(DafnyOptions afnyOptions) {
    private DafnyOptions dafnyOptions = afnyOptions;
    private AliasModuleDecl ImportDecl(ModuleDefinition parent) => new(
      dafnyOptions,
      new(Microsoft.Dafny.Token.NoToken, Microsoft.Dafny.Token.NoToken),
      new([ new(new Microsoft.Dafny.Token() { val = ProtectorFunctions.ContainingModuleName }, ProtectorFunctions.ContainingModuleName) ]),
      new(ProtectorFunctions.ContainingModuleName),
      null,
      parent,
      opened: false,
      [],
      Guid.NewGuid()
    );

    public void ImportIn(Microsoft.Dafny.Program p) => p.DefaultModuleDef.SourceDecls.OfType<LiteralModuleDecl>().Where(lmd => lmd.ModuleDef.Implements is null).ForEach(ImportIn);
    public void ImportIn(LiteralModuleDecl md) {
      md.ModuleDef.SourceDecls.Add(ImportDecl(md.ModuleDef));
      Microsoft.Dafny.Util.Concat(
        md.ModuleDef.SourceDecls.OfType<LiteralModuleDecl>(),
        md.ModuleDef.PrefixNamedModules.Select(pnm => pnm.Module)
      ).Where(lmd => lmd.ModuleDef.Implements is null).ForEach(ImportIn);
    }
  }
}
