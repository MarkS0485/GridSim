using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 reviews R4 and R6 on B34 (2.1.144, RAWX induction machines). WP34 made <c>imid</c> an alias of the machine id and
/// <c>hconst</c>, <c>aconst</c>, <c>bconst</c>, <c>dconst</c> and <c>econst</c> aliases of H, A, B, D and E. Before, the RAWX
/// reader dropped a name it did not know, so those fields were left empty and took their defaults. These tests pin, on one
/// RAWX file, what that changed: the record the RAW reader sees (old: defaults; new: the file's values), and the parsed
/// model, which is the same with and without the five torque fields because the power-flow reader uses none of them (they
/// are dynamic data). The id is the change a result can see: two machines at one bus with different imids were refused as
/// "defined twice" (both read as id 1), and now both are read.
/// </summary>
public sealed class Rc5B34WideningTests
{
    // The induction machine field list before WP34 (8e036f71^1, PsseFieldRemap.InductionMachine), copied here so the test
    // shows what the reader saw then.
    private static readonly string[] SpecBeforeWp34 =
    {
        "I|IBUS", "ID|INDID|MACHID*", "STAT|STATUS|ST", "SCODE", "DCODE", "AREA", "ZONE", "OWNER", "TCODE", "BCODE", "MBASE",
        "RATEKV", "PCODE", "PSET", "H", "A", "B", "D", "E", "RA", "XA", "XM", "R1", "X1", "R2", "X2", "X3", "E1", "SE1", "E2",
        "SE2", "IA1", "IA2", "XAMULT"
    };

    private static readonly string[] TorqueNames = ["hconst", "aconst", "bconst", "dconst", "econst"];

    // A two-bus RAWX case with induction machine rows at bus 2 (single cage, custom design: DCODE 0). With torque, each row
    // carries H, A, B, D and E values that are not their defaults; without, those five fields are left out of the file.
    // Without the imid column, each row is what the reader before WP34 kept of it (the id dropped).
    private static string Rawx(bool torque, params string[] imids) => Rawx(torque, imidColumn: true, imids);

    private static string Rawx(bool torque, bool imidColumn, params string[] imids)
    {
        var fields = new List<string> { "ibus" };
        if (imidColumn) fields.Add("imid");
        fields.AddRange(["stat", "scode", "dcode", "area", "zone", "owner", "tcode", "bcode", "mbase", "ratekv", "pcode", "pset"]);
        if (torque) fields.AddRange(TorqueNames);
        fields.AddRange(["ra", "xa", "xm", "r1", "x1", "r2", "x2", "x3", "e1", "se1", "e2", "se2"]);
        var rows = imids.Select(id =>
        {
            var v = new List<string> { "2" };
            if (imidColumn) v.Add("\"" + id + "\"");
            v.AddRange(["1", "1", "0", "1", "1", "1", "1", "1", "10.0", "0.0", "1", "2.0"]);
            if (torque) v.AddRange(["0.7", "1.1", "0.2", "0.3", "2.0"]);
            v.AddRange(["0.01", "0.1", "3.0", "0.02", "0.12", "0.0", "0.0", "0.0", "1.0", "0.0", "1.2", "0.0"]);
            return "[" + string.Join(",", v) + "]";
        });
        string indmach = "\"indmach\":{\"fields\":[" + string.Join(",", fields.Select(f => "\"" + f + "\"")) + "],\"data\":[" + string.Join(",", rows) + "]}";
        return """
        {"network":{
          "caseid":{"fields":["ic","sbase","rev","basfrq","title1","title2"],"data":[0,100.0,35,50.0,"B34","INDUCTION MACHINE"]},
          "bus":{"fields":["ibus","name","baskv","ide","area","zone","owner","vm","va"],"data":[[1,"A",11.0,3,1,1,1,1.0,0.0],[2,"B",11.0,1,1,1,1,1.0,0.0]]},
          "load":{"fields":["ibus","loadid","stat","pl","ql"],"data":[[2,"1",1,20.0,5.0]]},
          "generator":{"fields":["ibus","machid","pg","qg","qt","qb","vs","stat","mbase","pt","pb"],"data":[[1,"1",25.0,0.0,50.0,-50.0,1.0,1,100.0,100.0,0.0]]},
          "acline":{"fields":["ibus","jbus","ckt","rpu","xpu","bpu","rate1","stat"],"data":[[1,2,"1",0.01,0.05,0.0,100.0,1]]},
        """ + indmach + "}}";
    }

