// GridSim network map - a full-fidelity Canvas 2D port of the WPF FastSchematic.
//
// Topology (nodes/edges/fleet/boundaries) is pushed ONCE; per-frame state (node voltages, edge loading/flow)
// and the optional bridge overlays (condition-by-bus / stress-by-branch) are pushed each tick. The colour
// ramps, level-of-detail, viewport culling, co-located busbar fan-out, marching-dash flow, minimap, hit-test
// and keyboard/mouse interaction all live here - mirroring GridSim.Wpf/Visuals/FastSchematic.cs so the web
// map stays smooth at GB scale (~3,539 buses / ~5,918 branches).
//
// Architecture: a clean IIFE exposing a `gridsimMap` global plus a per-canvas state Map. Only the marching
// dash + wave ripple are animated (requestAnimationFrame, and only while playing / in WAVE mode) - the
// Blazor circuit is never touched to animate; data arrives on frames. Geography overlays (coast/DNO/GSP/
// assets) are consumed from the parallel `window.gridsimGeo` module and degrade to no-ops when it is absent.

(function () {
    const maps = new Map(); // canvasId -> state

    // ---- colour ramps (ported 1:1 from GridSim.Wpf/Visuals/Palette.cs) ---------------------------------
    function clamp(v, lo, hi) { return v < lo ? lo : v > hi ? hi : v; }
    function lerpN(a, b, t) { t = clamp(t, 0, 1); return Math.round(a + (b - a) * t); }
    function rgb(c) { return `rgb(${c[0]},${c[1]},${c[2]})`; }
    function rgba(c, a) { return `rgba(${c[0]},${c[1]},${c[2]},${a})`; }
    function mix(c1, c2, t) { return [lerpN(c1[0], c2[0], t), lerpN(c1[1], c2[1], t), lerpN(c1[2], c2[2], t)]; }

    const BLUE = [63, 169, 245], GREEN = [47, 210, 127], AMBER = [243, 193, 58], RED = [255, 91, 91];
    const WHITE = [230, 237, 243];
    const DARKFILL = '#0E141B', TIE = 'rgba(58,71,86,0.6)', DIM = '#5A6B7B', LABEL = '#C7D2DD';

    function voltageC(vm) {
        if (vm <= 0.96) return BLUE;
        if (vm < 1.00) return mix(BLUE, GREEN, (vm - 0.96) / 0.04);
        if (vm <= 1.03) return mix(GREEN, AMBER, (vm - 1.00) / 0.03);
        if (vm <= 1.06) return mix(AMBER, RED, (vm - 1.03) / 0.03);
        return RED;
    }
    function loadingC(frac) {
        if (frac <= 0.6) return mix(GREEN, AMBER, frac / 0.6);
        if (frac <= 1.0) return mix(AMBER, RED, (frac - 0.6) / 0.4);
        return RED;
    }
    function conditionC(t) { t = clamp(t, 0, 1); return t <= 0.5 ? mix(GREEN, AMBER, t / 0.5) : mix(AMBER, RED, (t - 0.5) / 0.5); }
    function stressC(t) { t = clamp(t, 0, 1); return t <= 0.5 ? mix(BLUE, AMBER, t / 0.5) : mix(AMBER, RED, (t - 0.5) / 0.5); }
    function waveC(v) { v = clamp(v, -1, 1); return rgb(v >= 0 ? mix(WHITE, RED, v) : mix(WHITE, BLUE, -v)); }

    // ---- bucketing (matches FastSchematic: 12 buckets so corridors batch into a handful of strokes) ------
    const BUCKETS = 12;
    function loadBucket(f) { return clamp(Math.round(clamp(f, 0, 1.2) / 1.2 * (BUCKETS - 1)), 0, BUCKETS - 1); }
    function voltBucket(vm) { return clamp(Math.round(clamp((vm - 0.94) / 0.12, 0, 1) * (BUCKETS - 1)), 0, BUCKETS - 1); }
    function bucket01(x) { return clamp(Math.round(clamp(x, 0, 1) * (BUCKETS - 1)), 0, BUCKETS - 1); }
    // Precomputed per-bucket colours for each mode (avoids re-lerping per edge per frame).
    const LOAD_COLS = Array.from({ length: BUCKETS }, (_, i) => loadingC(i / (BUCKETS - 1) * 1.2));
    const VOLT_COLS = Array.from({ length: BUCKETS }, (_, i) => voltageC(0.94 + i / (BUCKETS - 1) * 0.12));
    const COND_COLS = Array.from({ length: BUCKETS }, (_, i) => conditionC(i / (BUCKETS - 1)));
    const STRESS_COLS = Array.from({ length: BUCKETS }, (_, i) => stressC(i / (BUCKETS - 1)));

    // ---- legend descriptors (Blazor reads these to draw the gradient swatch) ---------------------------
    const LEGENDS = {
        loading: { title: 'CIRCUIT LOADING', min: '0%', max: '120%+', stops: [rgb(GREEN), rgb(AMBER), rgb(RED)] },
        voltage: { title: 'VOLTAGE (p.u.)', min: '0.94', max: '1.06', stops: [rgb(BLUE), rgb(GREEN), rgb(AMBER), rgb(RED)] },
        condition: { title: 'ASSET CONDITION', min: 'healthy', max: 'critical', stops: [rgb(GREEN), rgb(AMBER), rgb(RED)] },
        stress: { title: 'BRANCH STRESS', min: 'cool', max: 'hot', stops: [rgb(BLUE), rgb(AMBER), rgb(RED)] },
        wave: { title: 'ELECTROMECHANICAL WAVE', min: 'trough', max: 'crest', stops: [rgb(BLUE), rgb(WHITE), rgb(RED)] },
    };

    // ---- geometry helpers -------------------------------------------------------------------------------
    const GOLDEN = 2.399963229728653;

    // ---- named renderer constants (R111/R213: the same values as GridSim.Wpf FastSchematic) ----
    const LABEL_BUDGET = 220;          // most node labels drawn at once (FastSchematic.LabelBudget)
    const EHV_DISPLAY_KV = 200;        // EHV cut for ring weight and minimap dots (FastSchematic.EhvDisplayKv)
    const NODE_PICK_RADIUS_PX = 14;    // click pick radius added to a roundel (FastSchematic NodePickRadiusPx)
    const BRANCH_PICK_TOL_PX = 8;      // click distance that picks a branch (FastSchematic BranchPickTolerancePx)
    // Animation cadence (R111): the marching dash / wave ripple are redrawn at most every 65 ms (~15 fps), the
    // WPF FastSchematic cadence, instead of at the display rate (60+ fps). The dash advances by the real elapsed
    // time, so its on-screen speed is unchanged; a gap longer than ANIM_MAX_DT_S (tab hidden) counts as one step.
    const ANIM_MIN_INTERVAL_MS = 65;
    const ANIM_MAX_DT_S = 0.2;
    function roundelBaseR(kv) { return kv >= 300 ? 10.5 : kv >= 200 ? 8.5 : kv >= 100 ? 6.5 : kv >= 25 ? 5.0 : 3.6; }
    function kvOfBand(band) {
        switch (band) { case '400kV': return 400; case '275kV': return 275; case '132kV': return 132; case '66kV': return 66; case 'LV': return 11; default: return 11; }
    }
    function distSqSeg(px, py, ax, ay, bx, by) {
        const dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        if (l2 < 1e-9) { const ex = px - ax, ey = py - ay; return ex * ex + ey * ey; }
        let t = ((px - ax) * dx + (py - ay) * dy) / l2; t = clamp(t, 0, 1);
        const cx = ax + t * dx, cy = ay + t * dy, ox = px - cx, oy = py - cy; return ox * ox + oy * oy;
    }
    function segHitsRect(ax, ay, bx, by, L, T, R, B) {
        if ((ax >= L && ax <= R && ay >= T && ay <= B) || (bx >= L && bx <= R && by >= T && by <= B)) return true;
        if ((ax < L && bx < L) || (ax > R && bx > R) || (ay < T && by < T) || (ay > B && by > B)) return false;
        return true;
    }

    // ---- per-canvas state -------------------------------------------------------------------------------
    function ensure(canvasId) {
        let m = maps.get(canvasId);
        if (m) return m;
        const canvas = document.getElementById(canvasId);
        if (!canvas) return null;
        m = {
            canvas, ctx: canvas.getContext('2d'), dotnet: null,
            topo: null, nodes: null, idIndex: null, edges: null, fleet: null, boundaryEdges: null,
            worldBounds: { x: 0, y: 0, w: 1, h: 1 }, maxKv: 400, showAll: false, geolocated: false,
            fanCount: null, fanDirX: null, fanDirY: null, fanFullR: null, groupCol: null,
            vmById: new Map(), paById: new Map(), pinjById: new Map(), edgeState: [], condByBus: new Map(), stressByBranch: new Map(),
            hasState: false, hasOverlay: false, waveField: null,
            stateVer: 0, layers: null, layersKey: null,   // R111 cached static layers (see layersFresh)
            view: { zoom: 1, ox: 0, oy: 0 }, fitted: false,
            colorMode: 'loading', playing: false,
            toggles: { minimap: true, coast: false, dno: false, gsp: false, regions: false, assets: false, fleet: false, boundaries: false },
            geo: { coast: null, dno: null, regions: null, assets: null },
            dashPhase: 0, dashSpeed: 9, waveT: 0, waveStart: 0, raf: null, lastT: 0,
            selected: null, hover: null, mmRect: null, mmScale: 1,
            dragging: false, moved: false, dx0: 0, dy0: 0, ox0: 0, oy0: 0,
        };
        attach(m);
        maps.set(canvasId, m);
        return m;
    }

    // ---- transforms / LOD -------------------------------------------------------------------------------
    // World space mirrors the shared equirectangular GB box: x west->east 0..1, y north->south 0..1 (north at
    // top). The gridsimGeo overlays live in this box; the topology.json bus coords arrive north-at-high-y, so
    // buildNodes mirrors their y (1 - y) to line them up. Screen = world * zoom + offset.
    function project(m, wx, wy) { return [wx * m.view.zoom + m.view.ox, wy * m.view.zoom + m.view.oy]; }
    function unproject(m, sx, sy) { return [(sx - m.view.ox) / m.view.zoom, (sy - m.view.oy) / m.view.zoom]; }
    function fitZoom(m) {
        const w = m.canvas.width, h = m.canvas.height, b = m.worldBounds;
        if (w < 1 || h < 1 || b.w <= 0 || b.h <= 0) return 1;
        return Math.max(0.01, Math.min((w - 72) / b.w, (h - 72) / b.h));
    }
    function minZoom() { return 0.05; }
    function maxZoom(m) { return Math.max(400, fitZoom(m) * 600); }
    function rel(m) { return m.view.zoom / fitZoom(m); }
    function lodMinKv(m) {
        if (m.showAll) return 0;
        const r = rel(m);
        if (r < 1.4) return 200; if (r < 3.0) return 100; if (r < 7.0) return 20; if (r < 16.0) return 10; return 0;
    }
    function labelMinKv(m) {
        if (m.showAll) return 0;
        const r = rel(m);
        if (r < 3.0) return 200; if (r < 8.0) return 100; if (r < 18.0) return 20; return 0;
    }
    function showLabels(m) { return rel(m) >= 1.8; }
    function roundelR(m, kv) { return roundelBaseR(kv) * clamp(rel(m), 0.55, 1.8); }
    function fanT(m) { return clamp((rel(m) - 2.0) / 7.0, 0, 1); }
    function nodeScreen(m, i) {
        const n = m.nodes[i];
        let [sx, sy] = project(m, n.wx, n.wy);
        if (m.fanCount[i] > 1) {
            const t = fanT(m);
            if (t > 0.001) { const r = t * m.fanFullR[i]; sx += m.fanDirX[i] * r; sy += m.fanDirY[i] * r; }
        }
        return [sx, sy];
    }

    function fit(m) {
        const w = m.canvas.width, h = m.canvas.height, b = m.worldBounds;
        if (w < 1 || h < 1 || b.w <= 0 || b.h <= 0) { m.fitted = false; return; }
        const pad = 36;
        const z = clamp(Math.min((w - 2 * pad) / b.w, (h - 2 * pad) / b.h), minZoom(), maxZoom(m));
        m.view.zoom = z;
        const cx = b.x + b.w / 2, cy = b.y + b.h / 2;
        m.view.ox = w / 2 - cx * z; m.view.oy = h / 2 - cy * z;
        m.fitted = true;
    }

    function zoomAt(m, sx, sy, factor) {
        const nz = clamp(m.view.zoom * factor, minZoom(), maxZoom(m));
        if (Math.abs(nz - m.view.zoom) < 1e-9) return;
        const [wx, wy] = unproject(m, sx, sy);
        m.view.zoom = nz;
        m.view.ox = sx - wx * nz; m.view.oy = sy - wy * nz;
    }

    // ---- topology preprocessing (once per Load) ---------------------------------------------------------
    function buildNodes(m, topo) {
        const nn = topo.nodes.length;
        const nodes = new Array(nn);
        const idIndex = new Map();
        let minX = 1e18, minY = 1e18, maxX = -1e18, maxY = -1e18, maxKv = 1;
        for (let i = 0; i < nn; i++) {
            const n = topo.nodes[i];
            // N-S flip: topology.json bus coords come with NORTH at HIGH y, but the world box + the gridsimGeo
            // overlays put north at the TOP (y=0). Mirror y (1 - y) so the network lines up over the UK instead of
            // rendering upside-down. X (west->east) is already correct, so it is left alone.
            const wx = n.x, wy = 1 - n.y;
            const kv = n.baseKv > 0 ? n.baseKv : kvOfBand(n.kvBand);
            nodes[i] = { id: n.id, wx, wy, kv, name: n.name, type: n.busType, band: n.kvBand || '' };
            idIndex.set(n.id, i);
            if (wx < minX) minX = wx; if (wy < minY) minY = wy; if (wx > maxX) maxX = wx; if (wy > maxY) maxY = wy;
            if (kv > maxKv) maxKv = kv;
        }
        if (nn === 0) { minX = 0; minY = 0; maxX = 1; maxY = 1; }
        if (maxX - minX < 1e-9) { minX -= 0.5; maxX += 0.5; }
        if (maxY - minY < 1e-9) { minY -= 0.5; maxY += 0.5; }
        m.nodes = nodes; m.idIndex = idIndex; m.maxKv = maxKv;
        m.worldBounds = { x: minX, y: minY, w: maxX - minX, h: maxY - minY };
        m.showAll = nn > 0 && nn <= 800;

        // Edges resolved to node indices.
        m.edges = topo.edges.map(e => ({
            index: e.index, a: idIndex.has(e.from) ? idIndex.get(e.from) : -1, b: idIndex.has(e.to) ? idIndex.get(e.to) : -1,
            from: e.from, to: e.to, tx: e.isTransformer, name: e.name || '', rate: e.rateAMva || 0,
        }));
        m.edgeByIndex = new Map(); for (const e of m.edges) m.edgeByIndex.set(e.index, e);
        m.fleet = topo.fleet || [];
        m.boundaryEdges = new Set();
        (topo.boundaries || []).forEach(b => (b.edgeIndices || []).forEach(i => m.boundaryEdges.add(i)));

        buildFans(m);
        buildGroups(m);
    }

    // Co-located busbars (same site) fan out into a phyllotaxis disc as you zoom in (tube-map stacked assets).
    function buildFans(m) {
        const n = m.nodes.length;
        m.fanCount = new Int32Array(n).fill(1);
        m.fanDirX = new Float64Array(n); m.fanDirY = new Float64Array(n); m.fanFullR = new Float64Array(n);
        const byPos = new Map();
        for (let i = 0; i < n; i++) {
            const key = Math.round(m.nodes[i].wx * 1e6) + ':' + Math.round(m.nodes[i].wy * 1e6);
            let list = byPos.get(key); if (!list) byPos.set(key, list = []); list.push(i);
        }
        for (const members of byPos.values()) {
            const k = members.length; if (k < 2) continue;
            members.sort((a, b) => m.nodes[b].kv - m.nodes[a].kv || m.nodes[a].id - m.nodes[b].id);
            const fullR = clamp(11.0 * Math.sqrt(k) * 1.9, 26, 240);
            for (let s = 0; s < k; s++) {
                const ang = s * GOLDEN, rad = Math.sqrt((s + 0.5) / k), ni = members[s];
                m.fanCount[ni] = k; m.fanDirX[ni] = Math.cos(ang) * rad; m.fanDirY[ni] = Math.sin(ang) * rad; m.fanFullR[ni] = fullR;
            }
        }
    }

    // GSP supply-area grouping: multi-source BFS from every EHV (>=275 kV) node; each seeds its own area colour.
    function buildGroups(m) {
        const n = m.nodes.length;
        m.groupCol = new Array(n).fill('#4A5560');
        if (n === 0) return;
        const adj = Array.from({ length: n }, () => []);
        for (const e of m.edges) if (e.a >= 0 && e.b >= 0) { adj[e.a].push(e.b); adj[e.b].push(e.a); }
        const group = new Int32Array(n).fill(-1);
        const q = [];
        for (let i = 0; i < n; i++) if (m.nodes[i].kv >= 275) { group[i] = i; q.push(i); }
        for (let h = 0; h < q.length; h++) { const cur = q[h]; for (const nb of adj[cur]) if (group[nb] === -1) { group[nb] = group[cur]; q.push(nb); } }
        for (let i = 0; i < n; i++) if (group[i] >= 0) m.groupCol[i] = groupColor(group[i]);
    }
    function groupColor(seed) {
        const h = (seed * 0.6180339887498949) % 1.0, s = 0.55, v = 0.92;
        const i = Math.floor(h * 6) % 6, f = h * 6 - Math.floor(h * 6);
        const p = v * (1 - s), qq = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        let r, g, b;
        switch (i) { case 0: r = v; g = t; b = p; break; case 1: r = qq; g = v; b = p; break; case 2: r = p; g = v; b = t; break; case 3: r = p; g = qq; b = v; break; case 4: r = t; g = p; b = v; break; default: r = v; g = p; b = qq; }
        return `rgb(${Math.round(r * 255)},${Math.round(g * 255)},${Math.round(b * 255)})`;
    }

    // ---- edge colour per mode ---------------------------------------------------------------------------
    function edgeBucket(m, e, st) {
        switch (m.colorMode) {
            case 'voltage': { if (!m.hasState) return 0; const va = m.vmById.get(e.from) ?? 1, vb = m.vmById.get(e.to) ?? 1; return voltBucket((va + vb) * 0.5); }
            case 'condition': { if (!m.hasOverlay) return 0; const ca = m.condByBus.get(e.from) ?? 0, cb = m.condByBus.get(e.to) ?? 0; return bucket01((ca + cb) * 0.5); }
            case 'stress': { if (!m.hasOverlay) return 0; return bucket01(m.stressByBranch.get(e.name) ?? 0); }
            default: return m.hasState && st ? loadBucket(st.loadingFrac) : 0;
        }
    }
    function bucketCols(m) {
        switch (m.colorMode) { case 'voltage': return VOLT_COLS; case 'condition': return COND_COLS; case 'stress': return STRESS_COLS; default: return LOAD_COLS; }
    }

    // ---- draw -------------------------------------------------------------------------------------------
    function resize(m) {
        const c = m.canvas, w = c.clientWidth || 800, h = c.clientHeight || 600;
        if (c.width !== w || c.height !== h) { c.width = w; c.height = h; if (!m.fitted) fit(m); }
    }

    // ---- cached static layers (R111) ---------------------------------------------------------------------
    // draw() used to rebuild every layer on every animation frame: a template-string dedup key and Set entry per
    // visible corridor, tie and roundel, plus fresh Path2D objects and per-ring colour strings - several thousand
    // string allocations 60 times a second at gb-full fit zoom, just to advance the dash phase. None of that changes
    // between animation ticks: it depends only on the view (zoom/offset/canvas size), the colour mode and the pushed
    // state/overlays. So it is now built once per change of those inputs (buildLayers) and replayed each frame;
    // only the dash offset (and, in WAVE mode, the ring colours) are per-frame. The drawing itself - which segments,
    // which buckets, in which order, with which styles - is exactly what the per-frame code produced.
    function layersFresh(m) {
        const k = m.layersKey, v = m.view, c = m.canvas;
        return !!k && k.zoom === v.zoom && k.ox === v.ox && k.oy === v.oy && k.w === c.width && k.h === c.height
            && k.mode === m.colorMode && k.ver === m.stateVer;
    }

    function buildLayers(m, L, T, R, B, minKv, ehvOnly) {
        // Screen coords for in-LOD nodes (culling handled per element).
        const sx = m.sx || (m.sx = new Float64Array(m.nodes.length));
        const sy = m.sy || (m.sy = new Float64Array(m.nodes.length));
        const vis = m.visN || (m.visN = new Uint8Array(m.nodes.length));
        for (let i = 0; i < m.nodes.length; i++) {
            if (m.nodes[i].kv < minKv) { vis[i] = 0; continue; }
            const p = nodeScreen(m, i); sx[i] = p[0]; sy[i] = p[1]; vis[i] = 1;
        }
        m.layers = {
            corridors: buildCorridors(m, L, T, R, B, minKv, ehvOnly),
            ties: buildTies(m, L, T, R, B, minKv, ehvOnly),
            flow: m.hasState && m.colorMode !== 'wave' ? buildFlow(m, L, T, R, B, minKv, ehvOnly) : null,
            roundels: buildRoundels(m, minKv),
            labels: showLabels(m) ? buildLabels(m, minKv) : null,
        };
        const v = m.view, c = m.canvas;
        m.layersKey = { zoom: v.zoom, ox: v.ox, oy: v.oy, w: c.width, h: c.height, mode: m.colorMode, ver: m.stateVer };
    }

    function draw(m) {
        if (!m.topo) return;
        resize(m);
        if (!m.fitted) fit(m);
        const ctx = m.ctx, c = m.canvas;
        ctx.clearRect(0, 0, c.width, c.height);
        if (!m.nodes || m.nodes.length === 0) return;

        const L = -32, T = -32, R = c.width + 32, B = c.height + 32;
        const minKv = lodMinKv(m), ehvOnly = minKv > 1;

        // Rebuild the static layers only when the view, colour mode or pushed state changed (R111).
        if (!layersFresh(m)) buildLayers(m, L, T, R, B, minKv, ehvOnly);
        const lay = m.layers;

        // 1. geography backdrop (only for geolocated models, only when toggled + gridsimGeo present)
        drawGeoRegions(m, ctx);

        // 2. corridors, batched by colour bucket (dim buckets first so hot lines sit on top)
        strokeCorridors(ctx, lay.corridors);

        // 3. transformer ties
        strokeTies(ctx, lay.ties);

        // 4. marching-dash flow overlay (only with live state)
        if (lay.flow) strokeFlow(m, ctx, lay.flow);

        // 5. geocoded asset markers (over corridors, under roundels)
        if (m.toggles.assets && m.geo.assets) drawAssets(m, ctx);

        // 6. boundary cut overlay
        if (m.toggles.boundaries) drawBoundaries(m, ctx);

        // 7. roundels (fill + coloured ring), fan badges
        paintRoundels(m, ctx, lay.roundels);
        drawFanBadges(m, ctx, minKv);

        // 8. generation fleet diamonds
        if (m.toggles.fleet) drawFleet(m, ctx);

        // 9. labels (LOD + budget)
        if (lay.labels) paintLabels(ctx, lay.labels);

        // 10. selection highlight + minimap
        drawSelection(m, ctx);
        if (m.toggles.minimap) drawMinimap(m, ctx);
    }

    function buildCorridors(m, L, T, R, B, minKv, ehvOnly) {
        const cols = bucketCols(m);
        const paths = Array.from({ length: BUCKETS }, () => null);
        const seen = new Set();
        for (const e of m.edges) {
            if (e.a < 0 || e.b < 0 || e.tx) continue;
            const a = m.nodes[e.a], b = m.nodes[e.b];
            if (ehvOnly && (a.kv < minKv || b.kv < minKv)) continue;
            const ax = m.sx[e.a], ay = m.sy[e.a], bx = m.sx[e.b], by = m.sy[e.b];
            if (!segHitsRect(ax, ay, bx, by, L, T, R, B)) continue;
            const qax = Math.round(ax), qay = Math.round(ay), qbx = Math.round(bx), qby = Math.round(by);
            if (qax === qbx && qay === qby) continue;
            const key = qax < qbx || (qax === qbx && qay <= qby) ? `${qax},${qay},${qbx},${qby}` : `${qbx},${qby},${qax},${qay}`;
            if (seen.has(key)) continue; seen.add(key);
            const bk = edgeBucket(m, e, m.edgeState[e.index]);
            let p = paths[bk]; if (!p) paths[bk] = p = new Path2D();
            p.moveTo(ax, ay); p.lineTo(bx, by);
        }
        const styles = paths.map((p, i) => p ? rgba(cols[i], 0.42 + 0.22 * (i / (BUCKETS - 1))) : null);
        return { paths, styles };
    }
    function strokeCorridors(ctx, layer) {
        ctx.lineCap = 'round'; ctx.lineJoin = 'round'; ctx.lineWidth = 2.4;
        for (let i = 0; i < BUCKETS; i++) {
            if (!layer.paths[i]) continue;
            ctx.strokeStyle = layer.styles[i];
            ctx.stroke(layer.paths[i]);
        }
    }

    function buildTies(m, L, T, R, B, minKv, ehvOnly) {
        const p = new Path2D(); let any = false; const seen = new Set();
        for (const e of m.edges) {
            if (e.a < 0 || e.b < 0 || !e.tx) continue;
            const a = m.nodes[e.a], b = m.nodes[e.b];
            if (ehvOnly && (a.kv < minKv || b.kv < minKv)) continue;
            const ax = m.sx[e.a], ay = m.sy[e.a], bx = m.sx[e.b], by = m.sy[e.b];
            if (!segHitsRect(ax, ay, bx, by, L, T, R, B)) continue;
            const qax = Math.round(ax), qay = Math.round(ay), qbx = Math.round(bx), qby = Math.round(by);
            if (qax === qbx && qay === qby) continue;
            const key = `${Math.min(qax, qbx)},${Math.min(qay, qby)},${Math.max(qax, qbx)},${Math.max(qay, qby)}`;
            if (seen.has(key)) continue; seen.add(key);
            p.moveTo(ax, ay); p.lineTo(bx, by); any = true;
        }
        return any ? p : null;
    }
    function strokeTies(ctx, p) {
        if (p) { ctx.strokeStyle = TIE; ctx.lineWidth = 1.4; ctx.stroke(p); }
    }

    function buildFlow(m, L, T, R, B, minKv, ehvOnly) {
        // Marching dashes: group visible non-transformer edges by (bucket, direction) and stroke with a dashed
        // pen offset by the shared phase. Cheap, few state changes, each direction marches the right way.
        const cols = bucketCols(m);
        const dash = 5, gap = 7, period = dash + gap;
        const paths = {}; // key `${bk}:${dir}` -> Path2D
        for (const e of m.edges) {
            if (e.a < 0 || e.b < 0 || e.tx) continue;
            const st = m.edgeState[e.index]; if (!st) continue;
            const a = m.nodes[e.a], b = m.nodes[e.b];
            if (ehvOnly && (a.kv < minKv || b.kv < minKv)) continue;
            const ax = m.sx[e.a], ay = m.sy[e.a], bx = m.sx[e.b], by = m.sy[e.b];
            if (!segHitsRect(ax, ay, bx, by, L, T, R, B)) continue;
            const dxx = bx - ax, dyy = by - ay, len = Math.hypot(dxx, dyy);
            if (len < period * 0.5) continue;
            const bk = edgeBucket(m, e, st);
            const dir = st.pFromMw >= 0 ? 1 : -1;   // +flow => A->B (marches from `from` toward `to`)
            const k = bk + ':' + dir; let p = paths[k]; if (!p) paths[k] = p = new Path2D();
            p.moveTo(ax, ay); p.lineTo(bx, by);
        }
        // Same iteration order as the old per-frame `for (const k in paths)` loop, so the stroke order is unchanged.
        const out = [];
        for (const k in paths) {
            const [bk, dir] = k.split(':').map(Number);
            out.push({ path: paths[k], dir, style: rgba(cols[bk], 0.95) });
        }
        return { dash, gap, strokes: out };
    }
    function strokeFlow(m, ctx, layer) {
        ctx.save();
        ctx.lineWidth = 2.6; ctx.lineCap = 'butt'; ctx.setLineDash([layer.dash, layer.gap]);
        for (const s of layer.strokes) {
            ctx.strokeStyle = s.style;
            ctx.lineDashOffset = -m.dashPhase * s.dir;
            ctx.stroke(s.path);
        }
        ctx.restore();
    }

    function buildRoundels(m, minKv) {
        const c = m.canvas, byCond = m.colorMode === 'condition' && m.hasOverlay;
        const seen = new Set();
        const fillPath = new Path2D();
        const rings = [];
        for (let i = 0; i < m.nodes.length; i++) {
            if (!m.visN[i]) continue;
            const x = m.sx[i], y = m.sy[i];
            if (x < -20 || x > c.width + 20 || y < -20 || y > c.height + 20) continue;
            const r = roundelR(m, m.nodes[i].kv);
            const key = Math.round(x) + ',' + Math.round(y) + ',' + Math.round(r);
            if (seen.has(key)) continue; seen.add(key);
            fillPath.moveTo(x + r, y); fillPath.arc(x, y, r, 0, Math.PI * 2);
            rings.push(i, x, y, r);
        }
        // Ring colours are fixed between state pushes except in WAVE mode, where they ripple every frame.
        const wave = m.colorMode === 'wave';
        const styles = [], widths = [];
        for (let j = 0; j < rings.length; j += 4) {
            const i = rings[j], kv = m.nodes[i].kv;
            let col = null;
            if (byCond) col = rgb(conditionC(m.condByBus.get(m.nodes[i].id) ?? 0));
            else if (!wave) { const vm = m.hasState ? (m.vmById.get(m.nodes[i].id) ?? 1) : 1; col = rgb(voltageC(vm)); }
            styles.push(col); widths.push(kv >= EHV_DISPLAY_KV ? 2.6 : 1.8);
        }
        return { fillPath, rings, styles, widths };
    }

    function paintRoundels(m, ctx, layer) {
        const rings = layer.rings;
        // Fills first (dark), then per-node coloured rings.
        ctx.fillStyle = DARKFILL;
        ctx.fill(layer.fillPath);
        // GSP-area tint over the dark fill.
        if (m.toggles.gsp) {
            for (let j = 0; j < rings.length; j += 4) {
                const i = rings[j];
                ctx.fillStyle = m.groupCol[i]; ctx.globalAlpha = 0.66;
                ctx.beginPath(); ctx.arc(rings[j + 1], rings[j + 2], rings[j + 3], 0, Math.PI * 2); ctx.fill();
            }
            ctx.globalAlpha = 1;
        }
        // Coloured rings (voltage, or condition in condition mode; the wave colour is live per frame).
        for (let j = 0, q = 0; j < rings.length; j += 4, q++) {
            const i = rings[j], x = rings[j + 1], y = rings[j + 2], r = rings[j + 3];
            const col = layer.styles[q] ?? waveNodeColor(m, m.nodes[i].id);
            ctx.strokeStyle = col; ctx.lineWidth = layer.widths[q];
            ctx.beginPath(); ctx.arc(x, y, r, 0, Math.PI * 2); ctx.stroke();
        }
    }

    function waveNodeColor(m, id) {
        if (!m.waveField) return DIM;
        const wf = m.waveField.nodes.get(id); if (!wf) return DIM;
        const omega = 2 * Math.PI * m.waveField.freq;
        return waveC(wf.amp * Math.cos(omega * m.waveT - wf.phase));
    }

    function drawFanBadges(m, ctx, minKv) {
        const t = fanT(m), c = m.canvas;
        const clusters = m.clusters || (m.clusters = collectClusters(m));
        ctx.font = '600 10.5px Bahnschrift, "Segoe UI", sans-serif'; ctx.textBaseline = 'alphabetic';
        for (const cl of clusters) {
            if (cl.maxKv < minKv) continue;
            const [x, y] = project(m, cl.wx, cl.wy);
            if (x < -40 || x > c.width + 40 || y < -40 || y > c.height + 40) continue;
            if (t > 0.05) {
                const haloR = t * cl.fullR + roundelR(m, cl.maxKv) + 5;
                ctx.strokeStyle = 'rgba(120,150,175,0.6)'; ctx.fillStyle = 'rgba(90,120,150,0.11)'; ctx.lineWidth = 1;
                ctx.beginPath(); ctx.arc(x, y, haloR, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
                badge(ctx, x, y - haloR - 2, `${cl.count} assets`, true);
            } else if (cl.count >= 8) {
                const rr = roundelR(m, cl.maxKv);
                badge(ctx, x + rr * 0.7, y - rr - 1, `x${cl.count}`, false);
            }
        }
    }
    function collectClusters(m) {
        const byPos = new Map(); const out = [];
        for (let i = 0; i < m.nodes.length; i++) {
            if (m.fanCount[i] <= 1) continue;
            const key = Math.round(m.nodes[i].wx * 1e6) + ':' + Math.round(m.nodes[i].wy * 1e6);
            if (byPos.has(key)) continue; byPos.set(key, true);
            out.push({ wx: m.nodes[i].wx, wy: m.nodes[i].wy, count: m.fanCount[i], fullR: m.fanFullR[i], maxKv: m.nodes[i].kv });
        }
        return out;
    }
    function badge(ctx, ax, ay, text, centreAbove) {
        const w = ctx.measureText(text).width + 8, h = 15;
        const left = centreAbove ? ax - w / 2 : ax, top = centreAbove ? ay - h : ay - h / 2;
        ctx.fillStyle = 'rgba(14,20,28,0.86)';
        roundRect(ctx, left, top, w, h, 3); ctx.fill();
        ctx.fillStyle = LABEL; ctx.fillText(text, left + 4, top + 11);
    }
    function roundRect(ctx, x, y, w, h, r) {
        r = Math.min(r, w / 2, h / 2);
        ctx.beginPath();
        ctx.moveTo(x + r, y); ctx.arcTo(x + w, y, x + w, y + h, r); ctx.arcTo(x + w, y + h, x, y + h, r);
        ctx.arcTo(x, y + h, x, y, r); ctx.arcTo(x, y, x + w, y, r); ctx.closePath();
    }

    function drawFleet(m, ctx) {
        if (!m.fleet.length) return;
        // Power-flow-only devices (slack balancer, voltage-control units) are solver artefacts, not plant: they are
        // neither drawn nor allowed to set the size scale (gb-full's 266,425 MVA slack would shrink every real unit
        // to the minimum marker).
        const c = m.canvas; let maxMw = 1;
        for (const g of m.fleet) if (!g.powerFlowOnly && g.mw > maxMw) maxMw = g.mw;
        for (const g of m.fleet) {
            if (g.powerFlowOnly) continue;
            const ni = m.idIndex.get(g.bus); if (ni === undefined) continue;
            const [x, y] = nodeScreen(m, ni);
            if (x < -16 || x > c.width + 16 || y < -16 || y > c.height + 16) continue;
            const rel2 = Math.sqrt(g.mw / maxMw), h = 3 + 8 * rel2;
            ctx.fillStyle = g.fuelColor || '#9E9E9E'; ctx.strokeStyle = '#0B0F14'; ctx.lineWidth = 0.8;
            ctx.beginPath(); ctx.moveTo(x, y - h); ctx.lineTo(x + h, y); ctx.lineTo(x, y + h); ctx.lineTo(x - h, y); ctx.closePath();
            ctx.fill(); ctx.stroke();
        }
    }

    function buildLabels(m, minKv) {
        const lblMin = labelMinKv(m); let budget = LABEL_BUDGET;
        const out = [];
        const seen = new Set();
        for (let i = 0; i < m.nodes.length && budget > 0; i++) {
            const n = m.nodes[i]; if (!m.visN[i] || n.kv < lblMin || !n.name) continue;
            const x = m.sx[i], y = m.sy[i];
            if (x < 0 || x > m.canvas.width || y < 0 || y > m.canvas.height) continue;
            const key = Math.round(x) + ',' + Math.round(y); if (seen.has(key)) continue; seen.add(key);
            const text = n.name.replace(' 400kV', '').replace(' 275kV', '').toUpperCase();
            if (!text) continue;
            const r = roundelR(m, n.kv);
            out.push(text, x + r + 4, y); budget--;
        }
        return out;
    }
    function paintLabels(ctx, labels) {
        ctx.font = '600 11px Bahnschrift, "Segoe UI", sans-serif'; ctx.fillStyle = LABEL; ctx.textBaseline = 'middle';
        for (let j = 0; j < labels.length; j += 3) ctx.fillText(labels[j], labels[j + 1], labels[j + 2]);
    }

    function drawSelection(m, ctx) {
        if (!m.selected) return;
        if (m.selected.type === 'node') {
            const ni = m.idIndex.get(m.selected.id); if (ni === undefined || !m.visN[ni]) return;
            const [x, y] = nodeScreen(m, ni), r = roundelR(m, m.nodes[ni].kv) + 5;
            ctx.strokeStyle = '#21C7E6'; ctx.lineWidth = 2; ctx.beginPath(); ctx.arc(x, y, r, 0, Math.PI * 2); ctx.stroke();
        } else {
            const e = m.edgeByIndex.get(m.selected.id); if (!e || e.a < 0 || e.b < 0) return;
            const [ax, ay] = nodeScreen(m, e.a), [bx, by] = nodeScreen(m, e.b);
            ctx.strokeStyle = '#21C7E6'; ctx.lineWidth = 3; ctx.setLineDash([]); ctx.beginPath(); ctx.moveTo(ax, ay); ctx.lineTo(bx, by); ctx.stroke();
        }
    }

    // ---- geography overlays (consume window.gridsimGeo; no-op when absent) -------------------------------
    function geoApi() { return (typeof window !== 'undefined' && window.gridsimGeo) ? window.gridsimGeo : null; }
    function loadGeo(m, kind) {
        const api = geoApi(); if (!api) return;
        try {
            if (kind === 'coast' && !m.geo.coast && api.loadCoastline) Promise.resolve(api.loadCoastline()).then(d => { m.geo.coast = d || []; requestDraw(m); });
            if (kind === 'dno' && !m.geo.dno && api.loadRegions) Promise.resolve(api.loadRegions('dno')).then(d => { m.geo.dno = d || []; requestDraw(m); });
            if (kind === 'regions' && !m.geo.regions && api.loadRegions) Promise.resolve(api.loadRegions('gsp')).then(d => { m.geo.regions = d || []; requestDraw(m); });
            if (kind === 'assets' && !m.geo.assets && api.loadAssets) Promise.resolve(api.loadAssets()).then(d => { m.geo.assets = d || []; requestDraw(m); });
        } catch (_) { /* geo module malfunction -> silently stay off */ }
    }
    function drawGeoRegions(m, ctx) {
        if (!m.geolocated) return;
        if (m.toggles.coast && m.geo.coast) drawPolylines(m, ctx, m.geo.coast, 'rgba(51,72,92,0.8)', 'rgba(70,92,110,0.14)', 1.0);
        if (m.toggles.dno && m.geo.dno) drawRings(m, ctx, m.geo.dno, 'rgba(224,179,65,0.85)', 1.3);
        if (m.toggles.regions && m.geo.regions) drawRings(m, ctx, m.geo.regions, 'rgba(79,195,247,0.59)', 0.9);
    }
    function drawPolylines(m, ctx, polys, stroke, fill, w) {
        ctx.lineWidth = w; ctx.strokeStyle = stroke;
        for (const poly of polys) {
            if (!poly || poly.length < 2) continue;
            ctx.beginPath();
            for (let i = 0; i < poly.length; i++) { const [x, y] = project(m, poly[i][0], poly[i][1]); if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y); }
            if (fill) { ctx.fillStyle = fill; ctx.fill(); }
            ctx.stroke();
        }
    }
    function drawRings(m, ctx, regions, stroke, w) {
        ctx.lineWidth = w; ctx.strokeStyle = stroke; ctx.lineJoin = 'round';
        for (const rg of regions) for (const ring of (rg.rings || [])) {
            if (!ring || ring.length < 2) continue;
            ctx.beginPath();
            for (let i = 0; i < ring.length; i++) { const [x, y] = project(m, ring[i][0], ring[i][1]); if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y); }
            ctx.closePath(); ctx.stroke();
        }
    }
    function drawAssets(m, ctx) {
        const c = m.canvas, assets = m.geo.assets; if (!assets || !assets.length) return;
        let maxMw = 1; for (const a of assets) if (a.mw > maxMw) maxMw = a.mw;
        for (const a of assets) {
            const [x, y] = project(m, a.x, a.y);
            if (x < -12 || x > c.width + 12 || y < -12 || y > c.height + 12) continue;
            const relm = Math.sqrt(Math.max(0, a.mw) / maxMw), h = 2 + 5 * relm;
            ctx.fillStyle = a.color || '#9E9E9E'; ctx.strokeStyle = '#0B0F14'; ctx.lineWidth = 0.7;
            ctx.beginPath(); ctx.moveTo(x, y - h); ctx.lineTo(x + h, y); ctx.lineTo(x, y + h); ctx.lineTo(x - h, y); ctx.closePath();
            ctx.fill(); ctx.stroke();
        }
    }

    function drawBoundaries(m, ctx) {
        if (!m.boundaryEdges || m.boundaryEdges.size === 0) return;
        for (const idx of m.boundaryEdges) {
            const e = m.edgeByIndex.get(idx); if (!e || e.a < 0 || e.b < 0) continue;
            const st = m.edgeState[idx]; const col = st ? loadingC(st.loadingFrac) : AMBER;
            const [ax, ay] = nodeScreen(m, e.a), [bx, by] = nodeScreen(m, e.b);
            ctx.strokeStyle = rgba(col, 0.6); ctx.lineWidth = 7; ctx.lineCap = 'round';
            ctx.beginPath(); ctx.moveTo(ax, ay); ctx.lineTo(bx, by); ctx.stroke();
            const mx = (ax + bx) / 2, my = (ay + by) / 2, dx = bx - ax, dy = by - ay, len = Math.hypot(dx, dy);
            if (len > 1e-3) { const px = -dy / len, py = dx / len; ctx.strokeStyle = rgb(col); ctx.lineWidth = 2.2; ctx.beginPath(); ctx.moveTo(mx - px * 11, my - py * 11); ctx.lineTo(mx + px * 11, my + py * 11); ctx.stroke(); }
        }
    }

    // ---- minimap ----------------------------------------------------------------------------------------
    function drawMinimap(m, ctx) {
        const b = m.worldBounds, c = m.canvas; if (b.w <= 0 || b.h <= 0) return;
        const maxW = 156, maxH = 200, scale = Math.min(maxW / b.w, maxH / b.h);
        const mmW = b.w * scale, mmH = b.h * scale;
        if (mmW < 24 || mmH < 24 || c.height < mmH + 24) { m.mmRect = null; return; }
        const left = 12, top = c.height - mmH - 12;
        m.mmRect = { x: left, y: top, w: mmW, h: mmH }; m.mmScale = scale;
        ctx.fillStyle = 'rgba(10,16,22,0.77)'; ctx.strokeStyle = '#33414F'; ctx.lineWidth = 1;
        ctx.fillRect(left, top, mmW, mmH); ctx.strokeRect(left, top, mmW, mmH);
        ctx.save(); ctx.beginPath(); ctx.rect(left, top, mmW, mmH); ctx.clip();
        ctx.fillStyle = DIM;
        for (const n of m.nodes) { if (!m.showAll && n.kv < EHV_DISPLAY_KV) continue; const mx = left + (n.wx - b.x) * scale, my = top + (n.wy - b.y) * scale; ctx.fillRect(mx - 0.7, my - 0.7, 1.4, 1.4); }
        const [w0x, w0y] = unproject(m, 0, 0), [w1x, w1y] = unproject(m, c.width, c.height);
        ctx.strokeStyle = '#E0B341'; ctx.lineWidth = 1.4;
        ctx.strokeRect(left + (w0x - b.x) * scale, top + (w0y - b.y) * scale, (w1x - w0x) * scale, (w1y - w0y) * scale);
        ctx.restore();
    }
    function recenterFromMinimap(m, px, py) {
        const b = m.worldBounds;
        const wx = b.x + (px - m.mmRect.x) / m.mmScale, wy = b.y + (py - m.mmRect.y) / m.mmScale;
        m.view.ox = m.canvas.width / 2 - wx * m.view.zoom; m.view.oy = m.canvas.height / 2 - wy * m.view.zoom;
    }

    // ---- hit-testing ------------------------------------------------------------------------------------
    function hitNode(m, px, py) {
        const minKv = lodMinKv(m); let best = -1, bestD = Infinity, tol = NODE_PICK_RADIUS_PX;
        for (let i = 0; i < m.nodes.length; i++) {
            if (m.nodes[i].kv < minKv) continue;
            const [x, y] = nodeScreen(m, i), dx = x - px, dy = y - py, d2 = dx * dx + dy * dy;
            const r = roundelR(m, m.nodes[i].kv) + tol;
            if (d2 <= r * r && d2 < bestD) { bestD = d2; best = i; }
        }
        return best;
    }
    function hitBranch(m, px, py) {
        const minKv = lodMinKv(m), ehvOnly = minKv > 1; let best = -1, bestD = BRANCH_PICK_TOL_PX * BRANCH_PICK_TOL_PX;
        for (const e of m.edges) {
            if (e.a < 0 || e.b < 0) continue;
            const a = m.nodes[e.a], b = m.nodes[e.b];
            if (ehvOnly && (a.kv < minKv || b.kv < minKv)) continue;
            const [ax, ay] = nodeScreen(m, e.a), [bx, by] = nodeScreen(m, e.b);
            const d2 = distSqSeg(px, py, ax, ay, bx, by);
            if (d2 < bestD) { bestD = d2; best = e.index; }
        }
        return best;
    }

    // ---- detail payloads (assembled client-side, handed to Blazor for the fly-out) ----------------------
    function nodeDetail(m, ni) {
        const n = m.nodes[ni];
        const vm = m.vmById.get(n.id), va = m.paById.get(n.id), pinj = m.pinjById.get(n.id);
        let cons = 0, names = [];
        for (const e of m.edges) if (e.from === n.id || e.to === n.id) { cons++; if (names.length < 10) names.push(e.name || `${e.from}-${e.to}`); }
        // The topology fleet carries NAMEPLATE (MBase), not live output, so this row is labelled as capacity. Power-
        // flow-only devices are skipped: a 0 MW voltage-control unit is not 2,880 MVA of generating plant.
        let gen = 0; for (const g of m.fleet) if (g.bus === n.id && !g.powerFlowOnly) gen += g.mw;
        return {
            type: 'node', id: n.id, kind: 'SUBSTATION', name: n.name || `Bus ${n.id}`,
            sub: `${n.band || (n.kv | 0) + ' kV'} | ${n.type}`,
            rows: [
                ['Voltage', vm !== undefined ? vm.toFixed(4) + ' pu' : '-'],
                ['Angle', va !== undefined ? va.toFixed(2) + ' deg' : '-'],
                ['Net inj', pinj !== undefined ? pinj.toFixed(0) + ' MW' : '-'],
                ['Plant nameplate', gen > 0 ? gen.toFixed(0) + ' MVA' : '\u2014'],
                ['Base kV', (n.kv).toFixed(0) + ' kV'],
                ['Bus type', n.type],
            ],
            connections: `${cons} connection${cons === 1 ? '' : 's'}`, lines: names.join('  |  '),
        };
    }
    function branchDetail(m, idx) {
        const e = m.edgeByIndex.get(idx); if (!e) return null;
        const st = m.edgeState[idx];
        const na = e.a >= 0 ? m.nodes[e.a].name : e.from, nb = e.b >= 0 ? m.nodes[e.b].name : e.to;
        return {
            type: 'branch', id: idx, kind: e.tx ? 'TRANSFORMER' : 'CIRCUIT', name: e.name || `${na} -> ${nb}`,
            sub: `${na} -> ${nb}`,
            rows: [
                ['Flow', st ? st.pFromMw.toFixed(0) + ' MW' : '-'],
                ['Rating', e.rate > 0 ? e.rate.toFixed(0) + ' MVA' : '-'],
                ['Loading', st ? (st.loadingFrac * 100).toFixed(0) + ' %' : '-'],
                ['Losses', st && st.pLossMw !== undefined ? st.pLossMw.toFixed(2) + ' MW' : '-'],
                ['Q from', st && st.qFromMvar !== undefined ? st.qFromMvar.toFixed(0) + ' MVAr' : '-'],
                ['Type', e.tx ? 'Transformer' : 'Line'],
            ],
            connections: '', lines: '',
        };
    }

    // ---- interaction ------------------------------------------------------------------------------------
    function attach(m) {
        const c = m.canvas;
        c.tabIndex = 0; // focusable so keyboard shortcuts scope to the map
        if (getComputedStyle(c).cursor === 'auto') c.style.cursor = 'grab';

        c.addEventListener('mousedown', e => {
            if (e.button !== 0) return;
            c.focus();
            const [px, py] = local(c, e);
            if (m.toggles.minimap && m.mmRect && inRect(px, py, m.mmRect)) { recenterFromMinimap(m, px, py); draw(m); return; }
            m.dragging = true; m.moved = false; m.dx0 = e.clientX; m.dy0 = e.clientY; m.ox0 = m.view.ox; m.oy0 = m.view.oy;
        });
        m.onWinMove = e => {
            if (m.dragging) {
                const ddx = e.clientX - m.dx0, ddy = e.clientY - m.dy0;
                if (Math.hypot(ddx, ddy) > 3) m.moved = true;
                m.view.ox = m.ox0 + ddx; m.view.oy = m.oy0 + ddy; draw(m); return;
            }
            // hover tooltip
            const r = c.getBoundingClientRect();
            if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) { hideTip(m); return; }
            const px = e.clientX - r.left, py = e.clientY - r.top;
            const ni = hitNode(m, px, py);
            if (ni >= 0) { const n = m.nodes[ni]; const vm = m.vmById.get(n.id); showTip(m, e.clientX, e.clientY, `${n.name || 'Bus ' + n.id} | ${n.band || (n.kv | 0) + 'kV'}${vm !== undefined ? ' | ' + vm.toFixed(3) + ' pu' : ''}`); return; }
            const bi = hitBranch(m, px, py);
            if (bi >= 0) { const e2 = m.edgeByIndex.get(bi); const st = m.edgeState[bi]; showTip(m, e.clientX, e.clientY, `${e2.name || 'branch ' + bi}${st ? ' | ' + st.pFromMw.toFixed(0) + ' MW | ' + (st.loadingFrac * 100).toFixed(0) + '%' : ''}`); return; }
            hideTip(m);
        };
        m.onWinUp = e => {
            if (!m.dragging) return; m.dragging = false;
            if (m.moved) return;
            const [px, py] = local(c, e);
            const ni = hitNode(m, px, py);
            if (ni >= 0) { m.selected = { type: 'node', id: m.nodes[ni].id }; invoke(m, 'OnNodeSelected', nodeDetail(m, ni)); draw(m); return; }
            const bi = hitBranch(m, px, py);
            if (bi >= 0) { m.selected = { type: 'branch', id: bi }; invoke(m, 'OnBranchSelected', branchDetail(m, bi)); draw(m); return; }
        };
        window.addEventListener('mousemove', m.onWinMove);
        window.addEventListener('mouseup', m.onWinUp);

        c.addEventListener('dblclick', e => {
            const [px, py] = local(c, e);
            const ni = hitNode(m, px, py);
            if (ni >= 0) { const id = m.nodes[ni].id; centerOn(m, ni, Math.min(maxZoom(m), fitZoom(m) * 6)); invoke(m, 'OnDrill', id); draw(m); }
            else { zoomAt(m, px, py, 1.6); draw(m); }
        });

        c.addEventListener('contextmenu', e => {
            e.preventDefault();
            if (m.backdrop) return;   // raw backdrop: no TRIP
            const [px, py] = local(c, e);
            const ni = hitNode(m, px, py);
            if (ni >= 0) { m.selected = { type: 'node', id: m.nodes[ni].id }; invoke(m, 'OnTrip', m.nodes[ni].id); draw(m); }
        });

        c.addEventListener('wheel', e => {
            e.preventDefault();
            const [px, py] = local(c, e);
            zoomAt(m, px, py, e.deltaY < 0 ? 1.15 : 1 / 1.15); draw(m);
        }, { passive: false });

        c.addEventListener('keydown', e => {
            const tag = (e.target && e.target.tagName) || '';
            if (tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA') return;
            let handled = true;
            switch (e.key.toLowerCase()) {
                case 'f': fit(m); break;
                case 'm': flip(m, 'minimap'); break;
                case 'c': flip(m, 'coast'); break;
                case 'b': flip(m, 'boundaries'); break;
                case 'g': flip(m, 'gsp'); break;
                case 'd': flip(m, 'dno'); break;
                case 'r': flip(m, 'regions'); break;
                case 'a': flip(m, 'assets'); break;
                case 't': if (!m.backdrop) invoke(m, 'OnTripLargest', 0); break;   // raw backdrop: no TRIP
                case '.': m.dashSpeed = clamp(m.dashSpeed * 1.3, 2, 60); break; // finer/coarser flow step (map-local)
                case ',': m.dashSpeed = clamp(m.dashSpeed / 1.3, 2, 60); break;
                default: handled = false;
            }
            if (handled) { e.preventDefault(); draw(m); syncState(m); }
        });
    }

    function centerOn(m, ni, z) {
        m.view.zoom = clamp(z, minZoom(), maxZoom(m));
        const n = m.nodes[ni]; m.view.ox = m.canvas.width / 2 - n.wx * m.view.zoom; m.view.oy = m.canvas.height / 2 - n.wy * m.view.zoom;
    }
    function flip(m, name) {
        m.toggles[name] = !m.toggles[name];
        if (m.toggles[name]) { if (name === 'coast') loadGeo(m, 'coast'); if (name === 'dno') loadGeo(m, 'dno'); if (name === 'regions') loadGeo(m, 'regions'); if (name === 'assets') loadGeo(m, 'assets'); }
    }
    function local(c, e) { const r = c.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; }
    function inRect(x, y, r) { return x >= r.x && x <= r.x + r.w && y >= r.y && y <= r.y + r.h; }
    // GR-724: the try only catches a synchronous throw; the promise's own rejection (a disconnected circuit) is caught too,
    // so it is not an unhandled rejection in the browser console.
    function invoke(m, method, arg) { if (m.dotnet) { try { m.dotnet.invokeMethodAsync(method, arg).catch(() => { }); } catch (_) { } } }
    function syncState(m) { if (m.dotnet) { try { m.dotnet.invokeMethodAsync('OnMapStateChanged', { colorMode: m.colorMode, toggles: m.toggles }).catch(() => { }); } catch (_) { } } }

    // ---- tooltip (HTML overlay, managed here to avoid circuit churn) ------------------------------------
    function tipEl(m) {
        if (m.tip) return m.tip;
        const d = document.createElement('div');
        d.style.cssText = 'position:fixed;z-index:9999;pointer-events:none;display:none;padding:5px 8px;border-radius:4px;' +
            'background:#1B2430;border:1px solid #21C7E6;color:#E6EDF3;font:11px Bahnschrift,"Segoe UI",sans-serif;white-space:nowrap;box-shadow:0 2px 8px rgba(0,0,0,.5)';
        document.body.appendChild(d); m.tip = d; return d;
    }
    function showTip(m, cx, cy, text) { const d = tipEl(m); d.textContent = text; d.style.left = (cx + 14) + 'px'; d.style.top = (cy + 14) + 'px'; d.style.display = 'block'; }
    function hideTip(m) { if (m.tip) m.tip.style.display = 'none'; }

    // ---- animation loop (marching dash + wave; only while playing / in WAVE mode) -----------------------
    function shouldAnim(m) { return m.colorMode === 'wave' || (m.playing && m.hasState); }
    function requestDraw(m) { if (!m.raf) draw(m); }
    function ensureAnim(m) {
        if (m.raf || !shouldAnim(m)) return;
        m.lastT = performance.now(); m.waveStart = performance.now();
        m.raf = requestAnimationFrame(ts => tick(m, ts));
    }
    function tick(m, ts) {
        // R111: redraw at most every ANIM_MIN_INTERVAL_MS (~15 fps, the WPF cadence) rather than at display rate.
        // dt is the real time since the last drawn frame, so the dash marches at the same px/s as before.
        if (ts - m.lastT >= ANIM_MIN_INTERVAL_MS) {
            const dt = Math.min(ANIM_MAX_DT_S, Math.max(0, (ts - m.lastT) / 1000)); m.lastT = ts;
            if (m.colorMode === 'wave') m.waveT = (ts - m.waveStart) / 1000;
            else m.dashPhase += m.dashSpeed * dt * 3;
            draw(m);
        }
        if (shouldAnim(m)) m.raf = requestAnimationFrame(t => tick(m, t)); else m.raf = null;
    }

    // ---- exported interop -------------------------------------------------------------------------------
    window.gridsimMap = {
        setDotNetRef: function (canvasId, ref) { const m = ensure(canvasId); if (m) m.dotnet = ref; },

        setTopology: function (canvasId, topo) {
            const m = ensure(canvasId); if (!m) return;
            m.topo = topo; m.geolocated = !!topo.isGeolocated;
            m.vmById = new Map(); m.paById = new Map(); m.pinjById = new Map(); m.edgeState = [];
            m.condByBus = new Map(); m.stressByBranch = new Map(); m.hasState = false; m.hasOverlay = false;
            m.selected = null; m.clusters = null; m.sx = m.sy = m.visN = null;
            m.stateVer++;   // invalidate the cached layers (R111)
            buildNodes(m, topo);
            m.fitted = false; fit(m);
            // Auto-enable coast for geolocated GB models to match the "GB TRANSMISSION NETWORK" backdrop feel? WPF
            // leaves it off by default; keep parity - off until toggled.
            draw(m); syncState(m);
        },

        setState: function (canvasId, state) {
            const m = ensure(canvasId); if (!m || !m.topo) return;
            const vm = new Map(), pa = new Map(), pj = new Map();
            for (const n of state.nodes) { vm.set(n.id, n.vm); pa.set(n.id, n.vaDeg); pj.set(n.id, n.pInjMw); }
            m.vmById = vm; m.paById = pa; m.pinjById = pj;
            const es = []; for (const e of state.edges) es[e.index] = e; m.edgeState = es;
            m.hasState = true;
            m.stateVer++;   // new state: rebuild the cached layers on the next draw (R111)
            draw(m); ensureAnim(m);
        },

        // N022: the per-frame state in column form (MapStatePayload): one slot per topology node / edge, in the
        // order of the topology this map holds, so ids and property names are not resent every frame. A null slot
        // means "no state for this node / edge" - the same as the id being absent from the old arrays. The columns
        // are turned back into exactly the rows setState has always taken (same doubles, same field names) and handed
        // to setState, so everything downstream (colours, tooltips, detail panel, animation) runs the one code path.
        setStateColumns: function (canvasId, s) {
            const m = ensure(canvasId); if (!m || !m.topo || !s) return;
            const tn = m.topo.nodes, te = m.topo.edges;
            // Fail loud in the console rather than mis-assign values to the wrong buses / branches.
            if (s.vm.length !== tn.length || s.ep.length !== te.length) {
                console.error('gridsimMap.setStateColumns: payload does not match the topology (' +
                    s.vm.length + '/' + tn.length + ' nodes, ' + s.ep.length + '/' + te.length + ' edges); frame ignored');
                return;
            }
            const nodes = [];
            for (let i = 0; i < tn.length; i++) {
                if (s.vm[i] === null) continue;   // no state for this bus
                nodes.push({ id: tn[i].id, vm: s.vm[i], vaDeg: s.va[i] ?? undefined, pInjMw: s.pi[i] ?? undefined });
            }
            const edges = [];
            for (let i = 0; i < te.length; i++) {
                // An edge needs its flow and loading to be drawn; Q and losses are optional detail-panel rows
                // (the panel already shows "-" for an undefined field).
                if (s.ep[i] === null || s.ef[i] === null) continue;
                edges.push({ index: te[i].index, pFromMw: s.ep[i], qFromMvar: s.eq[i] ?? undefined,
                             pLossMw: s.el[i] ?? undefined, loadingFrac: s.ef[i] });
            }
            window.gridsimMap.setState(canvasId, { nodes: nodes, edges: edges });
        },

        setOverlays: function (canvasId, overlays) {
            const m = ensure(canvasId); if (!m || !overlays) return;
            m.condByBus = new Map(); m.stressByBranch = new Map();
            if (overlays.conditionByBus) for (const k in overlays.conditionByBus) m.condByBus.set(+k, overlays.conditionByBus[k]);
            if (overlays.stressByBranch) for (const k in overlays.stressByBranch) m.stressByBranch.set(k, overlays.stressByBranch[k]);
            m.hasOverlay = m.condByBus.size > 0 || m.stressByBranch.size > 0;
            m.stateVer++;   // overlays colour corridors / rings: rebuild the cached layers (R111)
            if (m.colorMode === 'condition' || m.colorMode === 'stress') draw(m);
        },

        setColorMode: function (canvasId, mode) {
            const m = ensure(canvasId); if (!m) return;
            m.colorMode = mode;
            if (mode === 'wave') { m.waveStart = performance.now(); }
            draw(m); ensureAnim(m);
        },

        cycleMode: function (canvasId) {
            const m = ensure(canvasId); if (!m) return m ? m.colorMode : 'loading';
            const order = ['loading', 'voltage', 'condition', 'stress', 'wave'];
            m.colorMode = order[(order.indexOf(m.colorMode) + 1) % order.length];
            window.gridsimMap.setColorMode(canvasId, m.colorMode);
            return m.colorMode;
        },

        setWaveField: function (canvasId, field) {
            const m = ensure(canvasId); if (!m || !field) return;
            const nodes = new Map(); (field.nodes || []).forEach(n => nodes.set(n.bus, { amp: n.amp, phase: n.phase }));
            m.waveField = { freq: field.frequencyHz || 0.5, nodes };
        },

        setPlaying: function (canvasId, playing) {
            const m = ensure(canvasId); if (!m) return;
            m.playing = !!playing; if (m.playing) ensureAnim(m);
        },

        // Static-topology backdrop (pure RAW replay): disable the right-click / T-key TRIP (there is no solve to
        // trip in raw mode). The graph still renders; the national frequency wash is a Razor overlay, not a JS draw.
        setBackdrop: function (canvasId, on) {
            const m = ensure(canvasId); if (m) m.backdrop = !!on;
        },

        setToggle: function (canvasId, name, on) {
            const m = ensure(canvasId); if (!m || !(name in m.toggles)) return;
            if (m.toggles[name] === !!on) return;
            flip(m, name); draw(m); syncState(m);
        },

        fit: function (canvasId) { const m = ensure(canvasId); if (m) { fit(m); draw(m); } },

        // Substation search: select a bus by id and centre the view on it (mirrors click-select + dbl-click drill).
        // Returns true when the bus exists in the current topology, false otherwise.
        focusBus: function (canvasId, busId) {
            const m = ensure(canvasId); if (!m || !m.nodes || !m.idIndex) return false;
            const ni = m.idIndex.get(busId);
            if (ni === undefined) return false;
            m.selected = { type: 'node', id: busId };
            // Zoom in enough that the bus clears the LOD threshold (so its selection ring actually draws),
            // keeping any already-closer zoom. centerOn clamps the zoom and re-centres via the node's world coords.
            centerOn(m, ni, Math.min(maxZoom(m), Math.max(m.view.zoom, fitZoom(m) * 6)));
            draw(m);
            return true;
        },

        getLegend: function (mode) { return LEGENDS[mode] || LEGENDS.loading; },

        clearSelection: function (canvasId) { const m = ensure(canvasId); if (m) { m.selected = null; draw(m); } },

        dispose: function (canvasId) {
            const m = maps.get(canvasId);
            if (m) {
                if (m.raf) cancelAnimationFrame(m.raf);
                if (m.onWinMove) window.removeEventListener('mousemove', m.onWinMove);
                if (m.onWinUp) window.removeEventListener('mouseup', m.onWinUp);
                if (m.tip && m.tip.parentNode) m.tip.parentNode.removeChild(m.tip);
                if (m.dotnet && m.dotnet.dispose) { try { m.dotnet.dispose(); } catch (_) { } }
            }
            maps.delete(canvasId);
        },
    };
})();
