#nullable enable
using DafnyCore;
using DafnyCore.IncrementalCompilation;
using DafnyCore.Options;
using DafnyDriver.Commands;
using Microsoft.Boogie;
using Microsoft.Dafny.Compilers;
using Microsoft.Dafny.LanguageServer.Language.Symbols;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static Microsoft.Dafny.LanguageServer.Language.Symbols.DafnyLangSymbolResolver;
using Position = OmniSharp.Extensions.LanguageServer.Protocol.Models.Position;

namespace Microsoft.Dafny;

public static class VerifyCommand {

  static VerifyCommand() {
    // Note these don't need checks because they are only "dafny verify" options;
    // they can't be specified when building a doo file.
    OptionRegistry.RegisterOption(FilterSymbol, OptionScope.Cli);
    OptionRegistry.RegisterOption(FilterPosition, OptionScope.Cli);
    OptionRegistry.RegisterOption(PerformanceStatisticsOption, OptionScope.Cli);
  }

  public static readonly Option<int> PerformanceStatisticsOption = new("--performance-stats",
    "Report a summary of the verification performance. " +
    "The given argument is used to divide all the output with, which can help ignore small differences.") {
    IsHidden = true
  };
  public static readonly Option<string> FilterSymbol = new("--filter-symbol",
    @"Filter what gets verified by selecting only symbols whose fully qualified name contains the given argument, for example: ""--filter-symbol=MyNestedModule.MyFooFunction"". Place a dot at the end of the argument to indicate the symbol name must end like this, which can be useful if one symbol name is a prefix of another.");

  public static readonly Option<string> FilterPosition = new("--filter-position",
    @"Filter what gets verified based on a source location. The location is specified as a file path suffix, optionally followed by a colon and a line number or line range. For example, `dafny verify dfyconfig.toml --filter-position=source1.dfy:5-7` will only verify things that between (and including) line 5 and 7 in the file `source1.dfy`. You can also use `:5`, `:5-`, `:-5` to specify individual lines or open ranges. In combination with `--isolate-assertions`, individual assertions can be verified by filtering on the line that contains them. When processing a single file, the filename can be skipped, for example: `dafny verify MyFile.dfy --filter-position=:23`");

  public static readonly Option<IncrementalCompCommand> IncCompCommand = new(
      name: "--inc-com-command"
    ) {
    Arity = ArgumentArity.ExactlyOne,
    IsHidden = true
  };

  public static Command Create() {
    var result = new Command("verify", "Verify the program.");
    result.AddArgument(DafnyCommands.FilesArgument);
    foreach (var option in VerifyOptions) {
      result.AddOption(option);
    }
    DafnyNewCli.SetHandlerUsingDafnyOptionsContinuation(result, (options, _) => HandleVerification(options));
    return result;
  }

  private static IReadOnlyList<Option> VerifyOptions =>
    new Option[] {
        PerformanceStatisticsOption,
        FilterSymbol,
        FilterPosition,
        DafnyFile.DoNotVerifyDependencies
      }.Concat(DafnyCommands.VerificationOptions).
      Concat(DafnyCommands.ConsoleOutputOptions).
      Concat(DafnyCommands.ResolverOptions);


  public abstract record IncrementalCompCommand;

  public sealed record PrintAllProcessedDafnyCode : IncrementalCompCommand;
  public abstract record PrintSomeProcessedDafnyCode : IncrementalCompCommand;
  public sealed record PrintProcessedDafnyCodeThatWasChanged : PrintSomeProcessedDafnyCode;
  public sealed record PrintProcessedDafnyCodeOfEntryPoints(IReadOnlyList<int> EntryPoints) : PrintSomeProcessedDafnyCode;
  public sealed record PrintAllBoogieCode : IncrementalCompCommand;
  public sealed record PrintBoogieCodeOfModules(IReadOnlyList<string> ModuleNames) : IncrementalCompCommand;
  public sealed record GenerateAllSMT2 : IncrementalCompCommand;
  public sealed record GenerateNeededSMT2 : IncrementalCompCommand;

