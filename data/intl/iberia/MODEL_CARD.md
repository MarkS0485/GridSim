# MODEL_CARD - Iberian peninsula (Spain + Portugal) - open transmission model

- buses: 1497 (ties merged: 0, star nodes: 0)
- branches: 2134 (series 2022, tx 112, oltcs 0)
- demand: 11591 MW over 1497 points (node 1497, bsp-aggregated 0, unmatched 0)
- embedded generation: 34839 MW capacity, 11591 MW dispatched, unmatched 0
- islands: 2 (largest 1411)
- slack: 2 island reference(s), 0 synthetic infeed

## buses by kV
- 380 kV: 450
- 220 kV: 1034
- 132 kV: 13

## buses by licence
- IB: 1497

## notes
- region=IB; source-derived open transmission model
- line impedances synthetic (per-km by voltage class) where the source lacks R/X - indicative, not planning-grade
- demand synthetic: national dispatch total distributed by bus degree
- geo box S36.00 W-9.80 N44.00 E3.50
- unplaced buses (parked at 0.5,0.5): 0
