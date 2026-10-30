// GridSim network-map geography loader.
// Exposes window.gridsimGeo with the shared equirectangular projection + async loaders for the
// coastline, DNO/GSP boundary overlays, geocoded generation-asset markers and interconnector routes.
//
// Projection contract (MUST match the map renderer and bus world coords):
//   Equirectangular GB box  LonMin -8.2, LonMax 1.9, LatMin 49.8, LatMax 59.0
//   world x = (lon-LonMin)/(LonMax-LonMin)   (0..1 west->east)
//   world y = 1 - (lat-LatMin)/(LatMax-LatMin)  (north-down, to match the bus world y)
//
// The coastline + interconnector JSON are pre-projected world coords and ship in wwwroot/geo/.
// The DNO/GSP/asset GeoJSON stay OUT of the repo (git-ignored gda-out/geo). This loader fetches
// them if the server maps them under /geo/ and degrades to [] (never throws) on a 404, so the
// overlays are simply unavailable when the data isn't served - exactly like the WPF app.
(function () {
    'use strict';

    const LON_MIN = -8.2, LON_MAX = 1.9, LAT_MIN = 49.8, LAT_MAX = 59.0;
    const BASE = 'geo/'; // relative to the app base href

    // lon/lat -> [x, y] world coords.
    function project(lon, lat) {
        const x = (lon - LON_MIN) / (LON_MAX - LON_MIN);
        const yUp = (lat - LAT_MIN) / (LAT_MAX - LAT_MIN);
        return [x, 1.0 - yUp];
    }

    // --- fuel taxonomy (JS mirror of GridSim.Core.Presentation.FuelTaxonomy.Classify) ------------
    // Maps a generator's free-text technology/fuel onto the same hex colours the WPF/web palette
    // uses, so asset markers read identically. First keyword hit wins (order matters).
    //
    // N016 (October 2026): this used to be a hand-kept copy that had drifted from Core - it lacked the hydrogen rule
    // (so "hydrogen" read as hydro blue) and the reciprocating-engine rule (grey "other"), and carried two keywords Core
    // does not have ('efw', 'incinerat'). The browser cannot call Core, so the table stays here, but it is now plain
    // data in exactly Core's order and colours, and FuelTaxonomyJsAntiDriftTests parses FUEL_RULES and checks every
    // rule and keyword against FuelTaxonomy.Classify: the build fails if the two ever disagree. Change the taxonomy in
    // Core first, then mirror it here. Colour changes from the old copy: hydrogen #29B6F6 -> #80CBC4, recip / diesel /
    // engine #9E9E9E -> #A1887F, and 'efw' / 'incinerat' (without "waste" in the name) #8BC34A -> #9E9E9E, matching
    // how Core (and so the web and WPF asset views) already class them.
    //
    // R305 rest / N020 (October 2026): Core's keyword list now lives in GridSim.Core.Model.FuelTypes (one taxonomy for
    // the importer, the shells, the DC-OPF merit order and carbon tracing) and matches whole words by their START, not
    // any substring ("Charcoal" is not coal, "Microwave" not wave power). This table mirrors the keywords the
    // presentation reader uses, in Core's order, one row per kind, and fuelColor matches the same way. Changes from the
    // previous table: Core's order (pumped storage and tidal moved up, coal after CHP); 'combined' became
    // 'combined cycle' (CCGT) and 'combined heat' (CHP), so "CHP (Combined Heat and Power)" is CHP not CCGT, and a bare
    // 'combined' reads as gas-fired plant (the same colour CCGT has); 'gas turbine' is OCGT; 'onshore' is onshore wind
    // (the type data/uk and data/gb-synthetic use; it was grey); 'reciprocating' is listed beside 'recip'.
    const FUEL_RULES = [
        [['nuclear'], '#7E57C2'],
        [['pumped'], '#26A69A'],
        [['ccgt', 'combined cycle'], '#FF8A3D'],
        [['ocgt', 'open cycle', 'gas turbine', 'peaker'], '#FFB55A'],
        [['chp', 'cogen', 'combined heat'], '#D98A45'],
        [['coal'], '#5D4037'],
        [['biomass', 'biogas', 'landfill', 'waste'], '#8BC34A'],
        [['hydrogen', 'fuel cell', 'electrolys', 'electrolyz'], '#80CBC4'],
        [['hydro'], '#29B6F6'],
        [['tidal', 'wave', 'marine'], '#26C6DA'],
        [['offshore'], '#00B8D4'],
        [['wind', 'onshore'], '#4DD0E1'],
        [['solar', 'pv', 'photovolt'], '#FFD54F'],
        [['storage', 'battery', 'bess'], '#EC407A'],
        [['interconnector', 'hvdc', 'import'], '#90A4AE'],
        [['grid', 'infeed'], '#607D8B'],
        [['gas', 'combined'], '#FF8A3D'],
        [['reciprocating', 'recip', 'diesel', 'engine'], '#A1887F'],
    ];
    const FUEL_OTHER = '#9E9E9E';

    // The words of a string: lower case, split at every character that is not a letter or digit.
    function fuelWords(type) {
        return (type || '').trim().toLowerCase().split(/[^0-9a-z]+/).filter(w => w.length > 0);
    }

    // True when every word of the keyword phrase is the start of consecutive words of the string.
    function phraseMatches(phrase, words) {
        const p = phrase.split(' ');
        for (let i = 0; i + p.length <= words.length; i++) {
            let all = true;
            for (let j = 0; j < p.length && all; j++) all = words[i + j].startsWith(p[j]);
            if (all) return true;
        }
        return false;
    }

    function fuelColor(type) {
        const words = fuelWords(type);
        for (const [keys, color] of FUEL_RULES) if (keys.some(k => phraseMatches(k, words))) return color;
        return FUEL_OTHER;
    }

    // --- fetch helpers -----------------------------------------------------------------------
    // Fetch JSON, returning null on 404/network error/parse error so callers degrade to [].
    async function fetchJsonOrNull(url) {
        let resp;
        try {
            resp = await fetch(url, { cache: 'no-cache' });
        } catch {
            return null; // network / offline
        }
        if (!resp.ok) return null; // 404 -> overlay unavailable
        try {
            return await resp.json();
        } catch {
            return null; // malformed file can't stop the app
        }
    }

    // Walk a GeoJSON geometry into a flat list of projected rings (Polygon rings + MultiPolygon
    // polygons' rings). Holes are kept as their own rings - for an outline overlay that's exactly
    // what we want to stroke. Matches GeographyData.CollectRings.
    function collectRings(geom) {
        const rings = [];
        if (!geom || !geom.type || !geom.coordinates) return rings;
        const addRing = (ring) => {
            const pts = [];
            for (const p of ring) {
                if (p && p.length >= 2) pts.push(project(p[0], p[1]));
            }
            if (pts.length >= 2) rings.push(pts);
        };
        if (geom.type === 'Polygon') {
            for (const ring of geom.coordinates) addRing(ring);
        } else if (geom.type === 'MultiPolygon') {
            for (const poly of geom.coordinates) for (const ring of poly) addRing(ring);
        }
        return rings;
    }

    function prop(feature, name) {
        return feature && feature.properties ? feature.properties[name] : undefined;
    }

    // --- caches (each layer loaded at most once) ---------------------------------------------
    const cache = {}; // key -> Promise

    function once(key, factory) {
        if (!cache[key]) cache[key] = factory();
        return cache[key];
    }

    // coastline: pre-projected world coords in wwwroot/geo/coastline-gb.json.
    // Returns Promise<number[][][]> - array of polylines, each an array of [x,y].
    function loadCoastline() {
        return once('coastline', async () => {
            const data = await fetchJsonOrNull(BASE + 'coastline-gb.json');
            if (!data || !Array.isArray(data.polylines)) return [];
            return data.polylines;
        });
    }

    // DNO / GSP boundary overlays from GeoJSON (git-ignored; may be absent -> []).
    // Returns Promise<Array<{label, tag, rings: number[][][]}>>.
    const REGION_CFG = {
        dno: { file: 'dno_areas.geojson', labelProp: 'area', tagProp: 'dno' },
        gsp: { file: 'gsp_regions.geojson', labelProp: 'gsps', tagProp: 'gspgroup' },
    };
    function loadRegions(which) {
        const cfg = REGION_CFG[which];
        if (!cfg) return Promise.resolve([]);
        return once('regions:' + which, async () => {
            const data = await fetchJsonOrNull(BASE + cfg.file);
            if (!data || !Array.isArray(data.features)) return [];
            const out = [];
            for (const ft of data.features) {
                const rings = collectRings(ft.geometry);
                if (rings.length === 0) continue;
                out.push({
                    label: prop(ft, cfg.labelProp) || '',
                    tag: prop(ft, cfg.tagProp) || '',
                    rings,
                });
            }
            return out;
        });
    }

    // Geocoded generation-asset markers from GeoJSON (git-ignored; may be absent -> []).
    // Returns Promise<Array<{x, y, mw, tech, name, color}>>.
    function loadAssets() {
        return once('assets', async () => {
            const data = await fetchJsonOrNull(BASE + 'assets.geojson');
            if (!data || !Array.isArray(data.features)) return [];
            const out = [];
            for (const ft of data.features) {
                const g = ft.geometry;
                if (!g || !g.coordinates || g.coordinates.length < 2) continue;
                const [x, y] = project(g.coordinates[0], g.coordinates[1]);
                const tech = prop(ft, 'technology_or_fuel') || '';
                out.push({
                    x, y,
                    mw: Number(prop(ft, 'capacity_mw')) || 0,
                    tech,
                    name: prop(ft, 'name') || '',
                    color: fuelColor(tech),
                });
            }
            return out;
        });
    }

    // Interconnector routes: pre-projected world coords in wwwroot/geo/interconnectors.json.
    // Returns Promise<Array<{name, country, landing, landingWorld:[x,y], seaWorld:[x,y]}>>.
    function loadInterconnectors() {
        return once('interconnectors', async () => {
            const data = await fetchJsonOrNull(BASE + 'interconnectors.json');
            if (!data || !Array.isArray(data.links)) return [];
            return data.links;
        });
    }

    window.gridsimGeo = {
        project,
        fuelColor,
        loadCoastline,
        loadRegions,
        loadAssets,
        loadInterconnectors,
    };
})();
