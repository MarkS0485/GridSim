# Inertia placement study: can GB run on less inertia if it is in the right places?

Study run 2026-10-25 on `gb-full` (3,539 buses, 5,918 branches) with `gridsim inertia-placement`, GridSim 2.0.151,
every placement in one run of one build (252 RMS runs) plus three sensitivity runs. All numbers below are the tool's
own output, rounded only for the tables. The RMS
model behind them is indicative (synthetic machine data, see Limits), so read the numbers as the shape of the answer,
not as operating limits.

## The question

Two positions on inertia:

1. **Operational:** the grid needs a minimum of about 240 GVA.s.
2. **Control room:** for a human in the loop and a non-startle environment, the frequency must take 3 to 5 seconds to
   reach the operating limit after the largest loss, which needs more than about 700 GVA.s.

The question is whether putting the synchronous inertia in ideal places lets the system meet either position with less
of it.

The starting hypothesis: placement cannot change the centre-of-inertia (system) frequency, whose RoCoF is
`f0 x dP / (2 E)` with no location in it, but it can shrink the extra RoCoF that buses far from the inertia see, the
spatial penalty `K = worst bus RoCoF / system RoCoF`. So it should lower a floor set by regional effects (thought to be
the 240) more than one set by the system frequency (the 700).

## Method

- **Network and dispatch fixed.** The `gb-full` case as it is, its dispatch and its power flow. Today's synchronous
  fleet is 242.233 GVA.s (950 machines). Every run keeps a quarter of it (60.558 GVA.s) on the fleet itself, each
  machine's H scaled, nothing moved.
- **The rest of each budget is synchronous condensers** (H 8 s, a high-inertia condenser with a flywheel), at no
  power and no reactive power at the solved state, keeping a unit bus's voltage setpoint, so the power flow stays the
  case's (largest bus voltage move 2.3e-11 pu, from the microvar each way each condenser adds to its bus's reactive
  limits). Each condenser is the same six-state GENROU machine as the fleet (damper windings, T''do 0.03 s, T''qo
  0.05 s, D = 0 as is usual for GENROU) with an IEEE type 1 exciter.
  Every placement adds the same total inertia and the same total condenser MVA; only where they sit changes.
- **Placements:**
  - `fleet`: where today's synchronous inertia is, in proportion to it (today's geography, scaled up);
  - `demand`: in proportion to demand, gathered to the nearest 275/400 kV bus;
  - `spine-t`: equal shares at eleven sites forming the inverted T / ladder discussed: Beauly (Inverness),
    Bonnybridge (central belt), Galashiels (Borders), Keadby (east corridor), Capenhurst (west corridor), Hams Hall
    (Midlands), Indian Queens (Cornwall), Bridgewater (South West), St Johns Wood (Thames), Canterbury (Kent) and
    Bramford (East Anglia), each the nearest 275/400 kV bus to its site (0.4 to 8.9 miles);
  - `optimised`: the same sites, weights from one pass of a coordinate search at 240 GVA.s (weights found: central
    belt, east and west corridors, Midlands and South West doubled; Borders and Thames halved; the rest unchanged;
    worst bus RoCoF 0.21513 to 0.21163 Hz/s, 1.6 % better than equal shares);
  - `south`: everything at St Johns Wood, a deliberately concentrated contrast.
- **Losses:** 1.8 GW (the SQSS infeed loss) at the 275/400 kV bus nearest Sizewell (Bramford), Hinkley Point
  (Bridgewater), the Kent interconnectors (Sellindge) and Torness (Dunbar, Scotland over B6), at t = 1 s.
- **Engine:** the RMS engine with six-state GENROU machines (synthetic data), IEEE type 1 exciters on every machine and
  condenser, mechanical power held (no frequency response), 5 ms step, 6 s after the loss. Every bus at 132 kV and
  above (2,597 buses) is measured: frequency from its voltage angle over one cycle, RoCoF over 500 ms (the GB
  loss-of-mains window), with the angle jump at the loss taken out and reported on its own.
- **Scan:** budgets 120, 180, 240, 360, 480, 700, 900 and 1,200 GVA.s; 5 placements x 8 budgets x 4 losses = 160 RMS
  runs (about 10 s each), plus 92 runs for the optimisation. Every run stayed in synchronism; no converter iteration
  hit its cap.

