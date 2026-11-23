using SettlementRecon.Pipeline;
using SettlementRecon.SampleData;
using SettlementRecon.Tests.TestSupport;

namespace SettlementRecon.Tests.Worker;

public class InboxLifecycleTests
{
    [Test]
    public async Task A_folder_that_cannot_be_reconciled_moves_to_failed_with_the_reason()
    {
        using var temp = new TempDirectory();
        await using var factory = new WorkerFactory(temp.Path);
        using var client = factory.CreateClient();

        var folder = Path.Combine(factory.InboxPath, "broken");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "ledger.csv"), "reference,provider\n");
        await new RunManifest("ledger.csv", [new ManifestSettlement("orbit", "missing.csv")]).WriteAsync(folder);

        var failed = await WaitForFolder(factory.FailedPath);

        Assert.That(Path.GetFileName(failed), Does.StartWith("broken-"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(failed, "error.txt")), Does.Contain("missing.csv"));
        Assert.That((await factory.WaitForRunsAsync(client, 0)).Total, Is.Zero);
    }

    [Test]
    public async Task A_folder_without_run_json_is_left_alone_until_it_is_ready()
    {
        using var temp = new TempDirectory();
        await using var factory = new WorkerFactory(temp.Path);
        using var client = factory.CreateClient();

        var staging = Path.Combine(temp.Path, "staging");
        var data = await new SampleDataGenerator(new SampleDataOptions { Transactions = 500 }).WriteAsync(staging);
        File.Delete(Path.Combine(staging, RunManifest.FileName));
        var folder = Path.Combine(factory.InboxPath, "half-copied");
        Directory.Move(staging, folder);

        await Task.Delay(500);
        Assert.That(Directory.Exists(folder), Is.True, "not picked up without run.json");

        await data.Manifest.WriteAsync(folder);
        await factory.WaitForRunsAsync(client, 1);
        Assert.That(Directory.Exists(folder), Is.False);
    }

    [Test]
    public async Task Runs_interrupted_by_a_shutdown_are_requeued_on_start()
    {
        using var temp = new TempDirectory();
        var interrupted = Path.Combine(temp.Path, "processing", "2026-11-03");
        await new SampleDataGenerator(new SampleDataOptions { Transactions = 500 }).WriteAsync(interrupted);

        await using var factory = new WorkerFactory(temp.Path);
        using var client = factory.CreateClient();

        var runs = await factory.WaitForRunsAsync(client, 1);
        Assert.That(runs.Items.Single().Source, Is.EqualTo("2026-11-03"));
    }

    private static async Task<string> WaitForFolder(string parent)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(parent) && Directory.EnumerateDirectories(parent).FirstOrDefault() is { } folder)
            {
                return folder;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Nothing arrived in {parent}.");
        return string.Empty;
    }
}
