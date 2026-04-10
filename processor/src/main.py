"""
Sonar processor main loop.

Usage:
    python -m processor.src.main --config processor/config/norbit_wbms.yaml
"""

import argparse
import logging
import signal
import sys
import time
from pathlib import Path

from .protocol import parse_ping_header, parse_rays_bulk, build_response, PING_HEADER_SIZE, RAY_RECORD_SIZE
from .network import SonarTCPServer
from .materials import MaterialTable
from .processing import process_ping

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
                        default=Path(__file__).parent.parent / "config" / "norbit_wbms.yaml")
    parser.add_argument("--port", type=int, default=9000)
    args = parser.parse_args()

    signal.signal(signal.SIGINT, _handle_signal)
    signal.signal(signal.SIGTERM, _handle_signal)

    materials = MaterialTable(args.config)
    logger.info("Loaded %d materials from %s", len(materials._materials), args.config)

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

            header = parse_ping_header(payload)
            num_rays = header.num_beams * header.sub_beams_per_beam
            rays = parse_rays_bulk(payload, PING_HEADER_SIZE, num_rays)

            image = process_ping(header, rays, materials)
            response = build_response(ping_id, image)
            server.send_response(ping_id, response)

            dt_ms = (time.perf_counter() - t0) * 1000
            ping_count += 1
            if ping_count % 10 == 0:
                logger.info("Ping %d processed in %.1f ms (image: %s)",
                            ping_id, dt_ms, image.shape)
    finally:
        server.close()
        logger.info("Processed %d pings total", ping_count)


if __name__ == "__main__":
    main()
