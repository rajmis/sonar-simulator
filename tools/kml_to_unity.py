#!/usr/bin/env python3
"""
kml_to_unity.py

Parses a Google Maps KML export and converts everything to Unity coordinates.
Handles both individual placemarks (pillars, jetty points, gate markers) and
path/line features (breakwater traced path).

Usage:
    python kml_to_unity.py my_export.kml

Outputs:
    lagoon_unity.csv          — all points with Unity XZ coordinates + auto-detected types
    breakwater_vertices.csv   — ordered breakwater path vertices for ProBuilder tracing
    scene_summary.txt         — human-readable summary of what was found

The origin point is auto-detected from placemarks containing "origin" or "deploy"
in their name. All coordinates are converted relative to that origin.
"""

import xml.etree.ElementTree as ET
import csv
import math
import sys
import re
from pathlib import Path
from dataclasses import dataclass, field


# ============================================================
# Data structures
# ============================================================

@dataclass
class UnityPoint:
    point_id: str
    point_type: str
    unity_x: float  # East
    unity_z: float  # North
    lat: float
    lng: float
    notes: str = ""


@dataclass
class PathData:
    name: str
    vertices: list = field(default_factory=list)  # [(lat, lng), ...]


# ============================================================
# KML Parsing
# ============================================================

# KML namespace
KML_NS = {"kml": "http://www.opengis.net/kml/2.2"}


def parse_kml(filepath: str):
    """Parse a KML file and extract all placemarks and paths."""
    tree = ET.parse(filepath)
    root = tree.getroot()

    # Handle KML files with or without namespace
    ns = ""
    if root.tag.startswith("{"):
        ns = root.tag.split("}")[0] + "}"

    placemarks = []
    paths = []

    for pm in root.iter(f"{ns}Placemark"):
        name_elem = pm.find(f"{ns}name")
        name = name_elem.text.strip() if name_elem is not None and name_elem.text else "unnamed"

        # Check for Point geometry
        point = pm.find(f"{ns}Point")
        if point is not None:
            coords_elem = point.find(f"{ns}coordinates")
            if coords_elem is not None and coords_elem.text:
                coords = parse_coordinate_string(coords_elem.text.strip())
                if coords:
                    lng, lat = coords[0]
                    placemarks.append((name, lat, lng))
                    continue

        # Check for LineString geometry (traced paths)
        linestring = pm.find(f"{ns}LineString")
        if linestring is not None:
            coords_elem = linestring.find(f"{ns}coordinates")
            if coords_elem is not None and coords_elem.text:
                coords = parse_coordinate_string(coords_elem.text.strip())
                if coords:
                    path = PathData(name=name)
                    path.vertices = [(lat, lng) for lng, lat in coords]
                    paths.append(path)
                    continue

        # Check for Polygon geometry
        polygon = pm.find(f"{ns}Polygon")
        if polygon is not None:
            outer = polygon.find(f".//{ns}coordinates")
            if outer is not None and outer.text:
                coords = parse_coordinate_string(outer.text.strip())
                if coords:
                    path = PathData(name=name)
                    path.vertices = [(lat, lng) for lng, lat in coords]
                    paths.append(path)

    return placemarks, paths


def parse_coordinate_string(coord_str: str):
    """Parse KML coordinate string: 'lng,lat,alt lng,lat,alt ...'"""
    coords = []
    for part in coord_str.split():
        part = part.strip()
        if not part:
            continue
        values = part.split(",")
        if len(values) >= 2:
            try:
                lng = float(values[0])
                lat = float(values[1])
                coords.append((lng, lat))
            except ValueError:
                continue
    return coords


# ============================================================
# Type classification
# ============================================================

def classify_point(name: str) -> tuple:
    """Auto-detect point type from its name. Returns (point_id, type)."""
    name_lower = name.lower().strip()

    # Clean up the name to make a valid point_id
    point_id = re.sub(r'[^a-z0-9_]', '_', name_lower)
    point_id = re.sub(r'_+', '_', point_id).strip('_')

    # Classification rules
    if any(w in name_lower for w in ["deploy", "origin"]):
        return point_id, "origin"

    if any(w in name_lower for w in ["pillar", "pylon", "pile", "post"]):
        return point_id, "pillar"

    if "ramp" in name_lower:
        return point_id, "ramp"

    if any(w in name_lower for w in ["jetty", "pier", "dock", "wharf"]):
        return point_id, "jetty"

    if any(w in name_lower for w in ["gate", "barrier", "opening"]):
        return point_id, "barrier"

    if any(w in name_lower for w in ["breakwater", "seawall", "wall"]):
        return point_id, "breakwater"

    if any(w in name_lower for w in ["buoy", "mooring", "anchor"]):
        return point_id, "mooring"

    if any(w in name_lower for w in ["rock", "boulder", "reef"]):
        return point_id, "rock"

    # Default: unknown type, will be placed as marker sphere
    return point_id, "unknown"


