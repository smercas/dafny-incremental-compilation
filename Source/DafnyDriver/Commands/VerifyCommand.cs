#nullable enable
using DafnyCore;
using DafnyCore.IncrementalCompilation;
using DafnyCore.Options;
using DafnyDriver.Commands;
using DafnyTestGeneration;
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



  
  public static async Task<int> HandleVerification(DafnyOptions options) {
    options.EmitDebugInformation = true;
    options.NormalizeNames = false;
    if (options.Get(CommonOptionBag.VerificationCoverageReport) != null) {
      options.TrackVerificationCoverage = true;
    }
    options.Set(CachingType, CachingMode.Incremental);
    options.Set(IncCompCommand.Option, new GenerateAllSMT2Code());
    options.Profiler = new ExecutionEngineOptions.ActualProfiler();

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
      async Task<bool> InputrocessingLoop() {
        async Task<bool?> readAndProcessInput() {
          await Write("Enter modification or command (type `:h` for help): ");
          var input = await options.Input.ReadLineAsync();
          switch (input) {
            case null or ":q": return null;
            case ":h":
              string sep = new('=', 72);
              await WriteLine( // IPMTODO: update with :h, :r and :c, explaining what each does, :r resets the changes and :c displays them
                sep,
                "Command Input Help",
                sep,
                "",
                "Usage:",
                "  ((<entryPoint>(wf|ph|ii|im): <modification> | <entryPoint>im<branchIndex>: <modification> | <command>)\\n)*",
                "",
                "",
                "Commands:",
                "  :q                 Quit the program.",
                "  :w                 Write changed dafny files into separate files",
                "                   bearing the same name and the \".dfyf\" extension",
                "  :ad                Print the entirety of the processed Dafny code",
                "                   to the console (for debugging purposes).",
                "  :d                 Print the processed Dafny code of the modified",
                "                   verification tasks.",
                "  :d i (<index> )*   Print the processed Dafny code of the verification",
                "                   tasks that contain the provided indexes. Indexes",
                "                   must be passed as integers separated by only one",
                "                   space.",
                "  :d s (<symbol> )*  Print the processed Dafny code of the specified",
                "                   symbol. Symbols must be separated by only one",
                "                   space.",
                "  :ab                Print the translation from Dafny code to Boogie",
                "                   code to the console (for debugging purposes).",
                "  :b                 Print the translation from Dafny code to Boogie",
                "                   code of the modified modules.",
                "  :b m (<module> )*  Print the translation from Dafny code to Boogie",
                "                   code of the specified modules. Modules must be",
                "                   separated by only one space.",
                "  :as                Generate all SMT2 files.",
                "  :s                 (DEFAULT) Generate only the SMT2 files that need",
                "                   regeneration based on the provided modifications.",
                "  :c                 Display currently active changes",
                "  :r                 Clear active changes",
                ""
              );
              return true;
            case ":w":
              options.Set(IncCompCommand.Option, new WriteFormattedDafnyCodeWithChanges());
              return false;
            case ":ad":
              options.Set(IncCompCommand.Option, new PrintAllProcessedDafnyCode());
              return false;
            case ":d":
              options.Set(IncCompCommand.Option, new PrintProcessedDafnyCodeOfChangedVerificationTasks());
              return false;
            case ":ab":
              options.Set(IncCompCommand.Option, new PrintAllBoogieCode());
              return false;
            case ":b":
              options.Set(IncCompCommand.Option, new PrintBoogieCodeOfChangedVerificationTasks());
              return false;
            case ":as":
              options.Set(IncCompCommand.Option, new GenerateAllSMT2Code());
              return false;
            case ":s" or "":
              options.Set(IncCompCommand.Option, new GenerateSMT2CodeOfChangedVerificationTasks());
              return false;
            case ":r":
              ProtectToProveApplySuffix.EmptyAllChanges();
              return true;
            case ":c": // IPMTODO: see if this works
              foreach (var (forEntryPoint, entryPoint) in ProtectToProveApplySuffix.ChangesPerEntryPoint.Indexed()) {
                var nonEmpty = forEntryPoint.Where(c => !c.IsEmptyChange).ToList();
                if (nonEmpty.Count > 0) {
                  await WriteLine($"for entry point {entryPoint}:");
                  foreach (var change in nonEmpty) {
                    await WriteLine($"\t{change.GetType().Name} ==> {change.Text}");
                  }
                }
              }
              return true;
            default:
              if (input.StartsWith(":d i ")) {
                options.Set(IncCompCommand.Option, new PrintProcessedDafnyCodeOfEntryPoints(input[":d i ".Length..].Split(' ').ConvertAll(int.Parse)));
                return false;
              }
              if (input.StartsWith(":d s ")) {
                options.Set(IncCompCommand.Option, new PrintProcessedDafnyCodeOfSymbols(input[":d s ".Length..].Split(' ').ConvertAll(static s => s.Split('.'))));
                return false;
              }
              if (input.StartsWith(":b m ")) {
                options.Set(IncCompCommand.Option, new PrintBoogieCodeOfModules(input[":b m ".Length..].Split(' ').ConvertAll(static s => s.Split('.'))));
                return false;
              }
              try {
                ProtectToProveApplySuffix.ModifyChangesWith(input);
              } catch (Exception e) {
                await WriteLine(e.Message);
              }
              return true;
          }
        }
        while (true) {
          var stayInLoop = await readAndProcessInput();
          if (stayInLoop is null) { return false; }
          if (stayInLoop is false) { break; }
        }
        return true;
      }
      var originalBoogieFile = options.Get(DeveloperOptionBag.BoogiePrint);
      while (true) {
        if (await InputrocessingLoop() is false) { break; }
        if (options.Get(IncCompCommand.Option) is PrintAllBoogieCode or PrintBoogieCodeOfModules or PrintBoogieCodeOfChangedVerificationTasks) {
          options.Set(DeveloperOptionBag.BoogiePrint, "-");
        } else {
          options.Set(DeveloperOptionBag.BoogiePrint, originalBoogieFile);
        }
        options.ApplyBinding(DeveloperOptionBag.BoogiePrint);
        compilation = CliCompilation.Create(options, compilation);
        compilation.Compilation.RootFiles = compilation.Compilation.RootFiles.Then(files => {
          var changesPerFile = ProtectToProveApplySuffix.AggregatedNonEmptyChanges
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
          if (options.Get(IncCompCommand.Option) is GenerateBoogie) {
            verificationResults = new();
            ReportVerificationDiagnostics(compilation, verificationResults);
            verificationSummarized = ReportVerificationSummary(compilation, verificationResults);
            proofDependenciesReported = ReportProofDependencies(compilation, resolution, verificationResults);
            verificationResultsLogged = LogVerificationResults(compilation, resolution, verificationResults);
            compilation.VerifyAllLazily().ToObservable().Subscribe(verificationResults);
            await verificationSummarized;
            await verificationResultsLogged;
            await proofDependenciesReported;
          } else {
            var printer = new Printer(options.BaseOutputWriter, options);
            switch (options.Get(IncCompCommand.Option)) {
              case WriteFormattedDafnyCodeWithChanges:
                var changedURIs = ProtectToProveApplySuffix.AggregatedNonEmptyChanges.Select(c => c.Uri).ToImmutableHashSet();
                var parsedProgram = (await compilation.Compilation.ParsedProgram)!;
                foreach (var file in await compilation.Compilation.RootFiles) {
                  if (!changedURIs.Contains(file.Uri)) { continue; }
                  var firstToken = parsedProgram.GetFirstTokenForUri(file.Uri)!; // null only if file is empty, which can't be the case, since it has at least a non-empty change
                  FileSnapshot? snapshot = null;
                  string? originalText = null;
                  try {
                    snapshot = file.GetContent();
                    originalText = await snapshot.Reader.ReadToEndAsync();
                  } finally {
                    // I'm not gonna bother, if this throws it's destined to throw
                    snapshot!.Reader.Close();
                    file.GetContent = () => snapshot with { Reader = new StringReader(originalText!) };
                  }
                  var formatted = Formatting.__default.ReindentProgramFromFirstToken(firstToken, IndentationFormatter.ForProgram(parsedProgram, file.Uri));
                  if (formatted == originalText) { continue; } // file is perfectly formatted :)
                  var path = $"{file.FilePath}f";
                  SynchronousCliCompilation.WriteFile(path, formatted);
                  await WriteLine($"wrote newly formatted version of {file.FilePath} to {path}");
                }
                break;
              case PrintAllProcessedDafnyCode:
                printer.PrintProgram(resolution.ResolvedProgram, true);
                break;
              case PrintSomeProcessedDafnyCode printSomeProcessedDafnyCode:
                switch (printSomeProcessedDafnyCode) {
                  case PrintProcessedDafnyCodeOfSymbols { SymbolNames: var symbolNames }:
                    foreach (var toPrint in symbolNames.Select(sns => {
                      LiteralModuleDecl result = resolution.ResolvedProgram.DefaultModule;
                      foreach (var sn in sns.SkipLast(1)) {
                        result = result.ChildSymbols.OfType<LiteralModuleDecl>().First(tld => tld.Name == sn);
                      }
                      return result.ChildSymbols.First(tld => (tld as Declaration)!.Name == sns[^1]);
                    })) {
                      switch (toPrint) {
                        case MemberDecl memberDecl:
                          printer.PrintMembers([memberDecl], 0, options.DafnyProject);
                          break;
                        case TopLevelDecl topLevelDecl:
                          printer.PrintTopLevelDecls(resolution.ResolvedProgram.Compilation, [topLevelDecl], 0, null);
                          break;
                        default:
                          throw new UnreachableException();
                      }
                    }
                    break;
                  case PrintProcessedDafnyCodeOfChangedVerificationTasks:
                    printer.PrintMembers(
                      [.. ProtectToProveApplySuffix.ChangedMembers],
                    0, options.DafnyProject);
                    break;
                  case PrintProcessedDafnyCodeOfEntryPoints { EntryPoints: var entryPoints }:
                    printer.PrintMembers(
                      [.. ProtectToProveApplySuffix.ChangesPerEntryPoint.Where((_, i) => entryPoints.Contains(i))
                                                         .Select(static c => c[0])
                                                         .OfType<IChangeToMemberDecl>()
                                                         .Select(static c => c.MemberDecl)
                                                         .Distinct()],
                    0, options.DafnyProject);
                    break;
                  default: throw new UnreachableException();
                }
                break;
              case GenerateBoogie: default: throw new UnreachableException();
            }
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