**What the machines feel is not exactly 1.8 GW.** The RMS loads are constant admittances and the converter plant holds
its current, so as the voltage sags both change their power: the loss the machines felt (`2 E RoCoF / f0`) ranged from
1,764 to 2,386 MW. So the regional figures are scaled to the full loss: the full-loss worst bus RoCoF is K times the
swing-equation system RoCoF `f0 x 1.8 GW / (2 E)`. The system frequency itself (criterion B) is taken from the swing
equation, which is placement-independent by construction; the RMS runs show how far that holds in a network (below).

## Results

### 1. Regional penalty K (worst bus RoCoF / system RoCoF), worst of the four losses

| Budget GVA.s | fleet | demand | spine-t | optimised | south |
|---|---|---|---|---|---|
| 120 | 1.035 | 1.078 | 1.083 | 1.085 | 1.082 |
| 180 | 1.041 | 1.102 | 1.090 | 1.092 | 1.561 |
| 240 | 1.154 | 1.112 | 1.147 | 1.129 | 2.103 |
| 360 | 1.430 | 1.409 | 1.296 | 1.271 | 3.237 |
| 480 | 1.623 | 1.567 | 1.471 | 1.442 | 4.390 |
| 700 | 2.165 | 2.010 | 1.715 | 1.670 | 6.515 |
| 900 | 2.543 | 2.305 | 1.848 | 1.951 | 8.450 |
| 1200 | 2.997 | 2.684 | 2.057 | 2.349 | 11.351 |

The worst loss is almost always Scotland over B6. With today's geography (fleet) and with demand-weighted placement
the worst bus for that loss is in the far north (Peterhead Grange 275 kV, latitude 57.5); with the T sites it is at
the far ends of the T (Indian Queens at 240 GVA.s, Beauly at 700); with everything in London it is in Yorkshire
(Bramham 132 kV).

### 2. Full-loss worst bus RoCoF (Hz/s), worst loss, against the system figure

| Budget GVA.s | system (swing) | fleet | demand | spine-t | optimised | south |
|---|---|---|---|---|---|---|
| 120 | 0.3750 | 0.3882 | 0.4043 | 0.4059 | 0.4068 | 0.4057 |
| 180 | 0.2500 | 0.2604 | 0.2754 | 0.2726 | 0.2731 | 0.3902 |
| 240 | 0.1875 | 0.2164 | 0.2085 | 0.2151 | 0.2116 | 0.3943 |
| 360 | 0.1250 | 0.1788 | 0.1762 | 0.1621 | 0.1589 | 0.4047 |
| 480 | 0.0938 | 0.1521 | 0.1469 | 0.1379 | 0.1352 | 0.4116 |
| 700 | 0.0643 | 0.1392 | 0.1292 | 0.1103 | 0.1074 | 0.4188 |
| 900 | 0.0500 | 0.1272 | 0.1153 | 0.0924 | 0.0975 | 0.4225 |
| 1200 | 0.0375 | 0.1124 | 0.1007 | 0.0771 | 0.0881 | 0.4257 |

### 3. Smallest budget meeting each criterion (GVA.s, linear between scanned budgets)

| Criterion | fleet | demand | spine-t | optimised | south |
|---|---|---|---|---|---|
| A: worst bus RoCoF <= 0.125 Hz/s | 944.1 | 760.6 | 582.7 | 560.8 | not met (> 1200) |
| A: worst bus RoCoF <= 0.25 Hz/s | 194.1 | 202.8 | 203.6 | 202.6 | not met (> 1200) |
| A: worst bus RoCoF <= 0.5 Hz/s | <= 120 | <= 120 | <= 120 | <= 120 | <= 120 |
| A: worst bus RoCoF <= 1 Hz/s | <= 120 | <= 120 | <= 120 | <= 120 | <= 120 |
| B: >= 3 s to 49.8 Hz, no response | 675.0 | 675.0 | 675.0 | 675.0 | 675.0 |
| B: >= 5 s to 49.8 Hz, no response | 1125.0 | 1125.0 | 1125.0 | 1125.0 | 1125.0 |

Criterion B matches the closed form exactly: the time to fall 0.2 Hz at `f0 x dP / (2E)` is 3 s at
`E = 3 x 50 x 1800 / 0.4 = 675,000 MW.s` and 5 s at 1,125,000 MW.s, whatever the placement.

