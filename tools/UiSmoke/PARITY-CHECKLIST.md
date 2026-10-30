# UiSmoke parity checklist (UI programme Phase 1 item 7)

The visual parity sign-off between the WPF window (src/GridSim.Wpf) and the cross-platform window (src/GridSim.App):
every WPF page, the picture the WPF smoke tool (tools/AssetSmoke) makes of it where it makes one, and the picture
UiSmoke makes of the same page. Tick a row when the two show the same content, layout, texts and colours (allowing for
the known differences listed below). On a difference, write what differs in Notes (a file name is enough). Marks:
[x] same, [!] a difference (in Notes), [ ] not checked.

This list extends the WPF click-through checklist, `review-progress/2026-10/WPF-CLICK-THROUGH.md` (sections A to E:
behaviour checks run by hand on the WPF app). That file is not changed; section "Click-through rows on the new window"
below names the rows of it that are about what a page shows, and the UiSmoke picture that covers each.

## Making the two sets

1. WPF, Windows only. AssetSmoke takes the case folder and the folder for the pictures:
   `dotnet run --project tools/AssetSmoke -c Release -- data/uk <wpf-shots>`
   It prints `ALL PAGES OK (<model name>: <n> buses)` and exits 0 when every page loaded. It draws every room tab
   that has a WPF view (all but HARMONICS, whose WPF panel is inline in the main window).
