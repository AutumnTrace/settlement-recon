using Microsoft.Extensions.Options;
using SettlementRecon.SampleData;
using SettlementRecon.Worker.Storage;

namespace SettlementRecon.Worker.Inbox;

/// <summary>
/// When <c>Demo:SeedSampleRun</c> is on and there is nothing to show yet, drops a generated run into the inbox,
/// so <c>dotnet run</c> or <c>docker compose up</c> on a fresh clone has a reconciled run within seconds.
/// </summary>
public sealed class DemoSeeder(
    IOptions<DemoOptions> demo,
    IOptions<InboxOptions> inbox,
    IRunStore store,
    ILogger<DemoSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = demo.Value;
        if (!options.SeedSampleRun)
        {
            return;
        }

        var folders = inbox.Value;
        folders.EnsureFolders();
        var anything = Directory.EnumerateDirectories(folders.InboxPath).Any()
            || Directory.EnumerateDirectories(folders.ProcessedPath).Any()
            || (await store.ListAsync(0, 1, cancellationToken).ConfigureAwait(false)).Total > 0;
        if (anything)
        {
            return;
        }

        var folder = Path.Combine(folders.InboxPath, $"sample-seed-{options.Seed}");
        await new SampleDataGenerator(new SampleDataOptions { Seed = options.Seed, Transactions = options.Transactions })
            .WriteAsync(folder, cancellationToken)
            .ConfigureAwait(false);
        Log.SeededSample(logger, Path.GetFileName(folder), options.Transactions, options.Seed);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
