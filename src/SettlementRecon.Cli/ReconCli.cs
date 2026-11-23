using System.CommandLine;
using System.Globalization;
using SettlementRecon.Formats;
using SettlementRecon.Matching;
using SettlementRecon.Pipeline;
using SettlementRecon.Reporting;
using SettlementRecon.SampleData;

namespace SettlementRecon.Cli;

/// <summary>The <c>recon</c> command tree. Built as a method so tests can run it in-process with captured output.</summary>
public static class ReconCli
{
    public const int ExitOk = 0;
    public const int ExitError = 1;
    public const int ExitBreaksFound = 2;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static RootCommand Create(SettlementFormatRegistry? registry = null)
    {
        var formats = registry ?? SettlementFormatRegistry.Default;
        var root = new RootCommand("settlement-recon: reconcile PSP settlement files against the internal ledger and explain every mismatch.");
        root.Add(RunCommand(formats));
        root.Add(GenerateCommand());
        root.Add(FormatsCommand(formats));
        return root;
    }

    private static Command RunCommand(SettlementFormatRegistry formats)
    {
        var ledger = new Option<FileInfo>("--ledger", "-l") { Description = "Ledger export (.csv or .jsonl).", Required = true };
        var settlements = new Option<FileInfo[]>("--settlement", "-s")
        {
            Description = "Settlement file. Repeat for several files.",
            Required = true,
            AllowMultipleArgumentsPerToken = true,
        };
        var providers = new Option<string[]>("--provider", "-p")
        {
            Description = "Provider of each settlement file, in the same order. Defaults to the file-name prefix (orbit-2026-11-03.csv is orbit).",
            AllowMultipleArgumentsPerToken = true,
        };
        var output = new Option<DirectoryInfo>("--out", "-o") { Description = "Folder for report.json and summary.md.", Required = true };
        var amountMinor = new Option<long>("--amount-tolerance-minor") { Description = "Absolute amount tolerance in minor units.", DefaultValueFactory = _ => new ReconciliationOptions().AmountToleranceMinor };
        var amountBps = new Option<int>("--amount-tolerance-bps") { Description = "Relative amount tolerance in basis points.", DefaultValueFactory = _ => new ReconciliationOptions().AmountToleranceBps };
        var feeMinor = new Option<long>("--fee-tolerance-minor") { Description = "Fee tolerance in minor units.", DefaultValueFactory = _ => new ReconciliationOptions().FeeToleranceMinor };
        var window = new Option<int>("--date-window-days") { Description = "Date window for the amount-and-date fallback.", DefaultValueFactory = _ => new ReconciliationOptions().DateWindowDays };
        var noFallback = new Option<bool>("--no-fallback") { Description = "Disable pairing by amount and date when the reference does not match." };
        var failOnBreaks = new Option<bool>("--fail-on-breaks") { Description = "Exit with code 2 when there are breaks or rejected lines (for schedulers and CI)." };
        var examples = new Option<int>("--examples") { Description = "Break examples per type in summary.md (report.json always has all of them).", DefaultValueFactory = _ => 3 };

        var command = new Command("run", "Reconcile settlement files against a ledger export and write a JSON report and a Markdown summary.")
        {
            ledger, settlements, providers, output, amountMinor, amountBps, feeMinor, window, noFallback, failOnBreaks, examples,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var stdout = parse.InvocationConfiguration.Output;
            var stderr = parse.InvocationConfiguration.Error;
            try
            {
                var files = parse.GetRequiredValue(settlements);
                var names = parse.GetValue(providers) ?? [];
                if (names.Length > 0 && names.Length != files.Length)
                {
                    throw new ArgumentException($"Got {files.Length} settlement file(s) but {names.Length} --provider value(s); give one per file or none.");
                }

                var inputs = files
                    .Select((file, i) => (Format: names.Length > 0 ? formats.Get(names[i]) : InferFormat(formats, file), Path: Existing(file)))
                    .ToArray();

                var options = new ReconciliationOptions
                {
                    AmountToleranceMinor = parse.GetValue(amountMinor),
                    AmountToleranceBps = parse.GetValue(amountBps),
                    FeeToleranceMinor = parse.GetValue(feeMinor),
                    DateWindowDays = parse.GetValue(window),
                    AmountAndDateFallback = !parse.GetValue(noFallback),
                };

                var request = ReconciliationRequest.FromPaths(Existing(parse.GetRequiredValue(ledger)), inputs);
                var report = await new ReconciliationEngine(options).RunAsync(request, cancellationToken);

                var outDir = parse.GetRequiredValue(output);
                outDir.Create();
                var jsonPath = Path.Combine(outDir.FullName, "report.json");
                var markdownPath = Path.Combine(outDir.FullName, "summary.md");
                await using (var json = File.Create(jsonPath))
                {
                    await JsonReport.WriteAsync(report, json, cancellationToken);
                }

                await File.WriteAllTextAsync(markdownPath, MarkdownReport.Render(report, Math.Max(1, parse.GetValue(examples))), cancellationToken);

                ConsoleSummary.Write(stdout, report);
                await stdout.WriteLineAsync();
                await stdout.WriteLineAsync($"Report:  {Path.GetRelativePath(Environment.CurrentDirectory, jsonPath)}");
                await stdout.WriteLineAsync($"Summary: {Path.GetRelativePath(Environment.CurrentDirectory, markdownPath)}");

                var hasProblems = report.Summary.Breaks > 0 || report.Summary.RejectedLines > 0;
                return parse.GetValue(failOnBreaks) && hasProblems ? ExitBreaksFound : ExitOk;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or KeyNotFoundException or NotSupportedException)
            {
                await stderr.WriteLineAsync($"error: {ex.Message}");
                return ExitError;
            }
        });

