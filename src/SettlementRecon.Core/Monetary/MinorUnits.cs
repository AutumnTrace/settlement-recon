using System.Globalization;

namespace SettlementRecon.Monetary;

/// <summary>
/// Converts decimal amount text straight into integer minor units (cents, satoshis, …) without going
/// through <see cref="decimal"/> or <see cref="double"/>. This runs once per amount column on the hot path.
/// </summary>
public static class MinorUnits
{
    private static readonly long[] PowersOfTen = BuildPowers();

    /// <summary>Largest exponent supported (BTC uses 8).</summary>
    public const int MaxExponent = 12;

    /// <summary>
    /// Parses <paramref name="text"/> into minor units with the given exponent.
    /// Accepts an optional leading sign, group separators between digits and up to
    /// <paramref name="exponent"/> fraction digits. Extra trailing zeros are fine, extra precision is rejected.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> text, int exponent, DecimalStyle style, out long minor)
    {
        minor = 0;
        text = text.Trim((byte)' ');
        if (text.IsEmpty || (uint)exponent > MaxExponent)
        {
            return false;
        }

        var negative = false;
        var i = 0;
        if (text[0] is (byte)'-' or (byte)'+')
        {
            negative = text[0] == (byte)'-';
            i = 1;
        }

        var decimalSeparator = style == DecimalStyle.Dot ? (byte)'.' : (byte)',';
        long value = 0;
        var digits = 0;
        var fractionDigits = 0;
        var inFraction = false;
        var previousWasDigit = false;

        // Group separators must split the integer part into groups of three ("1.234,56" yes, "12.50" no),
        // otherwise a dot-decimal amount fed to a comma-decimal parser would be silently read as 1250.
        var groupSeen = false;
        var digitsInGroup = 0;

        for (; i < text.Length; i++)
        {
            var b = text[i];
            var digit = (uint)(b - '0');
            if (digit <= 9)
            {
                previousWasDigit = true;
                if (!inFraction)
                {
                    digitsInGroup++;
                }

                if (inFraction && fractionDigits >= exponent)
                {
                    // More precision than the currency has: only trailing zeros are harmless.
                    if (digit != 0)
                    {
                        return false;
                    }

                    continue;
                }

                if (value > (long.MaxValue - digit) / 10)
                {
                    return false;
                }

                value = (value * 10) + digit;
                digits++;
                if (inFraction)
                {
                    fractionDigits++;
                }

                continue;
            }

            if (b == decimalSeparator && !inFraction && i < text.Length - 1)
            {
                if (groupSeen && digitsInGroup != 3)
                {
                    return false;
                }

                inFraction = true;
                previousWasDigit = false;
                continue;
            }

            var nextIsDigit = i + 1 < text.Length && (uint)(text[i + 1] - '0') <= 9;
            if (!inFraction && previousWasDigit && nextIsDigit && IsGroupSeparator(b, style))
            {
                if (groupSeen ? digitsInGroup != 3 : digitsInGroup > 3)
                {
                    return false;
                }

                groupSeen = true;
                digitsInGroup = 0;
                previousWasDigit = false;
                continue;
            }

            return false;
        }

        if (digits == 0 || (groupSeen && !inFraction && digitsInGroup != 3))
        {
            return false;
        }

        var scale = PowersOfTen[exponent - fractionDigits];
        if (value > long.MaxValue / scale)
        {
            return false;
        }

        value *= scale;
        minor = negative ? -value : value;
        return true;
    }

    /// <summary>Formats minor units as plain invariant text, e.g. <c>-1234.56</c>.</summary>
    public static string Format(long minor, int exponent) => Format(minor, exponent, grouped: false);

    /// <summary>Formats minor units with thousands separators, e.g. <c>-1,234.56</c>.</summary>
    public static string FormatGrouped(long minor, int exponent) => Format(minor, exponent, grouped: true);

    public static decimal ToDecimal(long minor, int exponent)
    {
        var magnitude = minor < 0 ? (ulong)-(minor + 1) + 1 : (ulong)minor;
        return new decimal((int)(uint)magnitude, (int)(uint)(magnitude >> 32), 0, minor < 0, (byte)exponent);
    }

    /// <summary>Converts a decimal amount to minor units. Throws if the value has more precision than the currency.</summary>
    public static long FromDecimal(decimal value, int exponent)
    {
        var scaled = value * PowersOfTen[exponent];
        if (scaled != decimal.Truncate(scaled))
        {
            throw new ArgumentException(
                $"{value.ToString(CultureInfo.InvariantCulture)} has more than {exponent} decimal places.", nameof(value));
        }

        return decimal.ToInt64(scaled);
    }

    private static string Format(long minor, int exponent, bool grouped)
    {
        var magnitude = minor < 0 ? (ulong)-(minor + 1) + 1 : (ulong)minor;
        var divisor = (ulong)PowersOfTen[exponent];
        var whole = magnitude / divisor;
        var fraction = magnitude % divisor;

        var sign = minor < 0 ? "-" : string.Empty;
        var wholeText = whole.ToString(grouped ? "#,0" : "0", CultureInfo.InvariantCulture);
        if (exponent == 0)
        {
            return sign + wholeText;
        }

        var fractionText = fraction.ToString(CultureInfo.InvariantCulture).PadLeft(exponent, '0');
        return string.Concat(sign, wholeText, ".", fractionText);
    }

    private static bool IsGroupSeparator(byte b, DecimalStyle style) =>
        style == DecimalStyle.Dot ? b == (byte)',' : b is (byte)'.' or (byte)' ' or (byte)'\'';

    private static long[] BuildPowers()
    {
        var powers = new long[MaxExponent + 1];
        powers[0] = 1;
        for (var i = 1; i < powers.Length; i++)
        {
            powers[i] = powers[i - 1] * 10;
        }

        return powers;
    }
}
