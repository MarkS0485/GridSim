"""Isolated worker for external Python power-flow packages."""
import argparse
import copy
import importlib.metadata
import json
import math
import time
from pathlib import Path

from case_reference import load_case


def prepare_ppc(case_path):
    import numpy as np

    ppc = {"version": "2"}
    for name, value in load_case(case_path).items():
        ppc[name] = np.array(value) if isinstance(value, list) else value
    return ppc


def pandapower_adapter(case_path):
    import pandapower as pp
    from pandapower.auxiliary import LoadflowNotConverged
    from pandapower.converter.pypower import from_ppc

    ppc = prepare_ppc(case_path)

    def prepare():
        network = from_ppc(copy.deepcopy(ppc), f_hz=50, validate_conversion=False)

        def solve():
            try:
                pp.runpp(network, algorithm="nr", calculate_voltage_angles=True,
                         init="flat", max_iteration=50, tolerance_mva=1e-8 * ppc["baseMVA"],
                         enforce_q_lims=False, distributed_slack=False,
                         trafo_model="pi", lightsim2grid=False, run_control=False)
            except LoadflowNotConverged:
                return False
            return bool(network.converged)

        def extract():
            return network.res_bus[["vm_pu", "va_degree"]].reset_index().values.tolist()

        return solve, extract, lambda: network._ppc.get("iterations")
    return prepare


def pypsa_adapter(case_path):
    import pypsa

    ppc = prepare_ppc(case_path)

    def prepare():
        network = pypsa.Network()
        network.import_from_pypower_ppc(copy.deepcopy(ppc), overwrite_zero_s_nom=ppc["baseMVA"])
        network.transformers.loc[:, "model"] = "pi"
        result = {}

        def solve():
            result.update(network.pf(x_tol=1e-8, use_seed=False, distribute_slack=False))
            return bool(result["converged"].to_numpy().all())

        def extract():
            vm = network.buses_t.v_mag_pu.iloc[0]
            va = network.buses_t.v_ang.iloc[0]
            return [[int(bus), value, math.degrees(va[bus])] for bus, value in vm.items()]

        return solve, extract, lambda: int(result["n_iter"].to_numpy().max())
    return prepare


def veragrid_adapter(case_path):
    import VeraGridEngine as vg

    source = load_case(case_path)
    bus_types = {int(bus[0]): bus[1] for bus in source["bus"]}
    options = vg.PowerFlowOptions(
        solver_type=vg.SolverType.NR, retry_with_other_methods=False,
        tolerance=1e-8, max_iter=50, control_q=False, control_taps_modules=False,
        control_taps_phase=False, control_remote_voltage=False, distributed_slack=False,
        use_stored_guess=True, initialize_angles=False, apply_temperature_correction=False)

    def prepare():
        network = vg.open_file(str(case_path))
        for bus in network.buses:
            bus.Va0 = 0
            if bus_types[int(bus.code)] == 1:
                bus.Vm0 = 1
        result = None

        def solve():
            nonlocal result
            result = vg.power_flow(network, options=options)
            return bool(result.converged)

        def extract():
            return [[int(bus.code), abs(v), math.degrees(math.atan2(v.imag, v.real))]
                    for bus, v in zip(network.buses, result.voltage, strict=True)]

        return solve, extract, lambda: result.iterations
    return prepare


def powsybl_adapter(case_path, output):
    import pypowsybl as pp

    from scipy.io import savemat

    converted = output / (case_path.stem + ".mat")
    savemat(converted, {"mpc": prepare_ppc(case_path)}, format="5")
    power_mismatch = str(load_case(case_path)["baseMVA"] * 1e-8)
    options = pp.loadflow.Parameters(
        voltage_init_mode=pp.loadflow.VoltageInitMode.UNIFORM_VALUES,
        transformer_voltage_control_on=False, use_reactive_limits=False,
        phase_shifter_regulation_on=False, shunt_compensator_voltage_control_on=False,
        distributed_slack=False, read_slack_bus=True, twt_split_shunt_admittance=True,
        provider_parameters={"maxNewtonRaphsonIterations": "50",
                             "newtonRaphsonStoppingCriteriaType": "PER_EQUATION_TYPE_CRITERIA",
                             "maxActivePowerMismatch": power_mismatch,
                             "maxReactivePowerMismatch": power_mismatch})

    def prepare():
        network = pp.network.load(str(converted))
        result = None

        def solve():
            nonlocal result
            result = pp.loadflow.run_ac(network, parameters=options, provider="OpenLoadFlow")
            return all(component.status == pp.loadflow.ComponentStatus.CONVERGED for component in result)

        def extract():
            buses = network.get_bus_breaker_view_buses()
            nominal = network.get_voltage_levels().nominal_v
            return [[int(bus.removeprefix("BUS-")), row.v_mag / nominal[row.voltage_level_id], row.v_angle]
                    for bus, row in buses.iterrows()]

        return solve, extract, lambda: max(component.iteration_count for component in result)
    return prepare


def andes_adapter(case_path):
    import andes

    def prepare():
        network = andes.load(str(case_path), setup=True, default_config=True,
                             no_output=True, config_option=["PQ.pq2z=0"])
        network.PFlow.config.tol = 1e-8
        network.PFlow.config.max_iter = 50
        network.PFlow.config.report = 0

        def solve():
            network.PFlow.run()
            return bool(network.PFlow.converged)

        def extract():
            return [[int(bus), float(vm), math.degrees(va)]
                    for bus, vm, va in zip(network.Bus.idx.v, network.Bus.v.v,
                                           network.Bus.a.v, strict=True)]

        return solve, extract, lambda: network.PFlow.niter
    return prepare


ADAPTERS = {
    "andes": ("andes", andes_adapter),
    "veragrid": ("VeraGridEngine", veragrid_adapter),
    "powsybl": ("pypowsybl", powsybl_adapter),
    "pandapower": ("pandapower", pandapower_adapter),
    "pypsa": ("pypsa", pypsa_adapter),
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("tool", choices=ADAPTERS)
    parser.add_argument("case", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    package, adapter = ADAPTERS[args.tool]
    version = importlib.metadata.version(package)
    iteration_limit = 100 if args.tool == "pypsa" else 50
    args.output.mkdir(parents=True, exist_ok=True)
    if args.tool in ("pandapower", "pypsa"):
        import numpy as np
        np.savez(args.output / "converted-ppc.npz", **prepare_ppc(args.case))
    prepare = adapter(args.case, args.output) if args.tool == "powsybl" else adapter(args.case)
    warmup, _, _ = prepare()
    warmup()
    records = []
    for attempt in range(1, 11):
        solve, extract, iterations = prepare()
        start = time.perf_counter_ns()
        converged = solve()
        elapsed_ms = (time.perf_counter_ns() - start) / 1e6
        count = iterations()
        records.append(dict(attempt=attempt, elapsedMs=elapsed_ms, converged=converged,
                            iterations=int(count) if count is not None else None,
                            bus=extract() if converged else None,
                            version=version, iterationLimit=iteration_limit))
    for row in records:
        name = f"attempt-{row['attempt']:02}.json"
        (args.output / name).write_text(json.dumps(row, allow_nan=False), encoding="utf-8")


if __name__ == "__main__":
    main()
