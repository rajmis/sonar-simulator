"""
Test the processing pipeline offline using a generated fixture.

Usage:
    python -m processor.tests.test_pipeline
"""

import sys
import time
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from processor.src.protocol import parse_frame
from processor.src.materials import MaterialTable
from processor.src.processing import process_frame, DEVICE
from processor.tests.generate_fixture import generate_frame_payload, PILE_MATERIAL


def test_pipeline():
    config_path = Path(__file__).parent.parent / "config" / "materials.yaml"
    materials = MaterialTable(config_path)

    # Generate fixture in memory and round-trip through the wire format
    payload = generate_frame_payload()
    header, ranges, material_ids = parse_frame(payload)

    print(f"Device: {DEVICE}")
    print(f"Grid: {header.grid_width}x{header.grid_height}, "
          f"returns: {(ranges > 0).sum()} / {ranges.size}")

    t0 = time.perf_counter()
    image = process_frame(header, ranges, material_ids, materials)
    dt = (time.perf_counter() - t0) * 1000

    print(f"Output shape: {image.shape}")
    print(f"Intensity range: [{image.min():.2e}, {image.max():.2e}]")
    print(f"Processing time: {dt:.1f} ms")

    # Basic sanity checks
    nb, nr = header.num_beams, header.num_range_bins
    assert image.shape == (nb, nr), f"Wrong shape: {image.shape}"
    assert image.dtype == np.float32
    assert np.all(image >= 0) and np.all(image <= 1), "Intensities outside [0, 1]"
    assert image.max() > 0.9, "Expected a strong return somewhere"

    # Locate the pile in the input frame and check it shows up brightly
    pile_mask = (material_ids == PILE_MATERIAL) & (ranges > 0)
    assert pile_mask.any(), "Fixture contains no pile returns"

    az_span = header.swath_angle_deg + 2 * header.padding_deg
    beam_step = header.swath_angle_deg / nb
    cols = np.where(pile_mask.any(axis=0))[0]
    az = -az_span / 2 + (cols + 0.5) * az_span / header.grid_width
    pile_beams = np.unique(((az + header.swath_angle_deg / 2) / beam_step).astype(int))
    pile_beams = pile_beams[(pile_beams >= 0) & (pile_beams < nb)]

    bin_width = header.max_range / nr
    pile_ranges = ranges[pile_mask]
    b0 = max(int(pile_ranges.min() / bin_width) - 2, 0)
    b1 = min(int(pile_ranges.max() / bin_width) + 3, nr)

    # The pile should be the brightest thing in the image (steel, head-on)
    pile_max = image[pile_beams, b0:b1].max(axis=1)
    print(f"Pile beams {pile_beams.min()}–{pile_beams.max()}, bins {b0}–{b1}: "
          f"per-beam max median {np.median(pile_max):.3f}")
    assert np.median(pile_max) > 0.8, "Pile returns should be bright"

    # Geometric shadow: the seabed behind the pile is occluded in the depth
    # map, so pile beams are dark beyond the pile, while the mirrored
    # (pile-free) bearing shows seabed backscatter at those same ranges.
    sw0, sw1 = b1 + 5, min(b1 + 40, nr)
    shadow_mean = image[pile_beams, sw0:sw1].mean()
    open_mean = image[nb - 1 - pile_beams, sw0:sw1].mean()
    print(f"Bins {sw0}–{sw1}: shadow behind pile {shadow_mean:.3f} "
          f"vs open seabed {open_mean:.3f}")
    assert open_mean > shadow_mean + 0.2, \
        "Expected occlusion shadow behind the pile vs open seabed"

    # Water column before the first return: only volume scatter + noise
    water = image[:, :30].mean()
    print(f"Water column (bins <30) mean: {water:.3f}")
    assert water < 0.3, "Water column should be mostly dark"

    # Dry transducer (in-water flag cleared): noise floor only, no returns
    import dataclasses
    dry_header = dataclasses.replace(header, flags=0)
    dry = process_frame(dry_header, ranges, material_ids, materials)
    print(f"Dry-transducer image max: {dry.max():.3f}")
    assert dry.max() < 0.5, "Dry transducer should produce noise only"

    print("All checks passed!")


if __name__ == "__main__":
    test_pipeline()
