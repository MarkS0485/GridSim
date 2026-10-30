using System.Globalization;
using GridSim.Core.Identity;

namespace GridSim.Cli;

/// <summary>
/// The <c>ref</c> verb (2026-10-24): electrical word references (GridSim.Core.Identity) from the command line.
/// <code>
///   gridsim ref encode &lt;value&gt;    [--words N] [--check]
///   gridsim ref decode &lt;reference&gt; [--words N] [--check]
///   gridsim ref scatter &lt;number|reference&gt; --seed S [--words N] [--check]
///   gridsim ref from-id &lt;id&gt;      [--words N] [--check]
/// </code>
/// Handled before the flag gate (as the licence verb is), because its arguments are a subcommand and one free value.
/// Exit 0 with the answer on standard output; exit 1 when a reference is refused (the reason on standard error); exit 2
/// on a usage error. Pure: no case, no licence, no network.
/// </summary>
internal static class RefCli
{
    /// <summary>The default number of data words (3 words = 16,777,216 references).</summary>
    public const int DefaultWords = 3;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const string Usage =
        "usage: gridsim ref encode <value> | decode <reference> | scatter <number|reference> --seed S | from-id <id>  [--words N] [--check]";

    /// <summary>Run the verb; <paramref name="rest"/> starts with "ref". Returns the exit code.</summary>
    public static int Run(IReadOnlyList<string> rest)
    {
        ArgumentNullException.ThrowIfNull(rest);
        try
        {
            var args = rest.Skip(1).ToList();
            bool check = Take(args, "--check");
            int words = TakeInt(args, "--words") ?? DefaultWords;
            ulong? seed = TakeULong(args, "--seed");
            string? unknown = args.FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal));
            if (unknown is not null) return UsageError($"unknown flag {unknown}.");
            if (args.Count != 2) return UsageError(args.Count == 0 ? "a subcommand is needed." : "one subcommand and one value are needed.");
            string sub = args[0], value = args[1];

            switch (sub)
            {
                case "encode":
                    Console.WriteLine(ElectricalRef.Encode(ParseValue(value), words, check));
                    return 0;
                case "decode":
                {
                    if (!ElectricalRef.TryDecode(value, words, check, out ulong v, out string? error)) return Refused(error!);
                    Console.WriteLine(v.ToString(Inv) + "  0x" + v.ToString("X" + (2 * words).ToString(Inv), Inv));
                    return 0;
                }
                case "scatter":
                {
                    if (seed is not ulong key) return UsageError("scatter needs --seed S (the key of the arrangement; the same seed always gives the same references).");
                    var scatter = new ElectricalScatter(key, words);
                    if (TryParseValue(value, out ulong number))
                    {
                        Console.WriteLine(scatter.RefOf(number, check));
                        return 0;
                    }
                    if (!ElectricalRef.TryDecode(value, words, check, out ulong scattered, out string? error)) return Refused(error!);
                    Console.WriteLine(scatter.Unpermute(scattered).ToString(Inv));
                    return 0;
                }
                case "from-id":
                    Console.WriteLine(ElectricalRefSource.FromId(value, words, check));
                    return 0;
                default:
                    return UsageError($"unknown subcommand '{sub}'.");
            }
        }
        catch (ArgumentException ex)
        {
            // A word count outside its range, a value that does not fit, an empty id: the caller's input, a usage error.
            return UsageError(ex.Message);
        }
    }

    private static ulong ParseValue(string text) =>
        TryParseValue(text, out ulong v) ? v : throw new ArgumentException($"'{text}' is not a whole number (decimal, or hexadecimal after 0x).");

    private static bool TryParseValue(string text, out ulong value) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, Inv, out value)
            : ulong.TryParse(text, NumberStyles.None, Inv, out value);

    private static bool Take(List<string> a, string flag)
    {
        bool found = false;
        int i;
        while ((i = a.IndexOf(flag)) >= 0) { a.RemoveAt(i); found = true; }
        return found;
    }

    private static string? TakeValue(List<string> a, string flag)
    {
        int i = a.IndexOf(flag);
        if (i < 0) return null;
        if (i + 1 >= a.Count) throw new ArgumentException($"{flag} needs a value.");
        string v = a[i + 1];
        a.RemoveRange(i, 2);
        if (a.Contains(flag)) throw new ArgumentException($"{flag} was given twice.");
        return v;
    }

    private static int? TakeInt(List<string> a, string flag) =>
        TakeValue(a, flag) is string v
            ? int.TryParse(v, NumberStyles.None, Inv, out int n) ? n : throw new ArgumentException($"{flag} needs a whole number, not '{v}'.")
            : null;

    private static ulong? TakeULong(List<string> a, string flag) =>
        TakeValue(a, flag) is string v
            ? TryParseValue(v, out ulong n) ? n : throw new ArgumentException($"{flag} needs a whole number (decimal, or hexadecimal after 0x), not '{v}'.")
            : null;

    private static int UsageError(string message)
    {
        Console.Error.WriteLine("ref: " + message);
        Console.Error.WriteLine(Usage);
        return 2;
    }

    private static int Refused(string reason)
    {
        Console.Error.WriteLine("ref: " + reason);
        return 1;
    }
}
