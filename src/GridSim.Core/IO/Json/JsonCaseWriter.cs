using System.Text.Json;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Json;

/// <summary>
/// Writes a <see cref="GridModel"/> as a JSON case folder that <see cref="JsonGridLoader.LoadDirectory"/> reads back
/// to the same network (F24, October 2026: the <c>convert</c> verb's JSON side).
///
/// <para><b>What "the same" means here.</b> Every number any power flow reads is written exactly (System.Text.Json
/// writes the shortest form that reads back to the same double) and in the same ORDER: buses, generators (the whole
/// fleet, out-of-service units included, F16) and branches stay in model order, because the solvers sum per-bus
/// admittances and injections in that order and a different order can change the last bit. So the loaded model
/// solves bit-identically to the model written. To make that possible the folder uses the F24 keys:
/// <c>system.json "dispatch": false</c> (no capacity-based balance: the written outputs are the operating point),
/// <c>generation.json</c> pMinMw / pMaxMw / qOutputMvar and the strings "Infinity" / "-Infinity" for unlimited
/// limits, and every branch in <c>transmission.json</c> (a transformer carries tapRatio / phaseShiftDeg /
/// isTransformer there, in place), never in transformer.json.</para>
///
/// <para><b>What JSON v1 cannot carry</b> is reported in the returned notes, never silently dropped: generator
/// subtransient / negative-sequence reactances and gencost curves, transformer winding connections and neutral
/// reactances, out-of-service branches, and the asset-dynamics and protection bundles.</para>
/// </summary>
public static class JsonCaseWriter
{
    /// <summary>The files this writer creates (it refuses a folder that already holds any of them).</summary>
    public static readonly IReadOnlyList<string> CaseFiles = new[]
    {
        "system.json", "buses.json", "generation.json", "demand.json", "transmission.json", "onloadtapchanger.json", "boundaries.json",
    };

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    /// <summary>
    /// Write <paramref name="model"/> into <paramref name="dir"/> (created if absent). Never overwrites: a folder that
    /// already holds any of <see cref="CaseFiles"/> is refused with <see cref="IOException"/>. Returns the notes on
    /// what could not be carried (empty when nothing was lost). A non-finite number where the loader needs a finite
    /// one (any value except an unlimited generator limit) is refused with <see cref="InvalidDataException"/>.
    /// </summary>
    public static IReadOnlyList<string> WriteDirectory(GridModel model, string dir)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(dir);
        CaseWriterChecks.RefuseReferencesToOmittedBranches(model, "JSON case writer");   // GR-164
        CaseWriterChecks.RefuseLccLinks(model, "JSON case writer");
        foreach (string f in CaseFiles)
            if (File.Exists(Path.Combine(dir, f)))
                throw new IOException($"'{dir}' already holds {f}; the JSON case writer never overwrites a case.");
        // GR-165: every file is built in memory first and written only once all of them are built, so a refusal (a
        // non-finite number, a name the loader would refuse) leaves NO file behind. A half-written folder used to block
        // the retry, because the writer never overwrites.
        var pending = new List<(string Path, byte[] Bytes)>();
        var notes = new List<string>();

        // Bus types by id, for each generator's role (the loader re-derives bus types from buses.json and the roles,
        // so writing the role of the bus it sits on reproduces every type exactly).
        var typeById = new Dictionary<int, BusType>();
        foreach (Bus b in model.Buses) typeById.TryAdd(b.Id, b.Type);

        Stage(pending, Path.Combine(dir, "system.json"), w =>
        {
            w.WriteStartObject();
            w.WriteString("name", model.Name);
            Num(w, "baseMva", model.BaseMva, "system baseMva");
            Num(w, "frequencyHz", model.FrequencyHz, "system frequencyHz");
            w.WriteBoolean("dispatch", false);   // the written outputs are the operating point (F24)
            w.WriteEndObject();
        });

