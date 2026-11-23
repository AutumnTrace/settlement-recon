namespace SettlementRecon.Parsing;

/// <summary>Position of one field inside a CSV line, without the surrounding quotes.</summary>
public readonly record struct CsvField(int Start, int Length, bool HasEscapedQuotes);

/// <summary>
/// RFC 4180-style field splitting on a UTF-8 span: delimiters inside double quotes are data, <c>""</c> is an
/// escaped quote. Uses vectorised <c>IndexOf</c> searches and never allocates. Newlines inside quoted fields are
/// not supported; settlement exports do not use them, and a line-oriented reader keeps memory bounded.
/// </summary>
public static class CsvTokenizer
{
    public static bool TrySplit(ReadOnlySpan<byte> line, byte delimiter, Span<CsvField> fields, out int count, out string? error)
    {
        count = 0;
        error = null;
        var i = 0;
        while (true)
        {
            if (count == fields.Length)
            {
                error = $"More than {fields.Length} fields.";
                return false;
            }

            if (i < line.Length && line[i] == (byte)'"')
            {
                var start = i + 1;
                var j = start;
                var escaped = false;
                while (true)
                {
                    var quote = line[j..].IndexOf((byte)'"');
                    if (quote < 0)
                    {
                        error = $"Unterminated quoted field starting at column {start}.";
                        return false;
                    }

                    j += quote;
                    if (j + 1 < line.Length && line[j + 1] == (byte)'"')
                    {
                        escaped = true;
                        j += 2;
                        continue;
                    }

                    break;
                }

                fields[count++] = new CsvField(start, j - start, escaped);
                i = j + 1;
                if (i == line.Length)
                {
                    return true;
                }

                if (line[i] != delimiter)
                {
                    error = $"Unexpected character after quoted field at column {i + 1}.";
                    return false;
                }

                i++;
                continue;
            }

            var next = line[i..].IndexOf(delimiter);
            if (next < 0)
            {
                fields[count++] = new CsvField(i, line.Length - i, false);
                return true;
            }

            fields[count++] = new CsvField(i, next, false);
            i += next + 1;
        }
    }

    /// <summary>The field's bytes with <c>""</c> collapsed to <c>"</c>. Uses <paramref name="scratch"/> only when needed.</summary>
    public static ReadOnlySpan<byte> GetValue(ReadOnlySpan<byte> line, CsvField field, Span<byte> scratch)
    {
        var raw = line.Slice(field.Start, field.Length);
        if (!field.HasEscapedQuotes)
        {
            return raw;
        }

        var written = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            scratch[written++] = raw[i];
            if (raw[i] == (byte)'"')
            {
                i++;
            }
        }

        return scratch[..written];
    }
}
