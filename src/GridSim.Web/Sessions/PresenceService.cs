using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace GridSim.Web.Sessions;

/// <summary>
/// Process-wide anonymised presence + server-load telemetry for the live "who's online" panel. A single
/// singleton is shared by every Blazor circuit, so a viewer registered on one circuit is visible to all. No PII
/// is held: each viewer gets a random short handle; only the replay instant they're looking at, how long they've
/// been on, and play/pause are exposed. Server CPU / RAM are sampled from /proc (Linux; NaN elsewhere).
/// </summary>
public sealed class PresenceService : IDisposable
{
    public sealed record Viewer(
        string Handle, string Case, bool Raw, DateTime? ReplayUtc, bool Playing,
        DateTime ConnectedUtc, DateTime LastSeenUtc);

    private readonly ConcurrentDictionary<string, Viewer> _viewers = new();
    private readonly Timer _sampler;
    // A circuit heartbeats ~every 4 s; drop a viewer three missed beats after its last sign of life.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(14);

    // ---- durable anonymised usage log ("crap analytics": one JSONL line per connect / leave, reviewed with jq) ----
    private readonly string? _logPath;
    private readonly object _logLock = new();
    private int _sessionsThisRun;
    private int _totalVisits;
    /// <summary>Sessions started since this server process started.</summary>
    public int SessionsThisRun => _sessionsThisRun;
    /// <summary>All-time visits recorded in the usage log (connect events).</summary>
    public int TotalVisits => _totalVisits;
    public string? LogPath => _logPath;
    /// <summary>Why the usage log is off (the path and the error), or null when it is on. Known limitation "The usage
    /// log" (chapter 36): a log folder that could not be created or a log file that could not be written used to switch
    /// the log off without a word; the reason is now kept here and printed once on stderr at start-up.</summary>
    public string? LogDisabledReason { get; }
    // Set to 1 the first time an append fails, so a log that stops being writable while the host runs is reported on
    // stderr once, not once per visit (and it is still never allowed to break a circuit).
    private int _appendFailureReported;

    // ---- server load (sampled every few seconds) ----
    public double CpuPercent { get; private set; } = double.NaN;
    public double MemUsedGb { get; private set; } = double.NaN;
    public double MemTotalGb { get; private set; } = double.NaN;
    public double ProcRamMb { get; private set; }
    public int ProcThreads { get; private set; }
    private (ulong idle, ulong total)? _lastCpu;

