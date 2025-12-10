#nullable enable
using Dafny;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Dafny;

public abstract class IncCompModification {
}

// the scope of modifications most likely will always be constrained to that of a module declaration
public abstract class ModificationToModuleDeclaration : IncCompModification {
  public class Pair<T>(T prev) where T : class {
    public T Old { get; private init; } = prev;
    private T? newlyProcessed = null;
    public T NewlyProcessed {
      get {
        Contract.Requires(newlyProcessed != null);
        return newlyProcessed!;
      }
      set {
        Contract.Requires(newlyProcessed == null);
        newlyProcessed = value;
      }
    }
  }
  public abstract Pair<ModuleDecl> AffectedModuleDecl { get; }
  public bool HasBeenProcessed => AffectedModuleDecl.NewlyProcessed != null;
}

public class AppendStatementToMethod(Method method) : ModificationToModuleDeclaration {
  private Pair<ModuleDecl> affectedModuleDecl = new(method.EnclosingClass.EnclosingModuleDefinition.EnclosingLiteralModuleDecl!);
  public override Pair<ModuleDecl> AffectedModuleDecl => affectedModuleDecl;
  public Method Method => method;
}
