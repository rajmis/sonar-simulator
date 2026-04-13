#!/usr/bin/env python3
"""
latlon_to_unity.py

Converts a CSV of lat/lng coordinates (from Google Maps measurements)
into Unity XZ positions in meters, using a local ENU reference frame.

The ORIGIN is auto-detected from the first row with type="origin" in the CSV.
This should be your sonar deployment point — it becomes (0, 0, 0) in Unity,
so all simulated ranges directly correspond to real sonar ranges.

Usage:
    1. Fill in lagoon_points_latlon.csv with your Google Maps coordinates
    2. Make sure the deployment point row has type="origin"
    3. Run: python latlon_to_unity.py
    4. Output: lagoon_points_unity.csv (ready for Unity's LagoonSceneBuilder)

    Add as many rows as you need — any type string is valid.
    The Unity builder auto-handles: pillar, jetty, pier, barrier
    Unknown types are placed as labeled markers for manual modeling.

Coordinate convention (ENU frame):
    Unity X = East   (positive → right in satellite view)
    Unity Z = North  (positive → top in satellite view)
    Unity Y = Up     (handled by Unity scripts, not this converter)
"""

import csv
import math
import sys
from pathlib import Path

INPUT_CSV = "lagoon_points_latlon.csv"
OUTPUT_CSV = "lagoon_points_unity.csv"


def find_origin(input_path: str) -> tuple[float, float]:
    """Find the row with type='origin' and return its lat/lng."""
    with open(input_path, 'r') as f:
        reader = csv.DictReader(f)
        for row in reader:
            if row['type'].strip().lower() == 'origin':
                lat = float(row['lat'])
                lng = float(row['lng'])
                print(f"Found origin: {row['point_id']} at ({lat}, {lng})")
                return lat, lng

    print("ERROR: No row with type='origin' found in CSV.")
    print("Add a row for your deployment point with type='origin'.")
    sys.exit(1)


def make_converter(origin_lat: float, origin_lng: float):
    """Create a lat/lng → ENU converter for the given origin."""
    m_per_deg_lat = 110574.0
    m_per_deg_lng = 110320.0 * math.cos(math.radians(origin_lat))

    def latlon_to_enu(lat: float, lng: float) -> tuple[float, float]:
        east = (lng - origin_lng) * m_per_deg_lng   # Unity X
        north = (lat - origin_lat) * m_per_deg_lat  # Unity Z
        return round(east, 3), round(north, 3)

    return latlon_to_enu


def convert_csv(input_path: str, output_path: str):
    """Read lat/lng CSV, output Unity XZ CSV."""
    input_file = Path(input_path)
    if not input_file.exists():
        print(f"Error: {input_path} not found.")
        print(f"Create it with columns: point_id, type, lat, lng, notes")
        sys.exit(1)

    # Auto-detect origin from CSV
    origin_lat, origin_lng = find_origin(input_path)
    convert = make_converter(origin_lat, origin_lng)

    rows = []
    type_counts = {}
    with open(input_path, 'r') as f:
        reader = csv.DictReader(f)
        for row in reader:
            lat = float(row['lat'])
            lng = float(row['lng'])
            x, z = convert(lat, lng)
            point_type = row['type'].strip()

            type_counts[point_type] = type_counts.get(point_type, 0) + 1

            rows.append({
                'point_id': row['point_id'].strip(),
                'type': point_type,
                'unity_x': x,
                'unity_z': z,
                'notes': row.get('notes', '').strip()
            })

    with open(output_path, 'w', newline='') as f:
        writer = csv.DictWriter(f, fieldnames=['point_id', 'type', 'unity_x', 'unity_z', 'notes'])
        writer.writeheader()
        writer.writerows(rows)

    print(f"\nConverted {len(rows)} points → {output_path}")
    print(f"Origin (deploy point): ({origin_lat}, {origin_lng}) → Unity (0, 0)")
    print(f"Scale: 1 Unity unit = 1 meter")
    print()

    # Type summary
    print("--- Structure Counts ---")
    for t, c in sorted(type_counts.items()):
        print(f"  {t}: {c}")
    print()

    # Full table
    print(f"{'point_id':<25} {'type':<12} {'X (East)':<12} {'Z (North)':<12} {'Range':<10}")
    print("-" * 71)
    for r in rows:
        # Range from origin (useful for sonar comparison)
        rng = math.sqrt(r['unity_x']**2 + r['unity_z']**2)
        print(f"{r['point_id']:<25} {r['type']:<12} {r['unity_x']:<12.2f} {r['unity_z']:<12.2f} {rng:<10.1f}")

    return rows


def print_distance_check(rows: list):
    """Print distances between key pairs for sanity-checking against Google Maps."""
    print("\n--- Distance Sanity Check ---")
    print("Compare these against your Google Maps measurements:")
    for i, r1 in enumerate(rows):
        for r2 in rows[i+1:]:
            dx = r1['unity_x'] - r2['unity_x']
            dz = r1['unity_z'] - r2['unity_z']
            dist = math.sqrt(dx*dx + dz*dz)
            if dist < 200:  # Only show nearby pairs
                print(f"  {r1['point_id']} ↔ {r2['point_id']}: {dist:.1f} m")


if __name__ == "__main__":
    rows = convert_csv(INPUT_CSV, OUTPUT_CSV)

    # Reload for distance check (with float types)
    rows_float = []
    with open(OUTPUT_CSV, 'r') as f:
        reader = csv.DictReader(f)
        for row in reader:
            row['unity_x'] = float(row['unity_x'])
            row['unity_z'] = float(row['unity_z'])
            rows_float.append(row)
    print_distance_check(rows_float)
