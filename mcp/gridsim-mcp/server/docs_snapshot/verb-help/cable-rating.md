### cable-rating

`gridsim cable-rating <study.json> [--out <result.json>]`

Steady-state current rating of one single-core unarmoured cable laid direct in the ground, isolated from other cables
(RC3 P8): IEC 60287-1-1 (rating equation with n = 1, T2 = 0 and lambda2 = 0; AC resistance with the skin effect for xs
up to 2.8 and no proximity effect; dielectric losses) and IEC 60287-2-1 (T1, T3, and T4 of a single isolated buried
cable, `rho/(2 pi) ln(u + sqrt(u^2 - 1))`, `u = 2L/De`). Groups, ducts, troughs, soil drying, the screen loss factor
from the bonding arrangement and transient or cyclic ratings (IEC 60853) are not implemented, and no published ampacity
example has been checked here.

The study file is strict JSON, schema version 1: `schemaVersion` (1); `label`; `cable` (`material` `Copper` or
`Aluminium`, `dcResistance20OhmPerKm`, `skinEffectKs`, `conductorDiameterMm`, `insulationDiameterMm`,
`screenDiameterMm`, `overallDiameterMm`, `insulationThermalResistivity` and `oversheathThermalResistivity` in K.m/W,
`relativePermittivity`, `lossTangent`, `maxConductorTemperatureC`); `system` (`u0Kv`, `frequencyHz` 50 or 60);
`installation` (`depthToAxisM` to the cable axis, `soilThermalResistivity`, `ambientGroundTemperatureC`);
`screenLoss` (`lambda1` and its `source`); optional `nameplate` (`amperes` and `source`); `selection` (`None`,
`Calculated`, `Nameplate` or `Lower`).

The result gives every intermediate quantity (resistance at the maximum temperature, xs, ys, AC resistance,
capacitance, dielectric loss, T1, T3, u, T4, the permissible rise, the conductor and screen losses at the rating), the
calculated rating in A, the nameplate rating with its source as separate evidence, their ratio, and the selected rating
and its basis (none unless `selection` asks for one). An input outside the method's scope gives `status` `OUT OF SCOPE`
with every reason. Exit 0 `EVALUATED`, 3 `OUT OF SCOPE`, 1 for input it cannot accept, 2 for an invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |
