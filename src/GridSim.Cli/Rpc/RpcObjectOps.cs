using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GridSim.Core.Api;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Cli;

/// <summary>
/// RC5 WP11: the object model operations of <c>gridsim rpc --protocol 2</c> (<c>gridsim-rpc/2</c>), over the Core
/// <see cref="CaseEditor"/>. None of them exists at level 1, and none changes how a level 1 operation answers.
/// <list type="bullet">
/// <item><c>elements</c> {handle, kind}: every element of the kind, in model order, with its key and every attribute.</item>
/// <item><c>get</c> {handle, kind, key, attributes?}: the attributes asked for (all, when none is named). On a branch, the
/// flow attributes (<see cref="RpcProtocol.FlowAttributes"/>) read the pf solve of the case as it stands.</item>
/// <item><c>set</c> {handle, kind, key, values}: write attributes (numbers); returns the new revision. A refused write
/// changes nothing.</item>
/// <item><c>edits</c> {handle}: the edit log.</item>
/// <item>RC5 WP15: <c>add</c> {handle, kind, key, values} and <c>delete</c> {handle, kind, key, cascade?}: add or delete an
/// element (<see cref="CaseEditor.Add"/>, <see cref="CaseEditor.Delete"/>); returns the new revision. Listed by hello last
/// (<see cref="RpcProtocol.OperationsV2"/>).</item>
/// <item><c>study</c> {handle, verb, args, timeout_s?}: run one of <see cref="RpcProtocol.StudyVerbs"/> on the held case as
/// <c>run-verb</c> runs a verb. With no edit the host passes the case argument it loaded, so the document is the CLI's
/// own; with edits it writes the model as a JSON v2 case to a temporary folder, reads it back and writes it again. It
/// runs the verb on the first copy only when (a) every object model attribute of every element of the re-read model equals
/// the held model's, doubles bit for bit, and (b) the two writes are byte-identical (write-read-write is idempotent);
/// otherwise the study is refused naming what differs. (a) covers every value an edit can change; (b) shows the writer and
/// reader agree with each other. Neither proves that a field outside the object model survives the JSON v2 writer.</item>
/// <item><c>save</c> {handle, path, format}: write the case as PSS/E RAW 33, 34 or 35 or a JSON v2 case, never over a
/// file with different content. Over <c>--listen</c> every file written (for v2, the manifest and its sibling
/// <c>.network.json</c>) must lie inside the host's working folder (<see cref="GridSim.Scripting.PathConfinement"/>) and
/// must not be a link, and network and device paths are refused. A file is only ever created new (an existing name, a
/// dangling link included, is never written through).</item>
/// </list>
/// Responses carry no time, process id or temporary path.
/// </summary>
internal static partial class RpcCommand
{
    private sealed partial class Session
    {
        private IEnumerable<string> OperationsOfLevel => Level == 2 ? Operations.Concat(RpcProtocol.OperationsV2) : Operations;

        private string ObjectOperation(JsonElement req, JsonElement? id, string op) => op switch
        {
            "elements" => ElementsOp(req, id),
            "get" => GetOp(req, id),
            "set" => SetOp(req, id),
            "edits" => EditsOp(req, id),
            "study" => StudyOp(req, id),
            "add" => AddOp(req, id),
            "delete" => DeleteOp(req, id),
            _ => SaveOp(req, id),
        };

        // ---- the editor of a held case ------------------------------------------------------------------------------

