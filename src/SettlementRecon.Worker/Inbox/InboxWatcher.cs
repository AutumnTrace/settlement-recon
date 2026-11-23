using Microsoft.Extensions.Options;
using SettlementRecon.Pipeline;

namespace SettlementRecon.Worker.Inbox;

/// <summary>
/// Polls the inbox for run folders. A folder is ready once it contains <c>run.json</c>, which uploaders write last.
/// Polling with <see cref="PeriodicTimer"/> instead of <see cref="FileSystemWatcher"/> works the same on local disks,
/// network shares and container volumes, where file change notifications are unreliable.
/// </summary>
public sealed class InboxWatcher(
    RunProcessor processor,
    IOptions<InboxOptions> options,
    TimeProvider timeProvider,
    ILogger<InboxWatcher> logger) : BackgroundService
{
    private readonly InboxOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _options.EnsureFolders();
        processor.RequeueInterrupted();
        Log.Watching(logger, _options.InboxPath, _options.PollInterval);

        using var timer = new PeriodicTimer(_options.PollInterval, timeProvider);
        try
        {
            do
            {
                await ScanOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown. A run interrupted mid-way stays in processing/ and is re-queued on the next start.
        }
    }

    /// <summary>Processes every ready folder once, oldest name first. Returns how many runs were stored.</summary>
    public async Task<int> ScanOnceAsync(CancellationToken cancellationToken)
    {
        var stored = 0;
        var ready = Directory.EnumerateDirectories(_options.InboxPath)
            .Where(d => File.Exists(Path.Combine(d, RunManifest.FileName)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreach (var folder in ready)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await processor.ProcessAsync(folder, cancellationToken).ConfigureAwait(false) is not null)
            {
                stored++;
            }
        }

        return stored;
    }
}
