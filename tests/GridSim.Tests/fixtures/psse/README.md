# PSS/E reader fixtures

| File | What it is | Source and licence |
|---|---|---|
| `case14.raw`, `case39.raw`, `case118.raw` | The IEEE 14, 39 (New England) and 118-bus cases written as PSS/E RAW revision 33 by GridSim's `PsseRawWriter`, from the MATPOWER files (case14 and case118 from `data/matpower/lib`, case39 from `case39.m` here) read at 60 Hz. `PsseRawParserTests` checks that the writer still writes exactly these bytes and that each reads back to the MATPOWER case bit for bit. | Generated from MATPOWER data (3-clause BSD, see `data/matpower/LICENSE-MATPOWER.txt`). |
| `case39.m` | MATPOWER's IEEE 39-bus (New England) case, unchanged. | MATPOWER `data/case39.m`, 3-clause BSD (`data/matpower/LICENSE-MATPOWER.txt`). |
| `t_case9_save2psse.raw`, `t_case9_save2psse.m` | MATPOWER's own test pair: the 9-bus case written by MATPOWER's `save2psse` (revision 33) and the MATPOWER case it came from. A third-party RAW file. | MATPOWER `lib/t/`, 3-clause BSD (`data/matpower/LICENSE-MATPOWER.txt`). |
| `t_psse_case3.raw` | MATPOWER's copy of the PSS/E sample case in revision 30 layout, written by PSS/E itself. Read since revision 30 support (October 2026); `PsseRev30AndSeqTests` checks its bus shunts, switched shunts and metadata value by value. | MATPOWER `lib/t/`, 3-clause BSD (`data/matpower/LICENSE-MATPOWER.txt`). |
| `sample-v35.raw` | A small network hand-written in the revision 35 layout, holding every section (system-wide data, switching devices, a three-winding transformer, an impedance correction table, substation data). Its expected values are worked out by hand in `PsseRawParserTests`. | Written for GridSim. |
| `ukgds-style-v30.raw`, `ukgds-style-v30.seq` | A small 132/33/11 kV network hand-written the way the UKGDS distribution cases are laid out: revision 30, tab separated, `@!` heading lines, padding tabs, `/` fillers, no REV field, Q at the end, CR LF line ends and a cp1252 curly quote round NAME in one heading (kept byte for byte by `.gitattributes`). The SEQ file holds every sequence section, with connection codes 2 and 3, a neutral grounding impedance and a record that runs the other way. Expected values are worked out by hand in `PsseRev30AndSeqTests`. | Written for GridSim. |
| `ukgds-style-v33.raw` | The same network in the revision 33 layout; it must read to the same model bit for bit. | Written for GridSim. |

The UKGDS cases themselves (github.com/sedg/ukgds, `raw/*.raw` and `*.seq`) are not committed: no licence has been
issued for them. `PsseRev30AndSeqTests` reads them from the folder named by `GRIDSIM_UKGDS_DIR` when it is set.