        return command;
    }

    private static Command GenerateCommand()
    {
        var output = new Option<DirectoryInfo>("--out", "-o") { Description = "Folder to write the data set into.", Required = true };
        var transactions = new Option<int>("--transactions", "-n") { Description = "Ledger transactions to generate.", DefaultValueFactory = _ => 10_000 };
        var seed = new Option<int>("--seed") { Description = "Random seed; the same seed gives byte-identical files.", DefaultValueFactory = _ => 42 };
        var breakRate = new Option<double>("--break-rate") { Description = "Share of transactions with an injected problem.", DefaultValueFactory = _ => 0.004 };
        var date = new Option<DateOnly>("--date") { Description = "Booking date (yyyy-MM-dd).", DefaultValueFactory = _ => new DateOnly(2026, 11, 2) };

        var command = new Command("generate", "Write a reproducible ledger plus orbit, canal and hashrail settlement files with injected breaks.")
        {
            output, transactions, seed, breakRate, date,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var stdout = parse.InvocationConfiguration.Output;
            var count = parse.GetValue(transactions);
            var rate = parse.GetValue(breakRate);
            if (count < 1 || rate is < 0 or > 0.5)
            {
                await parse.InvocationConfiguration.Error.WriteLineAsync("error: --transactions must be at least 1 and --break-rate between 0 and 0.5.");
                return ExitError;
            }

            var options = new SampleDataOptions { Transactions = count, Seed = parse.GetValue(seed), BreakRate = rate, BookingDate = parse.GetValue(date) };
            var dataSet = await new SampleDataGenerator(options).WriteAsync(parse.GetRequiredValue(output).FullName, cancellationToken);

            var expected = dataSet.ExpectedOutcomes;
            await stdout.WriteLineAsync(string.Create(Invariant,
                $"Wrote {dataSet.LedgerRows:#,0} ledger rows and {dataSet.SettlementRows:#,0} settlement lines to {Path.GetRelativePath(Environment.CurrentDirectory, dataSet.Directory)}"));
            await stdout.WriteLineAsync(string.Create(Invariant,
                $"Injected: {expected.AmountMismatch} amount mismatches, {expected.MatchedWithFeeDifference} fee differences, {expected.MissingInLedger} missing in ledger, {expected.MissingAtPsp} missing at PSP, {expected.Duplicate} duplicates, {expected.CurrencyMismatch} currency mismatches"));
            foreach (var settlement in dataSet.Manifest.Settlements)
            {
                await stdout.WriteLineAsync($"  {settlement.Provider,-9} {settlement.File}");
            }

            await stdout.WriteLineAsync($"  {"ledger",-9} {dataSet.Manifest.Ledger}");
            return ExitOk;
        });

        return command;
    }

    private static Command FormatsCommand(SettlementFormatRegistry formats)
    {
        var command = new Command("formats", "List the settlement formats this build understands.");
        command.SetAction(parse =>
        {
            var stdout = parse.InvocationConfiguration.Output;
            foreach (var format in formats.All.OrderBy(f => f.Provider, StringComparer.Ordinal))
            {
                stdout.WriteLine($"{format.Provider,-9} {format.DisplayName}");
                stdout.WriteLine($"{string.Empty,-9} {format.Description}");
            }

            return ExitOk;
        });
        return command;
    }

    private static ISettlementFormat InferFormat(SettlementFormatRegistry formats, FileInfo file)
    {
        var prefix = file.Name.Split(['-', '_', '.'], 2)[0];
        return formats.TryGet(prefix, out var format)
            ? format
            : throw new ArgumentException($"Cannot tell the provider of '{file.Name}' from its name; pass --provider.");
    }

    private static string Existing(FileInfo file) =>
        file.Exists ? file.FullName : throw new FileNotFoundException($"File not found: {file.FullName}");
}
