"""
Sonar processor main loop.

Usage:
    python -m processor.src.main --config processor/config/materials.yaml
"""

import argparse
import logging
import signal
import sys
import time
from pathlib import Path

from .protocol import parse_frame, build_response
from .network import SonarTCPServer
from .materials import MaterialTable
from .processing import process_frame, DEVICE

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(name)s: %(message)s")
logger = logging.getLogger(__name__)

_running = True


def _handle_signal(sig, frame):
    global _running
    logger.info("Shutting down...")
    _running = False


def main():
    parser = argparse.ArgumentParser(description="Sonar acoustic processor")
    parser.add_argument("--config", type=Path,
                        default=Path(__file__).parent.parent / "config" / "materials.yaml")
    parser.add_argument("--port", type=int, default=9000)
    parser.add_argument("--dump-dir", type=Path, default=None,
                        help="If set, save the latest frame and output image "
                             "as .npy files here (overwritten each ping)")
    args = parser.parse_args()

    signal.signal(signal.SIGINT, _handle_signal)
    signal.signal(signal.SIGTERM, _handle_signal)

    materials = MaterialTable(args.config)
    logger.info("Loaded %d materials from %s", len(materials._materials), args.config)
    logger.info("Processing device: %s", DEVICE)

    if args.dump_dir is not None:
        args.dump_dir.mkdir(parents=True, exist_ok=True)

    server = SonarTCPServer(listen_port=args.port)
    server.start()

    ping_count = 0
    try:
        while _running:
            # Wait for Unity to connect
            if not server.accept_connection():
                continue

            result = server.receive_ping()
            if result is None:
                continue

            t0 = time.perf_counter()
            ping_id, payload = result

            header, ranges, material_ids = parse_frame(payload)
            image = process_frame(header, ranges, material_ids, materials)
            response = build_response(ping_id, image)
            server.send_response(ping_id, response)

            if args.dump_dir is not None:
                import dataclasses
                import json
                import numpy as np
                np.save(args.dump_dir / "ranges.npy", ranges)
                np.save(args.dump_dir / "materials.npy", material_ids)
                np.save(args.dump_dir / "intensity.npy", image)
                (args.dump_dir / "header.json").write_text(
                    json.dumps(dataclasses.asdict(header), indent=2))

            dt_ms = (time.perf_counter() - t0) * 1000
            ping_count += 1
            if ping_count % 10 == 0:
                logger.info("Ping %d processed in %.1f ms (grid: %dx%d, image: %s)",
                            ping_id, dt_ms, header.grid_width, header.grid_height,
                            image.shape)
    finally:
        server.close()
        logger.info("Processed %d pings total", ping_count)


if __name__ == "__main__":
    main()
