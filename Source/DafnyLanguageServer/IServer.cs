using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Server;
using Serilog;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Dafny.LanguageServer {
  public interface IServer<Self> where Self : IServer<Self> {
    protected class LogWriter : TextWriter {
      public override void Write(char value) {
        Log.Logger.Verbose("Unexpected console output: {value}", value);
      }

      public override void Write(string? value) {
        Log.Logger.Verbose("Unexpected console output: {value}", value);
      }

      public override Encoding Encoding { get; } = Encoding.ASCII;
    }

    protected abstract static string ServerKind { get; }
    public abstract static IEnumerable<Option> Options { get; }

    // TODO: make this virtual when default implementation can be referenced in classes implementing this
    public static void ConfigureDafnyOptionsForServer(DafnyOptions dafnyOptions) { }

    public static abstract Task Start(DafnyOptions dafnyOptions);

    protected static IConfiguration CreateConfiguration() {
      return new ConfigurationBuilder()
        .AddJsonFile($"Dafny{Self.ServerKind}Server.appsettings.json", optional: true)
        .Build();
    }
    protected static void InitializeLogger(DafnyOptions options, IConfiguration configuration) {
      // The environment variable is used so a log file can be explicitly created in the application dir.
      var logLevel = options.Get(CommonOptionBag.LogLevelOption);
      var logLocation = options.Get(CommonOptionBag.LogLocation) ?? Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
      Environment.SetEnvironmentVariable("DAFNYLS_APP_DIR", logLocation);
      LoggerConfiguration config = new LoggerConfiguration()
        .ReadFrom.Configuration(configuration)
        .MinimumLevel.Override("Microsoft.Dafny", logLevel);
      if (logLocation != null) {
        var logFile = Path.Combine(logLocation,
          $"Dafny{Self.ServerKind}ServerLog" + DateTime.Now.ToString().Replace("/", "_").Replace("\\", "_") + ".txt");
        config = config.WriteTo.File(logFile);
      }
      Log.Logger = config.CreateLogger();
    }
    protected static void LogException(Exception exception) {
      Log.Logger.Error(exception, "captured unhandled exception");
    }
    protected static void SetupLogging(ILoggingBuilder builder) {
      builder
        .ClearProviders()
        .AddSerilog(Log.Logger);
    }
  }
}
