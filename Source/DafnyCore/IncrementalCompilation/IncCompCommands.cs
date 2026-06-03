#nullable enable
using System.Collections.Generic;
using System.CommandLine;

namespace Microsoft.Dafny;

public abstract record IncCompCommand {
  public static readonly Option<IncCompCommand?> Option = new(
      name: "--inc-com-command"
    ) {
    Arity = ArgumentArity.ExactlyOne,
    IsHidden = true
  };
}

public sealed record WriteFormattedDafnyCodeWithChanges : IncCompCommand;
public sealed record PrintAllProcessedDafnyCode : IncCompCommand;
public abstract record PrintSomeProcessedDafnyCode : IncCompCommand;
public sealed record PrintProcessedDafnyCodeOfChangedVerificationTasks : PrintSomeProcessedDafnyCode;
public sealed record PrintProcessedDafnyCodeOfEntryPoints(IReadOnlyList<int> EntryPoints) : PrintSomeProcessedDafnyCode;
public sealed record PrintProcessedDafnyCodeOfSymbols(IReadOnlyList<IReadOnlyList<string>> SymbolNames) : PrintSomeProcessedDafnyCode;

public abstract record GenerateBoogie : IncCompCommand;
public abstract record GenerateAllBoogie : GenerateBoogie;
public sealed record PrintAllBoogieCode : GenerateAllBoogie;
public sealed record GenerateAllSMT2Code : GenerateAllBoogie;
public abstract record GenerateSomeBoogie : GenerateBoogie;
public sealed record PrintBoogieCodeOfModules(IReadOnlyList<IReadOnlyList<string>> ModuleNames) : GenerateSomeBoogie;
public abstract record GeneratedBoogieCodeOfChangedVerificationTasks : GenerateSomeBoogie;
public sealed record PrintBoogieCodeOfChangedVerificationTasks : GeneratedBoogieCodeOfChangedVerificationTasks;
public sealed record GenerateSMT2CodeOfChangedVerificationTasks : GeneratedBoogieCodeOfChangedVerificationTasks;