  public static async Task<int> HandleVerification(DafnyOptions options) {
    options.NormalizeNames = false;
    if (options.Get(CommonOptionBag.VerificationCoverageReport) != null) {
      options.TrackVerificationCoverage = true;
    }
    options.Set(CachingType, CachingMode.Incremental);
    options.Set(IncCompCommand, new GenerateAllSMT2());
    var compilation = CliCompilation.Create(options);
    compilation.Start();

    var resolution = await compilation.Resolution;
    if (resolution is { HasErrors: false }) {
      Subject<CanVerifyResult> verificationResults = new();

      ReportVerificationDiagnostics(compilation, verificationResults);
      var verificationSummarized = ReportVerificationSummary(compilation, verificationResults);
      var proofDependenciesReported = ReportProofDependencies(compilation, resolution, verificationResults);
      var verificationResultsLogged = LogVerificationResults(compilation, resolution, verificationResults);
      compilation.VerifyAllLazily().ToObservable().Subscribe(verificationResults);
      await verificationSummarized;
      await verificationResultsLogged;
      await proofDependenciesReported;

      //(compilation.Compilation.GetType().GetField("boogieEngine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(compilation.Compilation) as ExecutionEngine)!.Dispose();

      async Task Write(string message) {
        await using var trailer = options.OutputWriter.StatusWriter();
        await trailer.WriteAsync(message);
      }
      async Task WriteLine(params string[] messages) {
        await using var trailer = options.OutputWriter.StatusWriter();
        foreach (var message in messages) {
          await trailer.WriteLineAsync(message);
        }
      }
      int AbsPositionFrom(IEnumerable<string> split, Position pos) => split.Take(pos.Line - 1).Sum(s => s.Length) + pos.Character - 1;
      IEnumerable<(string?, string?)> ParseChanges(IEnumerable<string> ss) {
        var withoutNull = new SortedDictionary<int, Dictionary<string, string?>>();
        const string pattern = @"^(?<entryPoint>0|[1-9]\d*)(?<kind>wf|ph): (?<text>.*)$";
        foreach (var s in ss) {
          var match = Regex.Match(s, pattern);

          if (!match.Success) {
            throw new ArgumentException($"{s} couldn't be matched with {pattern}");
          }
          withoutNull.GetOrCreate(int.Parse(match.Groups["entryPoint"].Value), () => [])[match.Groups["kind"].Value] = match.Groups["text"].Value;
        }
        var curr = 0;
        foreach (var (idx, inner) in withoutNull) {
          while (curr < idx) {
            yield return (null, null);
            curr += 1;
          }
          yield return (inner.GetValueOrDefault("wf", null), inner.GetValueOrDefault("ph", null));
          curr += 1;
        }
      }
      async Task<List<string>?> ReadChanges() {
        List<string> modifications = [];
        while (true) {
          await Write("Enter modification or command (type `:h` for help): ");
          var modification = (await options.Input.ReadLineAsync())!;
          switch (modification) {
            case null or ":q": return null;
            case ":h":
              string sep = new('=', 72);
              await WriteLine(
                sep,
                "Command Input Help",
                sep,
                "",
                "Usage:",
                "  ((<index>(wf|ph): <modification> | :h)\\n)* <command>",
                "",
                "",
                "Commands:",
                "  :q                 Quit the program.",
                "  :ad                 Print the processed Dafny code to the console",
                "                   (for debugging purposes).",
                "  :d                  Same as ':d', but restricted to the modified verification tasks.",
                "  :d (<index> )*      Same as ':d', but restricted to the verification tasks that",
                "                   contain the provided indexes. Indexes must be passed as integers",
                "                   separated by only one space.",
                "  :ab                 Print the translation from Dafny code to Boogie code to",
                "                   the console (for debugging purposes).",
                "  :b (<module> )*    Same as ':b', but restricted to the specified modules. Modules",
                "                   must be separated by only one space",
                "  :as                (DEFAULT) Generate all SMT2 files.",
                "  :s                 Generate only the SMT2 files that need regeneration based on the",
                "                   provided modifications.",
                ""
              );
              break;
            case ":ad":
              options.Set(IncCompCommand, new PrintAllProcessedDafnyCode());
              return modifications;
            case ":d":
              options.Set(IncCompCommand, new PrintProcessedDafnyCodeThatWasChanged());
              return modifications;
            case ":b":
              options.Set(IncCompCommand, new PrintAllBoogieCode());
              return modifications;
            case ":as" or "":
              options.Set(IncCompCommand, new GenerateAllSMT2());
              return modifications;
            case ":s":
              options.Set(IncCompCommand, new GenerateNeededSMT2());
              return modifications;
            default:
              if (modification.StartsWith(":d ")) {
                options.Set(IncCompCommand, new PrintProcessedDafnyCodeOfEntryPoints([.. modification[":d ".Length..].Split(' ').Select(int.Parse)]));
                return modifications;
              }
              if (modification.StartsWith(":b ")) {
                options.Set(IncCompCommand, new PrintBoogieCodeOfModules([.. modification[":b ".Length..].Split(' ')]));
                return modifications;
              }
              modifications.Add(modification);
              break;
          }
        }
      }
      while (true) {
        var modifications = await ReadChanges();
        if (modifications is null) { break; }
        ProtectToProveApplySuffix.ChangeTexts = ParseChanges(modifications);
        compilation = CliCompilation.Create(options, compilation);
        compilation.Compilation.RootFiles = compilation.Compilation.RootFiles.Then(files => {
          var changesPerFile = ProtectToProveApplySuffix.ChangesFlattened
                                .Where(c => !c.IsEmptyChange)
                                .GroupBy(c => files.First(file => file.Uri == c.Uri))
                                .Select(g => (g.Key, g.ToImmutableSortedSet(Change.Comparer)));
          foreach (var (file, changes) in changesPerFile) {
            var contents = file.GetContent().Reader.ReadToEnd();
            foreach (var change in changes.Reverse()) {
              List<string> split = [.. contents.SplitIntoLinesAndKeepLineEndings()];
              var (start, end) = (AbsPositionFrom(split, change.Range.Start), AbsPositionFrom(split, change.Range.End));
              contents = contents[..start] + change.Text + contents[end..];
            }
            file.GetContent = () => {
              return new FileSnapshot(new StringReader(contents), null);
            };
          }
        }); //normally this would be replaced by actually getting the file modified
        compilation.Start();
        resolution = await compilation.Resolution;

        if (resolution is { HasErrors: false }) {
          switch (options.Get(IncCompCommand)) {
            case PrintAllProcessedDafnyCode:
              new Printer(options.BaseOutputWriter, options).PrintProgram(resolution.ResolvedProgram, true);
              break;
            case PrintSomeProcessedDafnyCode printSomeProcessedDafnyCode:
              Func<(Change<WF> WF, Change<ProofHint> ProofHint), int, bool> filter = printSomeProcessedDafnyCode switch {
                PrintProcessedDafnyCodeThatWasChanged => static (c, _) => !(c.WF.IsEmptyChange && c.ProofHint.IsEmptyChange),
                PrintProcessedDafnyCodeOfEntryPoints { EntryPoints: var entryPoints } => (_, i) => entryPoints.Contains(i),
                _ => throw new UnreachableException(),
              };
              new Printer(options.BaseOutputWriter, options).PrintMembers(
                [.. ProtectToProveApplySuffix.Changes.Where(filter)
                                                     .Select(static c => c.WF)
                                                     .OfType<IChangeToMemberDecl>()
                                                     .Select(static c => c.MemberDecl)
                                                     .Distinct()],
              0, options.DafnyProject);
              break;
            default:
              verificationResults = new();
              Console.ResetColor();
              ReportVerificationDiagnostics(compilation, verificationResults);
              verificationSummarized = ReportVerificationSummary(compilation, verificationResults);
              proofDependenciesReported = ReportProofDependencies(compilation, resolution, verificationResults);
              verificationResultsLogged = LogVerificationResults(compilation, resolution, verificationResults);
              compilation.VerifyAllLazily().ToObservable().Subscribe(verificationResults);
              await verificationSummarized;
              await verificationResultsLogged;
              await proofDependenciesReported;
              break;
          }
        }
        //(compilation.Compilation.GetType().GetField("boogieEngine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(compilation.Compilation) as ExecutionEngine)!.Dispose();
      }
    }
    return await compilation.GetAndReportExitCode();
  }

