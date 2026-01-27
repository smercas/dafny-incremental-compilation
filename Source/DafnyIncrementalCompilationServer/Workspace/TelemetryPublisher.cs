using Microsoft.Dafny.LanguageServer.Workspace;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Dafny.IncrementalCompilation.Workspace {
  internal class IncrementalCompilationTelemetryPublisher(ILogger<TelemetryPublisherBase> logger) : TelemetryPublisherBase(logger) {
    public override void PublishTelemetry(ImmutableDictionary<string, object> data) {
    }
  }
}