        private static CaseEditor EditorOf(HeldCase held)
        {
            if (held.Editor is not null) return held.Editor;
            IReadOnlyList<ElementKeys.MachineId>? machines = null;
            IReadOnlyList<ElementKeys.BranchId>? branches = null;
            // A PSS/E case keeps its native identities (bus and machine id; from, to and circuit): read from the same file.
            if ((held.Arg.EndsWith(".raw", StringComparison.OrdinalIgnoreCase) || held.Arg.EndsWith(".rawx", StringComparison.OrdinalIgnoreCase)) && File.Exists(held.Arg))
            {
                try
                {
                    PsseParseResult raw = PsseRawParser.ParseFile(held.Arg, new PsseReadOptions());
                    if (raw.MachineKeys.Count == held.Model.AllGenerators.Count) machines = raw.MachineKeys.Select(k => new ElementKeys.MachineId(k.Bus, k.Id)).ToList();
                    if (raw.BranchKeys.Count == held.Model.Branches.Count)
                        branches = raw.BranchKeys.Select(k => new ElementKeys.BranchId(k.FromBus, k.ToBus, k.ThirdBus, k.Circuit, k.Winding)).ToList();
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException or ArgumentException) { /* the model's own names */ }
            }
            try { held.Editor = new CaseEditor(held.Model, machines, branches); }
            catch (InvalidDataException ex) { throw new RpcError("refused", ex.Message, CliExit.InputError, held.Sha); }
            return held.Editor;
        }

        private static ElementKind KindOf(JsonElement req)
        {
            string k = RequireString(req, "kind");
            if (!ElementKinds.TryParse(k, out ElementKind kind))
                throw new RpcError("bad-argument", $"unknown kind '{k}'; the kinds are: {ElementKinds.List}.", CliExit.Usage);
            return kind;
        }

