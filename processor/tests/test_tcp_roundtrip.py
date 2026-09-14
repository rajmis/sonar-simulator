"""
End-to-end TCP round-trip test — plays the role of Unity.

Starts the TCP server + processing loop in a thread, connects as a
client, sends one synthetic depth frame, and checks the returned
intensity image. No Unity needed.

Usage:
    python -m processor.tests.test_tcp_roundtrip
"""

import socket
import struct
import sys
import threading
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from processor.src.network import SonarTCPServer
from processor.src.protocol import parse_frame, build_response, RESPONSE_HEADER_SIZE
from processor.src.materials import MaterialTable
from processor.src.processing import process_frame
from processor.tests.generate_fixture import generate_frame_payload

TEST_PORT = 9100


def server_thread(server: SonarTCPServer, materials: MaterialTable):
    server.start()
    while not server.accept_connection():
        pass
    result = server.receive_ping()
    if result is None:
        return
    ping_id, payload = result
    header, ranges, material_ids = parse_frame(payload)
    image = process_frame(header, ranges, material_ids, materials)
    server.send_response(ping_id, build_response(ping_id, image))


def recv_exact(sock: socket.socket, n: int) -> bytes:
    data = bytearray()
    while len(data) < n:
        chunk = sock.recv(n - len(data))
        assert chunk, "server closed connection"
        data.extend(chunk)
    return bytes(data)


def test_tcp_roundtrip():
    config_path = Path(__file__).parent.parent / "config" / "materials.yaml"
    materials = MaterialTable(config_path)

    server = SonarTCPServer(listen_port=TEST_PORT)
    thread = threading.Thread(target=server_thread, args=(server, materials), daemon=True)
    thread.start()

    payload = generate_frame_payload()

    client = socket.create_connection(("127.0.0.1", TEST_PORT), timeout=30)
    client.sendall(struct.pack("<I", len(payload)) + payload)

    length = struct.unpack("<I", recv_exact(client, 4))[0]
    response = recv_exact(client, length)
    client.close()
    thread.join(timeout=30)
    server.close()

    ping_id, beams, bins = struct.unpack_from("<IHH", response, 0)
    image = np.frombuffer(response, dtype="<f4", offset=RESPONSE_HEADER_SIZE)

    print(f"Response: ping {ping_id}, {beams}x{bins}, {len(response)} bytes")
    assert ping_id == 1
    assert (beams, bins) == (256, 650)
    assert image.size == beams * bins
    assert image.max() > 0.9

    print("TCP round-trip OK!")


if __name__ == "__main__":
    test_tcp_roundtrip()
