### emt3

`gridsim emt3 <study.json> [--out <file>] [--json <file>] [--csv <file>]`

Three-phase electromagnetic transients on the RC4 nodal solver (Core `GridSim.Core.Dynamics.Emt.Nodal`, `Nonlinear` and
`FrequencyDependent`), in SI units with a fixed step and the trapezoidal rule. It is new alongside `emt` and `emt-line`, which are
unchanged. `<study.json>` is a strict `gridsim-emt3/1` document (an unknown key, a missing key or a wrong type exits 1 naming it).
A bus has three nodes, one per phase. Keys: `time_step_s`, `duration_s`, `sample_every` (default 1), `frequency_hz` (default
50), `integration` (`trapezoidal` or `backward-euler`), `event_rule` (`trapezoidal` or `backward-euler-step`: one backward-Euler step
at each switching event), `newton` (`max_iterations` 100, `voltage_abs_tol` 1e-9, `voltage_rel_tol` 1e-10, `residual_abs_tol`
1e-9, `residual_rel_tol` 1e-10, `max_backtracks` 30), `buses` (names), and the element lists, all optional but `outputs`:

- `sources`: `name`, `bus`, `v_ll_rms` (V), `frequency_hz`, `angle_deg`, `rs_ohm`, `ls_h` (a balanced wye source behind a series R-L;
  both zero gives an ideal source, whose current output is not measured);
- `branches`: `name`, `from`, `to`, `r1_ohm`, `l1_h`, `r0_ohm`, `l0_h` (total sequence impedance of a series three-phase branch; the
  zero-sequence values default to the positive ones);
- `shunts`: `name`, `bus`, `r_ohm`, `c_f`, `l_h` (wye to ground, per phase);
- `lines`: `name`, `from`, `to`, `length_km`, `model` (`bergeron` or `fd`), `geometry` (an inline `gridsim-lineconstants/1` document
  for a transposed three-phase line, see `line-constants`), and for `fd` `band_hz` ([low, high], default [1, 10000]), `order` (real
  poles, default 8), `samples` (default 80), `iterations` (default 20), `enforce_passivity` (default true);
- `transformers`: `name`, `primary_bus`, `secondary_bus`, `r1_ohm`, `ll1_h`, `r2_ohm`, `ll2_h`, `core_loss_ohm`, `curve` (the
  magnetising current-flux points `[flux_wb, current_a]`, positive branch, increasing), `residual_flux_wb` (one number or three);
- `saturable_inductors`: `name`, `bus`, `phases` (letters of `abc`, default all), `curve`, `initial_flux_wb`;
- `arresters`: `name`, `bus`, `phases`, `model` (`power-law` with `vref_v`, `iref_a`, `alpha`; or `piecewise` with `points`
  `[volts, amps]`);
- `switches`: `name`, `from`, `to` (omit for a switch to ground), `phases`, `ron_ohm`, `roff_ohm`, `closed`, `events`
  (`at_s`, `close`);
- `faults`: `name`, `bus`, `phases`, `r_ohm`, `to_ground` (default true), `at_s`, `clear_s`;
- `machines`, `dc_buses`, `converters` and `hvdc` (optional, see below);
- `outputs`: `name`, `kind` (`voltage` of a bus, `current` of an element, `flux` of a saturable device, for a machine `speed`,
  `torque`, `rotor-angle` and `field-current`, for a DC bus `dc-voltage-positive` and `dc-voltage-negative`, for an average converter
  `dc-current`, which also names the current of an LCC bridge, a DC line and a station's DC supply), `target`, `phase` (`a`, `b` or
  `c`; required for `voltage`, `current` and `flux`, refused for the machine, DC bus and `dc-current` kinds) and, for a line current or
  the `dc-current` of a DC line, `end` (`from` or `to`). The `name` is the trace column and, as `max_abs_<name>`, a summary
  key, so it must be lower-case snake_case (a letter, then letters, digits or underscores) and may not be `time_s`; anything else
  exits 1 before the run. Kind names added to `gridsim-emt3/1` are lower-case words joined by hyphens, as the other enumerated
  values are (`power-law`, `backward-euler`); trace columns and summary keys are always snake_case.

`machines` entries (the Core R4-17 machine models; a document without the key runs exactly as before). Every entry has `name`, `kind`
(`synchronous` or `induction`), `bus`, `rating_mva`, `rated_kv_ll` (kV), `parameters` and optionally `frequency_hz` (default the
study frequency). A machine's `current` output takes a phase and is the line current of the phase delivered to the network for a
synchronous machine (generator convention) and absorbed from it for an induction machine (motor convention). The machine outputs
are `speed` (pu of synchronous speed), `torque` (electromagnetic, pu of the machine base), `rotor-angle` (load angle, rad,
synchronous only) and `field-current` (pu, synchronous only); they take no `phase`. Angles in a machine entry use the source
convention: phase a is `sin(w t + angle_deg)`.

