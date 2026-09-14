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

    # The pile is a narrow bright return per beam — compare per-beam maxima
    # against the mirrored (pile-free) side of the swath at the same ranges.
    pile_max = image[pile_beams, b0:b1].max(axis=1)
    mirror_max = image[nb - 1 - pile_beams, b0:b1].max(axis=1)
    print(f"Pile beams {pile_beams.min()}–{pile_beams.max()}, bins {b0}–{b1}: "
          f"per-beam max median {np.median(pile_max):.3f} "
          f"vs mirror {np.median(mirror_max):.3f}")
    assert np.median(pile_max) > 0.7, "Pile returns should be bright"
    assert np.median(pile_max) > 2 * np.median(mirror_max), \
        "Pile beams should be clearly brighter than the mirrored empty region"

    # Acoustic shadow behind the pile: the seabed behind it is occluded and
    # the shadow zone carries only faint noise.
    mid_beam = int(np.median(pile_beams))
    pile_bin = int(image[mid_beam].argmax())
    shadow = image[mid_beam, pile_bin + 20:]
    print(f"Shadow behind pile (beam {mid_beam}, bins >{pile_bin + 20}): "
          f"mean {shadow.mean():.4f}")
    assert shadow.mean() < 0.05, "Expected near-silence behind the pile"

    print("All checks passed!")


if __name__ == "__main__":
    test_pipeline()
