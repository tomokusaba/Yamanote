using Microsoft.Extensions.Hosting;

namespace WalkLogger.App;

internal sealed class HostSmokeProbe : IHostedService, IDisposable
{
    public bool Started { get; private set; }
    public bool Stopped { get; private set; }
    public bool Disposed { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Started = true;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(25, cancellationToken);
        Stopped = true;
    }

    public void Dispose() => Disposed = true;
}
