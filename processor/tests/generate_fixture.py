"""
Generate synthetic depth/material frames for offline testing.

Scene: flat sand seabed 8 m below the sonar, plus one vertical steel
pile (radius 0.5 m) rising from the seabed to 2 m below the sonar,
offset 3 m to the +azimuth side.

Usage:
    python -m processor.tests.generate_fixture --output processor/tests/fixtures/test_frame.bin
"""

import argparse
from pathlib import Path

import numpy as np

from processor.src.protocol import FrameHeader, build_frame

# Scene constants (also used by test assertions)
SEABED_DEPTH = 8.0      # m below sonar
PILE_X = 3.0            # m to the side
PILE_RADIUS = 0.5
PILE_TOP = -2.0         # y of pile top (sonar at y=0, down = -y)
SEABED_MATERIAL = 3     # sand
PILE_MATERIAL = 1       # steel


def make_header(ping_id=1, num_beams=256, num_range_bins=650, max_range=100.0,
                swath_deg=150.0, vfov_deg=25.0, padding_deg=2.0,
                grid_w=512, grid_h=128) -> FrameHeader:
    return FrameHeader(
        ping_id=ping_id, timestamp=0.0,
        sonar_pos=(0.0, 0.0, 0.0), sonar_rot=(0.0, 0.0, 0.0, 1.0),
        num_beams=num_beams, num_range_bins=num_range_bins,
        max_range=max_range, swath_angle_deg=swath_deg,
        vertical_fov_deg=vfov_deg, padding_deg=padding_deg,
        mount_tilt_deg=0.0, speed_of_sound=1500.0,
        source_level_db=220.0, pulse_length_s=0.0001,
        frequency_hz=400000.0, grid_width=grid_w, grid_height=grid_h,
    )


def grid_directions(header: FrameHeader) -> np.ndarray:
    """Unit ray directions (H, W, 3) — same convention as the Unity capture
    and the Python processor: dir = (cos t·sin a, −cos t·cos a, −sin t)."""
    w, h = header.grid_width, header.grid_height
    az_span = header.swath_angle_deg + 2 * header.padding_deg
    el_span = header.vertical_fov_deg + 2 * header.padding_deg

    az = np.radians(-az_span / 2 + (np.arange(w) + 0.5) * az_span / w)
    el = np.radians(-el_span / 2 + (np.arange(h) + 0.5) * el_span / h)
    tt, aa = np.meshgrid(el + np.radians(header.mount_tilt_deg), az, indexing="ij")

    return np.stack([np.cos(tt) * np.sin(aa),
                     -np.cos(tt) * np.cos(aa),
                     -np.sin(tt)], axis=-1)


def generate_frame(header: FrameHeader = None):
    """Returns (header, ranges (H,W) float32, materials (H,W) uint8)."""
    if header is None:
        header = make_header()

    dirs = grid_directions(header)
    dx, dy, dz = dirs[..., 0], dirs[..., 1], dirs[..., 2]

    # Seabed plane at y = -SEABED_DEPTH
    with np.errstate(divide="ignore"):
        t_seabed = np.where(dy < 0, SEABED_DEPTH / -dy, np.inf)

    # Vertical cylinder: axis parallel to y through (PILE_X, z=0)
    a = dx**2 + dz**2
    b = -2.0 * PILE_X * dx
    c = PILE_X**2 - PILE_RADIUS**2
    disc = b**2 - 4 * a * c
    with np.errstate(divide="ignore", invalid="ignore"):
        t_pile = np.where(disc >= 0, (-b - np.sqrt(np.maximum(disc, 0))) / (2 * a), np.inf)
    hit_y = t_pile * dy
    t_pile = np.where((t_pile > 0) & (hit_y <= PILE_TOP) & (hit_y >= -SEABED_DEPTH),
                      t_pile, np.inf)

    ranges = np.minimum(t_seabed, t_pile)
    materials = np.where(t_pile < t_seabed, PILE_MATERIAL, SEABED_MATERIAL).astype(np.uint8)

    miss = ~np.isfinite(ranges) | (ranges > header.max_range)
    ranges = np.where(miss, 0.0, ranges).astype(np.float32)
    materials = np.where(miss, 0, materials).astype(np.uint8)

    return header, ranges, materials


def generate_frame_payload() -> bytes:
    return build_frame(*generate_frame())


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path,
                        default=Path(__file__).parent / "fixtures" / "test_frame.bin")
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)

    payload = generate_frame_payload()
    args.output.write_bytes(payload)
    print(f"Wrote {len(payload)} bytes to {args.output}")


if __name__ == "__main__":
    main()
