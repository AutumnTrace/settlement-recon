using Microsoft.Extensions.Options;
using SettlementRecon.Formats;
using SettlementRecon.Pipeline;
using SettlementRecon.Worker.Storage;

namespace SettlementRecon.Worker.Inbox;

/// <summary>
/// Processes one run folder: claim it (atomic rename into <c>processing/</c>), reconcile, store, archive.
/// A folder that cannot be reconciled goes to <c>failed/</c> with an <c>error.txt</c> explaining why.
/// </summary>
public sealed class RunProcessor(
    ReconciliationEngine engine,
    SettlementFormatRegistry formats,
    IRunStore store,
    IOptions<InboxOptions> options,
    TimeProvider timeProvider,
    ILogger<RunProcessor> logger)
{
    private readonly InboxOptions _options = options.Value;

    /// <returns>The stored run id, or null when the folder failed or could not be claimed.</returns>
    public async Task<string?> ProcessAsync(string folder, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(folder);
        var claimed = Path.Combine(_options.ProcessingPath, name);
        try
        {
            Directory.Move(folder, claimed);
        }
        catch (IOException ex)
        {
            Log.ClaimFailed(logger, ex, name);
            return null;
        }

        Log.PickedUp(logger, name);
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["RunFolder"] = name });
        try
        {
            var manifest = await RunManifest.ReadAsync(claimed, cancellationToken).ConfigureAwait(false);
            var request = manifest.ToRequest(claimed, formats);
            var report = await engine.RunAsync(request, cancellationToken).ConfigureAwait(false);

            await store.SaveAsync(new StoredRun(report.RunId, name, manifest.Label, report), cancellationToken).ConfigureAwait(false);
            Directory.Move(claimed, Unique(Path.Combine(_options.ProcessedPath, $"{name}-{report.RunId}")));

            var summary = report.Summary;
            Log.RunCompleted(logger, report.RunId, name, summary.SettlementLines, summary.LedgerEntries, report.DurationMs, summary.Breaks, summary.MatchRate);
            if (summary.RejectedLines > 0)
            {
                Log.RejectedLines(logger, report.RunId, summary.RejectedLines);
            }

            return report.RunId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var failed = Unique(Path.Combine(_options.FailedPath, $"{name}-{timeProvider.GetUtcNow():yyyyMMddTHHmmssZ}"));
            await File.WriteAllTextAsync(Path.Combine(claimed, "error.txt"), $"{ex.GetType().Name}: {ex.Message}{Environment.NewLine}", CancellationToken.None)
                .ConfigureAwait(false);
            Directory.Move(claimed, failed);
            Log.RunFailed(logger, ex, name, failed);
            return null;
        }
    }

    /// <summary>Puts folders left in <c>processing/</c> by a crash or shutdown back into the inbox.</summary>
    public void RequeueInterrupted()
    {
        foreach (var folder in Directory.EnumerateDirectories(_options.ProcessingPath))
        {
            Directory.Move(folder, Unique(Path.Combine(_options.InboxPath, Path.GetFileName(folder))));
            Log.Requeued(logger, Path.GetFileName(folder));
        }
    }

    private static string Unique(string path)
    {
        var candidate = path;
        for (var i = 2; Directory.Exists(candidate); i++)
        {
            candidate = $"{path}-{i}";
        }

        return candidate;
    }
}
