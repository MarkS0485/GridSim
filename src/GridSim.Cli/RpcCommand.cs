using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GridSim.Core.Lines;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Cli;

/// <summary>
/// RC4 R4-05: <c>gridsim rpc</c>, the in-process access endpoint the Python package (<c>gridsim.Session</c>) drives.
/// Protocol <c>gridsim-rpc/1</c>: one JSON request per line in, one JSON response per line out, sequentially. By default the
/// lines travel on stdin and stdout (no network at all); <c>--listen host:port</c> serves them on a TCP socket instead, and
/// only on a loopback address unless <c>--allow-non-loopback</c> is given (the token travels unencrypted, so beyond loopback
/// is an explicit choice).
///
/// <para>Hardening of the TCP transport (the stdio transport is unchanged): a request line is read with a bound of
/// <see cref="MaxLineChars"/> characters (reading stops there, nothing more is buffered); a line that is over it, is not
/// JSON or is not a JSON object ends the connection after its error response, so another protocol spoken at the port (an
/// HTTP request from a web page, for example) never reaches an operation; and a network (UNC) or device path is refused in
/// <c>load</c> and in the arguments of <c>run-verb</c>, in every item of a comma list (CLI-09).</para>
///
/// <para>CLI-03: the host draws a random token when it starts listening and writes it to a file only this user can read,
/// in the per-user data folder; the first output line names the file (never the token). Every request over the socket must
/// carry the token as <c>"token"</c>, or it is refused and the connection ends. The case of <c>load</c>, the verb of
/// <c>run-verb</c> and every file its arguments name must lie inside the host's working folder (the rule of <c>save</c>).</para>
///
/// Nothing here duplicates a study engine. <c>solve</c> runs the solver the <c>pf</c> verb runs on a case loaded once and
/// held by handle; <c>line-constants</c> runs the R4-04 Core solver; <c>run-verb</c> runs ANY existing verb exactly as the
/// command line would, as a child process of this host, so every legacy output stays the CLI's own. The licence gate is the
/// one the rest of the CLI resolved at start-up (<see cref="Gated"/>), so a refused load is the CLI's refusal.
///
/// Determinism: a response is a pure function of the request and the session's earlier requests. Responses carry no
/// timestamps, process ids or temporary paths; handles are numbered per session; numbers are written in round-trip form.
/// The one exception is protocol level 2, whose handles and hello carry a random token of the host instance (RC5
/// REVIEW-FIX-6), so a handle cannot be mistaken for another host's case.
/// </summary>
internal static partial class RpcCommand
{
    /// <summary>The protocol label carried by every response.</summary>
    public const string Protocol = "gridsim-rpc/1";

    /// <summary>The longest request line accepted, in characters.</summary>
    public const int MaxLineChars = 16 * 1024 * 1024;

    internal static readonly string[] Operations = ["hello", "load", "release", "solve", "stats", "run-verb", "line-constants", "shutdown"];

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed class HeldCase(string arg, GridModel model, string sha, string identity)
    {
        public string Arg { get; } = arg;
        /// <summary>The case as loaded; under protocol level 2 the object model's edits replace it (RpcObjectOps).</summary>
        public GridModel Model { get; set; } = model;
        public string Sha { get; } = sha;
        public string Identity { get; } = identity;
        /// <summary>Protocol level 2: the object model over this case (made on the first object operation).</summary>
        public GridSim.Core.Api.CaseEditor? Editor { get; set; }
        /// <summary>Protocol level 2: the pf solve of the current revision, for the branch flow attributes.</summary>
        public (int Revision, PowerFlowResult Result)? Flows { get; set; }
    }

    /// <summary>An operation failed: <see cref="Exit"/> is the CLI exit-code equivalent.</summary>
    private sealed class RpcError(string kind, string message, int exit, string? sha = null) : Exception(message)
    {
        public string Kind { get; } = kind;
        public int Exit { get; } = exit;
        public string? Sha { get; } = sha;
        public string? Stdout { get; init; }
        public string? Stderr { get; init; }
    }

    // ---- the verb ------------------------------------------------------------------------------------------------

