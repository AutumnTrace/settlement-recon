using System.Diagnostics;
using System.Diagnostics.Metrics;
using SettlementRecon.Matching;
using SettlementRecon.Reporting;

namespace SettlementRecon.Diagnostics;

/// <summary>
/// Metrics and traces for reconciliation runs, published through <see cref="System.Diagnostics"/> so any
/// OpenTelemetry exporter (OTLP, Azure Monitor, Prometheus) can pick them up without the engine knowing which.
/// </summary>
public sealed class ReconTelemetry : IDisposable
{
    public const string MeterName = "SettlementRecon";
    public const string ActivitySourceName = "SettlementRecon";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private readonly Meter _meter;
    private readonly Counter<long> _rows;
    private readonly Counter<long> _breaks;
    private readonly Counter<long> _runs;
    private readonly Histogram<double> _duration;
    private readonly Histogram<double> _throughput;

    public ReconTelemetry(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _rows = _meter.CreateCounter<long>("recon.rows", "{row}", "Rows parsed, by provider and side (ledger or settlement).");
        _breaks = _meter.CreateCounter<long>("recon.breaks", "{break}", "Breaks found, by provider and break type.");
        _runs = _meter.CreateCounter<long>("recon.runs", "{run}", "Reconciliation runs, by result.");
        _duration = _meter.CreateHistogram<double>("recon.run.duration", "s", "Wall-clock duration of a reconciliation run.");
        _throughput = _meter.CreateHistogram<double>("recon.run.throughput", "{row}/s", "Rows reconciled per second in a run.");
    }

    public void RecordCompleted(ReconciliationReport report)
    {
        _runs.Add(1, new KeyValuePair<string, object?>("result", "completed"));
        _duration.Record(report.DurationMs / 1000, new KeyValuePair<string, object?>("result", "completed"));
        _throughput.Record(report.Summary.RowsPerSecond);
        _rows.Add(report.Ledger.Rows, new("side", "ledger"), new("provider", "all"));

        foreach (var provider in report.Providers)
        {
            _rows.Add(provider.SettlementLines, new("side", "settlement"), new("provider", provider.Provider));
            foreach (var status in Enum.GetValues<ReconStatus>())
            {
                var count = provider.Outcomes[status];
                if (status.IsBreak() && count > 0)
                {
                    _breaks.Add(count, new("provider", provider.Provider), new("type", status.ToString()));
                }
            }
        }
    }

    public void RecordFailed(TimeSpan elapsed)
    {
        _runs.Add(1, new KeyValuePair<string, object?>("result", "failed"));
        _duration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("result", "failed"));
    }

    public void Dispose() => _meter.Dispose();
}
