using System.Globalization;
using System.Text.RegularExpressions;

namespace GridSim.Gda.Csv;

/// <summary>
/// Finds the newest "vintage" column of a DNO table (R300). DNOs publish one demand column per regulatory year
/// (UKPN <c>Maximum_Demand_24_25_MW</c>, NPg <c>2024/25 Net (Observed) Maximum Load (MVA)</c>) and rename it
/// with every refresh. The readers used to hard-code the 2024/25 name; a missing column reads as 0 and every
/// row was then skipped, so a new vintage silently built a network with no demand that still "converged".
/// This discovers the column by pattern and picks the newest year, so a 2025/26 table just works, and it
/// throws when there is no such column at all.
/// </summary>
internal static class VintageColumns
{
    /// <summary>
    /// The header matching <paramref name="pattern"/> whose first capture group (the starting year, two or four
    /// digits) is the largest, or null when no header matches. Two-digit years are read as 20YY.
    /// </summary>
    public static (string Header, int StartYear)? Newest(IEnumerable<string> headers, Regex pattern)
    {
        (string Header, int StartYear)? best = null;
        foreach (string h in headers)
        {
            Match m = pattern.Match(h);
            if (!m.Success) continue;
            int y = int.Parse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            if (y < 100) y += 2000;
            // Strictly newer wins; on an identical year the ordinal-smaller header wins, so the pick never
            // depends on column order.
            if (best is null || y > best.Value.StartYear
                || (y == best.Value.StartYear && string.CompareOrdinal(h, best.Value.Header) < 0))
                best = (h, y);
        }
        return best;
    }
}