    /// <summary>The entry point: <paramref name="a"/> is the whole argument list with the verb first.</summary>
    public static int Run(List<string> a, string baseDir, Func<string, GridModel> load)
    {
        string? listen = null;
        bool allowRemote = false;
        int level = 0;
        int? idleSeconds = null;
        for (int i = 1; i < a.Count; i++)
        {
            switch (a[i])
            {
                case "--protocol":
                    // RC5 WP11: protocol level 2 (the object model) is opt-in; 1 (the default) is gridsim-rpc/1, byte for byte.
                    if (i + 1 >= a.Count || a[i + 1].StartsWith("--", StringComparison.Ordinal)) return Usage("--protocol expected 1 or 2 but none was given");
                    if (level != 0) return Usage("--protocol was given twice");
                    level = a[++i] switch { "1" => 1, "2" => 2, _ => -1 };
                    if (level < 0) return Usage($"--protocol expected 1 or 2 but got '{a[i]}'");
                    break;
                case "--listen":
                    if (i + 1 >= a.Count || a[i + 1].StartsWith("--", StringComparison.Ordinal)) return Usage("--listen expected host:port but none was given");
                    if (listen is not null) return Usage("--listen was given twice");
                    listen = a[++i];
                    break;
                case "--allow-non-loopback":
                    allowRemote = true;
                    break;
                case "--idle-seconds":
                    // R1-07: how long an authenticated connection may stay silent before it is closed (0: never).
                    if (i + 1 >= a.Count) return Usage("--idle-seconds expected a whole number of seconds but none was given");
                    if (idleSeconds is not null) return Usage("--idle-seconds was given twice");
                    if (!int.TryParse(a[++i], NumberStyles.None, CultureInfo.InvariantCulture, out int idle) || idle > 86_400)
                        return Usage($"--idle-seconds expected a whole number of seconds from 0 to 86400 but got '{a[i]}'");
                    idleSeconds = idle;
                    break;
                default:
                    return Usage($"rpc does not use {a[i]}. rpc accepts: --listen, --allow-non-loopback, --idle-seconds, --protocol.");
            }
        }
        if (allowRemote && listen is null) return Usage("--allow-non-loopback only applies with --listen");
        if (idleSeconds is not null && listen is null) return Usage("--idle-seconds only applies with --listen");
        if (level == 0) level = 1;

        // Anything an engine prints to the console must not corrupt the protocol stream: stdout belongs to the protocol,
        // so the console writer is pointed at stderr for the whole session.
        TextWriter protocolOut = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        Console.SetOut(Console.Error);

        if (listen is null)
        {
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            new Session(baseDir, load, level: level).Serve(input, protocolOut);
            return CliExit.Ok;
        }

        if (!TryParseListen(listen, out string host, out int port))
            return Usage($"--listen expected host:port, [IPv6]:port or a bare port (0 to 65535) but got '{listen}'");
        IPAddress? address = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback
            : IPAddress.TryParse(host, out IPAddress? parsed) ? parsed : null;
        if (address is null) return Usage($"--listen host '{host}' is not an IP address or localhost");
        if (!IPAddress.IsLoopback(address))
        {
            if (!allowRemote)
            {
                Console.Error.WriteLine($"error: rpc: refusing to listen on {host}: it is not a loopback address, and the endpoint's token and data travel unencrypted. " +
                    "Use 127.0.0.1 or ::1, or add --allow-non-loopback to accept the risk.");
                return CliExit.Usage;
            }
            Console.Error.WriteLine($"warning: rpc: listening on {host}, beyond loopback; the token and the data travel unencrypted (--allow-non-loopback).");
        }

        // CLI-03: a random token for this host, written to a file only this user can read, in the per-user data folder; the
        // path is printed, never the token. Every request over --listen must carry it ("token"), so another local user (or a
        // host beyond loopback) who can reach the port cannot use the endpoint.
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        string tokenFile;
        try { tokenFile = WriteTokenFile(token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"error: rpc: cannot write the session token file: {ex.Message}");
            return CliExit.InputError;
        }
        // R1-06: Ctrl+C, SIGTERM and closing the console end the process without running finally blocks; the token file is
        // removed on those routes too, so no live-looking token is left behind.
        void RemoveTokenFile()
        {
            try { File.Delete(tokenFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        using var onInterrupt = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGINT, _ => RemoveTokenFile());
        using var onTerminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, _ => RemoveTokenFile());
        using var onHangUp = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGHUP, _ => RemoveTokenFile());
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RemoveTokenFile();
        // R1-07: a connection must present its first request within FirstRequestSeconds (so an idle peer cannot hold the
        // single accept loop without the token), then may stay silent for --idle-seconds (default 120; 0 never times out).
        int idleMs = (idleSeconds ?? DefaultIdleSeconds) * 1000;
        int firstMs = idleMs == 0 ? FirstRequestSeconds * 1000 : Math.Min(FirstRequestSeconds * 1000, idleMs);

        try
        {
            var listener = new TcpListener(address, port);
            try { listener.Start(); }
            catch (SocketException ex)
            {
                Console.Error.WriteLine($"error: rpc: cannot listen on {listen}: {ex.Message}");
                return CliExit.InputError;
            }
            try
            {
                IPEndPoint bound = (IPEndPoint)listener.LocalEndpoint;
                string shown = bound.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{bound.Address}]:{bound.Port}" : $"{bound.Address}:{bound.Port}";
                using (var ms = new MemoryStream())
                {
                    using (var w = new Utf8JsonWriter(ms, WriterOptions))
                    {
                        w.WriteStartObject();
                        w.WriteString("protocol", RpcProtocol.Label(level));
                        w.WriteString("listening", shown);
                        w.WriteString("token_file", tokenFile);
                        w.WriteEndObject();
                    }
                    protocolOut.WriteLine(Encoding.UTF8.GetString(ms.ToArray()));
                }
                bool stop = false;
                while (!stop)
                {
                    using TcpClient client = listener.AcceptTcpClient();
                    using NetworkStream stream = client.GetStream();
                    // R4-08: a response write to a peer that has stopped reading ends at the idle limit (an IOException,
                    // caught below), so such a peer cannot hold the endpoint while the idle watch is stopped for the write.
                    stream.WriteTimeout = idleMs == 0 ? Timeout.Infinite : idleMs;
                    using var reader = new StreamReader(stream, new UTF8Encoding(false));
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
                    // R2-03: one watch per connection. Until the token has been presented it is an absolute deadline
                    // (firstMs from the accept, whatever the peer sends: a trickle of bytes does not extend it). After that
                    // it is the idle limit, started when a response has been written and stopped while a request is
                    // handled, so a long verb does not count as silence and a partial line does not reset it.
                    bool authed = false;
                    using var watch = new Timer(_ => { try { client.Close(); } catch (ObjectDisposedException) { } }, null, firstMs, Timeout.Infinite);
                    void Authenticated() { authed = true; watch.Change(Timeout.Infinite, Timeout.Infinite); }
                    void Busy() { if (authed) watch.Change(Timeout.Infinite, Timeout.Infinite); }
                    void Idle() { if (authed) watch.Change(idleMs == 0 ? Timeout.Infinite : idleMs, Timeout.Infinite); }
                    try
                    {
                        stop = new Session(baseDir, load, network: true, level: level, token: token, onAuthenticated: Authenticated,
                            onBusy: Busy, onIdle: Idle).Serve(reader, writer);
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                    { /* the client went away, or its time ran out: the next connection gets a fresh session */ }
                }
            }
            finally { listener.Stop(); }
        }
        finally
        {
            RemoveTokenFile();
        }
        return CliExit.Ok;
    }