With the default GB frequency response (lumped model, placement-independent) the time to 49.8 Hz is 1.112 s at 240
GVA.s, 3.313 s at 700 and 5.806 s at 1,200: 3 s needs about 635 GVA.s and 5 s about 1,040. The nadir is 49.261 Hz at
240 GVA.s and 49.488 Hz at 700.

### 4. Does placement move the system frequency?

Across the four spread placements (fleet, demand, spine-t, optimised), the RMS system RoCoF for the same budget and
loss differs by at most 3.77 %: the system frequency is close to placement-independent, as the swing equation says.
With everything in London it differs by up to 29.2 %, through the voltage: concentrated inertia leaves the far
network's voltage less supported, so the voltage-dependent loads and converters change the loss the machines feel.

### 5. Angle jump and the north-south swing

The largest voltage angle jump at the loss (Dunbar, beside the Scotland loss) falls from about 3.8 to 4.0 degrees at
120 GVA.s to 2.41 to 2.70 degrees at 1,200 for the spread placements (spine-t lowest), and stays at 4.25 degrees with
everything in London. None comes near a vector-shift setting.

The north-south oscillation after the Scotland loss: the difference between the north (latitude 55.3 and above) and
south (52.3 and below) machines' mean speeds, smoothed over 0.4 s to average out local machine swings, with the damping
fitted over every half-cycle in the 6 s (half-cycles fitted in brackets). Damping ratio at frequency:

| Budget GVA.s | fleet | demand | spine-t | optimised | south |
|---|---|---|---|---|---|
| 120 | 0.181 at 0.95 Hz (10) | 0.177 at 0.95 Hz (10) | 0.150 at 0.90 Hz (9) | 0.154 at 0.90 Hz (10) | 0.124 at 1.08 Hz (12) |
| 240 | 0.134 at 0.85 Hz (9) | 0.131 at 0.84 Hz (9) | 0.100 at 0.73 Hz (8) | 0.105 at 0.75 Hz (8) | 0.067 at 1.13 Hz (12) |
| 480 | 0.095 at 0.71 Hz (7) | 0.091 at 0.71 Hz (7) | 0.062 at 0.57 Hz (6) | 0.066 at 0.59 Hz (6) | 0.040 at 1.16 Hz (13) |
| 700 | 0.077 at 0.63 Hz (6) | 0.072 at 0.63 Hz (6) | 0.046 at 0.49 Hz (5) | 0.049 at 0.51 Hz (5) | 0.032 at 1.17 Hz (13) |
| 1200 | 0.055 at 0.51 Hz (5) | 0.048 at 0.52 Hz (5) | 0.028 at 0.38 Hz (4) | 0.030 at 0.40 Hz (4) | 0.025 at 1.18 Hz (13) |

Every one of the 160 runs reads positive damping. (An earlier two-peak measure, 2.0.143 to 2.0.150, read negative
damping in some runs, for example fleet -0.085 at 2.47 Hz at 120 GVA.s: it had caught a local swing, not the
north-south one. The condenser model was checked at the same time: its damping comes from its damper windings, and
holding the field instead of the exciter moves the measured damping by under 0.004, so neither the condenser nor the
exciter was the cause.)

The trend is physics, not model error: more inertia slows the swing and the damper windings damp a slower swing less,
and putting inertia at both ends of the country (spine-t, optimised) makes a slower two-area swing (0.49 Hz against
0.63 Hz at 700 GVA.s) that is less damped (0.046 against 0.077). The RMS runs carry no governors, no power system
stabilisers and no load frequency damping, so the absolute damping is pessimistic; but a T layout would want
stabilisers on its condensers (or grid-forming damping) to keep the north-south swing as well damped as today's
geography gives.

### 6. Sensitivity (fleet / spine-t / south, worst-loss K and full-loss worst bus RoCoF)

| Variant | 240: fleet | 240: spine-t | 240: south | 700: fleet | 700: spine-t | 700: south |
|---|---|---|---|---|---|---|
| base (IEEET1, residual 0.25, H 8 s) | 1.154 / 0.2164 | 1.147 / 0.2151 | 2.103 / 0.3943 | 2.165 / 0.1392 | 1.715 / 0.1103 | 6.515 / 0.4188 |
| field held (no AVR) | 1.247 / 0.2338 | 1.221 / 0.2289 | 1.884 / 0.3532 | 2.048 / 0.1317 | 1.659 / 0.1066 | 4.760 / 0.3060 |
| residual fleet 0.5 | 1.141 / 0.2139 | 1.171 / 0.2196 | 1.646 / 0.3087 | 2.198 / 0.1413 | 1.744 / 0.1121 | 5.537 / 0.3559 |
| condenser H 4 s | 1.533 / 0.2874 | 1.425 / 0.2672 | 2.257 / 0.4232 | 3.044 / 0.1957 | 2.018 / 0.1297 | 6.662 / 0.4283 |

