using System.Text;

namespace SettlementRecon.Parsing;

/// <summary>A logical column a CSV parser needs, found by header name (case-insensitive) so column order may vary.</summary>
public sealed record CsvColumn(string Name, bool Required = true);

/// <summary>
/// Base class for header-driven CSV parsers. It binds the header once, splits each line into a stack-allocated
/// field table and hands derived parsers a <see cref="CsvRow"/> addressed by logical column index.
/// </summary>
public abstract class CsvRecordParser<TRow> : IRecordParser<TRow>
    where TRow : struct
{
    /// <summary>References longer than this are rejected; no real PSP reference comes close.</summary>
    public const int MaxReferenceBytes = 128;

    private const int MaxFields = 64;

    private readonly byte _delimiter;
    private readonly CsvColumn[] _columns;
    private readonly int[] _positions;
    private int _minimumFieldCount;

    protected CsvRecordParser(byte delimiter, CsvColumn[] columns)
    {
        _delimiter = delimiter;
        _columns = columns;
        _positions = new int[columns.Length];
        Array.Fill(_positions, -1);
    }

    public bool HasHeader => true;

    public void BindHeader(ReadOnlySpan<byte> header)
    {
        Span<CsvField> fields = stackalloc CsvField[MaxFields];
        if (!CsvTokenizer.TrySplit(header, _delimiter, fields, out var count, out var error))
        {
            throw new InvalidDataException($"Header row could not be read: {error}");
        }

        Span<byte> scratch = stackalloc byte[256];
        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            var value = fields[i].Length <= scratch.Length ? CsvTokenizer.GetValue(header, fields[i], scratch) : header.Slice(fields[i].Start, fields[i].Length);
            names[i] = Encoding.UTF8.GetString(value.Trim(" \t"u8));
        }

        var missing = new List<string>();
        for (var c = 0; c < _columns.Length; c++)
        {
            _positions[c] = Array.FindIndex(names, n => string.Equals(n, _columns[c].Name, StringComparison.OrdinalIgnoreCase));
            if (_positions[c] < 0 && _columns[c].Required)
            {
                missing.Add(_columns[c].Name);
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidDataException(
                $"Header is missing required column(s) {string.Join(", ", missing)}. Found: {string.Join(", ", names)}.");
        }

        _minimumFieldCount = _positions.Max() + 1;
    }

    public RecordParseStatus Parse(ReadOnlySpan<byte> line, int lineNumber, Utf8Arena arena, out TRow row, out string? error)
    {
        Span<CsvField> fields = stackalloc CsvField[MaxFields];
        if (!CsvTokenizer.TrySplit(line, _delimiter, fields, out var count, out error))
        {
            row = default;
            return RecordParseStatus.Rejected;
        }

        if (count < _minimumFieldCount)
        {
            return Reject($"Expected at least {_minimumFieldCount} fields but found {count}.", out row, out error);
        }

        return ParseRow(new CsvRow(line, fields[..count], _positions), lineNumber, arena, out row, out error);
    }

    protected abstract RecordParseStatus ParseRow(in CsvRow row, int lineNumber, Utf8Arena arena, out TRow result, out string? error);

    protected static RecordParseStatus Reject(string message, out TRow row, out string? error)
    {
        row = default;
        error = message;
        return RecordParseStatus.Rejected;
    }

    /// <summary>Copies a reference into the arena after validating it.</summary>
    protected static bool TryStoreReference(ReadOnlySpan<byte> value, Utf8Arena arena, out Utf8Slice slice)
    {
        slice = default;
        if (value.IsEmpty || value.Length > MaxReferenceBytes)
        {
            return false;
        }

        slice = arena.Append(value);
        return true;
    }

    protected static string Show(ReadOnlySpan<byte> value) => Encoding.UTF8.GetString(value);
}

/// <summary>One split CSV line, addressed by the logical column indexes of the parser.</summary>
public readonly ref struct CsvRow
{
    private readonly ReadOnlySpan<byte> _line;
    private readonly ReadOnlySpan<CsvField> _fields;
    private readonly int[] _positions;

    internal CsvRow(ReadOnlySpan<byte> line, ReadOnlySpan<CsvField> fields, int[] positions)
    {
        _line = line;
        _fields = fields;
        _positions = positions;
    }

    public bool Has(int column) => (uint)_positions[column] < (uint)_fields.Length;

    /// <summary>The trimmed field without quotes. Escaped quotes are left as they are; use <see cref="Text"/> for free text.</summary>
    public ReadOnlySpan<byte> Raw(int column)
    {
        if (!Has(column))
        {
            return [];
        }

        var field = _fields[_positions[column]];
        return _line.Slice(field.Start, field.Length).Trim(" \t"u8);
    }

    /// <summary>The trimmed field with <c>""</c> collapsed to <c>"</c>.</summary>
    public ReadOnlySpan<byte> Text(int column, Span<byte> scratch)
    {
        if (!Has(column))
        {
            return [];
        }

        var field = _fields[_positions[column]];
        return field.Length <= scratch.Length
            ? CsvTokenizer.GetValue(_line, field, scratch).Trim(" \t"u8)
            : _line.Slice(field.Start, field.Length).Trim(" \t"u8);
    }
}
