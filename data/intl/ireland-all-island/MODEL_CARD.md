# MODEL_CARD - Ireland all-island (EirGrid + SONI) - open transmission model

- buses: 60 (ties merged: 0, star nodes: 0)
- branches: 82 (series 77, tx 5, oltcs 0)
- demand: 2367 MW over 60 points (node 60, bsp-aggregated 0, unmatched 0)
- embedded generation: 9942 MW capacity, 2367 MW dispatched, unmatched 0
- islands: 1 (largest 60)
- slack: 1 island reference(s), 0 synthetic infeed

## buses by kV
- 380 kV: 4
- 220 kV: 56

## buses by licence
- IE: 60

## notes
- region=IE; source-derived open transmission model
- line impedances synthetic (per-km by voltage class) where the source lacks R/X - indicative, not planning-grade
- demand synthetic: national dispatch total distributed by bus degree
- geo box S51.30 W-10.80 N55.60 E-5.00
- unplaced buses (parked at 0.5,0.5): 0