    /// <summary>R1-07: the default of <c>--idle-seconds</c>, as <c>interpose</c> uses.</summary>
    internal const int DefaultIdleSeconds = 120;

    /// <summary>R1-07: the time a new connection has to send its first (authenticating) request.</summary>
    internal const int FirstRequestSeconds = 10;

    /// <summary>CLI-03: the folder of the <c>rpc --listen</c> token files, <c>GridSim/rpc</c> in the per-user local
    /// application-data folder (<c>%LOCALAPPDATA%</c> on Windows, whose permissions admit only the user; created 0700
    /// elsewhere).</summary>
    internal static string TokenFolder()
    {
        string data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        if (string.IsNullOrEmpty(data)) throw new InvalidOperationException("the per-user application-data folder is not known (no home folder)");
        return Path.Combine(data, "GridSim", "rpc");
    }

    /// <summary>CLI-03: write <paramref name="token"/> to a new file of this process in <see cref="TokenFolder"/>, readable and
    /// writable by this user only (0600 outside Windows, in a 0700 folder), and return its full path.</summary>
    private static string WriteTokenFile(string token)
    {
        string dir = TokenFolder();
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else
        {
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            // R1-06: a folder that already existed keeps the mode it was made with; it is set to 0700 on every start.
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        // R1-06: the token files of hosts that are no longer running (ended by a kill that ran no handler) are removed.
        // R2-04: on a home folder shared by several machines (an HPC cluster) only this machine's files are swept: a
        // process id means nothing on another machine.
        string host = RpcTokenFiles.HostName(Environment.MachineName);
        // R4-06: also the 2.1.156-2.1.158 shape rpc-<pid>.token, which has no machine part (best effort: a process of that
        // id that is not running here is taken as gone).
        foreach (string old in Directory.EnumerateFiles(dir, "rpc-*.token"))
        {
            string fileName = Path.GetFileName(old);
            if ((RpcTokenFiles.Pid(fileName, host) ?? RpcTokenFiles.MidShapePid(fileName, Environment.MachineName) ?? RpcTokenFiles.OldShapePid(fileName)) is not int pid || pid == Environment.ProcessId) continue;
            bool alive;
            try { using Process p = Process.GetProcessById(pid); alive = !p.HasExited; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { alive = false; }
            if (!alive) { try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        // R2-04: the machine, the process and a random part, so two hosts never share a name (the same process id on two
        // machines of a cluster, or a process id reused after a crash).
        string path = Path.Combine(dir, RpcTokenFiles.Name(host, Environment.ProcessId, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))));
        File.Delete(path);   // a stale file of an earlier process with this id: never written through
        var o = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) o.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var f = new FileStream(path, o)) f.Write(Encoding.ASCII.GetBytes(token));
        return path;
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine("error: rpc: " + message);
        return CliExit.Usage;
    }

