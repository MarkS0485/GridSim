"""Independent balanced AC reference calculations on literal MATPOWER v2 cases."""
import cmath
import math
import re
from pathlib import Path


def load_case(path):
    text = re.sub(r"%[^\n]*", "", Path(path).read_text(encoding="utf-8-sig"))
    base = float(re.search(r"mpc\.baseMVA\s*=\s*([^;]+);", text)[1])
    tables = {}
    for name in ("bus", "gen", "branch"):
        block = re.search(rf"mpc\.{name}\s*=\s*\[(.*?)\]\s*;", text, re.S)[1]
        tables[name] = [list(map(float, row.split())) for row in block.split(";")
                        if row.strip()]
    return {"baseMVA": base, **tables}


def calculate(case, voltages):
    """Return branch end flows and Newton power mismatches on the source case."""
    base = case["baseMVA"]
    buses = {int(row[0]): row for row in case["bus"]}
    if buses.keys() != voltages.keys():
        raise ValueError("Voltage bus IDs differ from the MATPOWER case")
    voltage = {bus: cmath.rect(vm, math.radians(va))
               for bus, (vm, va) in voltages.items()}
    actual = {bus: abs(voltage[bus])**2 * complex(row[4], -row[5])
              for bus, row in buses.items()}
    dispatch = {bus: -complex(row[2], row[3]) for bus, row in buses.items()}
    for gen in case["gen"]:
        if gen[7] > 0:
            dispatch[int(gen[0])] += complex(gen[1], gen[2])
    flows = []
    for index, branch in enumerate(case["branch"], 1):
        f, t = map(int, branch[:2])
        if branch[10] <= 0:
            flows.append((index, 0j, 0j))
            continue
        y = 1 / complex(branch[2], branch[3])
        ysh = complex(0, branch[4] / 2)
        tap = cmath.rect(branch[8] or 1.0, math.radians(branch[9]))
        vf, vt = voltage[f], voltage[t]
        sf = base * vf * ((y + ysh) * vf / abs(tap)**2 - y * vt / tap.conjugate()).conjugate()
        st = base * vt * ((y + ysh) * vt - y * vf / tap).conjugate()
        flows.append((index, sf, st))
        actual[f] += sf
        actual[t] += st
    mismatches = []
    for bus, row in buses.items():
        mismatch = (actual[bus] - dispatch[bus]) / base
        if row[1] in (1, 2):
            mismatches.append((abs(mismatch.real), bus, "P"))
        if row[1] == 1:
            mismatches.append((abs(mismatch.imag), bus, "Q"))
    return flows, max(mismatches)


def write_start_case(source, destination):
    """Set the starting point and spell shifted unity taps explicitly."""
    text = Path(source).read_text(encoding="utf-8-sig")
    block = re.search(r"(mpc\.bus\s*=\s*\[)(.*?)(\]\s*;)", text, re.S)
    targets = {}
    for generator in load_case(source)["gen"]:
        if generator[7] > 0:
            bus, target = int(generator[0]), generator[5]
            if bus in targets and targets[bus] != target:
                raise ValueError(f"Conflicting active voltage targets at bus {bus}")
            targets[bus] = target
    normalised = []
    for line in block[2].splitlines(keepends=True):
        data, separator, comment = line.partition("%")
        values = data.strip().rstrip(";").split()
        if values:
            if len(values) != 13 or not data.rstrip().endswith(";"):
                raise ValueError("Expected one 13-column MATPOWER bus per line")
            values[8] = "0"
            if float(values[1]) == 1:
                values[7] = "1"
            elif int(float(values[0])) in targets:
                values[7] = repr(targets[int(float(values[0]))])
            newline = "\n" if line.endswith("\n") else ""
            suffix = (" %" + comment.rstrip("\n")) if separator else ""
            line = "\t" + "\t".join(values) + ";" + suffix + newline
        normalised.append(line)
    output = text[:block.start(2)] + "".join(normalised) + text[block.end(2):]
    branch_block = re.search(r"(mpc\.branch\s*=\s*\[)(.*?)(\]\s*;)", output, re.S)
    branch_lines = []
    for line in branch_block[2].splitlines(keepends=True):
        values = list(re.finditer(r"[^\s;]+", line.partition("%")[0]))
        if values and float(values[8][0]) == 0 and float(values[9][0]) != 0:
            tap = values[8]
            line = line[:tap.start()] + "1" + line[tap.end():]
        branch_lines.append(line)
    output = output[:branch_block.start(2)] + "".join(branch_lines) + output[branch_block.end(2):]
    Path(destination).write_text(output, encoding="utf-8", newline="\n")


def gridsim_import_differences(case, model):
    """Compare GridSim's exported input model with the exact worker input."""
    def compare_rows(name, fields, expected, actual):
        if len(expected) != len(actual):
            return [{"table": name, "expectedCount": len(expected), "actualCount": len(actual)}]
        return [{"table": name, "row": index, "field": field,
                 "expected": wanted, "actual": got[field]}
                for index, (want, got) in enumerate(zip(expected, actual), 1)
                for field, wanted in zip(fields, want, strict=True)
                if got[field] != wanted]

    bus_types = {1: "Pq", 2: "Pv", 3: "Slack"}
    buses = [(b[0], bus_types[b[1]], b[9], *b[2:6], *b[7:9]) for b in case["bus"]]
    gens = [(*g[:3], g[5], bool(g[7])) for g in case["gen"]]
    branches = [(*b[:5], b[8] or 1, b[9], bool(b[10])) for b in case["branch"]]
    differences = []
    if model["baseMva"] != case["baseMVA"]:
        differences.append({"field": "baseMva", "expected": case["baseMVA"],
                            "actual": model["baseMva"]})
    differences.extend(compare_rows("bus", ("id", "type", "baseKv", "pd", "qd", "gs",
                                             "bs", "vm", "vaDeg"), buses, model["buses"]))
    differences.extend(compare_rows("gen", ("bus", "pg", "qg", "vg", "inService"),
                                    gens, model["generators"]))
    differences.extend(compare_rows("branch", ("from", "to", "r", "x", "b", "tap",
                                                "phaseShiftDeg", "inService"),
                                    branches, model["branches"]))
    return differences


def controlled_voltage_error(case, voltages):
    """Compare solved PV and slack magnitudes with active generator targets."""
    controlled = {int(bus[0]) for bus in case["bus"] if bus[1] in (2, 3)}
    return max((abs(voltages[int(gen[0])][0] - gen[5]), int(gen[0]))
               for gen in case["gen"] if gen[7] > 0 and int(gen[0]) in controlled)
