using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GridSim.Scripting;

/// <param name="BaseDirectory">Relative LOAD and EXPORT paths are taken from here (the script's folder by default).</param>
/// <param name="Out">Where statement output is printed as it happens; null prints nothing.</param>
/// <param name="Gate">Hosting hook: see <see cref="Session.Gate"/>. Null for the console.</param>
/// <param name="Limits">Hosting hook: see <see cref="SessionLimits"/>. Null for the console.</param>
public sealed record RunOptions(string BaseDirectory, TextWriter? Out = null, Func<Statement, Session, string?>? Gate = null, SessionLimits? Limits = null);

/// <param name="RecordJson">The run record (schema gridsim-script-run/1).</param>
/// <param name="Error">The error that stopped the run, or null when every statement ran.</param>
public sealed record RunResult(string RecordJson, ScriptException? Error, Session Session)
{
    public bool Ok => Error is null;
    public byte[] RecordBytes => new UTF8Encoding(false).GetBytes(RecordJson);
    public string RecordSha256 => Convert.ToHexString(SHA256.HashData(RecordBytes)).ToLowerInvariant();
}

/// <summary>Runs a whole script: parse everything, then validate and execute one statement at a time, and write the run record.</summary>
public static class Runner
{
    public const string RecordSchema = "gridsim-script-run/1";

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Decode script bytes as UTF-8 (a byte-order mark is dropped).</summary>
    public static string Decode(byte[] bytes) => new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');

    /// <summary>Check a script without running it: null when it parses and its procedures are defined once and never recurse, else the error.</summary>
    public static ScriptException? Check(string text)
    {
        try
        {
            Validator.CheckProcedureGraph(Parser.ParseScript(text));
            return null;
        }
        catch (ScriptException ex) { return ex; }
    }

    public static RunResult Run(byte[] scriptBytes, RunOptions options)
    {
        ArgumentNullException.ThrowIfNull(scriptBytes);
        ArgumentNullException.ThrowIfNull(options);
        var session = new Session(options.BaseDirectory) { Gate = options.Gate, Limits = options.Limits };
        string scriptSha = Sha256(scriptBytes);
        ScriptException? error = null;
        IReadOnlyList<Statement> statements = [];
        try { statements = Parser.ParseScript(Decode(scriptBytes)); }
        catch (ScriptException ex) { error = ex; }

        if (error is null)
        {
            try { Validator.CheckProcedureGraph(statements); }
            catch (ScriptException ex) { error = ex; }
        }

        if (error is null)
        {
            foreach (Statement s in statements)
            {
                try
                {
                    string output = session.Execute(s);
                    if (output.Length > 0) options.Out?.Write(output);
                }
                catch (ScriptException ex)
                {
                    error = ex;
                    break;
                }
            }
        }
        return new RunResult(WriteRecord(session, scriptSha, error), error, session);
    }

    /// <summary>The run record of a session (also used by the console, which executes line by line).</summary>
    public static string WriteRecord(Session session, string scriptSha256, ScriptException? error)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            w.WriteStartObject();
            w.WriteString("schema", RecordSchema);
            w.WriteString("gridsimVersion", GridSim.Core.BuildInfo.Version);
            w.WriteString("grammarVersion", Grammar.VersionText);
            w.WriteString("scriptSha256", scriptSha256);
            w.WriteString("canonicalScriptSha256", session.CanonicalScriptSha256);
            if (session.ModelSha256 is { } m) w.WriteString("modelSha256", m); else w.WriteNull("modelSha256");

