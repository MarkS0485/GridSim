using System.Globalization;

namespace GridSim.Cli;

/// <summary>R2-04: the names of the <c>rpc --listen</c> token files, <c>rpc-&lt;host&gt;-&lt;pid&gt;-&lt;random&gt;.token</c>. The machine
/// is part of the name because the per-user data folder can be one home folder shared by several machines (an HPC
/// cluster): a host sweeps only its own machine's stale files, and two hosts never share a name.</summary>
internal static class RpcTokenFiles
{
    /// <summary>R2-04: a machine name as it appears in a token file name: its letters, digits and hyphens only (the rest
    /// dropped), lower case ("host" when nothing is left), then R4-06 a dash and the first 8 hex digits of SHA-256 of the
    /// machine name as it is, so two machines whose cleaned names agree (node_1 and node1, Node1 and node1) never share
    /// a name and never sweep each other's live files.</summary>
    internal static string HostName(string machine)
    {
        string h = CleanName(machine);
        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(machine)))[..8];
        return h + "-" + hash;
    }

    /// <summary>The machine name's letters, digits and hyphens, lower case ("host" when nothing is left): the host part of
    /// the 2.1.160 to 2.1.161 token file names, before R4-06 added the hash.</summary>
    internal static string CleanName(string machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        string h = new string(machine.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray()).ToLowerInvariant();
        return h.Length == 0 ? "host" : h;
    }

    /// <summary>R6-03: the process id of a token file in the 2.1.160 to 2.1.161 shape, <c>rpc-&lt;clean&gt;-&lt;pid&gt;-&lt;8 hex&gt;.token</c>
    /// with this machine's cleaned name (no hash), or null for any other name. A file of today's shape has one more dash
    /// part and is not matched. Best effort, as for the older shape: two machines whose cleaned names agree cannot be told
    /// apart in this shape.</summary>
    internal static int? MidShapePid(string fileName, string machine) => Pid(fileName, CleanName(machine));

    /// <summary>R4-06: the process id of a token file in the 2.1.156 to 2.1.158 shape, exactly <c>rpc-&lt;digits&gt;.token</c>
    /// (no machine part), or null for any other name. Such a file is swept, best effort, when no process of that id runs
    /// on this machine.</summary>
    internal static int? OldShapePid(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (!fileName.StartsWith("rpc-", StringComparison.Ordinal) || !fileName.EndsWith(".token", StringComparison.Ordinal)) return null;
        string digits = fileName["rpc-".Length..^".token".Length];
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) return null;
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) ? pid : null;
    }

    /// <summary>R2-04: <c>rpc-&lt;host&gt;-&lt;pid&gt;-&lt;random&gt;.token</c>.</summary>
    internal static string Name(string host, int pid, string random)
        => "rpc-" + host + "-" + pid.ToString(CultureInfo.InvariantCulture) + "-" + random + ".token";

    /// <summary>R2-04: the process id in a token file name of <paramref name="host"/>, or null when the name is not one of
    /// that host's token files (another machine's, or another shape).</summary>
    internal static int? Pid(string fileName, string host)
    {
        string prefix = "rpc-" + host + "-";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal) || !fileName.EndsWith(".token", StringComparison.Ordinal)) return null;
        string rest = fileName[prefix.Length..^".token".Length];
        // Exactly <pid>-<8 hex digits>: a name of another host whose name only begins with this one ("node" and "node-07")
        // has another dash and is not taken for this host's.
        string[] parts = rest.Split('-');
        if (parts.Length != 2 || parts[1].Length != 8 || !parts[1].All(Uri.IsHexDigit)) return null;
        return int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) ? pid : null;
    }
}