    public PresenceService(IConfiguration config)
    {
        _logPath = ResolveLogPath(config, out string? disabledReason);
        LogDisabledReason = disabledReason;
        // The log stays best-effort (the app starts either way), but an operator now sees that it is off and why,
        // instead of finding an empty visit count weeks later (systemd ProtectSystem=strict, a container user).
        if (disabledReason is not null)
            Console.Error.WriteLine($"GridSim.Web: warning: the usage log is off: {disabledReason}");
        _totalVisits = CountVisits(_logPath);
        Sample(null);   // prime the CPU delta + first readings
        _sampler = new Timer(Sample, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    // Log path: Analytics:LogPath if set, else a usage/ dir NEXT TO (not inside) the deploy dir so it survives
    // redeploys. Best-effort - analytics must never take the app down.
    // Known limitation "The usage log" (chapter 36): a folder that can be created is not enough. Under systemd
    // ProtectSystem=strict or a container user the folder can exist while the file cannot be written, and every append
    // then failed in silence. The file is now appended with nothing once here, the same call a visit uses (it creates
    // the file empty when it is missing and leaves an existing log unchanged), so an unwritable log is found at
    // start-up. On any failure the log is off (null) and disabledReason names the path and the error, for the caller
    // to print.
    private static string? ResolveLogPath(IConfiguration config, out string? disabledReason)
    {
        string? path = null;
        try
        {
            path = config["Analytics:LogPath"]
                ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "gridsim-usage", "usage.jsonl"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, string.Empty);
            disabledReason = null;
            return path;
        }
        catch (Exception ex)
        {
            disabledReason = $"'{path ?? "(no path)"}' cannot be written ({ex.GetType().Name}: {ex.Message}); "
                + "set Analytics:LogPath to a file in a writable folder to record visits";
            return null;
        }
    }

    private static int CountVisits(string? path)
    {
        try
        {
            if (path is null || !File.Exists(path)) return 0;
            int n = 0;
            foreach (string line in File.ReadLines(path))
                if (line.Contains("\"ev\":\"visit\"", StringComparison.Ordinal)) n++;
            return n;
        }
        catch { return 0; }
    }

    private void LogEvent(object ev)
    {
        if (_logPath is null) return;
        try
        {
            string line = System.Text.Json.JsonSerializer.Serialize(ev) + "\n";
            lock (_logLock) File.AppendAllText(_logPath, line);
        }
        catch (Exception ex)
        {
            // Best-effort - never let the usage log break a circuit. Known limitation "The usage log" (chapter 36):
            // the first failed append is now reported on stderr (once per process), so a log that stops being
            // writable after start-up is not lost without a word.
            if (Interlocked.Exchange(ref _appendFailureReported, 1) == 0)
                Console.Error.WriteLine($"GridSim.Web: warning: the usage log '{_logPath}' could not be appended ({ex.GetType().Name}: {ex.Message}); visits are not being recorded.");
        }
    }

    // One clean line per COMPLETED session (>= 2 s, so reconnect churn / bounces are ignored). This is the whole
    // "over time" record: when it ended, how long it lasted, and the replay instant it was viewing.
    private void LogLeave(Viewer v)
    {
        DateTime now = DateTime.UtcNow;
        int dur = (int)Math.Max(0, (now - v.ConnectedUtc).TotalSeconds);
        if (dur < 2) return;
        Interlocked.Increment(ref _totalVisits);
        Interlocked.Increment(ref _sessionsThisRun);
        LogEvent(new
        {
            t = now.ToString("o"), ev = "visit", id = v.Handle, @case = v.Case, raw = v.Raw,
            connected = v.ConnectedUtc.ToString("o"), dur_s = dur, replay = v.ReplayUtc?.ToString("o"),
        });
    }

    // ---- presence -----------------------------------------------------------
    public string Register(string caseLabel, bool raw)
    {
        string handle = "guest-" + Guid.NewGuid().ToString("N")[..4];
        DateTime now = DateTime.UtcNow;
        _viewers[handle] = new Viewer(handle, caseLabel, raw, null, false, now, now);
        return handle;
    }

    /// <summary>Heartbeat + state update from a viewer's circuit (keeps it alive, incl. while paused).</summary>
    public void Touch(string? handle, string caseLabel, bool raw, DateTime? replayUtc, bool playing)
    {
        if (string.IsNullOrEmpty(handle)) return;
        DateTime now = DateTime.UtcNow;
        _viewers.AddOrUpdate(handle,
            _ => new Viewer(handle, caseLabel, raw, replayUtc, playing, now, now),
            (_, v) => v with { Case = caseLabel, Raw = raw, ReplayUtc = replayUtc, Playing = playing, LastSeenUtc = now });
    }

    public void Unregister(string? handle)
    {
        if (!string.IsNullOrEmpty(handle) && _viewers.TryRemove(handle, out Viewer? v)) LogLeave(v);
    }

    /// <summary>Live viewers, oldest first, with stale circuits pruned (a prune logs a 'leave' too).</summary>
    public IReadOnlyList<Viewer> Viewers()
    {
        DateTime cutoff = DateTime.UtcNow - StaleAfter;
        foreach (KeyValuePair<string, Viewer> kv in _viewers)
            if (kv.Value.LastSeenUtc < cutoff && _viewers.TryRemove(kv.Key, out Viewer? gone))
                LogLeave(gone);
        return _viewers.Values.OrderBy(v => v.ConnectedUtc).ToList();
    }

    /// <summary>Live viewer count without allocating/sorting (read every frame for the header badge).</summary>
    public int Count
    {
        get
        {
            DateTime cutoff = DateTime.UtcNow - StaleAfter;
            int n = 0;
            foreach (Viewer v in _viewers.Values)
                if (v.LastSeenUtc >= cutoff) n++;
            return n;
        }
    }

    // ---- server load sampling ----------------------------------------------
    private void Sample(object? _)
    {
        try
        {
            using Process p = Process.GetCurrentProcess();
            ProcThreads = p.Threads.Count;
            ProcRamMb = p.WorkingSet64 / 1024.0 / 1024.0;
        }
        catch { /* ignore */ }
        try { SampleCpu(); } catch { /* non-Linux / no /proc */ }
        try { SampleMem(); } catch { /* non-Linux / no /proc */ }
    }

    // /proc/stat first line: "cpu  user nice system idle iowait irq softirq steal ..." (USER_HZ ticks).
    private void SampleCpu()
    {
        string line = File.ReadLines("/proc/stat").First();
        string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        ulong total = 0, idle = 0;
        for (int i = 1; i < p.Length; i++)
        {
            if (!ulong.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong v)) continue;
            total += v;
            if (i == 4 || i == 5) idle += v;   // idle + iowait
        }
        if (_lastCpu is { } last && total > last.total)
        {
            double dt = total - last.total, di = idle - last.idle;
            CpuPercent = Math.Clamp((1.0 - di / dt) * 100.0, 0, 100);
        }
        _lastCpu = (idle, total);
    }

    // /proc/meminfo: "MemTotal: N kB", "MemAvailable: N kB".
    private void SampleMem()
    {
        double totalKb = 0, availKb = 0;
        foreach (string l in File.ReadLines("/proc/meminfo"))
        {
            if (l.StartsWith("MemTotal:", StringComparison.Ordinal)) totalKb = ParseKb(l);
            else if (l.StartsWith("MemAvailable:", StringComparison.Ordinal)) { availKb = ParseKb(l); break; }
        }
        if (totalKb > 0)
        {
            MemTotalGb = totalKb / 1024.0 / 1024.0;
            MemUsedGb = (totalKb - availKb) / 1024.0 / 1024.0;
        }
    }

    private static double ParseKb(string line)
    {
        string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);   // ["MemTotal:", "16384000", "kB"]
        return p.Length >= 2 && double.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out double kb) ? kb : 0;
    }

    public void Dispose() => _sampler.Dispose();
}
