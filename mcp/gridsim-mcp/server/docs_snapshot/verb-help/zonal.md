### zonal

`gridsim zonal <pypsa-gb-zonal|folder>`

Show a PyPSA-GB zonal transport model: the bundled `pypsa-gb-zonal` (PyPSA-GB @ 8e084af, MIT, 20 zones and 37 links,
under `data/matpower/gb/pypsa-gb/zonal`) or a folder holding the same two files, `buses.csv`
(`name,y,x,v_nom,carrier`: zone name, latitude, longitude) and `links.csv` (`name,bus0,bus1,carrier,p_nom,p_min_pu`).
The headers must be exactly these. Prints the zones, every link with its capacity each way (p_nom forwards, -p_min_pu x
p_nom backwards; an interconnector to a foreign node is marked), and, when the folder has a `boundaries.json`, the
transfer capacity across each boundary cut: the bundled cuts B4, B6, B7 and B8 were derived from NESO's ETYS boundary
GIS lines (March 2025) by `tools/gb-reference/derive_boundaries.py`.

A zonal model has links with a MW capacity and no impedance, so it has no power flow. This verb only reports what the
files hold, and every verb that solves a network refuses `pypsa-gb-zonal` as an input error (`error: case
'pypsa-gb-zonal' cannot be solved: it is a zonal transport model ...`, exit 1).

Exit 0 when the model is shown; 1 when the files are missing or malformed (the message names the file, line and
column) or the argument is neither the bundled name nor a folder; 2 for a usage error.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the zones, links and
boundary capacities to a file ([Result files](#result-files)).