        private static void Value(Utf8JsonWriter w, object? v)
        {
            switch (v)
            {
                case null: w.WriteNullValue(); break;
                case string s: w.WriteStringValue(s); break;
                case double d: NumValue(w, d); break;
                default: w.WriteStringValue(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            }
        }

        // ---- elements / get / set / edits ---------------------------------------------------------------------------

        private string ElementsOp(JsonElement req, JsonElement? id)
        {
            Allow(req, "elements", "handle", "kind");
            HeldCase held = Held(req, out _);
            ElementKind kind = KindOf(req);
            CaseEditor ed = EditorOf(held);
            IReadOnlyList<ElementKey> keys = ed.Elements(kind);
            return Respond(id, "elements", "ok", 0, held.Sha, w =>
            {
                w.WriteString("kind", ElementKinds.Name(kind));
                w.WriteNumber("revision", ed.Revision);
                w.WriteStartArray("attributes");
                foreach (ElementField a in ElementAttributes.Of(kind)) w.WriteStringValue(a.Name);
                w.WriteEndArray();
                w.WriteStartArray("rows");
                foreach (ElementKey k in keys)
                {
                    w.WriteStartObject();
                    w.WriteString("key", k.Key);
                    foreach (KeyValuePair<string, object?> kv in ed.GetAll(kind, k.Key))
                    {
                        w.WritePropertyName(kv.Key);
                        Value(w, kv.Value);
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
        }

        private string GetOp(JsonElement req, JsonElement? id)
        {
            Allow(req, "get", "handle", "kind", "key", "attributes");
            HeldCase held = Held(req, out _);
            ElementKind kind = KindOf(req);
            string key = RequireString(req, "key");
            CaseEditor ed = EditorOf(held);
            var names = new List<string>();
            if (req.TryGetProperty("attributes", out JsonElement list))
            {
                if (list.ValueKind != JsonValueKind.Array) throw new RpcError("bad-argument", "\"attributes\" must be an array of strings.", CliExit.Usage);
                foreach (JsonElement e in list.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.String) throw new RpcError("bad-argument", "\"attributes\" must be an array of strings.", CliExit.Usage);
                    names.Add(e.GetString()!);
                }
            }
            if (names.Count == 0) names.AddRange(ElementAttributes.Of(kind).Select(a => a.Name));
            var values = new List<KeyValuePair<string, object?>>();
            try
            {
                ElementKey k = ed.Find(kind, key);
                foreach (string n in names)
                {
                    if (kind == ElementKind.Branch && Array.IndexOf(RpcProtocol.FlowAttributes, n) >= 0)
                    {
                        LineFlow f = FlowsOf(held, ed).LineFlows[k.Index];
                        double v = n switch
                        {
                            "p_from_mw" => f.PFromMw,
                            "q_from_mvar" => f.QFromMvar,
                            "p_to_mw" => f.PToMw,
                            "q_to_mvar" => f.QToMvar,
                            _ => f.LoadingPercent,
                        };
                        values.Add(new KeyValuePair<string, object?>(n, v));
                        continue;
                    }
                    if (kind == ElementKind.Generator && Array.IndexOf(RpcProtocol.GeneratorSolvedAttributes, n) >= 0)
                    {
                        values.Add(new KeyValuePair<string, object?>(n, SolvedUnit(held, ed, k.Index, n)));
                        continue;
                    }
                    if (ElementAttributes.Find(kind, n) is null)
                        throw new CaseEditException($"a {ElementKinds.Name(kind)} has no attribute '{n}'; its attributes are: {ElementAttributes.List(kind)}"
                            + (kind == ElementKind.Branch ? $" (and, read from the solved case: {string.Join(", ", RpcProtocol.FlowAttributes)})."
                                : kind == ElementKind.Generator ? $" (and, read from the solved case: {string.Join(", ", RpcProtocol.GeneratorSolvedAttributes)})." : "."));
                    values.Add(new KeyValuePair<string, object?>(n, ed.Get(kind, key, n)));
                }
            }
            catch (CaseEditException ex) { throw new RpcError("refused", ex.Message, CliExit.Usage, held.Sha); }
            return Respond(id, "get", "ok", 0, held.Sha, w =>
            {
                w.WriteString("kind", ElementKinds.Name(kind));
                w.WriteString("key", key);
                w.WriteNumber("revision", ed.Revision);
                w.WriteStartObject("values");
                foreach (KeyValuePair<string, object?> kv in values)
                {
                    w.WritePropertyName(kv.Key);
                    Value(w, kv.Value);
                }
                w.WriteEndObject();
            });
        }

        /// <summary>The pf solve (the solver and options of the level 1 <c>solve</c>) of the held case at its current
        /// revision, kept until the next write.</summary>
        private static PowerFlowResult FlowsOf(HeldCase held, CaseEditor ed)
        {
            if (held.Flows is { } f && f.Revision == ed.Revision) return f.Result;
            AdmitHeld(held);
            PowerFlowResult r;
            try { r = SolverBackend.Select(null).Solver(held.Model).Solve(held.Model, new PowerFlowOptions { MaxIterations = 100, EnforceQLimits = true }); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { throw new RpcError("solve-failed", ex.Message, CliExit.InputError, held.Sha); }
            if (!r.Converged)
                throw new RpcError("not-solved", "the case as it stands does not converge, so it has no branch flows.", CliExit.Verdict, held.Sha);
            held.Flows = (ed.Revision, r);
            return r;
        }

        /// <summary>RC5 REVIEW-FIX-6: the solved output of the generator at <paramref name="index"/> of the whole fleet
        /// (<see cref="GridModel.AllGenerators"/>), from the pf solve of the case as it stands, by
        /// <see cref="SolvedGeneration.UnitOutput"/> (the bus net injection plus the bus load at the solved voltage; a lone
        /// unit takes the whole bus, several units share it by that method's rule). An out-of-service unit is refused.</summary>
        private static double SolvedUnit(HeldCase held, CaseEditor ed, int index, string attribute)
        {
            PowerFlowResult r = FlowsOf(held, ed);
            IReadOnlyList<Generator> all = held.Model.AllGenerators;
            if (!all[index].InService)
                throw new CaseEditException($"generator {index + 1} of the fleet is out of service, so it has no solved output.");
            int inService = 0;
            for (int i = 0; i < index; i++) if (all[i].InService) inService++;
            (double p, double q) = SolvedGeneration.UnitOutput(held.Model, r, inService);
            return attribute == "p_solved_mw" ? p : q;
        }

        private string SetOp(JsonElement req, JsonElement? id)
        {
            Allow(req, "set", "handle", "kind", "key", "values");
            HeldCase held = Held(req, out _);
            ElementKind kind = KindOf(req);
            string key = RequireString(req, "key");
            if (!req.TryGetProperty("values", out JsonElement vals) || vals.ValueKind != JsonValueKind.Object)
                throw new RpcError("bad-argument", "\"values\" is required: an object of attribute names and numbers.", CliExit.Usage);
            var values = new List<KeyValuePair<string, double>>();
            foreach (JsonProperty p in vals.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetDouble(out double d) || !double.IsFinite(d))
                    throw new RpcError("bad-argument", $"values.{p.Name} must be a finite number.", CliExit.Usage, held.Sha);
                values.Add(new KeyValuePair<string, double>(p.Name, d));
            }
            CaseEditor ed = EditorOf(held);
            int revision;
            try { revision = ed.Set(kind, key, values); }
            catch (CaseEditException ex) { throw new RpcError("refused", ex.Message, CliExit.Usage, held.Sha); }
            held.Model = ed.Model;
            held.Flows = null;
            return Respond(id, "set", "ok", 0, held.Sha, w =>
            {
                w.WriteString("kind", ElementKinds.Name(kind));
                w.WriteString("key", key);
                w.WriteNumber("revision", revision);
            });
        }

        // ---- add / delete (RC5 WP15) -------------------------------------------------------------------------------------
        // Both change only the held model in memory (no file is read or written), under the handle's host token check, as set
        // does; a refused one changes nothing. The reply is set's: kind, key and the new revision.

        private string AddOp(JsonElement req, JsonElement? id)
        {
            Allow(req, "add", "handle", "kind", "key", "values");
            HeldCase held = Held(req, out _);
            ElementKind kind = KindOf(req);
            string key = RequireString(req, "key");
            var values = new List<KeyValuePair<string, double>>();
            if (req.TryGetProperty("values", out JsonElement vals))
            {
                if (vals.ValueKind != JsonValueKind.Object)
                    throw new RpcError("bad-argument", "\"values\" must be an object of attribute names and numbers.", CliExit.Usage, held.Sha);
                foreach (JsonProperty p in vals.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetDouble(out double d) || !double.IsFinite(d))
                        throw new RpcError("bad-argument", $"values.{p.Name} must be a finite number.", CliExit.Usage, held.Sha);
                    values.Add(new KeyValuePair<string, double>(p.Name, d));
                }
            }
            CaseEditor ed = EditorOf(held);
            int revision;
            try { revision = ed.Add(kind, key, values); }
            catch (CaseEditException ex) { throw new RpcError("refused", ex.Message, CliExit.Usage, held.Sha); }
            held.Model = ed.Model;
            held.Flows = null;
            return Respond(id, "add", "ok", 0, held.Sha, w =>
            {
                w.WriteString("kind", ElementKinds.Name(kind));
                w.WriteString("key", key);
                w.WriteNumber("revision", revision);
            });
        }

        private string DeleteOp(JsonElement req, JsonElement? id)
        {
            Allow(req, "delete", "handle", "kind", "key", "cascade");
            HeldCase held = Held(req, out _);
            ElementKind kind = KindOf(req);
            string key = RequireString(req, "key");
            bool cascade = false;
            if (req.TryGetProperty("cascade", out JsonElement c))
            {
                if (c.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new RpcError("bad-argument", "\"cascade\" must be true or false.", CliExit.Usage, held.Sha);
                cascade = c.GetBoolean();
            }
            CaseEditor ed = EditorOf(held);
            int revision;
            try { revision = ed.Delete(kind, key, cascade); }
            catch (CaseEditException ex) { throw new RpcError("refused", ex.Message, CliExit.Usage, held.Sha); }
            held.Model = ed.Model;
            held.Flows = null;
            return Respond(id, "delete", "ok", 0, held.Sha, w =>
            {
                w.WriteString("kind", ElementKinds.Name(kind));
                w.WriteString("key", key);
                w.WriteNumber("revision", revision);
            });
        }

        private string EditsOp(JsonElement req, JsonElement? id)
        {
            Allow(req, "edits", "handle");
            HeldCase held = Held(req, out _);
            CaseEditor ed = EditorOf(held);
            return Respond(id, "edits", "ok", 0, held.Sha, w =>
            {
                w.WriteNumber("revision", ed.Revision);
                w.WriteStartArray("edits");
                foreach (CaseEdit e in ed.Edits)
                {
                    w.WriteStartObject();
                    w.WriteNumber("revision", e.Revision);
                    w.WriteString("kind", ElementKinds.Name(e.Kind));
                    w.WriteString("key", e.Key);
                    w.WriteString("attribute", e.Attribute);
                    w.WritePropertyName("before");
                    Value(w, e.Before);
                    w.WritePropertyName("after");
                    NumValue(w, e.After);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
        }

        // ---- study ----------------------------------------------------------------------------------------------------

        private string StudyOp(JsonElement req, JsonElement? id)
        {
            Allow(req, "study", "handle", "verb", "args", "timeout_s");
            HeldCase held = Held(req, out _);
            string verb = RequireString(req, "verb");
            if (Array.IndexOf(RpcProtocol.StudyVerbs, verb.ToLowerInvariant()) < 0)
                throw new RpcError("bad-argument", $"study runs {string.Join(", ", RpcProtocol.StudyVerbs)}; '{verb}' is not one of them (run other verbs with run-verb on a saved case).", CliExit.Usage, held.Sha);
            CaseEditor ed = EditorOf(held);
            if (ed.Revision == 0)
                return RunVerb(req, id, "study", hostCase: held.Arg, hostCaseFolder: null, sha: held.Sha);
            AdmitHeld(held);

            string work = Path.Combine(Path.GetTempPath(), "gridsim-rpc-case-" + Guid.NewGuid().ToString("N"));
            try
            {
                string first = Path.Combine(work, "a"), second = Path.Combine(work, "b");
                string name = "case.json";
                string schemas = Gated.FindV2Schemas(BaseDir)
                    ?? throw new RpcError("host-failed", "the v2 schemas (schemas/v2) were not found beside the program or above it, so an edited case cannot be written for a study.", CliExit.InputError, held.Sha);
                try
                {
                    V2CaseWriter.Write(held.Model, Path.Combine(first, name));
                    GridModel back = V2CaseLoader.LoadWithNotes(Path.Combine(first, name), schemas, Path.Combine(BaseDir, "data")).Model;
                    if (CaseEditor.AttributeDifference(held.Model, back) is { } what)
                        throw new RpcError("refused", $"the edited case does not survive a JSON v2 write and read unchanged ({what}), so a study on it would not be a study of this model; save it and run the verb on the saved case instead.", CliExit.InputError, held.Sha);
                    V2CaseWriter.Write(back, Path.Combine(second, name));
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or ArgumentException or InvalidOperationException)
                {
                    throw new RpcError("refused", "the edited case cannot be written as a JSON v2 case for the study: " + CliExit.UserMessage(ex), CliExit.InputError, held.Sha);
                }
                foreach (string file in new[] { name, V2CaseWriter.NetworkFileNameFor(name) })
                {
                    byte[] a = File.ReadAllBytes(Path.Combine(first, file)), b = File.ReadAllBytes(Path.Combine(second, file));
                    if (!a.AsSpan().SequenceEqual(b))
                        throw new RpcError("refused", $"the edited case's JSON v2 write-read-write is not idempotent ({file} differs), so a study on it would not be a study of this model; save it and run the verb on the saved case instead.", CliExit.InputError, held.Sha);
                }
                return RunVerb(req, id, "study", hostCase: Path.Combine(first, name), hostCaseFolder: first, sha: held.Sha);
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        // ---- save -----------------------------------------------------------------------------------------------------

        private string SaveOp(JsonElement req, JsonElement? id)
        {
            Allow(req, "save", "handle", "path", "format");
            HeldCase held = Held(req, out _);
            string path = RequireString(req, "path");
            string format = RequireString(req, "format");
            if (Array.IndexOf(RpcProtocol.SaveFormats, format) < 0)
                throw new RpcError("bad-argument", $"format is one of {string.Join(", ", RpcProtocol.SaveFormats)}; got '{format}'.", CliExit.Usage, held.Sha);
            AdmitHeld(held);
            string cwd = Directory.GetCurrentDirectory();
            if (OnNetwork && IsNetworkPath(path))
                throw new RpcError("bad-argument", "a network (UNC) or device path is not accepted over --listen; use a path inside the host's working folder.", CliExit.Usage, held.Sha);
            string full = Path.GetFullPath(path, cwd);
            if (OnNetwork && GridSim.Scripting.PathConfinement.Refusal(path, full, cwd) is { } why)
                throw new RpcError("bad-argument", $"save path \"{path}\" is refused over --listen: {why}.", CliExit.Usage, held.Sha);

            var files = new List<(string Name, byte[] Bytes)>();
            IReadOnlyList<string> notes;
            try
            {
                if (format == "v2")
                {
                    string tmp = Path.Combine(Path.GetTempPath(), "gridsim-rpc-save-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        string manifestName = Path.GetFileName(full);
                        V2CaseWriteResult wr = V2CaseWriter.Write(held.Model, Path.Combine(tmp, manifestName));
                        notes = wr.Notes;
                        files.Add((V2CaseWriter.NetworkFileNameFor(manifestName), File.ReadAllBytes(wr.NetworkPath)));
                        files.Add((manifestName, File.ReadAllBytes(wr.ManifestPath)));
                    }
                    finally
                    {
                        try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }
                else
                {
                    // RC5 WP34: rawx is PSS/E RAWX (JSON), revision 35; rawNN is RAW revision NN.
                    PsseWriteResult wr = format == "rawx"
                        ? PsseRawxWriter.Write(held.Model)
                        : PsseRawWriter.Write(held.Model, int.Parse(format.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture));
                    notes = wr.Notes;
                    files.Add((Path.GetFileName(full), new UTF8Encoding(false).GetBytes(wr.Text)));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                throw new RpcError("refused", $"the case cannot be written as {format}: {CliExit.UserMessage(ex)}", CliExit.InputError, held.Sha);
            }

            string dir = Path.GetDirectoryName(full)!;
            // Every target is checked before any is written: over --listen each one (the v2 sibling .network.json too) must be
            // confined and must not be a link, and an existing file must hold exactly these bytes.
            foreach ((string n, byte[] bytes) in files)
            {
                string target = Path.Combine(dir, n);
                if (OnNetwork)
                {
                    string? refusal = GridSim.Scripting.PathConfinement.Refusal(target, target, cwd)
                        ?? (new FileInfo(target).LinkTarget is not null ? "it is a symbolic link or junction" : null);
                    if (refusal is not null)
                        throw new RpcError("bad-argument", $"save target \"{n}\" is refused over --listen: {refusal}.", CliExit.Usage, held.Sha);
                }
                if (Directory.Exists(target) || new FileInfo(target).LinkTarget is not null
                    || (File.Exists(target) && !File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)))
                    throw new RpcError("refused", $"\"{n}\" already exists with different content (or is a link); save never overwrites a file (remove it first or name another).", CliExit.Usage, held.Sha);
            }
            try
            {
                Directory.CreateDirectory(dir);
                foreach ((string n, byte[] bytes) in files)
                {
                    string target = Path.Combine(dir, n);
                    if (File.Exists(target)) continue;   // the same bytes, checked above
                    // Create-new only: an existing name (a link placed there after the check included) is never opened.
                    using var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    fs.Write(bytes);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new RpcError("write-failed", $"cannot write \"{path}\": {ex.Message}", CliExit.InputError, held.Sha);
            }
            return Respond(id, "save", "ok", 0, held.Sha, w =>
            {
                w.WriteString("format", format);
                w.WriteNumber("revision", EditorOf(held).Revision);
                w.WriteStartArray("files");
                foreach ((string n, byte[] bytes) in files)
                {
                    w.WriteStartObject();
                    w.WriteString("name", n);
                    w.WriteNumber("bytes", bytes.Length);
                    w.WriteString("sha256", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("notes");
                foreach (string note in notes) w.WriteStringValue(note);
                w.WriteEndArray();
            });
        }
    }
}
