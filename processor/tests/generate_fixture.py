"""
Generate synthetic ping fixtures for offline testing.

Usage:
    python -m processor.tests.generate_fixture --output processor/tests/fixtures/test_ping.bin
"""

import argparse
import struct
from pathlib import Path

import numpy as np


def generate_ping(num_beams=256, sub_beams=75, max_range=100.0,
                  range_bins=650) -> bytes:
    """Create a synthetic ping payload with a simulated seabed + one pillar."""
    # Header (80 bytes)
    header = struct.pack(
        "<I d 3f 4f HH f H ffffff HH H",
        1,              # pingID
        0.0,            # timestamp
        0.0, -5.0, 0.0, # sonarPos
        0.0, 0.0, 0.0, 1.0,  # sonarRot (identity)
        num_beams, sub_beams,
        max_range,
        range_bins,
        150.0,          # swathAngleDeg
        25.0,           # verticalFOVDeg
        1500.0,         # speedOfSound
        220.0,          # sourceLevel_dB
        0.0001,         # pulseLength_s
        400000.0,       # frequency_Hz
        5, 15,          # subBeamsH, subBeamsV
        0,              # reserved
    )

    rng = np.random.default_rng(42)
    rays = bytearray()

    for beam in range(num_beams):
        beam_angle = (beam / (num_beams - 1) - 0.5) * np.radians(150)
        for sub in range(sub_beams):
            # Simulate seabed at ~8-12m depending on angle
            seabed_dist = 8.0 + 4.0 * abs(np.sin(beam_angle))
            seabed_dist += rng.normal(0, 0.1)

            # Pillar hit for beams 120-135
            if 120 <= beam <= 135 and sub % 3 == 0:
                hit_dist = 5.0 + rng.normal(0, 0.05)
                mat = 1  # steel
                normal = (np.cos(beam_angle), 0.0, -np.sin(beam_angle))
            else:
                hit_dist = seabed_dist
                mat = 3  # sand
                normal = (0.0, 1.0, 0.0)

            ray_dir = (np.sin(beam_angle), -0.8, np.cos(beam_angle))
            hit_pt = (hit_dist * ray_dir[0], hit_dist * ray_dir[1], hit_dist * ray_dir[2])

            ray = struct.pack(
                "<HH B f 3f 3f H B 3f",
                beam, sub,
                1,  # didHit
                float(hit_dist),
                *normal,
                *ray_dir,
                0,  # objectID
                mat,
                *hit_pt,
            )
            rays.extend(ray)

    return header + bytes(rays)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path,
                        default=Path(__file__).parent / "fixtures" / "test_ping.bin")
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)

    payload = generate_ping()
    args.output.write_bytes(payload)
    print(f"Wrote {len(payload)} bytes to {args.output}")


if __name__ == "__main__":
    main()