The ranking at 700 GVA.s (spine-t better than fleet, south far worse) holds in every variant. At 240 GVA.s fleet and
spine-t stay within 7 % of each other and swap places in one variant. The absolute numbers move a lot with the
condenser rating: the same inertia on condensers of half the H (twice the MVA) raises K by up to 40 %.

## What it says

1. **At about 240 GVA.s, placement buys almost nothing.** Any sensible spread (today's geography, demand-weighted, the T)
   gives a worst bus RoCoF only 11 to 15 % above the system figure, and the budgets needed for a 0.25 Hz/s regional
   limit are 194.1 to 203.6 GVA.s whichever spread is used; today's geography is as good as the T there. Only a bad
   placement (everything in one place) costs: K 2.1, and the 0.25 Hz/s limit is then not met at any budget up to
   1,200. So if the 240 floor is set by a regional RoCoF limit, ideal placement does not let it fall much; avoiding a
   concentrated layout is what matters.
2. **Placement matters more as the inertia rises, not less.** The system RoCoF falls as 1/E, but the far end of the
   country still swings locally before the rest of the inertia joins in, so K grows with the budget: today's geography
   reaches K 2.165 at 700 GVA.s and 2.997 at 1,200. For a strict regional limit (0.125 Hz/s) the T sites need 560.8
   (optimised) and 582.7 (equal shares) GVA.s against 944.1 for today's geography: 40.6 % and 38.3 % less inertia for
   the same worst-bus RoCoF. The gain comes from
   putting inertia at the electrical ends (the far north of Scotland, Cornwall), which today's geography leaves thin.
3. **The 3 to 5 second window is not placement's to give.** Time to 49.8 Hz is a system-frequency quantity: 675 GVA.s
   for 3 s and 1,125 for 5 s with no response (about 635 and 1,040 with GB response), the same for every placement, and
   the RMS runs confirm the system frequency moves by under 4 % between spread placements. What placement does change
   is what an operator sees regionally in that window: at 700 GVA.s the worst bus falls at 0.11 Hz/s with the T and
   0.14 Hz/s with today's geography, against 0.064 Hz/s for the system, and a slower, less damped (still positively
   damped) north-south swing with the T.
4. **So the hypothesis holds for the system frequency and is reversed for the regional penalty:** placement cannot lower
   the 700 (system) floor, and it lowers a regional floor only when that floor sits at high inertia; at the 240 level
   the regional penalty is already small for any spread layout.

## Limits

- RMS (phasor) model, not EMT: sub-cycle effects, converter control detail and phase-jump transients inside the first
  cycle are not modelled.
- Machine data are synthetic (typical GENROU ratios off X'd 0.30 pu, `stability --rms`'s indicative data); exciters are
  typical IEEET1 settings; no governors, no power system stabilisers, no frequency response in the RMS runs.
- Loads are constant admittances in the RMS engine and the converter fleet is current-controlled; the loss is a
  constant-admittance step. The full-loss scaling assumes the regional penalty K scales with the loss.
- The condensers are idealised (one H, X'd 0.30 pu on their own rating, no Q at the solved state) and their size per
  site is whatever the budget asks for; real units come in a few hundred MVA each. Results depend on the rating
  (sensitivity 6).
- One dispatch (the `gb-full` case's), four loss locations, one residual share; the optimised placement is one pass of
  a local search over the eleven T sites, not a proven optimum.
- The inter-area damping is fitted over four to thirteen half-cycles of a 6 s run with no governors, stabilisers or
  load frequency damping; read the trend, not the absolute figures.

## Reproduce

```
gridsim inertia-placement gb-full --json study.json
```

runs the whole study (about 45 minutes on a laptop: 252 RMS runs, about 10 s each). `--placements`, `--budgets`, `--losses`,
`--residual`, `--condenser-h`, `--avr` and `--rocof-limits` select parts of it or the sensitivity variants above
(chapter 19, [`inertia-placement`](<../../manual/0019 CLI reference.md#inertia-placement>)).