    /// <summary>host:port, [IPv6]:port or a bare port (host 127.0.0.1); the port 0 to 65535 (0 lets the system choose).</summary>
    internal static bool TryParseListen(string text, out string host, out int port)
    {
        host = "127.0.0.1"; port = 0;
        string portText = text;
        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']');
            if (close < 0 || close + 1 >= text.Length || text[close + 1] != ':') return false;
            host = text[1..close];
            portText = text[(close + 2)..];
        }
        else if (text.Contains(':'))
        {
            int colon = text.LastIndexOf(':');
            if (text.IndexOf(':') != colon) return false;   // several colons without brackets: ambiguous
            host = text[..colon];
            portText = text[(colon + 1)..];
            if (host.Length == 0) return false;
        }
        return int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 0 and <= 65535;
    }

    // ---- one session ----------------------------------------------------------------------------------------------

    /// <param name="network">True for a TCP connection (<c>--listen</c>): the line bound is applied while reading, a line that is not a
    /// JSON object ends the connection, and network or device paths are refused.</param>
    /// <param name="level">The protocol level: 1 (gridsim-rpc/1) or 2 (gridsim-rpc/2, the object model; RpcObjectOps).</param>
    /// <param name="token">Over <c>--listen</c>, the host's token, which every request must carry as <c>"token"</c> (CLI-03).</param>
    /// <param name="onAuthenticated">Over <c>--listen</c>, called once, when the first request that carries the token is read
    /// (R2-03: only a matching token authenticates; any other first frame ends the connection).</param>
    /// <param name="onBusy">Over <c>--listen</c>, called when a whole request line has been read (the idle watch stops).</param>
    /// <param name="onIdle">Over <c>--listen</c>, called when its response has been written (the idle watch starts).</param>
    private sealed partial class Session(string baseDir, Func<string, GridModel> load, bool network = false, int level = 1, string? token = null,
        Action? onAuthenticated = null, Action? onBusy = null, Action? onIdle = null)
    {
        private bool _authenticated;

        private string ProtocolLabel => RpcProtocol.Label(level);
        private int Level => level;
        private string BaseDir => baseDir;
        private bool OnNetwork => network;

        private readonly Dictionary<string, HeldCase> _cases = new(StringComparer.Ordinal);
        private int _nextHandle = 1, _loads, _solves, _verbRuns, _requests;

        /// <summary>RC5 REVIEW-FIX-6: level 2 only, a random token of this host instance (one per session: one per process on
        /// stdio, one per connection over --listen). It is part of every level 2 handle and of the level 2 hello, so a handle
        /// issued by another host, or by this program before a restart, is never taken for one of this host's cases. Level 1
        /// handles stay <c>case-1</c>, <c>case-2</c>, ... byte for byte.</summary>
        private readonly string? _hostToken = level == 2 ? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)) : null;

        /// <summary>The handle of the n-th case loaded in this session.</summary>
        private string HandleFor(int n) => _hostToken is null
            ? "case-" + n.ToString(CultureInfo.InvariantCulture)
            : "case-" + _hostToken + "-" + n.ToString(CultureInfo.InvariantCulture);

        /// <summary>Serve requests until end of input or a shutdown request; true when the server should stop.</summary>
        public bool Serve(TextReader input, TextWriter output)
        {
            while (true)
            {
                bool overLong = false;
                string? line = network ? ReadBoundedLine(input, MaxLineChars, out overLong) : input.ReadLine();
                if (line is null) return false;
                if (!overLong && line.Trim().Length == 0) continue;   // a blank line is not a request
                if (network) onBusy?.Invoke();
                string response = Handle(line, overLong, out bool shutdown, out bool fatal);
                output.WriteLine(response);
                output.Flush();
                if (network && !fatal) onIdle?.Invoke();
                if (shutdown) return true;
                if (network && fatal) return false;   // not this protocol: the connection ends here
            }
        }

        /// <summary>
        /// One line of at most <paramref name="max"/> characters, ended by a line feed (a carriage return before it is dropped), or
        /// null at the end of input. A longer line is not gathered: <paramref name="overLong"/> is set and reading stops at the bound.
        /// </summary>
        private static string? ReadBoundedLine(TextReader input, int max, out bool overLong)
        {
            overLong = false;
            var sb = new StringBuilder();
            while (true)
            {
                int c = input.Read();
                if (c < 0) return sb.Length == 0 ? null : sb.ToString();
                if (c == '\n')
                {
                    if (sb.Length > 0 && sb[^1] == '\r') sb.Length--;
                    return sb.ToString();
                }
                if (sb.Length >= max)
                {
                    overLong = true;
                    return "";
                }
                sb.Append((char)c);
            }
        }

        private string Handle(string line, bool overLong, out bool shutdown, out bool fatal)
        {
            shutdown = false;
            fatal = false;
            _requests++;
            JsonElement? id = null;
            string op = "";
            try
            {
                if (overLong || line.Length > MaxLineChars)
                {
                    fatal = true;
                    throw new RpcError("malformed-request", $"the request line exceeds {MaxLineChars} characters.", CliExit.Usage);
                }
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 64 }); }
                catch (JsonException ex)
                {
                    fatal = true;
                    throw new RpcError("malformed-request", "the line is not valid JSON: " + ex.Message, CliExit.Usage);
                }
                using (doc)
                {
                    JsonElement req = doc.RootElement.Clone();
                    if (req.ValueKind != JsonValueKind.Object)
                    {
                        fatal = true;
                        throw new RpcError("malformed-request", "a request must be a JSON object.", CliExit.Usage);
                    }
                    // CLI-03 / R2-03: over --listen the token is checked first, before anything else in the request (an id of
                    // the wrong type no longer earns an answer without it); a request without it, or with another, gets its
                    // error response and ends the connection. The first request that carries it authenticates the connection.
                    if (network && !TokenMatches(req))
                    {
                        fatal = true;
                        throw new RpcError("unauthorised", "a request over --listen must carry the host's token as \"token\" (read it from the file that \"token_file\" names on the host's first output line).", CliExit.Usage);
                    }
                    if (network && !_authenticated) { _authenticated = true; onAuthenticated?.Invoke(); }
                    if (req.TryGetProperty("id", out JsonElement idv))
                    {
                        if (idv.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null))
                            throw new RpcError("malformed-request", "id must be a string, a number or null.", CliExit.Usage);
                        id = idv;
                    }
                    if (!req.TryGetProperty("op", out JsonElement opv) || opv.ValueKind != JsonValueKind.String)
                        throw new RpcError("malformed-request", "a request needs a string \"op\".", CliExit.Usage);
                    op = opv.GetString()!;
                    return Dispatch(req, id, op, out shutdown);
                }
            }
            catch (RpcError e)
            {
                return Respond(id, op, "error", e.Exit, e.Sha, null, e);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A defect in an operation must not end the session: it is reported as an error response.
                return Respond(id, op, "error", CliExit.InputError, null, null, new RpcError("internal-error", ex.GetType().Name + ": " + ex.Message, CliExit.InputError));
            }
        }

        private string Dispatch(JsonElement req, JsonElement? id, string op, out bool shutdown)
        {
            shutdown = false;
            switch (op)
            {
                case "hello":
                    Allow(req, op);
                    return Respond(id, op, "ok", 0, null, w =>
                    {
                        w.WriteString("protocol", ProtocolLabel);
                        w.WriteString("tool", "gridsim " + GridSim.Core.BuildInfo.InformationalVersion);
                        w.WriteString("build_status", GridSim.Core.Diagnostics.SupplyChain.BuildStatus.Current);   // additive
                        w.WriteStartArray("operations");
                        foreach (string o in OperationsOfLevel) w.WriteStringValue(o);
                        w.WriteEndArray();
                        if (_hostToken is not null) w.WriteString("host", _hostToken);   // level 2 only
                    });
                case "stats":
                    Allow(req, op);
                    return Respond(id, op, "ok", 0, null, w =>
                    {
                        w.WriteNumber("requests", _requests);
                        w.WriteNumber("loads", _loads);
                        w.WriteNumber("solves", _solves);
                        w.WriteNumber("verb_runs", _verbRuns);
                        w.WriteNumber("handles", _cases.Count);
                    });
                case "shutdown":
                    Allow(req, op);
                    shutdown = true;
                    return Respond(id, op, "ok", 0, null, w => w.WriteBoolean("stopping", true));
                case "load": return Load(req, id);
                case "release": return Release(req, id);
                case "solve": return Solve(req, id);
                case "run-verb": return RunVerb(req, id);
                case "line-constants": return LineConstants(req, id);
                case var v2 when level == 2 && Array.IndexOf(RpcProtocol.OperationsV2, v2) >= 0: return ObjectOperation(req, id, v2);
                default:
                    throw new RpcError("unknown-operation", $"unknown operation '{op}'; the operations are: {string.Join(", ", OperationsOfLevel)}.", CliExit.Usage);
            }
        }

        /// <summary>True for a network share, a <c>\\?\</c> or a <c>\\.\</c> device path (either slash), alone or as the value of a
        /// <c>--flag=value</c> argument.</summary>
        /// <remarks>CLI-09: every item of a comma list is tested too (the case lists of bench and transient), so a network path
        /// cannot hide after a comma.</remarks>
        private static bool IsNetworkPath(string arg) => Pieces(arg).Any(GridSim.Core.Security.PathConfinement.IsNetworkOrDevice);

        /// <summary>The values an argument may name a file by: the argument itself (or the value of <c>--flag=value</c>; a bare
        /// flag names none), split at commas, each trimmed.</summary>
        private static IEnumerable<string> Pieces(string arg)
        {
            string v;
            if (arg.StartsWith('-'))
            {
                if (arg.IndexOf('=') is > 0 and int eq) v = arg[(eq + 1)..];
                else yield break;
            }
            else v = arg;
            foreach (string piece in v.Split(','))
                if (piece.Trim() is { Length: > 0 } p) yield return p;
        }

        /// <summary>
        /// CLI-03 / CLI-09: over <c>--listen</c>, the rule of <c>save</c> for every value an argument may name a file by
        /// (<see cref="Pieces"/>; the verb of <c>run-verb</c> and the case of <c>load</c> too): it must resolve inside the host's
        /// working folder, without passing through a link, and must not be a network or device path. A value that is not a
        /// path (a number, a bus id, an event) resolves inside the folder and passes. Null when every value passes, else the
        /// reason.
        /// </summary>
        private static string? ListenPathRefusal(string arg)
        {
            string cwd = Directory.GetCurrentDirectory();
            foreach (string piece in Pieces(arg))
            {
                if (GridSim.Core.Security.PathConfinement.IsNetworkOrDevice(piece)) return $"'{piece}' is a network (UNC) or device path";
                // R1-05: on Windows "v:5" (a probe, an event) has the shape of a drive-relative path. It names a file only when
                // that drive exists: then it is refused (a drive-relative path is outside the folder by construction);
                // otherwise it is not a path.
                if (OperatingSystem.IsWindows() && piece.Length >= 2 && char.IsAsciiLetter(piece[0]) && piece[1] == ':'
                    && (piece.Length == 2 || piece[2] is not ('\\' or '/')))
                {
                    // R2-11: the local drive list, not Directory.Exists (which can make Windows reconnect a network drive).
                    string drive = char.ToUpperInvariant(piece[0]) + ":\\";
                    if (DriveInfo.GetDrives().Any(d => string.Equals(d.Name, drive, StringComparison.OrdinalIgnoreCase))) return $"'{piece}' is a drive-relative path";
                    continue;
                }
                string full;
                try { full = Path.GetFullPath(piece, cwd); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }   // not a path
                if (GridSim.Core.Security.PathConfinement.Refusal(piece, full, cwd) is { } why) return $"'{piece}': {why}";
            }
            return null;
        }

        /// <summary>CLI-03: whether the request's <c>"token"</c> is this host's token (compared in fixed time).</summary>
        private bool TokenMatches(JsonElement req)
        {
            if (token is null || !req.TryGetProperty("token", out JsonElement t) || t.ValueKind != JsonValueKind.String) return false;
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(t.GetString()!), Encoding.UTF8.GetBytes(token));
        }

        private void Allow(JsonElement req, string op, params string[] keys)
        {
            foreach (JsonProperty p in req.EnumerateObject())
                if (p.Name is not ("id" or "op") && !(network && p.Name == "token") && Array.IndexOf(keys, p.Name) < 0)
                    throw new RpcError("bad-argument", $"operation '{op}' does not take '{p.Name}'" + (keys.Length > 0 ? $" (it takes: {string.Join(", ", keys)})." : "."), CliExit.Usage);
        }

        private static string RequireString(JsonElement req, string key)
        {
            if (!req.TryGetProperty(key, out JsonElement v) || v.ValueKind != JsonValueKind.String || v.GetString()!.Length == 0)
                throw new RpcError("bad-argument", $"\"{key}\" is required and must be a non-empty string.", CliExit.Usage);
            return v.GetString()!;
        }

        private HeldCase Held(JsonElement req, out string handle)
        {
            handle = RequireString(req, "handle");
            if (!_cases.TryGetValue(handle, out HeldCase? held))
            {
                if (_hostToken is not null && !handle.StartsWith("case-" + _hostToken + "-", StringComparison.Ordinal))
                    throw new RpcError("unknown-handle", $"no case is held under handle '{handle}': it was not issued by this host (another session, or this program before a restart).", CliExit.Usage);
                throw new RpcError("unknown-handle", $"no case is held under handle '{handle}' (it was never issued or has been released).", CliExit.Usage);
            }
            return held;
        }

        // ---- load / release -------------------------------------------------------------------------------------

        private string Load(JsonElement req, JsonElement? id)
        {
            Allow(req, "load", "case");
            string arg = RequireString(req, "case");
            if (network && IsNetworkPath(arg))
                throw new RpcError("bad-argument", "a network (UNC) or device path is not accepted over --listen; copy the case to a local folder.", CliExit.Usage);
            // CLI-03: over --listen a case file must lie inside the host's working folder (a built-in name passes).
            if (network && ListenPathRefusal(arg) is { } outside)
                throw new RpcError("bad-argument", $"the case is refused over --listen: {outside}; a case must lie inside the host's working folder.", CliExit.Usage);
            GridModel model;
            try { model = load(arg); }
            catch (GateRefusedException ex)
            {
                // The same text the command line prints after "error: ".
                throw new RpcError("licence-refused", ex.Message, CliExit.Usage);
            }
            catch (UnknownCaseException ex) { throw new RpcError("case-load-failed", $"error loading case '{arg}': {ex.Message}", CliExit.Usage); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new RpcError("case-load-failed", $"error loading case '{arg}': {ex.Message}", CliExit.CaseLoadFailure(ex));
            }
            (string sha, string identity) = Identify(arg);
            string handle = HandleFor(_nextHandle++);
            _cases[handle] = new HeldCase(arg, model, sha, identity);
            _loads++;
            return Respond(id, "load", "ok", 0, sha, w =>
            {
                w.WriteString("handle", handle);
                w.WriteString("case", model.Name);
                w.WriteNumber("buses", model.BusCount);
                w.WriteNumber("branches", model.Branches.Count);
                w.WriteString("case_identity", identity);
            });
        }

        private (string Sha, string Identity) Identify(string arg)
        {
            try
            {
                if (Directory.Exists(arg) || (File.Exists(arg) && arg.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                {
                    List<(string Label, string Sha)> files = CaseIdentity.Inputs(arg, baseDir, (label, sha) => (label, sha));
                    if (files.Count > 0)
                    {
                        string manifest = string.Concat(files.Select(f => f.Label + " " + f.Sha + "\n"));
                        return (CaseIdentity.Hash(Encoding.UTF8.GetBytes(manifest)), "files");
                    }
                }
                if (File.Exists(arg)) return (CaseIdentity.Hash(File.ReadAllBytes(arg)), "file");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { /* fall through to the name */ }
            // A built-in case name (case9, uk, ...): identified by the name and the build that defines it.
            return (CaseIdentity.Hash(Encoding.UTF8.GetBytes("built-in:" + arg + ":" + GridSim.Core.BuildInfo.InformationalVersion)), "built-in-name");
        }

        /// <summary>
        /// CLI-01: the licence's bus limit at solve time. <c>load</c> checks the case as loaded; a level 2 edit (<c>add</c>,
        /// <c>delete</c>, <c>set</c>) then changes the network in memory, so a case with any edit is checked again as a user
        /// network (an edited bundled network is no longer the bundled network) before it is solved (<c>solve</c>, the flow
        /// and solved-output attributes of <c>get</c>), studied or saved. A case with no edit passed the gate at load.
        /// </summary>
        private static void AdmitHeld(HeldCase held)
        {
            if (held.Editor is not { Revision: > 0 }) return;
            try { Gated.AdmitEdited(held.Model); }
            catch (GateRefusedException ex) { throw new RpcError("licence-refused", ex.Message, CliExit.Usage, held.Sha); }
        }

        private string Release(JsonElement req, JsonElement? id)
        {
            Allow(req, "release", "handle");
            HeldCase held = Held(req, out string handle);
            _cases.Remove(handle);
            return Respond(id, "release", "ok", 0, held.Sha, w => w.WriteBoolean("released", true));
        }

        // ---- solve ----------------------------------------------------------------------------------------------

        private string Solve(JsonElement req, JsonElement? id)
        {
            Allow(req, "solve", "handle");
            HeldCase held = Held(req, out _);
            AdmitHeld(held);
            PowerFlowResult r;
            try
            {
                // The engine and options the pf verb uses by default (SolverBackend.Solver, 100 iterations, Q limits enforced).
                r = SolverBackend.Select(null).Solver(held.Model).Solve(held.Model, new PowerFlowOptions { MaxIterations = 100, EnforceQLimits = true });
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                throw new RpcError("solve-failed", ex.Message, CliExit.InputError, held.Sha);
            }
            _solves++;
            int exit = r.Converged ? CliExit.Ok : CliExit.Verdict;
            return Respond(id, "solve", "ok", exit, held.Sha, w =>
            {
                w.WriteString("case", held.Model.Name);
                w.WriteBoolean("converged", r.Converged);
                w.WriteNumber("iterations", r.Iterations);
                Num(w, "max_mismatch_pu", r.MaxMismatch);
                Num(w, "loss_mw", r.TotalLossMw);
                Num(w, "loss_mvar", r.TotalLossMvar);
                w.WriteStartArray("bus");
                foreach (int b in r.BusIds) w.WriteNumberValue(b);
                w.WriteEndArray();
                Column(w, "vm_pu", r.Vm);
                Column(w, "va_deg", r.VaDeg);
                Column(w, "p_inj_mw", r.PInjMw);
                Column(w, "q_inj_mvar", r.QInjMvar);
            });
        }

        // ---- run-verb -------------------------------------------------------------------------------------------

        /// <param name="op">The operation answered: run-verb, or (level 2) study.</param>
        /// <param name="hostCase">Level 2 study: the case argument passed first (the held case, or the copy the host wrote, which is taken back out
        /// of the document's args); null for run-verb.</param>
        /// <param name="hostCaseFolder">The folder of <paramref name="hostCase"/>, named neutrally in the printed text.</param>
        /// <param name="sha">The held case's identity for the response (level 2 study); null for run-verb.</param>
        private string RunVerb(JsonElement req, JsonElement? id, string op = "run-verb", string? hostCase = null, string? hostCaseFolder = null, string? sha = null)
        {
            if (op == "run-verb") Allow(req, "run-verb", "verb", "args", "timeout_s");
            string verb = RequireString(req, "verb");
            var args = new List<string>();
            if (req.TryGetProperty("args", out JsonElement list))
            {
                if (list.ValueKind != JsonValueKind.Array) throw new RpcError("bad-argument", "\"args\" must be an array of strings.", CliExit.Usage);
                foreach (JsonElement item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) throw new RpcError("bad-argument", "\"args\" must be an array of strings.", CliExit.Usage);
                    args.Add(item.GetString()!);
                }
            }
            if (network && args.Prepend(verb).Any(IsNetworkPath))
                throw new RpcError("bad-argument", "a network (UNC) or device path is not accepted in \"args\" over --listen; use a local path.", CliExit.Usage);
            // CLI-03: over --listen every file the verb or an argument names must lie inside the host's working folder, as for
            // save (a level 2 study's own case copy is the host's, passed separately, and not checked here).
            if (network)
                foreach (string arg in args.Prepend(verb))
                    if (ListenPathRefusal(arg) is { } outside)
                        throw new RpcError("bad-argument", $"\"args\" refused over --listen: {outside}; every file must lie inside the host's working folder.", CliExit.Usage);
            // RC5 WP12: a network-reachable host never loads code: plug-in models are refused over --listen.
            // The '=' form too, so a later change to the verb's option parsing cannot open the route.
            // R1-09: matched without regard to case, as the replay policy matches it.
            if (network && (args.Any(arg => arg.Equals("--model-plugins", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("--model-plugins=", StringComparison.OrdinalIgnoreCase))
                || (verb.Equals("user-model", StringComparison.OrdinalIgnoreCase) && args.Contains("check-plugin", StringComparer.OrdinalIgnoreCase))))
                throw new RpcError("bad-argument", "model plug-ins (--model-plugins, user-model check-plugin) are not loaded over --listen; a network-reachable host does not load code.", CliExit.Usage);
            double timeoutS = 0;
            if (req.TryGetProperty("timeout_s", out JsonElement tv))
            {
                if (tv.ValueKind != JsonValueKind.Number || !tv.TryGetDouble(out timeoutS) || !(timeoutS > 0) || !double.IsFinite(timeoutS))
                    throw new RpcError("bad-argument", "\"timeout_s\" must be a number above 0.", CliExit.Usage);
            }
            string lowered = verb.ToLowerInvariant();
            if (lowered is "rpc" or "serve")
                throw new RpcError("bad-argument", $"'{verb}' cannot be run through rpc (it is a resident process of its own).", CliExit.Usage);
            if (verb.StartsWith('-') || verb.Length == 0)
                throw new RpcError("bad-argument", "\"verb\" must be a verb name.", CliExit.Usage);
            bool writer = CliOutput.WriterVerbs.Contains(verb);
            foreach (string flag in new[] { "--json", "--csv", "--out" })
                if (writer && args.Contains(flag) && !(flag == "--out" && CliOutput.VerbsWithoutOut.Contains(verb) && !verb.Equals("convert", StringComparison.OrdinalIgnoreCase)))
                    throw new RpcError("bad-argument", $"{flag} is the endpoint's to give: it writes the result to a temporary file and returns the document.", CliExit.Usage);

            string work = Path.Combine(Path.GetTempPath(), "gridsim-rpc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            string resultFile = Path.Combine(work, "result.json");
            try
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath ?? throw new RpcError("host-failed", "the host process path is unknown.", CliExit.InputError))
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                if (string.Equals(Path.GetFileNameWithoutExtension(psi.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
                    psi.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
                psi.ArgumentList.Add(verb);
                if (hostCase is not null) psi.ArgumentList.Add(hostCase);
                foreach (string s in args) psi.ArgumentList.Add(s);
                if (writer) { psi.ArgumentList.Add("--json"); psi.ArgumentList.Add(resultFile); }

                using Process p = Process.Start(psi) ?? throw new RpcError("host-failed", "could not start the verb process.", CliExit.InputError);
                p.StandardInput.Close();
                Task<string> stdout = p.StandardOutput.ReadToEndAsync();
                Task<string> stderr = p.StandardError.ReadToEndAsync();
                if (timeoutS > 0 ? !p.WaitForExit(TimeSpan.FromSeconds(timeoutS)) : !p.WaitForExit(Timeout.Infinite))
                {
                    p.Kill(entireProcessTree: true);
                    throw new RpcError("timeout", $"gridsim {verb} did not finish within {timeoutS.ToString("R", CultureInfo.InvariantCulture)} s.", CliExit.InputError);
                }
                p.WaitForExit();
                _verbRuns++;
                string outText = stdout.Result;
                // The temporary result path is the only run-specific text a deterministic verb prints; it is named neutrally.
                string errText = stderr.Result.Replace(resultFile, "<result>", StringComparison.Ordinal);
                if (hostCaseFolder is not null)
                {
                    outText = outText.Replace(hostCaseFolder, "<case>", StringComparison.Ordinal);
                    errText = errText.Replace(hostCaseFolder, "<case>", StringComparison.Ordinal);
                }
                int code = p.ExitCode;
                string? documentText = writer && File.Exists(resultFile) ? File.ReadAllText(resultFile, Encoding.UTF8) : null;
                if (documentText is null && code != 0)
                {
                    string last = errText.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
                    throw new RpcError("verb-failed", $"gridsim {verb} wrote no result (exit {code}): {last}", code, sha) { Stdout = outText, Stderr = errText };
                }
                return Respond(id, op, "ok", code, sha, w =>
                {
                    w.WritePropertyName("document");
                    if (documentText is null) w.WriteNullValue();
                    else WriteDocument(w, documentText, resultFile, hostCaseFolder is null ? null : hostCase);
                    w.WriteString("stdout", outText);
                    w.WriteString("stderr", errText);
                });
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>
        /// The verb's own result document, as written, compacted onto one line (numbers keep their exact text). The one
        /// change: the closing <c>--json &lt;temporary path&gt;</c> pair, which the endpoint added, is taken out of "args" so
        /// the document does not depend on where the temporary file was.
        /// </summary>
        private static void WriteDocument(Utf8JsonWriter w, string text, string resultFile, string? hostCase = null)
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            w.WriteStartObject();
            foreach (JsonProperty p in root.EnumerateObject())
            {
                if (p.Name != "args" || p.Value.ValueKind != JsonValueKind.Array) { p.WriteTo(w); continue; }
                var items = p.Value.EnumerateArray().ToList();
                int keep = items.Count;
                if (keep >= 2 && items[keep - 2].ValueKind == JsonValueKind.String && items[keep - 2].GetString() == "--json"
                    && items[keep - 1].ValueKind == JsonValueKind.String && items[keep - 1].GetString() == resultFile) keep -= 2;
                // A level 2 study of an edited case: the case the host wrote (the argument after the verb) is not echoed either.
                int skip = hostCase is not null && keep >= 2 && items[1].ValueKind == JsonValueKind.String && items[1].GetString() == hostCase ? 1 : -1;
                w.WriteStartArray("args");
                for (int i = 0; i < keep; i++) if (i != skip) items[i].WriteTo(w);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }

        // ---- line-constants -------------------------------------------------------------------------------------

        private string LineConstants(JsonElement req, JsonElement? id)
        {
            Allow(req, "line-constants", "geometry", "per_unit");
            if (!req.TryGetProperty("geometry", out JsonElement geometry)) throw new RpcError("bad-argument", "\"geometry\" is required (a gridsim-lineconstants/1 object).", CliExit.Usage);
            LineGeometry g;
            try { g = LineConstantsCommand.Parse(geometry); }
            catch (LineConstantsCommand.InputException ex) { throw new RpcError("bad-argument", ex.Message, CliExit.Usage); }
            LineConstantsCommand.PerUnitBase? pu = null;
            if (req.TryGetProperty("per_unit", out JsonElement p))
            {
                double[] v = new double[3];
                string[] keys = ["length_km", "base_kv", "base_mva"];
                if (p.ValueKind != JsonValueKind.Object || p.EnumerateObject().Count() != 3) throw new RpcError("bad-argument", "\"per_unit\" must be an object with length_km, base_kv and base_mva.", CliExit.Usage);
                for (int i = 0; i < 3; i++)
                    if (!p.TryGetProperty(keys[i], out JsonElement e) || e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out v[i]) || !(v[i] > 0) || !double.IsFinite(v[i]))
                        throw new RpcError("bad-argument", $"per_unit.{keys[i]} must be a finite number above 0.", CliExit.Usage);
                pu = new LineConstantsCommand.PerUnitBase(v[0], v[1], v[2]);
            }
            LineConstantsResult r;
            try { r = LineConstantsSolver.Solve(g); }
            catch (ArgumentException ex) { throw new RpcError("bad-geometry", CliExit.UserMessage(ex), CliExit.InputError); }
            int exit = pu is not null && r.Sequence is null ? CliExit.Verdict : CliExit.Ok;
            var rep = new CliResult("line-constants");
            LineConstantsCommand.Fill(rep, g, r, pu);
            string document = CliResultWriter.JsonText(rep, "gridsim " + GridSim.Core.BuildInfo.InformationalVersion, ["line-constants", "<rpc>"], exit);
            return Respond(id, "line-constants", "ok", exit, null, w =>
            {
                w.WritePropertyName("document");
                using JsonDocument d = JsonDocument.Parse(document);
                d.RootElement.WriteTo(w);
            });
        }

        // ---- response writing -----------------------------------------------------------------------------------

        private string Respond(JsonElement? id, string op, string status, int exit, string? sha, Action<Utf8JsonWriter>? result, RpcError? error = null)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, WriterOptions))
            {
                w.WriteStartObject();
                w.WriteString("protocol", ProtocolLabel);
                w.WritePropertyName("id");
                if (id is JsonElement e) e.WriteTo(w); else w.WriteNullValue();
                w.WriteString("op", op);
                w.WriteString("status", status);
                w.WriteNumber("exitCode", exit);
                if (sha is null) w.WriteNull("case_sha256"); else w.WriteString("case_sha256", sha);
                if (error is null)
                {
                    w.WriteStartObject("result");
                    result?.Invoke(w);
                    w.WriteEndObject();
                }
                else
                {
                    w.WriteStartObject("error");
                    w.WriteString("kind", error.Kind);
                    w.WriteString("message", error.Message);
                    if (error.Stdout is not null) w.WriteString("stdout", error.Stdout);
                    if (error.Stderr is not null) w.WriteString("stderr", error.Stderr);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static void Num(Utf8JsonWriter w, string name, double d)
        {
            w.WritePropertyName(name);
            NumValue(w, d);
        }

        private static void NumValue(Utf8JsonWriter w, double d)
        {
            // The result writer's spelling: round-trip digits, and "NaN" / "Infinity" / "-Infinity" as strings.
            if (double.IsFinite(d)) w.WriteRawValue(CliResultWriter.Number(d));
            else w.WriteStringValue(CliResultWriter.Number(d));
        }

        private static void Column(Utf8JsonWriter w, string name, double[] values)
        {
            w.WriteStartArray(name);
            foreach (double d in values) NumValue(w, d);
            w.WriteEndArray();
        }
    }
}