- `kind` `synchronous`: a dq machine with field and damper windings, no saturation. `neutral` is `grounded` (default) or `isolated`.
  `parameters` are the RMS data set on the machine base: `inertia_s`, `damping_pu` (default 0), `xd_pu`, `xdp_pu`, `xdpp_pu`, `xq_pu`,
  `xqp_pu`, `xqpp_pu`, `tdop_s`, `tdopp_s`, `tqop_s`, `tqopp_s`, `xl_pu` (leakage; 0 or absent takes half the smaller subtransient
  reactance), `ra_pu` (default 0) and `x0_pu`. `initial` is `{"mode": "no-load", "v_pu", "angle_deg"}` (default 1 pu at angle 0, no
  current) or `{"mode": "phasor", "v_pu", "angle_deg", "p_mw", "q_mvar"}`: the machine starts in the steady state that delivers that
  power at that terminal voltage, with the field voltage and mechanical torque that balance it (the result's `machines` table lists
  them). The network must hold the terminal voltage at that value for the start to be a steady state; a source or a line has no initial
  current, so a machine behind a line starts with a transient. `torque_steps` and `field_voltage_steps` are lists of `{"at_s", "delta_pu"}`:
  from `at_s` the input is its initial balanced value plus `delta_pu`.
- `kind` `induction`: a single-cage machine in the stationary frame, three-wire (no `neutral`). `parameters`: `ra_pu`, `xa_pu`,
  `xm_pu`, `r1_pu`, `x1_pu`, `inertia_s`. `start_speed_pu` (default 0: a direct start from standstill with zero flux) and
  `load_torque` `{"k_pu", "speed_exponent"}` (shaft load `k_pu * w^speed_exponent` in pu of rated torque, exponent default 2; absent means
  no load).

