namespace SettlementRecon.Parsing;

/// <summary>Allocation-free date parsing for the handful of layouts settlement files use.</summary>
public static class Utf8Dates
{
    /// <summary><c>2026-11-03</c>.</summary>
    public static bool TryParseIsoDate(ReadOnlySpan<byte> text, out DateOnly date)
    {
        date = default;
        return text.Length == 10
            && text[4] == (byte)'-'
            && text[7] == (byte)'-'
            && TryDigits(text[..4], out var year)
            && TryDigits(text.Slice(5, 2), out var month)
            && TryDigits(text.Slice(8, 2), out var day)
            && TryCreate(year, month, day, out date);
    }

    /// <summary><c>03.11.2026</c> (or with another separator such as <c>/</c>).</summary>
    public static bool TryParseDayMonthYear(ReadOnlySpan<byte> text, byte separator, out DateOnly date)
    {
        date = default;
        return text.Length == 10
            && text[2] == separator
            && text[5] == separator
            && TryDigits(text[..2], out var day)
            && TryDigits(text.Slice(3, 2), out var month)
            && TryDigits(text.Slice(6, 4), out var year)
            && TryCreate(year, month, day, out date);
    }

    /// <summary>
    /// Reads an ISO-8601 date or timestamp and returns the calendar date in UTC:
    /// <c>2026-11-03</c>, <c>2026-11-03T23:30:00Z</c>, <c>2026-11-03T23:30:00.123+02:00</c>.
    /// </summary>
    public static bool TryParseUtcDate(ReadOnlySpan<byte> text, out DateOnly date)
    {
        if (!TryParseIsoDate(text[..Math.Min(10, text.Length)], out date))
        {
            return false;
        }

        if (text.Length == 10)
        {
            return true;
        }

        // yyyy-MM-ddTHH:mm[:ss[.fffffff]](Z|±HH:mm)
        if (text.Length < 16 || text[10] is not ((byte)'T' or (byte)' ') || text[13] != (byte)':'
            || !TryDigits(text.Slice(11, 2), out var hour) || !TryDigits(text.Slice(14, 2), out var minute)
            || hour > 23 || minute > 59)
        {
            return false;
        }

        var zone = text[16..];
        var zoneStart = zone.IndexOfAny("Z+-"u8);
        if (zoneStart < 0)
        {
            // No zone designator: treat as UTC, which is what every export in this repo writes.
            return true;
        }

        zone = zone[zoneStart..];
        if (zone.Length == 1 && zone[0] == (byte)'Z')
        {
            return true;
        }

        if (zone.Length != 6 || zone[3] != (byte)':' || !TryDigits(zone.Slice(1, 2), out var offsetHours) || !TryDigits(zone.Slice(4, 2), out var offsetMinutes))
        {
            return false;
        }

        var offset = (offsetHours * 60) + offsetMinutes;
        var minutesUtc = (hour * 60) + minute - (zone[0] == (byte)'+' ? offset : -offset);
        date = date.AddDays((int)Math.Floor(minutesUtc / 1440.0));
        return true;
    }

    private static bool TryDigits(ReadOnlySpan<byte> text, out int value)
    {
        value = 0;
        foreach (var b in text)
        {
            var digit = (uint)(b - '0');
            if (digit > 9)
            {
                return false;
            }

            value = (value * 10) + (int)digit;
        }

        return true;
    }

    private static bool TryCreate(int year, int month, int day, out DateOnly date)
    {
        date = default;
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        date = new DateOnly(year, month, day);
        return true;
    }
}
