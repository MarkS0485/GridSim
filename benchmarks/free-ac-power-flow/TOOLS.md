# Solver roster

| Path | Upstream distribution | Licence | Input route |
| --- | --- | --- | --- |
| GridSim Dense and Sparse | [GridSim](https://github.com/MarkS0485/GridSim) | GridSim source-available licence | MATPOWER .m via GridSim Core |
| MATPOWER | [MATPOWER 8.1](https://github.com/MATPOWER/matpower/releases/tag/8.1), [GNU Octave](https://octave.org/download) | BSD-3-Clause; Octave GPL | MATPOWER .m via loadcase |
| pandapower | [PyPI](https://pypi.org/project/pandapower/) | BSD-3-Clause | PYPOWER dictionary from the common .m input |
| VeraGrid | [PyPI](https://pypi.org/project/VeraGridEngine/) | MPL-2.0 | MATPOWER .m via VeraGridEngine |
| PowSyBl Open Load Flow | [PyPI](https://pypi.org/project/pypowsybl/) | MPL-2.0 | MATPOWER binary .mat derived from the common .m input |
| PyPSA | [PyPI](https://pypi.org/project/pypsa/) | MIT | PYPOWER dictionary from the common .m input |
| ANDES | [PyPI](https://pypi.org/project/andes/) | GPL-3.0 | MATPOWER case import |
| PowerModels.jl | [Julia package](https://github.com/lanl-ansi/PowerModels.jl) | BSD-3-Clause | MATPOWER .m via parse_file |

Other candidates include LightSim2Grid (an alternative pandapower
backend), ExaPF (a separate CPU/GPU solver), and GridLAB-D and OpenDSS
(distribution workflows). PowerWorld's free demonstration is limited to
13 buses. The first roster is bounded by the published balanced
transmission case and named solver paths.

ANDES 2.0.0 adds 1e-8 pu to series resistance and reactance internally
when forming its line admittance. The common-case residual therefore
uses different impedances from the ANDES-native residual. Keep and label
both comparisons. Disable ANDES PQ-to-impedance switching for this study.

PowSyBl Open Load Flow splits transformer shunt admittance across both
ends for the MATPOWER pi-model comparison. This includes branches whose
ends have different nominal voltages and whose tap ratio is unity.