2. The cross-platform window, any OS. UiSmoke takes named options:
   `dotnet run --project tools/UiSmoke -c Release -- --case uk --ticks 4 --size 1440x860 --out <app-shots>`
   It prints `ALL PAGES OK (<n> pages in <s> s)` and exits 0 when every page rendered (1 when a page failed, named in
   the output; 2 for a refused command line). `--help` lists every option.
   `--size 1440x860` and `--ticks 4` match AssetSmoke's off-screen window and its four steps; the defaults
   (1580x940, the WPF main window's size, and 30 ticks) are for a fuller picture.
3. Open `<app-shots>/index.html` (a contact sheet linking every picture) beside the WPF folder. The pictures are in
   `<app-shots>/uk/`, named as AssetSmoke names them where WPF has the page, so `<wpf-shots>/scada.png` pairs with
   `<app-shots>/uk/scada.png`. `<app-shots>/manifest.json` lists every picture with its size and SHA-256, so two
   UiSmoke runs (for example on Windows and on Linux) can be compared file by file.

Known differences, not parity failures:

- AssetSmoke draws each page alone in its window (the view is the window's content); UiSmoke draws the whole window
  (menu bar, header, tab strip, transport bar and status bar around the page), as the user sees it.
- AssetSmoke's window is 1440x860 and plays 4 steps; pass `--size 1440x860 --ticks 4` to UiSmoke to match.
- UiSmoke uses the free licence tier and an empty settings file of its own, so the window title, the event log's
  licence line and the map's view settings are the defaults, whatever the machine's own licence and settings are,
  except that the two NETWORK pictures draw the coastline and the fleet glyphs, as AssetSmoke does.
- The bundled Inter font is the default text font in the cross-platform window on every OS; WPF uses the Windows fonts
  its theme names.
- The unit pages carry the unit's model name, so their file names change with the case (AssetSmoke and UiSmoke choose
  the same units by the same rule: the first transformer, the first line or cable, the first generator that is not an
  interconnector, the first interconnector and the first bus).

## The room tabs (WPF MainWindow.xaml, in its order)

| # | WPF tab | AssetSmoke picture | UiSmoke picture (`<case>/...`) | Parity | Notes |
|---|---|---|---|---|---|
| T1 | NETWORK (map fitted) | network-map-fitted.png (the map alone) | network-map-fitted.png (the dashboard, map fitted) | [x] | same content; map viewport smaller beside the dashboard tiles (known difference). Header fixed 2.0.106/2.0.119, legend clear of minimap 2.0.111 |
| T2 | NETWORK (map dived in on the busiest 33 kV substation) | network-map-dived-in.png | network-map-dived-in.png | [x] | same substation in view; WPF dives closer because its map fills the window (known difference) |
| T3 | GENERATION & INERTIA | generation-tab.png | generation-tab.png | [x] | same figures; side column scrolls and values clear of the scroll bar (2.0.109) |
| T4 | THE MACHINE | the-machine.png | the-machine.png | [x] | same figures, never overlapped (2.0.107); the page now scrolls so the headline, both 70 px traces and the shaft are always whole (2.0.129) |
| T5 | SCADA | scada.png | scada.png | [x] | same rows, columns and header |
| T6 | OPERATOR | operator.png | operator.png | [x] | same cards, rows and columns; note wraps (2.0.114) |
| T7 | MARKET | market.png | market.png | [x] | same cards; both show the offline state (no lake feed) |
| T8 | NESO RP1 | neso-rp1.png | neso-rp1.png | [x] | same cards; dispatch stack and list rows padded as WPF draws them (2.0.131); body scrolls on a short window (2.0.113) |
| T9 | MECHANICAL | mechanical.png | mechanical.png | [x] | same rows and columns; per-machine rows padded 10,7 as WPF draws them (2.0.131); lists clear of scroll bars (2.0.112, 2.0.114) |
| T10 | HUMAN SCALE | human-scale.png | human-scale.png | [x] | same cards and ranked list; both pages scroll |
| T11 | SPEED DOMAINS | speed-domains.png | speed-domains.png | [x] | same domains, bars and cards (AssetSmoke now makes the tab active, as selecting it does) |
| T12 | INVERTER FLEET | inverter-fleet.png | inverter-fleet.png | [x] | same off state for uk (no authored asset-class library) |
| T13 | EXPERIMENTAL | experimental.png | experimental.png | [x] | same audit rows; the power-flow trace is below the first screen in the new window |
| T14 | HARMONICS | (none: the WPF panel is inline in MainWindow.xaml, no view to draw alone) | harmonics.png | [ ] | WPF side not drawn by AssetSmoke; compare by hand |
| T15 | CYCLE | cycle.png | cycle.png | [x] | same budget rows and job list |
| T16 | ASSETS | (see the ASSETS pages below) | (see the ASSETS pages below) | [x] | see A1-A13 |
| T17 | ABOUT | (none) | (none: Help > About... opens the About box) | [x] | by design, no tab in the new window; by design |

## The ASSETS pages (WPF Views/Assets, in AssetSmoke's order)

| # | WPF page | AssetSmoke picture | UiSmoke picture (`<case>/...`) | Parity | Notes |
|---|---|---|---|---|---|
| A1 | Landing page (ASSETS) | home.png | home.png | [x] | same classes and counts |
| A2 | INTERCONNECTORS fleet | interconnector-fleet.png | interconnector-fleet.png | [x] | same rows |
| A3 | Interconnector unit page | interconnector-detail-(name).png | interconnector-detail-(name).png | [x] | uk only (case9 has none); same figures |
| A4 | GENERATORS fleet | generator-fleet.png | generator-fleet.png | [x] | same rows |
| A5 | Generator unit page | generator-detail-(name).png | generator-detail-(name).png | [x] | same figures |
| A6 | GENERATOR PHASORS | generator-phasors.png | generator-phasors.png | [!] | same figures; lagging current drawn below the axis in both apps since 2.0.118 (the AssetSmoke picture is older); cards narrower beside the tree |
| A7 | TRANSFORMERS fleet | transformer-fleet.png | transformer-fleet.png | [x] | same rows |
| A8 | Transformer unit page | transformer-detail-(name).png | transformer-detail-(name).png | [x] | uk only (case9 has none); same figures; page scrolls, cards clip (2.0.108, 2.0.110) |
| A9 | TAP CHANGERS fleet | tap-changer-fleet.png | tap-changer-fleet.png | [x] | same rows |
| A10 | CABLES & LINES fleet | circuit-fleet.png | circuit-fleet.png | [x] | same rows, columns line up (2.0.112) |
| A11 | Circuit unit page | circuit-detail-(name).png | circuit-detail-(name).png | [x] | same figures |
| A12 | SUBSTATIONS fleet | substation-fleet.png | substation-fleet.png | [x] | same rows |
| A13 | Substation unit page | substation-detail-(bus-N).png | substation-detail-(bus-N).png | [x] | same diagram; page scrolls (2.0.110), bays spaced for the longest label (2.0.124) |

## Pages the new window has and WPF does not (no parity row; check they render and read sensibly)

| # | Page | UiSmoke picture (`<case>/...`) | Renders | Notes |
|---|---|---|---|---|
| N1 | Substation page, IEC VIEW on | substation-detail-(bus-N)-iec-view.png | [x] |  |
| N2 | Transformer page, IEC VIEW on | transformer-detail-(name)-iec-view.png | [x] | uk only |
| N3 | PROPERTIES sub-tab (on the substation) | assets-properties.png | [x] | a case file only; not shown for a MATPOWER source (by design) |
| N4 | CONNECTIONS sub-tab (on the substation) | assets-connections.png | [x] |  |
| N5 | DEMANDS, BOUNDARIES, PROTECTIONS, ASSET CLASSES, OUT OF SERVICE class pages and one member of each | assets-demand-fleet.png, assets-demand.png and so on, one per page kind the case has | [x] | the kinds a case has vary |
| N6 | OVERLAY node | assets-overlay-fleet.png (and its kind and record pages) | [ ] | only a case with an asset overlay; uk has no asset overlay: skipped by the run |
| N7 | AUTHOR mode home (on a temporary copy of the case) | assets-author-home.png | [x] | uk and case9; skipped on gb-full (the copy is above the free tier's bus limit, as the app refuses it) |

## Click-through rows on the new window

The rows of `review-progress/2026-10/WPF-CLICK-THROUGH.md` that check what a page shows, and the UiSmoke picture that
shows the same thing in the new window. The rest of that file (launch arguments, tab jumps, the bridge, regional
formats) is behaviour, not a picture: it is run by hand on each window.

| WPF row | What it checks | UiSmoke picture | Same in the new window | Notes |
|---|---|---|---|---|
| A2, A3 | gb-full's two Auchencrosh interconnectors, each with its own row and page | `--case gb-full`: interconnector-fleet.png, interconnector-detail-(...).png | [x] | gb-full is a bundled network, so the free tier loads it; it is large, so the run takes longer; gb-full now loads on the free tier (2.0.126). Since 2.0.128 the INTERCONNECTORS page lists the 12 real links (the 1,072 grid infeeds were listed as links before, in both windows; each is now under its substation bus); the two Auchencrosh rows are separate |
| C5 | the amber STATIC DEMO tags on the transformer, circuit and generator pages, and the dial captions | transformer-detail-(...).png, circuit-detail-(...).png, generator-detail-(...).png | [x] |  |
| C6 | a case9 circuit page shows its own two ends and flow | `--case case9`: circuit-detail-().png | [x] | bus 1 -> bus 4, +72 MW |
| C7 | CYCLE reads `N-1 shards covered since load` | cycle.png | [x] | 8 / 8 shards |
| C8 | NESO RP1 trend header `STABILITY TREND  *  last 60 frames` | neso-rp1.png | [x] |  |
| C9 | AssetSmoke prints `ALL PAGES OK` and exits 0 | UiSmoke prints `ALL PAGES OK (<n> pages in <s> s)` and exits 0 | [x] | on Windows, Linux and macOS; Windows 11: uk 37 pages, case9 29, gb-full 39; Linux and macOS not run here |

## Result

| Set | Rows ticked | Rows with a difference | Checked by / date / OS |
|---|---|---|---|
| Room tabs (T1-T17) | 16 | 0; T14 (HARMONICS) has no WPF picture | UiSmoke + AssetSmoke / 2026-10-24 / Windows 11 |
| ASSETS pages (A1-A13) | 12 | 1 (A6, by design since 2.0.118) | UiSmoke + AssetSmoke / 2026-10-24 / Windows 11 |
| New pages (N1-N7) | 6 | 0 (N6 not applicable to uk) | UiSmoke / 2026-10-24 / Windows 11 |
| Click-through rows | 6 | 0 | UiSmoke / 2026-10-24 / Windows 11 |
