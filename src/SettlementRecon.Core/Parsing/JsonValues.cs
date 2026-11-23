using System.Text.Json;

namespace SettlementRecon.Parsing;

/// <summary>Helpers for reading scalar JSON values as raw UTF-8 without allocating.</summary>
public static class JsonValues
{
    /// <summary>
    /// Reads a string (unescaped into <paramref name="buffer"/> only when it contains escapes) or the raw text
    /// of a number. <c>null</c> reads as empty. Returns false for other tokens or values longer than the buffer.
    /// </summary>
    public static bool TryRead(in Utf8JsonReader reader, Span<byte> buffer, out ReadOnlySpan<byte> value)
    {
        value = default;
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return true;
            case JsonTokenType.Number:
                value = reader.ValueSpan;
                return value.Length <= buffer.Length;
            case JsonTokenType.String when !reader.ValueIsEscaped:
                value = reader.ValueSpan;
                return value.Length <= buffer.Length;
            case JsonTokenType.String when reader.ValueSpan.Length <= buffer.Length:
                value = buffer[..reader.CopyString(buffer)];
                return true;
            default:
                return false;
        }
    }
}