            w.WriteStartArray("inputs");
            foreach (InputEntry i in session.Inputs)
            {
                w.WriteStartObject();
                w.WriteNumber("seq", i.Seq);
                w.WriteNumber("line", i.Line);
                w.WriteString("kind", i.Kind);
                w.WriteString("path", i.Path);
                w.WriteString("sha256", i.Sha256);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartObject("solver");
            var o = session.SolverOptions;
            w.WriteString("powerFlow", session.SolverName.Length == 0 ? "not run" : session.SolverName);
            w.WriteNumber("tolerancePu", o.Tolerance);
            w.WriteNumber("maxIterations", o.MaxIterations);
            w.WriteBoolean("flatStart", o.FlatStart);
            w.WriteBoolean("enforceQLimits", o.EnforceQLimits);
            w.WriteNumber("maxQLimitPasses", o.MaxQLimitPasses);
            w.WriteBoolean("retryFromDcStart", o.RetryFromDcStart);
            w.WriteString("engine", session.EngineKind.ToString().ToUpperInvariant());
            if (session.LiveSettings is { } ls)
            {
                w.WriteString("liveFlowSolver", ls.Solver);
                w.WriteNumber("integrationStepUs", ls.StepUs);
                w.WriteNumber("nominalHz", ls.NominalHz);
                w.WriteNumber("liveSimSeed", GridSim.Core.Simulation.LiveSolveSettings.LiveSimSeed);
            }
            w.WriteEndObject();

            w.WriteStartArray("statements");
            foreach (StatementEntry s in session.Statements)
            {
                w.WriteStartObject();
                w.WriteNumber("seq", s.Seq);
                w.WriteNumber("line", s.Line);
                w.WriteNumber("column", s.Column);
                w.WriteNumber("timeUs", s.TimeUs);
                w.WriteString("text", s.Text);
                if (s.Depth > 0) w.WriteNumber("depth", s.Depth);
                if (s.Resolved is { } rs) w.WriteString("resolved", rs);
                if (s.OutputSha256 is { } h) w.WriteString("outputSha256", h);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("engines");
            foreach (EngineEntry en in session.Engines)
            {
                w.WriteStartObject();
                w.WriteNumber("seq", en.Seq);
                w.WriteNumber("line", en.Line);
                w.WriteString("engine", en.Kind);
                w.WriteNumber("stepUs", en.StepUs);
                w.WriteString("settings", en.Settings);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("procedures");
            foreach (ProcEntry pe in session.ProcEntries)
            {
                w.WriteStartObject();
                w.WriteNumber("seq", pe.Seq);
                w.WriteNumber("line", pe.Line);
                w.WriteString("name", pe.Name);
                w.WriteString("header", pe.Header);
                w.WriteString("bodySha256", pe.BodySha256);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("decisions");
            foreach (DecisionEntry d in session.Decisions)
            {
                w.WriteStartObject();
                w.WriteNumber("seq", d.Seq);
                w.WriteNumber("line", d.Line);
                w.WriteString("condition", d.Condition);
                w.WriteString("evaluated", d.Detail);
                w.WriteBoolean("result", d.Result);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("loops");
            foreach (LoopEntry l in session.Loops)
            {
                w.WriteStartObject();
                w.WriteNumber("seq", l.Seq);
                w.WriteNumber("line", l.Line);
                w.WriteString("noun", l.Noun);
                w.WriteString("where", l.Where);
                w.WriteNumber("elements", l.Total);
                w.WriteStartArray("matched");
                foreach (string mn in l.Matched) w.WriteStringValue(mn);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("events");
            foreach (AppliedEvent e in session.AppliedEvents)
            {
                w.WriteStartObject();
                w.WriteNumber("stepSeq", e.StepSeq);
                w.WriteNumber("timeUs", e.TimeUs);
                w.WriteString("text", e.Text);
                w.WriteNumber("scheduledAtLine", e.ScheduledAtLine);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("steps");
            foreach (StepEntry s in session.Steps)
            {
                w.WriteStartObject();
                w.WriteNumber("seq", s.Seq);
                w.WriteNumber("line", s.Line);
                w.WriteNumber("fromUs", s.FromUs);
                w.WriteNumber("toUs", s.ToUs);
                w.WriteNumber("ticks", s.Ticks);
                w.WriteString("engine", s.Engine);
                w.WriteStartObject("final");
                foreach (KeyValuePair<string, string> kv in s.Final) w.WriteString(kv.Key, kv.Value);
                w.WriteEndObject();
                w.WriteString("seriesSha256", s.SeriesSha256);
                if (s.Final.Any(kv => kv.Key == "frequencyHz"))
                {
                    w.WriteString("finalFrequencyHz", s.Final.First(kv => kv.Key == "frequencyHz").Value);
                    w.WriteString("finalRocofHzPerS", s.Final.First(kv => kv.Key == "rocofHzPerS").Value);
                    w.WriteString("frequencySeriesSha256", s.SeriesSha256);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("outputs");
            foreach (OutputEntry x in session.Outputs)
            {
                w.WriteStartObject();
                w.WriteNumber("seq", x.Seq);
                w.WriteNumber("line", x.Line);
                w.WriteString("kind", x.Kind);
                w.WriteString("name", x.Name);
                w.WriteNumber("bytes", x.Bytes);
                w.WriteString("sha256", x.Sha256);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("variables");
            foreach (KeyValuePair<string, Value> kv in session.Globals.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("name", kv.Key);
                w.WriteString("value", kv.Value.Display());
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartObject("result");
            w.WriteString("status", error is null ? "ok" : "error");
            w.WriteNumber("finalClockUs", session.ClockUs);
            if (error is not null)
            {
                w.WriteStartObject("error");
                w.WriteNumber("line", error.Pos.Line);
                w.WriteNumber("column", error.Pos.Column);
                w.WriteString("phase", error.Phase.ToString().ToLowerInvariant());
                w.WriteString("message", error.Message);
                w.WriteEndObject();
            }
            w.WriteEndObject();

            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }
}