    [Fact]
    public void The_record_the_raw_reader_sees_had_default_torque_fields_before_wp34_and_has_the_files_values_now()
    {
        List<string> names = new[] { "ibus", "imid", "stat", "dcode", "pset", "hconst", "aconst", "bconst", "dconst", "econst", "ra" }
            .Select(PsseFieldRemap.Key).ToList();
        string?[] values = ["2", "7", "1", "0", "2.0", "0.7", "1.1", "0.2", "0.3", "2.0", "0.01"];

        List<string?> before = PsseFieldRemap.Reorder(SpecBeforeWp34, names, values);
        List<string?> now = PsseFieldRemap.Reorder(PsseFieldRemap.InductionMachine, names, values);

        // Fields 2 (ID) and 15 to 19 (H, A, B, D, E) of the RAW record, 1-based.
        Assert.Null(before[1]);
        Assert.Equal("7", now[1]);
        Assert.Equal(new string?[] { null, null, null, null, null }, before.Skip(14).Take(5));
        Assert.Equal(new string?[] { "0.7", "1.1", "0.2", "0.3", "2.0" }, now.Skip(14).Take(5));
        // Every other field is read the same way before and after.
        Assert.Equal("2", before[0]);
        Assert.Equal("2", now[0]);
        Assert.Equal("0.01", before[19]);
        Assert.Equal("0.01", now[19]);
    }

    [Fact]
    public void Reading_the_torque_fields_changes_no_number_of_the_parsed_model()
    {
        PsseParseResult withTorque = PsseRawParser.ParseWithDiagnostics(Rawx(torque: true, "1"), "b34");
        PsseParseResult defaulted = PsseRawParser.ParseWithDiagnostics(Rawx(torque: false, "1"), "b34");

        InductionMachine m = Assert.Single(withTorque.Model.InductionMachines);
        Assert.Equal((2, 2.0, 10.0, 0.01, 0.1, 3.0, 0.02, 0.12), (m.Bus, m.ScheduledMw, m.MBase, m.RaPu, m.XaPu, m.XmPu, m.R1Pu, m.X1Pu));
        Assert.Equal(defaulted.Model.InductionMachines, withTorque.Model.InductionMachines);
        Assert.Equal(defaulted.Model.AllGenerators, withTorque.Model.AllGenerators);
        Assert.Equal(defaulted.Model.Branches, withTorque.Model.Branches);
        Assert.Equal(defaulted.Model.Buses.Select(b => (b.Id, b.Type, b.Pd, b.Qd, b.Gs, b.Bs, b.Vm, b.VaDeg, b.BaseKv)),
            withTorque.Model.Buses.Select(b => (b.Id, b.Type, b.Pd, b.Qd, b.Gs, b.Bs, b.Vm, b.VaDeg, b.BaseKv)));
        Assert.Equal(defaulted.Notes, withTorque.Notes);
    }

    [Fact]
    public void Two_machines_at_one_bus_with_different_imids_now_read_where_they_were_refused_as_defined_twice()
    {
        PsseParseResult now = PsseRawParser.ParseWithDiagnostics(Rawx(torque: true, "1", "2"), "b34");
        Assert.Equal(new[] { "induction machine 2 1", "induction machine 2 2" }, now.Model.InductionMachines.Select(x => x.Name));

        // Before WP34 the imid column was dropped and both rows read as id 1. The same file without it is what that reader
        // saw, and it is refused.
        string withoutImid = Rawx(torque: true, imidColumn: false, "1", "2");
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(withoutImid, "b34"));
        Assert.Contains("defined twice", ex.Message, StringComparison.Ordinal);

        // A single machine with imid 7 was id 1 before; it is id 7 now.
        Assert.Equal("induction machine 2 7", Assert.Single(PsseRawParser.ParseWithDiagnostics(Rawx(torque: true, "7"), "b34").Model.InductionMachines).Name);
    }
}
