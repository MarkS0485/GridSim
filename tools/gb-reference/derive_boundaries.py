#!/usr/bin/env python3
"""Derive ETYS boundary cuts for the bundled PyPSA-GB reduced and zonal networks (S2 / P02, October 2026).

Why this exists
---------------
GridSim reports the flow across a transmission boundary (B4, B6, ...) as the signed sum of the flows on the branches
that cross it (`GridSim.Core.Analysis.BoundaryAnalysis`). The PyPSA-GB reduced (29-bus) and zonal (20-zone) networks
do not say which of their lines or links cross which boundary, so this script works it out from the OFFICIAL boundary
geometry, never by hand:

  * boundary lines: NESO "ETYS boundary GIS data Mar25" shapefile (polylines, Web Mercator EPSG:3857), shipped by
    PyPSA-GB @ 8e084af under data/network/GSP/ and copied to docs/reference/etys-pypsa-gb/network/GSP/;
  * bus positions: PyPSA-GB reduced_network/buses.csv (OSGB36 British National Grid easting / northing, EPSG:27700)
    and zonal/buses.csv (WGS84 latitude / longitude of each zone's representative point).

Method (plain and checkable)
----------------------------
1. Every bus position is converted to Web Mercator metres, the shapefile's projection. OSGB easting / northing is
   first converted to WGS84 latitude / longitude with the Ordnance Survey method ("A guide to coordinate systems in
   Great Britain", OS, v3.6 2020: inverse transverse Mercator on the Airy 1830 ellipsoid, then the 7-parameter
   Helmert OSGB36 -> WGS84 transformation, about 5 m worst case; ample for deciding which side of a boundary a
   substation lies on, the nearest bus being many kilometres from any boundary line).
2. A line (or zonal link) CROSSES a boundary when the straight segment between its two end buses intersects the
   boundary polyline an odd number of times. The ETYS boundary lines are open, slanted polylines that end off the
   coasts, so a "which side is this point" ray test is not reliable for buses beyond a line's ends (tried first and
   rejected for that reason); the segment test asks only whether a circuit's two ends are separated by the drawn
   boundary, which is what a cut is.
3. Self-check: with the crossing circuits removed, the network must fall into exactly two connected parts, and every
   crossing circuit must join the two. The part holding the most northerly bus is NORTH. Anything else stops the
   script with an error, so no boundary is written that is not a clean cut.
4. Direction is +1 when the circuit's FROM end (bus0) is in the north part, so a positive boundary flow is a
   north-to-south transfer, the convention of the bundled gb-full boundaries (for example B6 positive = Scotland to
   England).

Only the four boundaries that the bundled gb-full case also defines are derived (B4, B6, B7, B8), so the reduced
models and gb-full can be compared like for like (P05 cross-check report). No transfer limit is invented: limitMw is 0,
which GridSim reads as "no limit authored" (BoundaryAnalysis reports the flow and never an overload).

Outputs (overwritten; review the diff before committing):
  data/matpower/gb/pypsa-gb/reduced/boundaries.json        GridSim boundary format, branch names "<bus0>-<bus1> #<name>"
  data/matpower/gb/pypsa-gb/zonal/boundaries.json          same format, the link names of zonal/links.csv
  data/matpower/gb/pypsa-gb/boundary-sides.json            for audit: which buses / zones are north of each boundary

Usage: python tools/gb-reference/derive_boundaries.py      (from the repo root; pure Python 3, no packages)
"""
import csv
import json
import math
import os
import struct
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
SHP = os.path.join(ROOT, 'docs', 'reference', 'etys-pypsa-gb', 'network', 'GSP', 'etys-boundary-gis-data-mar25',
                   'ETYS boundary GIS data Mar25')
OUT = os.path.join(ROOT, 'data', 'matpower', 'gb', 'pypsa-gb')

# The boundaries derived, in report order, with the gb-full boundary name each corresponds to (data/gb-full/
# boundaries.json), so the two sets of results line up by name.
BOUNDARIES = [
    ('B4', 'B4 SHETL-SPT (SSE-SP)'),
    ('B6', 'B6 Scotland-England (SCOTEX)'),
    ('B7', 'B7 Upper North England (HARSPNBLY)'),
    ('B8', 'B8 North to Midlands (FLOWSTH)'),
]

