using Microsoft.Extensions.Hosting;
using OmniSharp.Extensions.JsonRpc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Dafny.IncrementalCompilation;

sealed class Startup : IHostedService {
  private readonly JsonRpcServer RpcServer;

  public Startup(
    JsonRpcServer rpcServer
  ) {
    RpcServer = rpcServer;
  }

  public async Task StartAsync(CancellationToken cancellationToken) {
    await RpcServer.Initialize(cancellationToken);
  }

  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

