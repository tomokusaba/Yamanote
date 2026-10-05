using Microsoft.Extensions.Hosting;

namespace WalkLogger.App;

internal sealed class WpfHostLifetime : IHostLifetime
{
    // The WPF dispatcher owns the event loop; App starts and stops the host.
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
