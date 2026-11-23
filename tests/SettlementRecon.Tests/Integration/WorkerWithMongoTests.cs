using System.Net.Http.Json;
using SettlementRecon.Reporting;
using SettlementRecon.SampleData;
using SettlementRecon.Tests.TestSupport;
using SettlementRecon.Tests.Worker;
using SettlementRecon.Worker.Storage;

namespace SettlementRecon.Tests.Integration;

/// <summary>The whole worker against a real MongoDB: inbox → engine → Mongo → API, plus the readiness probe.</summary>
[Category("Integration")]
public class WorkerWithMongoTests
{
    [Test]
    public async Task A_dropped_run_is_stored_in_mongodb_and_served_by_the_api()
    {
        await MongoFixture.EnsureStartedAsync();
        using var temp = new TempDirectory();
        var database = $"recon_{Guid.NewGuid():N}";
        await using var factory = new WorkerFactory(temp.Path, new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Mongo",
            ["Storage:Mongo:ConnectionString"] = MongoFixture.ConnectionString,
            ["Storage:Mongo:Database"] = database,
        });
        using var client = factory.CreateClient();

        var data = await new SampleDataGenerator(new SampleDataOptions { Seed = 4, Transactions = 2_000, BreakRate = 0.03 })
            .WriteAsync(Path.Combine(factory.InboxPath, "2026-11-03"));
        var run = (await factory.WaitForRunsAsync(client, 1)).Items.Single();

        var breaks = await client.GetFromJsonAsync<Page<ReconItem>>($"/api/runs/{run.RunId}/breaks?take=500", WorkerFactory.Json);
        var ready = await client.GetStringAsync("/health/ready");

        Assert.That(breaks!.Total, Is.EqualTo(data.ExpectedOutcomes.Breaks));
        Assert.That(ready, Does.Contain("\"name\":\"mongodb\",\"status\":\"Healthy\""));
    }

    [Test]
    public void Mongo_storage_without_a_connection_string_fails_at_startup()
    {
        using var temp = new TempDirectory();
        using var factory = new WorkerFactory(temp.Path, new Dictionary<string, string?> { ["Storage:Provider"] = "Mongo" });

        Assert.That(() => factory.CreateClient(), Throws.Exception.With.Message.Contains("Storage:Mongo:ConnectionString is required"));
    }
}
