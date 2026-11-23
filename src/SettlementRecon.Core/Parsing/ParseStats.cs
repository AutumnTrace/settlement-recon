namespace SettlementRecon.Parsing;

/// <summary>A line that could not be parsed, with the reason.</summary>
public sealed record ParseIssue(string Source, int Line, string Message);

/// <summary>Counters for one input file. Written by a single reader, read after it finishes.</summary>
public sealed class ParseStats(string source)
{
    /// <summary>Only the first issues are kept with their text; the count is always exact.</summary>
    public const int MaxIssuesKept = 100;

    private readonly List<ParseIssue> _issues = [];

    public string Source { get; } = source;

    public long Rows { get; internal set; }

    public long Skipped { get; internal set; }

    public long Rejected { get; private set; }

    public long Bytes { get; internal set; }

    public int Lines { get; internal set; }

    public IReadOnlyList<ParseIssue> Issues => _issues;

    internal void Reject(int line, string message)
    {
        Rejected++;
        if (_issues.Count < MaxIssuesKept)
        {
            _issues.Add(new ParseIssue(Source, line, message));
        }
    }
}
