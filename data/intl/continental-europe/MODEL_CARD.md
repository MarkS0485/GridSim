# MODEL_CARD - Continental Europe synchronous area - open transmission model

- buses: 5824 (ties merged: 0, star nodes: 0)
- branches: 8079 (series 7397, tx 682, oltcs 0)
- demand: 202829 MW over 5824 points (node 5824, bsp-aggregated 0, unmatched 0)
- embedded generation: 587721 MW capacity, 202829 MW dispatched, unmatched 0
- islands: 4 (largest 5468)
- slack: 4 island reference(s), 1 synthetic infeed

## buses by kV
- 750 kV: 30
- 500 kV: 4
- 380 kV: 2244
- 300 kV: 483
- 220 kV: 3063

## buses by licence
- CE: 5824

## notes
- region=CE; source-derived open transmission model
- line impedances synthetic (per-km by voltage class) where the source lacks R/X - indicative, not planning-grade
- demand synthetic: national dispatch total distributed by bus degree
- geo box S35.00 W-10.00 N60.00 E30.00
- unplaced buses (parked at 0.5,0.5): 0