# ---- OSGB36 grid -> WGS84 (Ordnance Survey, "A guide to coordinate systems in Great Britain") -----------------------
AIRY_A, AIRY_B = 6377563.396, 6356256.909          # Airy 1830 ellipsoid (OSGB36 datum), metres
GRS80_A, GRS80_B = 6378137.000, 6356752.3141       # GRS80 / WGS84 ellipsoid, metres
F0 = 0.9996012717                                   # National Grid scale factor on the central meridian
LAT0, LON0 = math.radians(49.0), math.radians(-2.0)  # true origin 49 N, 2 W
E0, N0 = 400000.0, -100000.0                        # false easting / northing, metres
# Helmert OSGB36 -> WGS84: translations (m), scale (ppm), rotations (arc-seconds)
TX, TY, TZ, S_PPM, RX_S, RY_S, RZ_S = 446.448, -125.157, 542.060, -20.4894, 0.1502, 0.2470, 0.8421


def _meridional_arc(lat):
    """Developed meridional arc M from the true origin to latitude lat (radians), Airy 1830, scaled by F0."""
    n = (AIRY_A - AIRY_B) / (AIRY_A + AIRY_B)
    n2, n3 = n * n, n * n * n
    d, s = lat - LAT0, lat + LAT0
    return AIRY_B * F0 * ((1 + n + 1.25 * n2 + 1.25 * n3) * d
                          - (3 * n + 3 * n2 + 2.625 * n3) * math.sin(d) * math.cos(s)
                          + (1.875 * n2 + 1.875 * n3) * math.sin(2 * d) * math.cos(2 * s)
                          - (35.0 / 24.0) * n3 * math.sin(3 * d) * math.cos(3 * s))


def osgb_to_wgs84(easting, northing):
    """OSGB36 National Grid easting / northing (m) -> WGS84 latitude, longitude (degrees)."""
    a, b = AIRY_A, AIRY_B
    e2 = 1 - (b * b) / (a * a)
    lat, m = LAT0, 0.0
    # Iterate the latitude until the meridional arc matches the northing to 0.01 mm (OS guide, C.6).
    while True:
        lat = (northing - N0 - m) / (a * F0) + lat
        m = _meridional_arc(lat)
        if abs(northing - N0 - m) < 1e-5:
            break
    s, c, t = math.sin(lat), math.cos(lat), math.tan(lat)
    nu = a * F0 / math.sqrt(1 - e2 * s * s)
    rho = a * F0 * (1 - e2) / (1 - e2 * s * s) ** 1.5
    eta2 = nu / rho - 1
    vii = t / (2 * rho * nu)
    viii = t / (24 * rho * nu ** 3) * (5 + 3 * t * t + eta2 - 9 * t * t * eta2)
    ix = t / (720 * rho * nu ** 5) * (61 + 90 * t * t + 45 * t ** 4)
    x_ = 1 / (c * nu)
    xi = 1 / (c * 6 * nu ** 3) * (nu / rho + 2 * t * t)
    xii = 1 / (c * 120 * nu ** 5) * (5 + 28 * t * t + 24 * t ** 4)
    xiia = 1 / (c * 5040 * nu ** 7) * (61 + 662 * t * t + 1320 * t ** 4 + 720 * t ** 6)
    de = easting - E0
    lat_o = lat - vii * de ** 2 + viii * de ** 4 - ix * de ** 6
    lon_o = LON0 + x_ * de - xi * de ** 3 + xii * de ** 5 - xiia * de ** 7
    # OSGB36 geodetic -> cartesian (Airy), Helmert to WGS84, cartesian -> geodetic (GRS80).
    sl, cl = math.sin(lat_o), math.cos(lat_o)
    nu_a = a / math.sqrt(1 - e2 * sl * sl)
    x1, y1, z1 = nu_a * cl * math.cos(lon_o), nu_a * cl * math.sin(lon_o), (1 - e2) * nu_a * sl
    sc = S_PPM * 1e-6
    rx, ry, rz = (math.radians(v / 3600.0) for v in (RX_S, RY_S, RZ_S))
    x2 = TX + (1 + sc) * x1 - rz * y1 + ry * z1
    y2 = TY + rz * x1 + (1 + sc) * y1 - rx * z1
    z2 = TZ - ry * x1 + rx * y1 + (1 + sc) * z1
    a2, b2 = GRS80_A, GRS80_B
    e2w = 1 - (b2 * b2) / (a2 * a2)
    p = math.hypot(x2, y2)
    phi = math.atan2(z2, p * (1 - e2w))
    for _ in range(20):
        nu2 = a2 / math.sqrt(1 - e2w * math.sin(phi) ** 2)
        phi = math.atan2(z2 + e2w * nu2 * math.sin(phi), p)
    return math.degrees(phi), math.degrees(math.atan2(y2, x2))


