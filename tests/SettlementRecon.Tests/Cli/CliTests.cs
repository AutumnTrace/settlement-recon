using SettlementRecon.Cli;
using SettlementRecon.Reporting;
using SettlementRecon.Tests.TestSupport;

namespace SettlementRecon.Tests.Cli;

public class CliTests
{
    private static async Task<(int ExitCode, string Output, string Error)> Recon(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = await ReconCli.Create().Parse(args).InvokeAsync(new System.CommandLine.InvocationConfiguration { Output = output, Error = error });
        return (exitCode, output.ToString(), error.ToString());
    }

    [Test]
    public async Task Generate_then_run_writes_a_json_report_and_a_markdown_summary()
    {
        using var temp = new TempDirectory();
        var data = Path.Combine(temp.Path, "data");
        var outDir = Path.Combine(temp.Path, "out");

        var generate = await Recon("generate", "--out", data, "--transactions", "2000", "--seed", "9");
        var run = await Recon(
            "run",
            "--ledger", Path.Combine(data, "ledger.csv"),
            "--settlement", Path.Combine(data, "orbit-2026-11-03.csv"), Path.Combine(data, "canal-2026-11-03.csv"),
            "--settlement", Path.Combine(data, "hashrail-2026-11-03.jsonl"),
            "--out", outDir);

        Assert.That(generate.ExitCode, Is.Zero, generate.Error);
        Assert.That(run.ExitCode, Is.Zero, run.Error);
        Assert.That(run.Output, Does.StartWith("Reconciled "));
        Assert.That(run.Output, Does.Contain("Matched with fee difference"));

        await using var json = File.OpenRead(Path.Combine(outDir, "report.json"));
        var report = await JsonReport.ReadAsync(json);
        Assert.That(report.Providers.Select(p => p.Provider), Is.EqualTo(new[] { "canal", "hashrail", "orbit" }));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(outDir, "summary.md")), Does.StartWith("# Settlement reconciliation"));
    }

    [Test]
    public async Task Fail_on_breaks_turns_breaks_into_exit_code_2()
    {
        using var temp = new TempDirectory();
        var data = Path.Combine(temp.Path, "data");
        await Recon("generate", "--out", data, "--transactions", "1000", "--break-rate", "0.05");

        var run = await Recon("run", "-l", Path.Combine(data, "ledger.csv"), "-s", Path.Combine(data, "orbit-2026-11-03.csv"), "-o", Path.Combine(temp.Path, "out"), "--fail-on-breaks");

        Assert.That(run.ExitCode, Is.EqualTo(ReconCli.ExitBreaksFound));
    }

    [Test]
    public async Task Explicit_providers_must_line_up_with_the_files()
    {
        using var temp = new TempDirectory();
        var ledger = temp.File("ledger.csv", "reference,provider,type,amount,currency,booked_at\n");
        var settlement = temp.File("export.csv", "x");

        var run = await Recon("run", "-l", ledger, "-s", settlement, settlement, "-p", "orbit", "-o", temp.Path);

        Assert.That(run.ExitCode, Is.EqualTo(ReconCli.ExitError));
        Assert.That(run.Error, Does.Contain("2 settlement file(s) but 1 --provider"));
    }

    [Test]
    public async Task A_file_name_that_does_not_name_a_provider_needs_the_provider_option()
    {
        using var temp = new TempDirectory();
        var ledger = temp.File("ledger.csv", "reference,provider,type,amount,currency,booked_at\n");
        var settlement = temp.File("export.csv", "x");

        var run = await Recon("run", "-l", ledger, "-s", settlement, "-o", temp.Path);

        Assert.That(run.ExitCode, Is.EqualTo(ReconCli.ExitError));
        Assert.That(run.Error, Does.Contain("pass --provider"));
    }

    [Test]
    public async Task Missing_files_are_reported_without_a_stack_trace()
    {
        using var temp = new TempDirectory();

        var run = await Recon("run", "-l", Path.Combine(temp.Path, "nope.csv"), "-s", Path.Combine(temp.Path, "orbit.csv"), "-o", temp.Path);

        Assert.That(run.ExitCode, Is.EqualTo(ReconCli.ExitError));
        Assert.That(run.Error, Does.StartWith("error: File not found"));
        Assert.That(run.Error, Does.Not.Contain("   at "));
    }

    [TestCase("--amount-tolerance-minor", "-1", "--amount-tolerance-minor must be between 0 and 1000000 (got -1).")]
    [TestCase("--amount-tolerance-bps", "20000", "--amount-tolerance-bps must be between 0 and 1000 (got 20000).")]
    [TestCase("--fee-tolerance-minor", "-1", "--fee-tolerance-minor must be between 0 and 1000000 (got -1).")]
    [TestCase("--date-window-days", "32", "--date-window-days must be between 0 and 31 (got 32).")]
    public async Task Tolerances_outside_the_worker_ranges_are_refused_before_any_file_is_read(string option, string value, string message)
    {
        using var temp = new TempDirectory();

        // The files don't exist: the range check has to come first.
        var run = await Recon("run", "-l", Path.Combine(temp.Path, "nope.csv"), "-s", Path.Combine(temp.Path, "orbit.csv"), "-o", temp.Path, option, value);

        Assert.That(run.ExitCode, Is.EqualTo(ReconCli.ExitError));
        Assert.That(run.Error.Trim(), Is.EqualTo($"error: {message}"));
        Assert.That(run.Output, Is.Empty);
    }

    [Test]
    public async Task Every_out_of_range_option_is_named_at_once()
    {
        using var temp = new TempDirectory();

        var run = await Recon("run", "-l", "ledger.csv", "-s", "orbit.csv", "-o", temp.Path, "--amount-tolerance-minor", "-1", "--fee-tolerance-minor", "-1");

        Assert.That(run.ExitCode, Is.EqualTo(ReconCli.ExitError));
        Assert.That(run.Error, Does.Contain("--amount-tolerance-minor").And.Contain("--fee-tolerance-minor"));
    }

    [Test]
    public async Task Formats_lists_every_registered_provider()
    {
        var formats = await Recon("formats");

        Assert.That(formats.Output, Does.Contain("orbit").And.Contain("canal").And.Contain("hashrail"));
    }
}
