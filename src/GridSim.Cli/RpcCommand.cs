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
/// only on a loopback address unless <c>--allow-non-loopback</c> is given (there is no authentication, so beyond loopback
/// is an explicit choice).
///
/// Nothing here duplicates a study engine. <c>solve</c> runs the solver the <c>pf</c> verb runs on a case loaded once and
/// held by handle; <c>line-constants</c> runs the R4-04 Core solver; <c>run-verb</c> runs ANY existing verb exactly as the
/// command line would, as a child process of this host, so every legacy output stays the CLI's own. The licence gate is the
/// one the rest of the CLI resolved at start-up (<see cref="Gated"/>), so a refused load is the CLI's refusal.
///
/// Determinism: a response is a pure function of the request and the session's earlier requests. Responses carry no
/// timestamps, process ids or temporary paths; handles are numbered per session; numbers are written in round-trip form.
/// </summary>
internal static class RpcCommand
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
        public GridModel Model { get; } = model;
        public string Sha { get; } = sha;
        public string Identity { get; } = identity;
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
        for (int i = 1; i < a.Count; i++)
        {
            switch (a[i])
            {
                case "--listen":
                    if (i + 1 >= a.Count || a[i + 1].StartsWith("--", StringComparison.Ordinal)) return Usage("--listen expected host:port but none was given");
                    if (listen is not null) return Usage("--listen was given twice");
                    listen = a[++i];
                    break;
                case "--allow-non-loopback":
                    allowRemote = true;
                    break;
                default:
                    return Usage($"rpc does not use {a[i]}. rpc accepts: --listen, --allow-non-loopback.");
            }
        }
        if (allowRemote && listen is null) return Usage("--allow-non-loopback only applies with --listen");

        // Anything an engine prints to the console must not corrupt the protocol stream: stdout belongs to the protocol,
        // so the console writer is pointed at stderr for the whole session.
        TextWriter protocolOut = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        Console.SetOut(Console.Error);

        if (listen is null)
        {
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            new Session(baseDir, load).Serve(input, protocolOut);
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
                Console.Error.WriteLine($"error: rpc: refusing to listen on {host}: it is not a loopback address, and the endpoint has no authentication. " +
                    "Use 127.0.0.1 or ::1, or add --allow-non-loopback to accept the risk.");
                return CliExit.Usage;
            }
            Console.Error.WriteLine($"warning: rpc: listening on {host}, beyond loopback, with no authentication (--allow-non-loopback).");
        }

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
            protocolOut.WriteLine("{\"protocol\":\"" + Protocol + "\",\"listening\":\"" + shown + "\"}");
            bool stop = false;
            while (!stop)
            {
                using TcpClient client = listener.AcceptTcpClient();
                using NetworkStream stream = client.GetStream();
                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
                try { stop = new Session(baseDir, load).Serve(reader, writer); }
                catch (IOException) { /* the client went away: the next connection gets a fresh session */ }
            }
        }
        finally { listener.Stop(); }
        return CliExit.Ok;
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

    private sealed class Session(string baseDir, Func<string, GridModel> load)
    {
        private readonly Dictionary<string, HeldCase> _cases = new(StringComparer.Ordinal);
        private int _nextHandle = 1, _loads, _solves, _verbRuns, _requests;

        /// <summary>Serve requests until end of input or a shutdown request; true when the server should stop.</summary>
        public bool Serve(TextReader input, TextWriter output)
        {
            string? line;
            while ((line = input.ReadLine()) is not null)
            {
                if (line.Trim().Length == 0) continue;   // a blank line is not a request
                string response = Handle(line, out bool shutdown);
                output.WriteLine(response);
                output.Flush();
                if (shutdown) return true;
            }
            return false;
        }

        private string Handle(string line, out bool shutdown)
        {
            shutdown = false;
            _requests++;
            JsonElement? id = null;
            string op = "";
            try
            {
                if (line.Length > MaxLineChars) throw new RpcError("malformed-request", $"the request line exceeds {MaxLineChars} characters.", CliExit.Usage);
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 64 }); }
                catch (JsonException ex) { throw new RpcError("malformed-request", "the line is not valid JSON: " + ex.Message, CliExit.Usage); }
                using (doc)
                {
                    JsonElement req = doc.RootElement.Clone();
                    if (req.ValueKind != JsonValueKind.Object) throw new RpcError("malformed-request", "a request must be a JSON object.", CliExit.Usage);
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
                        w.WriteString("protocol", Protocol);
                        w.WriteString("tool", "gridsim " + GridSim.Core.BuildInfo.InformationalVersion);
                        w.WriteStartArray("operations");
                        foreach (string o in Operations) w.WriteStringValue(o);
                        w.WriteEndArray();
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
                default:
                    throw new RpcError("unknown-operation", $"unknown operation '{op}'; the operations are: {string.Join(", ", Operations)}.", CliExit.Usage);
            }
        }

        private static void Allow(JsonElement req, string op, params string[] keys)
        {
            foreach (JsonProperty p in req.EnumerateObject())
                if (p.Name is not ("id" or "op") && Array.IndexOf(keys, p.Name) < 0)
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
                throw new RpcError("unknown-handle", $"no case is held under handle '{handle}' (it was never issued or has been released).", CliExit.Usage);
            return held;
        }

        // ---- load / release -------------------------------------------------------------------------------------

        private string Load(JsonElement req, JsonElement? id)
        {
            Allow(req, "load", "case");
            string arg = RequireString(req, "case");
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
            string handle = "case-" + (_nextHandle++).ToString(CultureInfo.InvariantCulture);
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

        private string RunVerb(JsonElement req, JsonElement? id)
        {
            Allow(req, "run-verb", "verb", "args", "timeout_s");
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
                int code = p.ExitCode;
                string? documentText = writer && File.Exists(resultFile) ? File.ReadAllText(resultFile, Encoding.UTF8) : null;
                if (documentText is null && code != 0)
                {
                    string last = errText.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
                    throw new RpcError("verb-failed", $"gridsim {verb} wrote no result (exit {code}): {last}", code) { Stdout = outText, Stderr = errText };
                }
                return Respond(id, "run-verb", "ok", code, null, w =>
                {
                    w.WritePropertyName("document");
                    if (documentText is null) w.WriteNullValue();
                    else WriteDocument(w, documentText, resultFile);
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
        private static void WriteDocument(Utf8JsonWriter w, string text, string resultFile)
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
                w.WriteStartArray("args");
                for (int i = 0; i < keep; i++) items[i].WriteTo(w);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }

        // ---- line-constants -------------------------------------------------------------------------------------

        private static string LineConstants(JsonElement req, JsonElement? id)
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

        private static string Respond(JsonElement? id, string op, string status, int exit, string? sha, Action<Utf8JsonWriter>? result, RpcError? error = null)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, WriterOptions))
            {
                w.WriteStartObject();
                w.WriteString("protocol", Protocol);
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