Core refusals (a machine whose data cannot form a circuit, for example X'd not below Xd) exit 1 with the Core's own text. With machines
present the summary adds `comparator_status` (`COMPARATOR-UNVERIFIED`) and the result has a `machines` table: `machine`, `kind`,
`initial_load_angle_rad`, `initial_field_voltage_pu`, `initial_field_current_pu`, `initial_mechanical_torque_pu`, `initial_id_pu`,
`initial_iq_pu`, `start_speed_pu` (empty where a kind has no such quantity).

`dc_buses` and `converters` (optional; the Core R4-17 power-electronics models on the nodal solver). A DC bus is a split DC link:
`name`, `capacitance_f` (the series value of the two equal capacitors), `initial_v` (pole to pole), `supply_v` (pole to pole, default 0
for no supply) and `supply_ohm` (the supply's Thevenin resistance, default 0 for an ideal supply). Its rails are `<name>.p` and
`<name>.n` about a grounded mid-point; the outputs `dc-voltage-positive` and `dc-voltage-negative` give each rail to the mid-point in
volts (the pole-to-pole voltage is their difference). A converter is a three-phase two-level voltage-source converter between an AC `ac_bus`
and a `dc_bus`: `name`, `kind`, `rs_ohm` (default 1e-3), `nominal_vdc_v` (default the DC bus `initial_v`), an optional `filter`
(`r_ohm`, `l_h`: a series R-L from the converter terminals to the AC bus) and exactly one of `modulation` and `control`.

- `kind` `average`: the average-value model, a controlled voltage source `m Vdc/2` behind `rs_ohm` in each leg with no switching ripple;
  the DC current follows from the power balance and the DC voltage is the one of the previous step.
- `kind` `switched`: a bridge of gated diodes with `rs_ohm` as the on resistance and `roff_ohm` (default 1e7) as the off resistance,
  gated by a sine PWM `pwm` (`carrier_hz`, `carrier_phase` as a fraction of a carrier period, default 0; required). Switching instants
  are quantised up to the step grid, with no interpolation and no dead time; the summary states this as `switching_timing`.
- `modulation`: an open-loop reference `index * sin(2 pi f t + phase_deg - 120 deg k)` for phase k (`frequency_hz` default the study
  frequency, `phase_deg` default 0), limited to plus or minus 1.
- `control`: the sampled dq current controller, which needs a `filter`: `bandwidth_rad_s` (Kp = alpha L, Ki = alpha R on the filter),
  `control_every` (solver steps per control sample, default 1), `frequency_hz`, `phase_deg` (the d axis lies on the voltage of a
  source with that `angle_deg`), `id_a` and `iq_a` (initial references, amperes peak) and `steps` (`at_s`, `id_a`, `iq_a`).

A `current` output of a converter is the filter current of the phase (converter to bus) or, for an unfiltered average converter, the leg
current; an unfiltered switched converter has no single current and the output is refused. The result gains the `converters` table
(`converter`, `model_class` `AverageValue` or `Switched`, `description`) and the summary keys `switching_timing` and
`comparator_status`. A load that needs a floating star point cannot be written in a study (a branch load returns to a bus, and a
grounded bus gives the star point a ground path); the verified cases use a grounded star point. The Core grid-following and
grid-forming inverter models live in the older phase-domain engine and are not on the nodal solver, so they are not available here.

`hvdc` (optional; the Core R4-19 HVDC models, a link described as entries of three kinds; every converter is listed in the `converters`
table). Each station and bridge carries its own AC source, so an `hvdc` entry needs no AC bus of the study (`buses` is still required and may be an empty list; a bus with no element would leave its nodes floating and the matrix singular).

- `kind` `vsc`: a VSC station, the Core `VscHvdcStation`: an ideal grid source (`grid_v_ll_rms`, `frequency_hz`, `grid_angle_deg`) behind
  `grid_r_ohm` and `grid_l_h`, the converter reactor `filter_r_ohm` and `filter_l_h`, a converter (`converter`: `average`, the two-level
  average-value model with `converter_rs_ohm`, default 1e-3; `mmc-average` or `mmc-detailed`, with `mmc`: `submodules_per_arm`,
  `submodule_cap_f`, `arm_resistance_ohm`, `arm_inductance_h`), a DC link (`dc_nominal_v` pole to pole, `dc_cap_f`, `initial_dc_v` default
  `dc_nominal_v`), the measurement, the dq current controller (`current_bandwidth_rad_s` 1000, `control_every` 2, `current_limit_a`
  1e6, `reactive_current_a` 0) and the outer loop: `mode` `power` (`set_point` is the active power injected into the grid at the PCC in
  watts, negative when drawn) or `dc-voltage` (`set_point` is the pole-to-pole voltage in volts), `outer_bandwidth_rad_s` 60. For an MMC,
  `suppression_bandwidth_rad_s` (default 0) enables the circulating-current control. `dc_supply_v` adds a stiff DC supply of that
  voltage pole to pole, each pole behind `dc_supply_ohm` (default 0, an ideal supply as the Core MMC cases use; the current of an ideal
  source is not measured, so give a small resistance to read it). Outputs: `dc-voltage-positive` and `dc-voltage-negative` (each pole of the station's DC side to
  the mid-point), `current` with a phase (the reactor current, converter to grid) and, with a `dc_supply_v` and a `dc_supply_ohm` above zero, `dc-current` (the supply's current).
- `kind` `lcc`: a 6- or 12-pulse thyristor bridge group, the Core `LccBridgeEmt`, with its own transformer secondary sources
  (`v_ll_rms`, `frequency_hz`, `source_r_ohm` 1e-3, `commutation_l_h`, `pulses` 6 or 12, `valve` `ron_ohm` 1e-3 and `roff_ohm` 1e7,
  `neutral_reference_ohm` 1e6), a firing delay `alpha_deg` with optional `alpha_steps` (`at_s`, `alpha_deg`), into a series R-L DC `load`
  (`r_ohm`, `l_h`). Outputs: `dc-voltage-positive` and `dc-voltage-negative` and `dc-current` (the load current).
- `kind` `dc-line`: `from` and `to` (two `vsc` stations of the study), `r_ohm` (the loop resistance, half in each pole at the `from` end),
  `surge_ohm`, `delay_s` (the travel time) and `initial_current_a` (default 0): a Bergeron line per pole, started in the steady state that
  carries that current from `from` to `to` at the stations' initial DC voltages. Output: `dc-current` with `end` `from` or `to`.

A refusal of the Core (a pulse count other than 6 or 12, a DC voltage set point that is not positive, a line shorter than the step) exits 1
with the Core's own text. The grid-forming and grid-following inverters, a DC line with sheath or frequency dependence and multi-terminal
DC grids are not available. A `partition` key (parallel runs cut at Bergeron lines) is not offered: the Core partitioned engine cuts only
single-conductor Bergeron lines of a network built per partition, while a study builds one network of three-phase buses whose lines are modal
(`bergeron`, `fd`) and cannot be cut, so a faithful `partition` key would need a new three-phase cuttable line element. The
partitioned engine has its own verb, [`emt-partition`](#emt-partition), on a single-conductor network; [`cosim`](#cosim) runs an
emt3 study against an RMS phasor side.

An `fd` line is fitted for the study's step: the delay removed from the propagation function is the group delay at the top of the band
rounded down to whole steps, and the step must not exceed the shortest line travel time. The fit is refused (exit 1) when the fitted
characteristic admittance or propagation function is not passive and a perturbation of at most 5 per cent cannot correct it. The
result gives, per line and mode, the fit error (maximum and rms, separately for the characteristic admittance and the propagation
function), the delay and any passivity correction. Nothing is compared with PSCAD or EMTP (COMPARATOR-UNVERIFIED); chapter 16 of the
manual lists what is and is not verified.

Exit 0 when the run completed; 1 when the file is missing or invalid, or the network or a line fit is refused; 2 for a usage error;
3 when the run ended with a named status other than completed (the Newton iteration did not converge, or the nodal matrix was
singular): the traces up to the last good step are still written and the status and time are in the summary.

| Flag | Default | Meaning |
|---|---|---|
| `--out` / `--json` / `--csv` | - | Write the result through the shared [result writer](#result-files): the summary (status, step, method, factorisation and Newton counts and tolerances, the largest absolute value of each output) and the tables `line_fits` (one row per line and mode) and `traces` (`time_s` and one column per output). |
