namespace SettlementRecon.Parsing;

public enum RecordParseStatus
{
    /// <summary>The line produced a row.</summary>
    Parsed,

    /// <summary>The line is valid but does not settle money (for example a pending crypto transfer).</summary>
    Skipped,

    /// <summary>The line is malformed. It is reported as a parse issue with a reason.</summary>
    Rejected,
}

/// <summary>
/// Parses one line of a file into a row. Implementations work on UTF-8 spans and write text fields into the
/// supplied arena, so the hot path never allocates. A parser instance is used for one file at a time.
/// </summary>
public interface IRecordParser<TRow>
    where TRow : struct
{
    /// <summary>Whether the first non-empty line is a header row.</summary>
    bool HasHeader { get; }

    /// <summary>Maps column names to positions. Throws <see cref="InvalidDataException"/> when required columns are missing.</summary>
    void BindHeader(ReadOnlySpan<byte> header);

    /// <param name="line">One line without its line terminator or byte-order mark.</param>
    /// <param name="error">Why the line was rejected; only allocated on failure.</param>
    RecordParseStatus Parse(ReadOnlySpan<byte> line, int lineNumber, Utf8Arena arena, out TRow row, out string? error);
}
