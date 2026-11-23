namespace SettlementRecon.Worker;

/// <summary>Source-generated log messages: no boxing or formatting unless the level is enabled.</summary>
internal static partial class Log
{
    [LoggerMessage(1000, LogLevel.Information, "Watching {Inbox} for run folders every {PollInterval}")]
    public static partial void Watching(ILogger logger, string inbox, TimeSpan pollInterval);

    [LoggerMessage(1001, LogLevel.Information, "Picked up run folder {Folder}")]
    public static partial void PickedUp(ILogger logger, string folder);

    [LoggerMessage(1002, LogLevel.Information,
        "Run {RunId} from {Folder} reconciled {SettlementLines} settlement lines against {LedgerEntries} ledger entries in {DurationMs} ms: {Breaks} breaks, match rate {MatchRate:P2}")]
    public static partial void RunCompleted(ILogger logger, string runId, string folder, long settlementLines, long ledgerEntries, double durationMs, long breaks, double matchRate);

    [LoggerMessage(1003, LogLevel.Error, "Run folder {Folder} failed and was moved to {FailedPath}")]
    public static partial void RunFailed(ILogger logger, Exception exception, string folder, string failedPath);

    [LoggerMessage(1004, LogLevel.Warning, "Could not claim run folder {Folder}; another worker may have taken it or it is still being written")]
    public static partial void ClaimFailed(ILogger logger, Exception exception, string folder);

    [LoggerMessage(1005, LogLevel.Warning, "Re-queued run folder {Folder} left in processing by a previous shutdown")]
    public static partial void Requeued(ILogger logger, string folder);

    [LoggerMessage(1006, LogLevel.Warning, "Run {RunId} has {Rejected} rejected lines; see the run's parse issues")]
    public static partial void RejectedLines(ILogger logger, string runId, long rejected);

    [LoggerMessage(1007, LogLevel.Information, "Seeded a sample run into {Folder} ({Transactions} transactions, seed {Seed})")]
    public static partial void SeededSample(ILogger logger, string folder, int transactions, int seed);

    [LoggerMessage(1100, LogLevel.Warning, "Could not create MongoDB indexes at startup; readiness will report the database until it is reachable")]
    public static partial void MongoIndexesFailed(ILogger logger, Exception exception);
}
