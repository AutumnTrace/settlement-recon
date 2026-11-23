using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using SettlementRecon.Matching;
using SettlementRecon.Reporting;
using SettlementRecon.SampleData;
using SettlementRecon.Tests.TestSupport;
using SettlementRecon.Worker.Api;
using SettlementRecon.Worker.Storage;

namespace SettlementRecon.Tests.Worker;

/// <summary>
/// End to end through the worker with in-memory storage: drop a run folder into the inbox, let the hosted
/// watcher pick it up, then read the run back through the REST API.
/// </summary>
public class WorkerApiTests
{
    private TempDirectory _temp = null!;
    private WorkerFactory _factory = null!;
    private HttpClient _client = null!;
    private GeneratedDataSet _data = null!;
    private RunSummary _run = null!;

    [OneTimeSetUp]
    public async Task StartWorkerAndDropARun()
    {
        _temp = new TempDirectory();
        _factory = new WorkerFactory(_temp.Path);
        _client = _factory.CreateClient();

        _data = await new SampleDataGenerator(new SampleDataOptions { Seed = 21, Transactions = 4_000, BreakRate = 0.05 })
            .WriteAsync(Path.Combine(_factory.InboxPath, "2026-11-03"));

        _run = (await _factory.WaitForRunsAsync(_client, 1)).Items.Single();
    }

    [OneTimeTearDown]
    public void Stop()
    {
        _client.Dispose();
        _factory.Dispose();
        _temp.Dispose();
    }

    [Test]
    public void The_dropped_folder_becomes_a_stored_run_and_is_archived()
    {
        Assert.That(_run.Source, Is.EqualTo("2026-11-03"));
        Assert.That(_run.Label, Is.EqualTo(_data.Manifest.Label));
        Assert.That(_run.Providers, Is.EqualTo(new[] { "canal", "hashrail", "orbit" }));
        Assert.That(Directory.EnumerateDirectories(_factory.InboxPath), Is.Empty);
        Assert.That(Directory.EnumerateDirectories(_factory.ProcessedPath).Single(), Does.EndWith($"2026-11-03-{_run.RunId}"));
    }

    [Test]
    public async Task Run_detail_reports_exactly_the_injected_outcomes()
    {
        var run = await _client.GetFromJsonAsync<RunDetail>($"/api/runs/{_run.RunId}", WorkerFactory.Json);

        Assert.That(run!.Summary.Outcomes, Is.EqualTo(_data.ExpectedOutcomes));
        Assert.That(run.Providers, Has.Count.EqualTo(3));
        Assert.That(run.Links.Breaks, Is.EqualTo($"/api/runs/{_run.RunId}/breaks"));
    }

    [Test]
    public async Task Breaks_page_and_filter_by_status_and_provider()
    {
        var all = await Items($"/api/runs/{_run.RunId}/breaks?take=500");
        var page = await Items($"/api/runs/{_run.RunId}/breaks?skip=2&take=3");
        var missing = await Items($"/api/runs/{_run.RunId}/breaks?status=missingAtPsp&take=500");
        var canal = await Items($"/api/runs/{_run.RunId}/breaks?provider=CANAL&take=500");

        Assert.That(all.Total, Is.EqualTo(_run.Breaks));
        Assert.That(page.Items.Select(i => i.Reason), Is.EqualTo(all.Items.Skip(2).Take(3).Select(i => i.Reason)));
        Assert.That(missing.Total, Is.EqualTo(_data.ExpectedOutcomes.MissingAtPsp));
        Assert.That(missing.Items, Is.All.Matches<ReconItem>(i => i!.Status == ReconStatus.MissingAtPsp));
        Assert.That(canal.Items, Is.All.Matches<ReconItem>(i => i!.Provider == "canal"));
    }

    [Test]
    public async Task Review_matches_list_the_amount_and_date_pairings()
    {
        var reviews = await Items($"/api/runs/{_run.RunId}/review-matches");
        var expected = _data.Expected.Sum(p => p.MatchedByRule.AmountAndDate);

        Assert.That(reviews.Total, Is.GreaterThan(0));
        Assert.That(reviews.Total, Is.EqualTo(expected));
        Assert.That(reviews.Items, Is.All.Matches<ReconItem>(i => i!.Rule == MatchRule.AmountAndDate));
    }

    [Test]
    public async Task Summary_markdown_is_served_as_markdown()
    {
        var response = await _client.GetAsync($"/api/runs/{_run.RunId}/summary.md?examples=2");

        Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("text/markdown"));
        var text = await response.Content.ReadAsStringAsync();
        Assert.That(text, Does.StartWith("# Settlement reconciliation"));
        Assert.That(text, Does.Contain("## Break examples (up to 2 per type)"));
        Assert.That(text, Does.Contain("## Breaks by provider"));
    }

    [Test]
    public async Task Unknown_runs_are_404_problem_details()
    {
        var response = await _client.GetAsync("/api/runs/does-not-exist/breaks");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/problem+json"));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Run not found"));
    }

    [TestCase("/api/runs?take=0", "take")]
    [TestCase("/api/runs?skip=-1", "skip")]
    [TestCase("/api/runs/{run}/breaks?status=late", "status")]
    public async Task Invalid_queries_are_400_validation_problems(string url, string field)
    {
        var response = await _client.GetAsync(url.Replace("{run}", _run.RunId, StringComparison.Ordinal));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.That(problem!.Errors.Keys, Does.Contain(field));
    }

    [Test]
    public async Task Formats_endpoint_lists_the_registered_providers()
    {
        var formats = await _client.GetFromJsonAsync<FormatInfo[]>("/api/formats", WorkerFactory.Json);

        Assert.That(formats!.Select(f => f.Provider), Is.EqualTo(new[] { "canal", "hashrail", "orbit" }));
    }

    [TestCase("/health/live")]
    [TestCase("/health/ready")]
    public async Task Health_endpoints_report_healthy(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("\"status\":\"Healthy\""));
    }

    private async Task<Page<ReconItem>> Items(string url) =>
        (await _client.GetFromJsonAsync<Page<ReconItem>>(url, WorkerFactory.Json))!;
}