        Stage(pending, Path.Combine(dir, "buses.json"), w =>
        {
            w.WriteStartArray();
            foreach (Bus b in model.Buses)
            {
                string at = $"bus {b.Id}";
                w.WriteStartObject();
                w.WriteNumber("id", b.Id);
                if (b.Name is not null) w.WriteString("name", b.Name);
                Num(w, "baseKv", b.BaseKv, at + " baseKv");
                w.WriteString("type", b.Type switch { BusType.Slack => "slack", BusType.Pv => "pv", _ => "pq" });
                Num(w, "vm", b.Vm, at + " vm");
                Num(w, "vaDeg", b.VaDeg, at + " vaDeg");
                Num(w, "gs", b.Gs, at + " gs");
                Num(w, "bs", b.Bs, at + " bs");
                Num(w, "x", b.X, at + " x");
                Num(w, "y", b.Y, at + " y");
                if (b.Zip is ZipLoad z)
                {
                    w.WriteStartObject("zip");
                    Num(w, "ap", z.Ap, at + " zip ap"); Num(w, "bp", z.Bp, at + " zip bp"); Num(w, "cp", z.Cp, at + " zip cp");
                    Num(w, "aq", z.Aq, at + " zip aq"); Num(w, "bq", z.Bq, at + " zip bq"); Num(w, "cq", z.Cq, at + " zip cq");
                    w.WriteEndObject();
                }
                if (b.VminPu is double lo) Num(w, "vminPu", lo, at + " vminPu");
                if (b.VmaxPu is double hi) Num(w, "vmaxPu", hi, at + " vmaxPu");
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });

        int lostReactances = 0, lostCosts = 0;
        Stage(pending, Path.Combine(dir, "generation.json"), w =>
        {
            w.WriteStartArray();
            for (int i = 0; i < model.AllGenerators.Count; i++)
            {
                Generator g = model.AllGenerators[i];
                string at = $"generator {i} (bus {g.Bus})";
                w.WriteStartObject();
                if (g.Name is not null) w.WriteString("name", g.Name);
                w.WriteNumber("bus", g.Bus);
                w.WriteString("type", g.Type);   // always: absent would become "ccgt"
                Num(w, "capacityMva", g.MBase, at + " MBase");
                Num(w, "outputMw", g.Pg, at + " Pg");
                Num(w, "qOutputMvar", g.Qg, at + " Qg");
                Num(w, "voltageSetpoint", g.Vg, at + " Vg");
                Limit(w, "qMinMvar", g.Qmin, at + " Qmin");
                Limit(w, "qMaxMvar", g.Qmax, at + " Qmax");
                Num(w, "pMinMw", g.Pmin, at + " Pmin");
                Limit(w, "pMaxMw", g.Pmax, at + " Pmax");
                Num(w, "inertiaH", g.H, at + " H");
                w.WriteString("role", typeById.GetValueOrDefault(g.Bus) switch { BusType.Slack => "slack", BusType.Pv => "pv", _ => "pq" });
                if (g.DynamicsClass is not null) w.WriteString("dynamicsClass", g.DynamicsClass);
                if (g.ConverterInterfaced is bool ci) w.WriteBoolean("converterInterfaced", ci);
                if (g.ConverterFaultCurrentPu is double k) Num(w, "converterFaultCurrentPu", k, at + " converterFaultCurrentPu");
                if (g.PowerFlowOnly) w.WriteBoolean("powerFlowOnly", true);
                if (g.UnitTransformerXPu != 0) Num(w, "unitTransformerXPu", g.UnitTransformerXPu, at + " unitTransformerXPu");
                if (g.GridForming) w.WriteBoolean("gridForming", true);
                if (!g.InService) w.WriteBoolean("inService", false);
                w.WriteEndObject();
                if (g.SubtransientXdPu != 0 || g.NegativeSeqXPu != 0) lostReactances++;
                if (g.Cost is not null || g.ReactiveCost is not null) lostCosts++;
            }
            w.WriteEndArray();
        });
        if (lostReactances > 0)
            notes.Add($"{lostReactances} generator(s) carry a subtransient or negative-sequence reactance, which JSON v1 cannot hold; the fault studies of the written case use the synthesised defaults for them.");
        if (lostCosts > 0)
            notes.Add($"{lostCosts} generator(s) carry a MATPOWER cost curve (gencost), which JSON v1 cannot hold yet (F19); no solver uses it today.");

        // One demand row per bus that carries any demand, in bus order. (A demand of -0.0 is written as a row too, but
        // the loader sums it onto 0.0 and reads +0.0 back: the one bit pattern a JSON case does not keep.)
        Stage(pending, Path.Combine(dir, "demand.json"), w =>
        {
            w.WriteStartArray();
            foreach (Bus b in model.Buses)
            {
                if (BitConverter.DoubleToInt64Bits(b.Pd) == 0 && BitConverter.DoubleToInt64Bits(b.Qd) == 0) continue;
                w.WriteStartObject();
                w.WriteNumber("bus", b.Id);
                Num(w, "pMw", b.Pd, $"bus {b.Id} Pd");
                Num(w, "qMvar", b.Qd, $"bus {b.Id} Qd");
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });

        int outOfService = 0, lostZeroSeq = 0;
        Stage(pending, Path.Combine(dir, "transmission.json"), w =>
        {
            w.WriteStartArray();
            for (int i = 0; i < model.Branches.Count; i++)
            {
                Branch br = model.Branches[i];
                if (!br.InService) { outOfService++; continue; }
                string at = $"branch {i} ({br.From}-{br.To})";
                w.WriteStartObject();
                if (br.Name is not null) w.WriteString("name", br.Name);
                w.WriteNumber("from", br.From);
                w.WriteNumber("to", br.To);
                Num(w, "rPu", br.R, at + " R");
                Num(w, "xPu", br.X, at + " X");
                Num(w, "bPu", br.B, at + " B");
                Num(w, "ratingMva", br.RateAMva, at + " RateA");
                // F20: short-term / emergency ratings, written only when given (absent reads back as 0).
                if (br.RateBMva != 0) Num(w, "ratingBMva", br.RateBMva, at + " RateB");
                if (br.RateCMva != 0) Num(w, "ratingCMva", br.RateCMva, at + " RateC");
                if (BitConverter.DoubleToInt64Bits(br.TapRatio) != BitConverter.DoubleToInt64Bits(1.0)) Num(w, "tapRatio", br.TapRatio, at + " TapRatio");
                if (BitConverter.DoubleToInt64Bits(br.PhaseShiftDeg) != 0) Num(w, "phaseShiftDeg", br.PhaseShiftDeg, at + " PhaseShiftDeg");
                if (br.IsTransformer) w.WriteBoolean("isTransformer", true);
                if (br.DynamicsClass is not null) w.WriteString("dynamicsClass", br.DynamicsClass);
                if (br.LengthKm != 0) Num(w, "lengthKm", br.LengthKm, at + " LengthKm");
                if (br.Equipment is not null) w.WriteString("equipment", br.Equipment);
                if (br.R0 != 0) Num(w, "rPu0", br.R0, at + " R0");
                if (br.X0 != 0) Num(w, "xPu0", br.X0, at + " X0");
                w.WriteEndObject();
                if (br.FromWinding is not null || br.ToWinding is not null || br.FromNeutralXPu != 0 || br.ToNeutralXPu != 0) lostZeroSeq++;
            }
            w.WriteEndArray();
        });
        if (outOfService > 0)
            notes.Add($"{outOfService} out-of-service branch(es) not written (JSON v1 has no branch status); the written case solves the same, but its branch list is shorter.");
        if (lostZeroSeq > 0)
            notes.Add($"{lostZeroSeq} branch(es) carry transformer winding connections or neutral reactances, which JSON v1 cannot hold; earth-fault studies of the written case will differ.");

        if (model.Oltcs.Count > 0)
            Stage(pending, Path.Combine(dir, "onloadtapchanger.json"), w =>
            {
                w.WriteStartArray();
                foreach (Oltc o in model.Oltcs)
                {
                    w.WriteStartObject();
                    w.WriteString("transformer", o.Transformer);
                    w.WriteNumber("controlsBus", o.ControlsBus);
                    Num(w, "targetVpu", o.TargetVpu, "OLTC targetVpu");
                    Num(w, "minTap", o.MinTap, "OLTC minTap");
                    Num(w, "maxTap", o.MaxTap, "OLTC maxTap");
                    Num(w, "stepPu", o.StepPu, "OLTC stepPu");
                    Num(w, "deadbandPu", o.DeadbandPu, "OLTC deadbandPu");
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });

        if (model.Boundaries.Count > 0)
            Stage(pending, Path.Combine(dir, "boundaries.json"), w =>
            {
                w.WriteStartArray();
                foreach (Boundary b in model.Boundaries)
                {
                    w.WriteStartObject();
                    w.WriteString("name", b.Name);
                    Num(w, "limitMw", b.LimitMw, $"boundary {b.Name} limitMw");
                    w.WriteStartArray("branches");
                    foreach (BoundaryBranch bb in b.Branches)
                    {
                        w.WriteStartObject();
                        w.WriteString("branch", bb.Branch);
                        w.WriteNumber("direction", bb.Direction);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });

        if (model.AssetDynamics is not null)
            notes.Add("the authored asset-dynamics bundle (asset-classes.json / asset-bindings.json) is not written; copy those files across by hand if the written case needs them.");
        if (model.Protections is not null)
            notes.Add("the protection bundle (protections.json) is not written; copy it (and asset-classes.json) across by hand if the written case needs it.");
        Directory.CreateDirectory(dir);
        Flush(pending);
        return notes;
    }

    // Render one file into memory (GR-165: nothing reaches the disk until every file has been built).
    private static void Stage(List<(string Path, byte[] Bytes)> pending, string path, Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOptions)) body(w);
        pending.Add((path, ms.ToArray()));
    }

    // Write the staged files, never overwriting. If a write fails part-way (a file appeared meanwhile, the disk is
    // full), the files this call already created are removed again, so the folder is left as it was found.
    private static void Flush(List<(string Path, byte[] Bytes)> pending)
    {
        var written = new List<string>(pending.Count);
        try
        {
            foreach ((string path, byte[] bytes) in pending)
            {
                using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) fs.Write(bytes);
                written.Add(path);
            }
        }
        catch
        {
            foreach (string p in written)
                try { File.Delete(p); } catch (IOException) { /* best effort: the original failure is what is reported */ }
            throw;
        }
    }

    // A number the loader needs finite: refused, naming it, when it is not.
    private static void Num(Utf8JsonWriter w, string name, double value, string what)
    {
        if (!double.IsFinite(value))
            throw new InvalidDataException($"{what} is {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}, which a JSON case cannot hold.");
        w.WriteNumber(name, value);
    }

    // A generator limit: finite as a number, unlimited as the string "Infinity" / "-Infinity", NaN refused.
    private static void Limit(Utf8JsonWriter w, string name, double value, string what)
    {
        if (double.IsPositiveInfinity(value)) w.WriteString(name, "Infinity");
        else if (double.IsNegativeInfinity(value)) w.WriteString(name, "-Infinity");
        else Num(w, name, value, what);
    }
}