def wgs84_to_web_mercator(lat, lon):
    """WGS84 latitude / longitude (degrees) -> Web Mercator EPSG:3857 metres (spherical, R = 6378137 m)."""
    r = 6378137.0
    return r * math.radians(lon), r * math.log(math.tan(math.pi / 4 + math.radians(lat) / 2))


# ---- shapefile (polyline, type 3) and its .dbf names -----------------------------------------------------------------
def read_boundary_lines():
    """Return {boundary name: [(x, y), ...]} from the ETYS boundary shapefile (single-part polylines)."""
    shp = open(SHP + '.shp', 'rb').read()
    dbf = open(SHP + '.dbf', 'rb').read()
    if struct.unpack('>i', shp[0:4])[0] != 9994 or struct.unpack('<i', shp[32:36])[0] != 3:
        sys.exit('error: the ETYS boundary shapefile is not a polyline (type 3) shapefile')
    # .dbf: header length and record length, then the first character field (Boundary_n) of each record.
    nrec = struct.unpack('<I', dbf[4:8])[0]
    hlen, rlen = struct.unpack('<H', dbf[8:10])[0], struct.unpack('<H', dbf[10:12])[0]
    flen = dbf[32 + 16]
    names = [dbf[hlen + i * rlen + 1: hlen + i * rlen + 1 + flen].decode('latin1').strip() for i in range(nrec)]
    lines, off, i = {}, 100, 0
    while off < len(shp):
        _, length = struct.unpack('>ii', shp[off:off + 8])
        nparts, npts = struct.unpack('<ii', shp[off + 44:off + 52])
        if nparts != 1:
            sys.exit(f'error: boundary {names[i]} has {nparts} parts; this script expects single-part polylines')
        base = off + 52 + 4 * nparts
        lines[names[i]] = [struct.unpack('<2d', shp[base + 16 * k: base + 16 * k + 16]) for k in range(npts)]
        off += 8 + length * 2
        i += 1
    if i != nrec:
        sys.exit(f'error: shapefile has {i} shapes but the .dbf has {nrec} records')
    return lines


def _segments_cross(p1, p2, q1, q2):
    """True when segment p1-p2 properly intersects segment q1-q2 (orientation test). A touch or a collinear overlap
    counts as no crossing; with real coordinates in metres neither happens here, and the self-check in cut() would
    catch the consequence if it did."""
    def orient(a, b, c):
        return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])
    d1, d2 = orient(q1, q2, p1), orient(q1, q2, p2)
    d3, d4 = orient(p1, p2, q1), orient(p1, p2, q2)
    if 0 in (d1, d2, d3, d4):
        return False
    return (d1 > 0) != (d2 > 0) and (d3 > 0) != (d4 > 0)


def crosses(a, b, line):
    """True when the straight segment a-b crosses the polyline an odd number of times."""
    n = sum(1 for q1, q2 in zip(line, line[1:]) if _segments_cross(a, b, q1, q2))
    return n % 2 == 1