  public static async Task ReportVerificationSummary(
    CliCompilation cliCompilation,
    IObservable<CanVerifyResult> verificationResults) {
    var statistics = new VerificationStatistics();

    verificationResults.Subscribe(result => {
      foreach (var taskResult in result.Results) {
        var runResult = taskResult.Result;
        Interlocked.Add(ref statistics.TotalResourcesUsed, runResult.ResourceCount);
        lock (statistics) {
          statistics.MaxVcResourcesUsed = Math.Max(statistics.MaxVcResourcesUsed, runResult.ResourceCount);
        }

        switch (runResult.Outcome) {
          case SolverOutcome.Valid:
          case SolverOutcome.Bounded:
            Interlocked.Increment(ref statistics.VerifiedSymbols);
            Interlocked.Add(ref statistics.VerifiedAssertions, runResult.Asserts.Count);
            break;
          case SolverOutcome.Invalid:
            var total = runResult.Asserts.Count;
            var errors = runResult.CounterExamples.Count;
            Interlocked.Add(ref statistics.VerifiedAssertions, total - errors);
            Interlocked.Add(ref statistics.ErrorCount, errors);
            break;
          case SolverOutcome.TimeOut:
            Interlocked.Increment(ref statistics.TimeoutCount);
            break;
          case SolverOutcome.OutOfMemory:
            Interlocked.Increment(ref statistics.OutOfMemoryCount);
            break;
          case SolverOutcome.OutOfResource:
            Interlocked.Increment(ref statistics.OutOfResourceCount);
            break;
          case SolverOutcome.Undetermined:
            Interlocked.Increment(ref statistics.InconclusiveCount);
            break;
          default:
            throw new ArgumentOutOfRangeException();
        }
      }
    }, e => {
      Interlocked.Increment(ref statistics.SolverExceptionCount);
    });
    await verificationResults.WaitForComplete();
    await WriteTrailer(cliCompilation, statistics);
    var performanceStatisticsDivisor = cliCompilation.Options.Get(PerformanceStatisticsOption);
    if (performanceStatisticsDivisor != 0) {
      int Round(int number) {
        var numberForUpRounding = number + performanceStatisticsDivisor / 2;
        return (numberForUpRounding / performanceStatisticsDivisor) * performanceStatisticsDivisor;
      }
      var output = cliCompilation.Options.OutputWriter;
      await output.Status($"Total resources used is {Round(statistics.TotalResourcesUsed)}");
      await output.Status($"Max resources used by VC is {Round(statistics.MaxVcResourcesUsed)}");
    }
  }

