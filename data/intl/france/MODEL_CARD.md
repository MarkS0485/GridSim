# MODEL_CARD - France (RTE) - open transmission model

- buses: 1976 (ties merged: 0, star nodes: 0)
- branches: 2618 (series 2376, tx 242, oltcs 0)
- demand: 94767 MW over 1976 points (node 1976, bsp-aggregated 0, unmatched 0)
- embedded generation: 187128 MW capacity, 94767 MW dispatched, unmatched 0
- islands: 2 (largest 1971)
- slack: 2 island reference(s), 1 synthetic infeed

## buses by kV
- 380 kV: 694
- 300 kV: 14
- 220 kV: 1264
- 132 kV: 4

## buses by licence
- FR: 1976

## notes
- region=FR; source-derived open transmission model
- line impedances synthetic (per-km by voltage class) where the source lacks R/X - indicative, not planning-grade
- demand synthetic: national dispatch total distributed by bus degree
- geo box S41.00 W-5.50 N51.50 E9.80
- unplaced buses (parked at 0.5,0.5): 0
