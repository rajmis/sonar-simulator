"""
TCP server for Unity <-> Python communication.
Length-prefixed messages — no chunking needed.

Protocol:
  Each message = [4-byte little-endian length] + [payload]
  Bidirectional on a single TCP connection.
"""

import logging
import socket
import struct
from typing import Optional

from .protocol import HOST, PORT

logger = logging.getLogger(__name__)

_HEADER_SIZE = 4
_RECV_CHUNK = 65536


def _recv_exact(sock: socket.socket, n: int) -> Optional[bytes]:
    """Read exactly n bytes from socket. Returns None on disconnect."""
    data = bytearray()
    while len(data) < n:
        try:
            chunk = sock.recv(min(n - len(data), _RECV_CHUNK))
        except socket.timeout:
            continue
        if not chunk:
            return None
        data.extend(chunk)
    return bytes(data)


class SonarTCPServer:
    """TCP server that receives pings and sends back intensity images."""

    def __init__(self, listen_port: int = PORT):
        self.listen_port = listen_port
        self._server: Optional[socket.socket] = None
        self._conn: Optional[socket.socket] = None

    def start(self):
        self._server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._server.bind((HOST, self.listen_port))
        self._server.listen(1)
        self._server.settimeout(1.0)
        logger.info("TCP server listening on %s:%d — waiting for Unity...",
                     HOST, self.listen_port)

    def accept_connection(self) -> bool:
        if self._conn is not None:
            return True
        try:
            self._conn, addr = self._server.accept()
            self._conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            self._conn.settimeout(0.5)
            logger.info("Unity connected from %s", addr)
            return True
        except socket.timeout:
            return False

    def receive_ping(self) -> Optional[tuple[int, bytes]]:
        if self._conn is None:
            return None
        header = _recv_exact(self._conn, _HEADER_SIZE)
        if header is None:
            logger.warning("Unity disconnected")
            self._conn.close()
            self._conn = None
            return None
        length = struct.unpack("<I", header)[0]
        payload = _recv_exact(self._conn, length)
        if payload is None:
            logger.warning("Unity disconnected mid-message")
            self._conn.close()
            self._conn = None
            return None
        ping_id = struct.unpack("<I", payload[:4])[0]
        return (ping_id, payload)

    def send_response(self, ping_id: int, response_payload: bytes):
        if self._conn is None:
            return
        try:
            header = struct.pack("<I", len(response_payload))
            self._conn.sendall(header + response_payload)
        except BrokenPipeError:
            logger.warning("Unity disconnected during send")
            self._conn.close()
            self._conn = None

    def close(self):
        if self._conn:
            self._conn.close()
            self._conn = None
        if self._server:
            self._server.close()
            self._server = None
