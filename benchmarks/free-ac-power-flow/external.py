"""Collect isolated external worker attempts and compare electrical states."""
import copy
import hashlib
import json
import math
import subprocess

from case_reference import calculate, controlled_voltage_error


def run_external(name, command, output, case):
    folder = output / name
    folder.mkdir()
    row = {"mode": name, "command": command, "attempts": []}
    with (folder / "worker.log").open("wb") as log:
        try:
            process = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT,
                                     timeout=900, check=False)
            row["exitCode"] = process.returncode
        except (OSError, subprocess.TimeoutExpired) as error:
            row["error"] = str(error)
    converted = folder / "converted-ppc.npz"
    if name == "powsybl":
        converted = next(folder.glob("*.mat"), converted)
    if converted.is_file():
        row["convertedInput"] = {"file": converted.name,
                                 "sha256": hashlib.sha256(converted.read_bytes()).hexdigest()}
    native_case = None
    if name == "andes":
        native_case = copy.deepcopy(case)
        for branch in native_case["branch"]:
            branch[2] += 1e-8
            branch[3] += 1e-8
        row["modelDifference"] = "ANDES adds 1e-8 pu to line resistance and reactance."
    first = None
    for index in range(1, 11):
        file = folder / f"attempt-{index:02}.json"
        if not file.exists():
            row["attempts"].append({"attempt": index, "status": "missing"})
            continue
        try:
            attempt = json.loads(file.read_text(encoding="utf-8"))
            if attempt["attempt"] != index or not isinstance(attempt["converged"], bool):
                raise ValueError("Inconsistent attempt number or convergence flag")
        except (OSError, ValueError, TypeError, KeyError) as error:
            row["attempts"].append({"attempt": index, "status": "invalid", "error": str(error)})
            continue
        record = {key: value for key, value in attempt.items() if key not in ("bus", "branch")}
        row["attempts"].append(record)
        if not attempt["converged"]:
            continue
        try:
            voltages = {int(bus): (vm, va) for bus, vm, va in attempt["bus"]}
            if len(voltages) != len(attempt["bus"]):
                raise ValueError("Duplicate output bus IDs")
            if any(not (math.isfinite(vm) and math.isfinite(va)) for vm, va in voltages.values()):
                raise ValueError("Non-finite voltage in converged result")
            flows, mismatch = calculate(case, voltages)
            record["referenceMismatchPu"] = mismatch
            record["referenceVoltageTargetErrorPu"] = controlled_voltage_error(case, voltages)
            record["referenceResidualPassed"] = mismatch[0] <= 1e-8
            if native_case is not None:
                record["regularisedModelMismatchPu"] = calculate(native_case, voltages)[1]
            if attempt.get("branch") is not None:
                native = attempt["branch"]
                names = ("fromMw", "fromMvar", "toMw", "toMvar")
                record["nativeVsReferenceBranchFlows"] = {
                    name: max((abs(actual[column] - expected[column]), index)
                              for actual, (index, sf, st) in zip(native, flows, strict=True)
                              for expected in [(sf.real, sf.imag, st.real, st.imag)])
                    for column, name in enumerate(names)
                }
            if first is None:
                first = (voltages, flows)
        except (ValueError, TypeError, ArithmeticError) as error:
            record["comparisonError"] = str(error)
    return row, first


def differences(case, reference, measured):
    ref_voltage, ref_flows = reference
    voltage, flows = measured
    slack, = (int(bus[0]) for bus in case["bus"] if bus[1] == 3)
    slack_difference = voltage[slack][1] - ref_voltage[slack][1]
    result = {
        "voltagePu": max((abs(vm - ref_voltage[bus][0]), bus)
                         for bus, (vm, _) in voltage.items()),
        "angleDeg": max((abs(math.remainder(va - ref_voltage[bus][1] - slack_difference, 360)),
                         bus) for bus, (_, va) in voltage.items()),
    }
    for end, direction in enumerate(("from", "to"), 1):
        for field, units in (("real", "Mw"), ("imag", "Mvar")):
            result[direction + units] = max(
                (abs(getattr(actual[end] - expected[end], field)), actual[0])
                for actual, expected in zip(flows, ref_flows, strict=True))
    return result
