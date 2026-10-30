using GridSim.Core.Licensing;

namespace GridSim.Cli;

/// <summary>
/// The <c>licence</c> verb (and the <c>--importLicence</c> global switch): inspect and install the
/// software licence.
/// <code>
///   gridsim licence status                 show the active tier, holder, bus limit and expiry
///   gridsim licence import file.gridsim     validate a .gridsim key file and install it
///   gridsim licence path                   print where the installed licence file lives
///   gridsim --importLicence file.gridsim    shorthand for `licence import` from anywhere on the command line
/// </code>
/// A licence file is a <c>.gridsim</c> file holding one <c>GSK1 …</c> key (optionally with <c>#</c> comment
/// lines). It is installed to <c>%LOCALAPPDATA%\GridSim\licence.gridsim</c>, which every front-end reads.
/// </summary>
internal static class LicenceCli
{
    public static int Run(List<string> rest, LicenceGate gate)
    {
        string sub = rest.Count > 1 ? rest[1] : "status";
        // GR-697: the verb runs before the flag gates, so it refuses what it does not use itself: status and path take
        // nothing more, import takes exactly one file (a second one used to be ignored, and --bogus accepted).
        int allowed = sub == "import" ? 3 : 2;
        if ((sub is "status" or "path" or "import") && rest.Count > allowed)
        {
            Console.Error.WriteLine($"error: licence {sub} takes {(sub == "import" ? "one licence file" : "no further argument")}; "
                + $"got '{rest[allowed]}'.");
            return 2;
        }
        switch (sub)
        {
            case "status":
                Console.WriteLine(gate.Describe());
                Console.WriteLine($"licence file: {LicenceGate.LicenceFilePath}");
                return 0;

            case "path":
                Console.WriteLine(LicenceGate.LicenceFilePath);
                return 0;

            case "import":
                string? path = rest.Count > 2 ? rest[2] : null;
                if (string.IsNullOrWhiteSpace(path))
                {
                    Console.Error.WriteLine("usage: gridsim licence import <file.gridsim>");
                    return 2;
                }
                return Import(path);

            default:
                Console.Error.WriteLine($"unknown licence subcommand '{sub}'. Use: status | import | path");
                return 2;
        }
    }

    /// <summary>Import and install a <c>.gridsim</c> licence file. Shared by the verb and <c>--importLicence</c>.</summary>
    public static int Import(string filePath)
    {
        // D-DOC-007: an empty or blank path used to reach File.ReadAllText and crash with an unhandled ArgumentException.
        // Both callers now refuse it before calling (the verb's usage check, the switch's value check in Program.cs); this
        // is the last line of defence, with the same code (usage error, exit 2).
        if (string.IsNullOrWhiteSpace(filePath))
        {
            Console.Error.WriteLine("usage: gridsim licence import <file.gridsim> (or gridsim --importLicence <file.gridsim>)");
            return 2;
        }
        try
        {
            Entitlement ent = LicenceGate.ImportFromFile(filePath);
            Console.WriteLine($"Installed: {new LicenceGate(ent).Describe()}");
            Console.WriteLine($"Saved to:  {LicenceGate.LicenceFilePath}");
            return 0;
        }
        catch (LicenceException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 2;
        }
    }
}