  private static async Task WriteTrailer(CliCompilation cliCompilation,
    VerificationStatistics statistics) {
    if (cliCompilation.Options.Verbosity <= CoreOptions.VerbosityLevel.Quiet) {
      return;
    }

    if (!cliCompilation.DidVerification) {
      return;
    }

    var output = cliCompilation.Options.OutputWriter;

    await using var trailer = output.StatusWriter();
    await trailer.WriteLineAsync();

    if (cliCompilation.VerifiedAssertions) {
      await trailer.WriteAsync($"{cliCompilation.Options.DescriptiveToolName} finished with {statistics.VerifiedAssertions} assertions verified, {statistics.ErrorCount} error{Util.Plural(statistics.ErrorCount)}");
    } else {
      await trailer.WriteAsync($"{cliCompilation.Options.DescriptiveToolName} finished with {statistics.VerifiedSymbols} verified, {statistics.ErrorCount} error{Util.Plural(statistics.ErrorCount)}");
    };
    if (statistics.InconclusiveCount != 0) {
      await trailer.WriteAsync($", {statistics.InconclusiveCount} inconclusive{Util.Plural(statistics.InconclusiveCount)}");
    }

    if (statistics.TimeoutCount != 0) {
      await trailer.WriteAsync($", {statistics.TimeoutCount} time out{Util.Plural(statistics.TimeoutCount)}");
    }

    if (statistics.OutOfMemoryCount != 0) {
      await trailer.WriteAsync($", {statistics.OutOfMemoryCount} out of memory");
    }

    if (statistics.OutOfResourceCount != 0) {
      await trailer.WriteAsync($", {statistics.OutOfResourceCount} out of resource");
    }

    if (statistics.SolverExceptionCount != 0) {
      await trailer.WriteAsync($", {statistics.SolverExceptionCount} solver exceptions");
    }

    await trailer.WriteLineAsync();
  }

