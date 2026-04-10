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

from processor.src.protocol import parse_ping_header, parse_rays_bulk, PING_HEADER_SIZE
from processor.src.materials import MaterialTable
from processor.src.processing import process_ping
from processor.tests.generate_fixture import generate_ping


def test_pipeline():
    config_path = Path(__file__).parent.parent / "config" / "norbit_wbms.yaml"
    materials = MaterialTable(config_path)

    # Generate fixture in memory
    payload = generate_ping()
    header = parse_ping_header(payload)
    num_rays = header.num_beams * header.sub_beams_per_beam
    rays = parse_rays_bulk(payload, PING_HEADER_SIZE, num_rays)

    print(f"Header: {header.num_beams} beams × {header.sub_beams_per_beam} sub-beams = {num_rays} rays")
    print(f"Hits: {rays['did_hit'].sum()} / {num_rays}")

    t0 = time.perf_counter()
    image = process_ping(header, rays, materials)
    dt = (time.perf_counter() - t0) * 1000

    print(f"Output shape: {image.shape}")
    print(f"Intensity range: [{image.min():.2e}, {image.max():.2e}]")
    print(f"Processing time: {dt:.1f} ms")

    # Basic sanity checks
    assert image.shape == (256, 650), f"Wrong shape: {image.shape}"
    assert image.dtype == np.float32
    assert np.all(image >= 0), "Negative intensities found"
    assert image.max() > 0, "All-zero image"

    # Pillar beams (120-135) should have higher intensity at short range (~bin 32)
    pillar_region = image[120:136, 25:40]
    other_region = image[0:16, 25:40]
    assert pillar_region.mean() > other_region.mean(), \
        "Pillar region should be brighter than empty region at same range"

    print("All checks passed!")


if __name__ == "__main__":
    test_pipeline()
