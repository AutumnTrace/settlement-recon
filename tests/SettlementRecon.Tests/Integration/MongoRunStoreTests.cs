using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using SettlementRecon.Formats;
using SettlementRecon.Matching;
using SettlementRecon.Pipeline;
using SettlementRecon.Reporting;
using SettlementRecon.SampleData;
using SettlementRecon.Tests.TestSupport;
using SettlementRecon.Worker.Storage;

namespace SettlementRecon.Tests.Integration;

[Category("Integration")]
public class MongoRunStoreTests
{
    private MongoClient _client = null!;
    private MongoRunStore _store = null!;
    private string _database = null!;
    private ReconciliationReport _report = null!;

    [OneTimeSetUp]
    public async Task StartMongoAndReconcile()
    {
        await MongoFixture.EnsureStartedAsync();
        _client = new MongoClient(MongoFixture.ConnectionString);
        _database = $"recon_{Guid.NewGuid():N}";
        _store = new MongoRunStore(_client, Options.Create(new StorageOptions { Mongo = { Database = _database } }));
        await _store.EnsureIndexesAsync(CancellationToken.None);

        using var temp = new TempDirectory();
        var data = await new SampleDataGenerator(new SampleDataOptions { Seed = 8, Transactions = 3_000, BreakRate = 0.05 }).WriteAsync(temp.Path);
        _report = await new ReconciliationEngine(new ReconciliationOptions())
            .RunAsync(data.Manifest.ToRequest(data.Directory, SettlementFormatRegistry.Default));
    }

    [OneTimeTearDown]
    public async Task DropDatabase()
    {
        await _client.DropDatabaseAsync(_database);
        _client.Dispose();
    }

    [Test]
    public async Task A_saved_run_reads_back_with_the_same_report_header()
    {
        await _store.SaveAsync(new StoredRun(_report.RunId, "2026-11-03", "nightly", _report), CancellationToken.None);

        var run = await _store.GetAsync(_report.RunId, CancellationToken.None);

        Assert.That(run, Is.Not.Null);
        Assert.That(run!.Label, Is.EqualTo("nightly"));
        Assert.That(run.Report.Summary, Is.EqualTo(_report.Summary));
        Assert.That(JsonReport.Serialize(run.Report), Is.EqualTo(JsonReport.Serialize(_report with { Breaks = [], ReviewMatches = [] })));
    }

    [Test]
    public async Task Breaks_are_paged_in_file_order_and_filtered_through_indexes()
    {
        await _store.SaveAsync(new StoredRun(_report.RunId, "2026-11-03", null, _report), CancellationToken.None);

        var firstPage = await _store.GetItemsAsync(_report.RunId, new ItemQuery(ItemKind.Break, null, null, 0, 10), CancellationToken.None);
        var secondPage = await _store.GetItemsAsync(_report.RunId, new ItemQuery(ItemKind.Break, null, null, 10, 10), CancellationToken.None);
        var mismatches = await _store.GetItemsAsync(_report.RunId, new ItemQuery(ItemKind.Break, ReconStatus.AmountMismatch, "ORBIT", 0, 500), CancellationToken.None);

        Assert.That(firstPage.Total, Is.EqualTo(_report.Breaks.Count));
        Assert.That(firstPage.Items.Concat(secondPage.Items), Is.EqualTo(_report.Breaks.Take(20)));
        Assert.That(mismatches.Items, Is.EqualTo(_report.Breaks.Where(b => b.Status == ReconStatus.AmountMismatch && b.Provider == "orbit")));
    }

    [Test]
    public async Task Saving_the_same_run_twice_does_not_duplicate_items()
    {
        var run = new StoredRun(_report.RunId, "2026-11-03", null, _report);
        await _store.SaveAsync(run, CancellationToken.None);
        await _store.SaveAsync(run, CancellationToken.None);

        var breaks = await _store.GetItemsAsync(_report.RunId, new ItemQuery(ItemKind.Break, null, null, 0, 1), CancellationToken.None);
        var reviews = await _store.GetItemsAsync(_report.RunId, new ItemQuery(ItemKind.Review, null, null, 0, 1), CancellationToken.None);

        Assert.That(breaks.Total, Is.EqualTo(_report.Breaks.Count));
        Assert.That(reviews.Total, Is.EqualTo(_report.ReviewMatches.Count));
    }

    [Test]
    public async Task Runs_are_listed_newest_first()
    {
        var older = _report with { RunId = "run-older", StartedAt = _report.StartedAt.AddDays(-1) };
        var newer = _report with { RunId = "run-newer", StartedAt = _report.StartedAt.AddDays(1) };
        await _store.SaveAsync(new StoredRun(older.RunId, "a", null, older), CancellationToken.None);
        await _store.SaveAsync(new StoredRun(newer.RunId, "b", null, newer), CancellationToken.None);

        var page = await _store.ListAsync(0, 2, CancellationToken.None);

        Assert.That(page.Items[0].RunId, Is.EqualTo("run-newer"));
        Assert.That(page.Total, Is.GreaterThanOrEqualTo(2));
        Assert.That(page.Items[0].Breaks, Is.EqualTo(_report.Summary.Breaks));
    }

    [Test]
    public async Task The_expected_indexes_exist()
    {
        var database = _client.GetDatabase(_database);
        var runIndexes = await (await database.GetCollection<BsonDocument>("runs").Indexes.ListAsync()).ToListAsync();
        var itemIndexes = await (await database.GetCollection<BsonDocument>("items").Indexes.ListAsync()).ToListAsync();

        Assert.That(runIndexes.Select(i => i["name"].AsString), Does.Contain("startedAt_desc"));
        Assert.That(itemIndexes.Select(i => i["name"].AsString), Is.SupersetOf(new[] { "run_kind_seq", "run_kind_status_seq", "run_kind_provider_seq", "reference" }));
    }
}