  public static void ReportVerificationDiagnostics(CliCompilation compilation, IObservable<CanVerifyResult> verificationResults) {
    verificationResults.Subscribe(result => {
      // We use an intermediate reporter so we can sort the diagnostics from all parts by token
      var batchReporter = new BatchErrorReporter(compilation.Options);
      foreach (var completed in result.Results) {
        Compilation.ReportDiagnosticsInResult(compilation.Options, result.CanVerify.FullDafnyName,
          BoogieGenerator.ToDafnyToken(completed.Task.Token),
          (uint)completed.Result.RunTime.TotalSeconds,
          completed.Result, batchReporter);
      }

      foreach (var diagnostic in batchReporter.AllMessages.Order()) {
        compilation.Compilation.Reporter.MessageCore(diagnostic);
      }
    });

  }

  public static async Task LogVerificationResults(CliCompilation cliCompilation, ResolutionResult resolution,
    IObservable<CanVerifyResult> verificationResults) {
    VerificationResultLogger? verificationResultLogger = null;
    var proofDependencyManager = resolution.ResolvedProgram.ProofDependencyManager;
    try {
      verificationResultLogger = new VerificationResultLogger(cliCompilation.Options, proofDependencyManager);
    } catch (ArgumentException e) {
      cliCompilation.Compilation.Reporter.Error(MessageSource.Verifier, cliCompilation.Compilation.Project.StartingToken, e.Message);
    }

    verificationResults.Subscribe(result => verificationResultLogger?.Report(result),
      e => { },
      () => {
      });
    await verificationResults.WaitForComplete();
    if (verificationResultLogger != null) {
      await verificationResultLogger.Finish();
    }
  }

  public static async Task ReportProofDependencies(
    CliCompilation cliCompilation,
    ResolutionResult resolution,
    IObservable<CanVerifyResult> verificationResults) {
    var usedDependencies = new HashSet<TrackedNodeComponent>();
    var proofDependencyManager = resolution.ResolvedProgram.ProofDependencyManager;

    verificationResults.Subscribe(result => {
      ProofDependencyWarnings.ReportSuspiciousDependencies(cliCompilation.Options, result.Results,
        resolution.ResolvedProgram.Reporter, resolution.ResolvedProgram.ProofDependencyManager);

      foreach (var used in result.Results.SelectMany(part => part.Result.CoveredElements)) {
        usedDependencies.Add(used);
      }
    }, e => { }, () => { });
    await verificationResults.WaitForComplete();
    var coverageReportDir = cliCompilation.Options.Get(CommonOptionBag.VerificationCoverageReport);
    if (coverageReportDir != null) {
      await new CoverageReporter(cliCompilation.Options).SerializeVerificationCoverageReport(
        proofDependencyManager, resolution.ResolvedProgram,
        usedDependencies,
        coverageReportDir);
    }
  }
}