def cut(name, nodes, edges, pos, line):
    """Split the nodes by a boundary polyline. edges: list of (label, bus0, bus1). Returns (north set, crossing edges).
    Stops with an error unless removing the crossing edges leaves exactly two connected parts, joined only by them."""
    crossing = [e for e in edges if crosses(pos[e[1]], pos[e[2]], line)]
    keep = [e for e in edges if e not in crossing]
    parent = {n: n for n in nodes}

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x
    for _, u, v in keep:
        parent[find(u)] = find(v)
    parts = {}
    for n in nodes:
        parts.setdefault(find(n), set()).add(n)
    if len(parts) != 2:
        sys.exit(f'error: {name}: removing the {len(crossing)} crossing circuit(s) leaves {len(parts)} part(s), not 2: '
                 + ' | '.join(sorted(', '.join(sorted(p)) for p in parts.values())))
    northmost = max(nodes, key=lambda n: pos[n][1])
    north = parts[find(northmost)]
    for label, u, v in crossing:
        if (u in north) == (v in north):
            sys.exit(f'error: {name}: crossing circuit {label} has both ends on one side')
    return north, crossing


def main():
    lines = read_boundary_lines()
    red = os.path.join(ROOT, 'docs', 'reference', 'etys-pypsa-gb', 'network', 'reduced_network')
    zon = os.path.join(ROOT, 'docs', 'reference', 'etys-pypsa-gb', 'network', 'zonal')
    rbuses = list(csv.DictReader(open(os.path.join(red, 'buses.csv'), newline='')))
    rlines = list(csv.DictReader(open(os.path.join(red, 'lines.csv'), newline='')))
    zbuses = list(csv.DictReader(open(os.path.join(zon, 'buses.csv'), newline='')))
    zlinks = list(csv.DictReader(open(os.path.join(zon, 'links.csv'), newline='')))

    rpos = {b['name']: wgs84_to_web_mercator(*osgb_to_wgs84(float(b['x']), float(b['y']))) for b in rbuses}
    zpos = {b['name']: wgs84_to_web_mercator(float(b['y']), float(b['x'])) for b in zbuses}

    rnodes = [b['name'] for b in rbuses]
    znodes = [b['name'] for b in zbuses]
    # Branch labels exactly as GridSim's PyPSA-GB importer names the lines (PypsaGbReduced.BranchName).
    redges = [(f"{l['bus0']}-{l['bus1']} #{l['name']}", l['bus0'], l['bus1']) for l in rlines]
    # Only links between two GB zones: the six interconnector links end at a foreign node (Netherlands, France1, ...)
    # that is not a zone and has no position here; they cross no internal GB boundary.
    zedges = [(l['name'], l['bus0'], l['bus1']) for l in zlinks if l['bus0'] in zpos and l['bus1'] in zpos]

    sides, red_out, zon_out = {}, [], []
    for short, full in BOUNDARIES:
        line = lines[short]
        rnorth, rcross = cut(short + ' reduced', rnodes, redges, rpos, line)
        znorth, zcross = cut(short + ' zonal', znodes, zedges, zpos, line)
        sides[short] = {'gbFullName': full,
                        'reducedNorth': [n for n in rnodes if n in rnorth],
                        'zonalNorth': [n for n in znodes if n in znorth]}
        red_out.append({'name': full, 'limitMw': 0,
                        'branches': [{'branch': e[0], 'direction': 1 if e[1] in rnorth else -1} for e in rcross]})
        zon_out.append({'name': full, 'limitMw': 0,
                        'branches': [{'branch': e[0], 'direction': 1 if e[1] in znorth else -1} for e in zcross]})

    def dump(path, obj):
        with open(path, 'w', newline='\n') as f:
            json.dump(obj, f, indent=2)
            f.write('\n')

    dump(os.path.join(OUT, 'reduced', 'boundaries.json'), red_out)
    dump(os.path.join(OUT, 'zonal', 'boundaries.json'), zon_out)
    dump(os.path.join(OUT, 'boundary-sides.json'), sides)
    for short, _ in BOUNDARIES:
        print(short, 'reduced north:', sides[short]['reducedNorth'])
        print(short, 'zonal north:', sides[short]['zonalNorth'])


if __name__ == '__main__':
    main()