def classify_path(name: str) -> str:
    """Auto-detect path type from its name."""
    name_lower = name.lower()

    if any(w in name_lower for w in ["breakwater", "seawall", "wall", "curve"]):
        return "breakwater"
    if any(w in name_lower for w in ["shore", "coast", "beach"]):
        return "shoreline"
    if any(w in name_lower for w in ["depth", "bathy"]):
        return "bathymetry"

    return "path"


# ============================================================
# Coordinate conversion
# ============================================================

def make_converter(origin_lat: float, origin_lng: float):
    """Create a lat/lng → ENU converter for the given origin."""
    m_per_deg_lat = 110574.0
    m_per_deg_lng = 110320.0 * math.cos(math.radians(origin_lat))

    def convert(lat: float, lng: float) -> tuple:
        east = (lng - origin_lng) * m_per_deg_lng
        north = (lat - origin_lat) * m_per_deg_lat
        return round(east, 3), round(north, 3)

    return convert


# ============================================================
# Main processing
# ============================================================

def process_kml(filepath: str):
    print(f"Parsing: {filepath}")
    placemarks, paths = parse_kml(filepath)
    print(f"Found {len(placemarks)} placemarks and {len(paths)} paths")

    if not placemarks and not paths:
        print("ERROR: No data found in KML file.")
        sys.exit(1)

    # --- Find origin ---
    origin_lat, origin_lng = None, None
    origin_name = None

    for name, lat, lng in placemarks:
        pid, ptype = classify_point(name)
        if ptype == "origin":
            origin_lat, origin_lng = lat, lng
            origin_name = name
            break

    if origin_lat is None:
        print("WARNING: No origin point found (name containing 'deploy' or 'origin').")
        print("Using centroid of all placemarks as origin.")
        all_lats = [lat for _, lat, _ in placemarks]
        all_lngs = [lng for _, _, lng in placemarks]
        origin_lat = sum(all_lats) / len(all_lats)
        origin_lng = sum(all_lngs) / len(all_lngs)
        origin_name = "centroid"

    print(f"Origin: '{origin_name}' at ({origin_lat:.6f}, {origin_lng:.6f})")
    convert = make_converter(origin_lat, origin_lng)

    # --- Convert placemarks ---
    unity_points = []
    for name, lat, lng in placemarks:
        pid, ptype = classify_point(name)
        x, z = convert(lat, lng)
        unity_points.append(UnityPoint(
            point_id=pid, point_type=ptype,
            unity_x=x, unity_z=z,
            lat=lat, lng=lng,
            notes=name
        ))

    # --- Convert paths ---
    all_path_vertices = {}
    for path in paths:
        path_type = classify_path(path.name)
        converted = []
        for lat, lng in path.vertices:
            x, z = convert(lat, lng)
            converted.append((x, z, lat, lng))

        all_path_vertices[path.name] = {
            "type": path_type,
            "vertices": converted,
            "count": len(converted)
        }

        # Also add path vertices as breakwater points in the main CSV
        if path_type == "breakwater":
            for i, (x, z, lat, lng) in enumerate(converted):
                unity_points.append(UnityPoint(
                    point_id=f"bw_path_{i:03d}",
                    point_type="breakwater",
                    unity_x=x, unity_z=z,
                    lat=lat, lng=lng,
                    notes=f"breakwater path vertex {i}"
                ))

    # --- Write main CSV ---
    csv_path = "lagoon_unity.csv"
    with open(csv_path, 'w', newline='') as f:
        writer = csv.writer(f)
        writer.writerow(["point_id", "type", "unity_x", "unity_z", "lat", "lng", "notes"])
        for pt in unity_points:
            writer.writerow([
                pt.point_id, pt.point_type,
                pt.unity_x, pt.unity_z,
                f"{pt.lat:.7f}", f"{pt.lng:.7f}",
                pt.notes
            ])
    print(f"Wrote {len(unity_points)} points → {csv_path}")

    # --- Write breakwater vertices CSV (for ProBuilder) ---
    for path_name, pdata in all_path_vertices.items():
        if pdata["type"] == "breakwater":
            bw_path = "breakwater_vertices.csv"
            with open(bw_path, 'w', newline='') as f:
                writer = csv.writer(f)
                writer.writerow(["index", "unity_x", "unity_z", "lat", "lng"])
                for i, (x, z, lat, lng) in enumerate(pdata["vertices"]):
                    writer.writerow([i, x, z, f"{lat:.7f}", f"{lng:.7f}"])
            print(f"Wrote {pdata['count']} breakwater vertices → {bw_path}")

    # --- Write summary ---
    summary_path = "scene_summary.txt"
    with open(summary_path, 'w') as f:
        f.write("LAGOON SCENE SUMMARY\n")
        f.write("=" * 50 + "\n\n")
        f.write(f"Origin: {origin_name}\n")
        f.write(f"  Lat/Lng: ({origin_lat:.7f}, {origin_lng:.7f})\n")
        f.write(f"  Unity:   (0.000, 0.000)\n\n")

        # Type counts
        type_counts = {}
        for pt in unity_points:
            type_counts[pt.point_type] = type_counts.get(pt.point_type, 0) + 1

        f.write("Structure counts:\n")
        for t, c in sorted(type_counts.items()):
            f.write(f"  {t}: {c}\n")
        f.write("\n")

        # Path info
        if all_path_vertices:
            f.write("Paths:\n")
            for name, pdata in all_path_vertices.items():
                f.write(f"  '{name}': {pdata['count']} vertices ({pdata['type']})\n")
            f.write("\n")

        # All points with ranges
        f.write(f"{'ID':<30} {'Type':<12} {'X (E)':<10} {'Z (N)':<10} {'Range':<10}\n")
        f.write("-" * 72 + "\n")
        for pt in unity_points:
            if pt.point_type == "breakwater" and pt.point_id.startswith("bw_path_"):
                continue  # Skip path vertices in summary table
            rng = math.sqrt(pt.unity_x**2 + pt.unity_z**2)
            f.write(f"{pt.point_id:<30} {pt.point_type:<12} {pt.unity_x:<10.2f} {pt.unity_z:<10.2f} {rng:<10.1f}\n")

        # Distance sanity check
        f.write("\nKey distances (for sanity check):\n")
        named_pts = [p for p in unity_points if not p.point_id.startswith("bw_path_")]
        for i, p1 in enumerate(named_pts):
            for p2 in named_pts[i+1:]:
                dx = p1.unity_x - p2.unity_x
                dz = p1.unity_z - p2.unity_z
                dist = math.sqrt(dx*dx + dz*dz)
                if dist < 150:
                    f.write(f"  {p1.point_id} ↔ {p2.point_id}: {dist:.1f} m\n")

    print(f"Wrote summary → {summary_path}")

    # --- Console output ---
    print("\n" + "=" * 60)
    print("STRUCTURE SUMMARY")
    print("=" * 60)
    for t, c in sorted(type_counts.items()):
        print(f"  {t}: {c}")

    print(f"\n{'ID':<30} {'Type':<12} {'X (E)':<10} {'Z (N)':<10} {'Range':<8}")
    print("-" * 70)
    for pt in unity_points:
        if pt.point_id.startswith("bw_path_"):
            continue
        rng = math.sqrt(pt.unity_x**2 + pt.unity_z**2)
        print(f"{pt.point_id:<30} {pt.point_type:<12} {pt.unity_x:<10.2f} {pt.unity_z:<10.2f} {rng:<8.1f}")

    if all_path_vertices:
        print(f"\nBreakwater path: {sum(p['count'] for p in all_path_vertices.values())} vertices")
        print("Use breakwater_vertices.csv to trace in ProBuilder/Blender")

    print(f"\nAll files written. Import lagoon_unity.csv into Unity's LagoonSceneBuilder.")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python kml_to_unity.py <path_to_kml_file>")
        print("Export your Google Maps data as KML and pass the file path.")
        sys.exit(1)

    kml_file = sys.argv[1]
    if not Path(kml_file).exists():
        print(f"ERROR: File not found: {kml_file}")
        sys.exit(1)

    process_kml(kml_file)
