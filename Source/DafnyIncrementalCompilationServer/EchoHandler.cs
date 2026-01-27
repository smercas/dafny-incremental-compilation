using MediatR;
using Microsoft.Dafny.LanguageServer;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Dafny.IncrementalCompilation {
  public sealed class EchoHandler
  : IJsonRpcRequestHandler<EchoHandler.Params, string> {
    [Parallel]
    [Method("echo", Direction.ClientToServer)]
    public sealed record Params(string Text) : IRequest<string>;

    public Task<string> Handle(Params request, CancellationToken _) => Task.FromResult(request.Text);
  }
}
