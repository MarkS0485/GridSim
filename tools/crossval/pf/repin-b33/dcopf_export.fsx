// B33 re-pin helper: runs the built GridSim.Core's DcOptimalPowerFlow.Solve(model, null, true) exactly as
// DcOpfSecuritySimplexParityTests does (same loaders), prints each row's pins (status, feasible, constrained, cost and
// generation bits, counts, dispatch and binding hashes) and writes the model and the answer as JSON for
// dcopf_optimality_check.py. Build the solution in Release first.
//     dotnet fsi dcopf_export.fsx <output folder> <case> [<case> ...]     (case as in the test, e.g. matpower/lib/case145.m)
#r "../../../../tests/GridSim.Tests/bin/Release/net10.0/CommunityToolkit.HighPerformance.dll"
#r "../../../../tests/GridSim.Tests/bin/Release/net10.0/GridSim.Core.dll"
open System
open System.IO
open System.Text
open System.Security.Cryptography
open System.Text.Json
open GridSim.Core.Model
open GridSim.Core.Analysis
open GridSim.Core.IO.Json
open GridSim.Core.IO.Matpower

let dataDir = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "..", "data")
let outDir = fsi.CommandLineArgs.[1]
let load (rel: string) =
    let path = Path.Combine(dataDir, rel.Replace('/', Path.DirectorySeparatorChar))
    if rel.EndsWith(".m", StringComparison.OrdinalIgnoreCase) then MatpowerUnitNormalizer.Normalize(MatpowerParser.ParseFile(path))
    else JsonGridLoader.LoadDirectory(path)
let bits (v: float) = BitConverter.DoubleToInt64Bits v
let sha (s: string) = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes s))
let r17 (v: float) = v.ToString("R", Globalization.CultureInfo.InvariantCulture)

for rel in fsi.CommandLineArgs.[2..] do
    let m = load rel
    let r = DcOptimalPowerFlow.Solve(m, null, true)
    let dh = sha (String.Join(",", r.Dispatch |> Seq.map (fun d -> (bits d.Pg).ToString("X16"))))
    let bh = sha (String.Join(";", r.BindingLines |> Seq.map (fun b -> sprintf "%d:%s" b.Branch ((bits b.FlowMw).ToString("X16")))))
    printfn "ROW %s | %A | %b | %b | %s | %s | %d | %d | %s | %s | cost %s gen %s | unrelievable %d" rel r.SecurityLpStatus r.Feasible
        r.NetworkConstrained ((bits r.TotalCost).ToString("X16")) ((bits r.TotalGenMw).ToString("X16")) r.Dispatch.Count
        r.BindingLines.Count dh bh (r17 r.TotalCost) (r17 r.TotalGenMw) r.UnrelievableLines.Count
    use o = new MemoryStream()
    use w = new Utf8JsonWriter(o, JsonWriterOptions(Indented = false))
    w.WriteStartObject()
    w.WriteString("case", rel)
    w.WriteNumber("base", m.BaseMva)
    w.WriteStartArray("bus")
    for b in m.Buses do
        w.WriteStartObject(); w.WriteNumber("id", b.Id); w.WriteString("type", string b.Type); w.WriteNumber("pd", b.Pd); w.WriteNumber("gs", b.Gs); w.WriteEndObject()
    w.WriteEndArray()
    w.WriteStartArray("br")
    for br in m.Branches do
        w.WriteStartObject(); w.WriteNumber("f", br.From); w.WriteNumber("t", br.To); w.WriteNumber("x", br.X); w.WriteNumber("ratio", br.TapRatio)
        w.WriteNumber("shift", br.PhaseShiftDeg); w.WriteNumber("rate", br.RateAMva); w.WriteBoolean("on", br.InService); w.WriteEndObject()
    w.WriteEndArray()
    w.WriteStartArray("gen")
    for i in 0 .. m.Generators.Count - 1 do
        let g = m.Generators.[i]
        w.WriteStartObject(); w.WriteNumber("bus", g.Bus); w.WriteNumber("pmin", g.Pmin)
        w.WriteNumber("pmax", (if Double.IsInfinity g.Pmax then 1e300 else g.Pmax))
        w.WriteNumber("pg", r.Dispatch.[i].Pg); w.WriteNumber("cost", r.Dispatch.[i].CostPerMwh); w.WriteEndObject()
    w.WriteEndArray()
    w.WriteStartArray("binding")
    for b in r.BindingLines do
        w.WriteStartObject(); w.WriteNumber("branch", b.Branch); w.WriteNumber("flow", b.FlowMw); w.WriteNumber("rate", b.RateMw); w.WriteEndObject()
    w.WriteEndArray()
    w.WriteStartArray("unrelievable")
    for u in r.UnrelievableLines do w.WriteNumberValue(u.Branch)
    w.WriteEndArray()
    w.WriteNumber("total_cost", r.TotalCost); w.WriteNumber("total_gen", r.TotalGenMw); w.WriteNumber("total_load", r.TotalLoadMw)
    w.WriteNumber("islands", (match r.Islands with | null -> 1 | x -> x.Count))
    w.WriteEndObject(); w.Flush()
    File.WriteAllBytes(Path.Combine(outDir, rel.Replace('/', '_') + ".json"), o.ToArray())
