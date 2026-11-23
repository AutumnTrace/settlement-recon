using SettlementRecon.Domain;

namespace SettlementRecon.Matching;

/// <summary>
/// The fallback rule for lines whose reference matched nothing: pair a leftover settlement line with an unclaimed
/// ledger entry of the same currency when the amounts agree within tolerance and the dates fall inside the window.
/// A pairing is only made when exactly one candidate qualifies; ambiguity is reported, never guessed.
/// Runs on leftovers only (normally a tiny fraction of the file), in O((n + m) log m).
/// </summary>
public static class AmountAndDateMatcher
{
    /// <param name="Candidates">How many unclaimed ledger entries qualified for this line.</param>
    /// <param name="LedgerIndex">Index into the ledger leftovers of the chosen entry, or -1.</param>
    public readonly record struct Decision(int Candidates, int LedgerIndex);

    /// <summary>Decides, in settlement-line order, which leftover ledger entry (if any) each leftover line pairs with.</summary>
    public static Decision[] Match(IReadOnlyList<SettlementRecord> lines, IReadOnlyList<LedgerEntry> entries, ReconciliationOptions options)
    {
        var decisions = new Decision[lines.Count];
        if (!options.AmountAndDateFallback || lines.Count == 0 || entries.Count == 0)
        {
            Array.Fill(decisions, new Decision(0, -1));
            return decisions;
        }

        // Sort ledger leftovers by (currency, amount) once, then binary-search a window per line.
        var order = Enumerable.Range(0, entries.Count)
            .OrderBy(i => entries[i].Amount.Currency.GetHashCode())
            .ThenBy(i => entries[i].Amount.Minor)
            .ToArray();
        var taken = new bool[entries.Count];

        for (var l = 0; l < lines.Count; l++)
        {
            var line = lines[l];
            var currencyKey = line.Gross.Currency.GetHashCode();

            // Tolerance is relative to the ledger amount, which is close to the line amount: searching with
            // the tolerance of twice the magnitude is a safe superset, then each candidate is checked exactly.
            var radius = options.AmountToleranceFor(Math.Abs(line.Gross.Minor) * 2);
            var start = LowerBound(order, entries, currencyKey, line.Gross.Minor - radius);

            var candidates = 0;
            var chosen = -1;
            for (var k = start; k < order.Length; k++)
            {
                var entry = entries[order[k]];
                if (entry.Amount.Currency.GetHashCode() != currencyKey || entry.Amount.Minor > line.Gross.Minor + radius)
                {
                    break;
                }

                if (!taken[order[k]] && MatchRules.IsAmountAndDateCandidate(line.Gross, line.ValueDate, entry.Amount, entry.BookingDate, options))
                {
                    candidates++;
                    chosen = order[k];
                }
            }

            if (candidates == 1)
            {
                taken[chosen] = true;
                decisions[l] = new Decision(1, chosen);
            }
            else
            {
                decisions[l] = new Decision(candidates, -1);
            }
        }

        return decisions;
    }

    private static int LowerBound(int[] order, IReadOnlyList<LedgerEntry> entries, int currencyKey, long amount)
    {
        int low = 0, high = order.Length;
        while (low < high)
        {
            var mid = (low + high) >>> 1;
            var entry = entries[order[mid]];
            var key = entry.Amount.Currency.GetHashCode();
            if (key < currencyKey || (key == currencyKey && entry.Amount.Minor < amount))
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }
}
