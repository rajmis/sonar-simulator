"""
Byte-level protocol definitions for Unity ↔ Python UDP communication.
See shared/protocol.md for the full specification.
"""

import struct
from dataclasses import dataclass
from typing import Optional

import numpy as np

# Network
HOST = "127.0.0.1"
UNITY_TO_PYTHON_PORT = 9000
PYTHON_TO_UNITY_PORT = 9001
CHUNK_PAYLOAD_SIZE = 60_000

# Sizes
CHUNK_HEADER_SIZE = 12
PING_HEADER_SIZE = 80
RAY_RECORD_SIZE = 48
RESPONSE_HEADER_SIZE = 8

# Struct formats (little-endian)
CHUNK_HEADER_FMT = "<IHHi"  # pingID, chunkIndex, totalChunks, payloadLength
PING_HEADER_FMT = "<Id3f4fHHfHffff2fHH"  # see below
RAY_RECORD_FMT = "<HHBf3f3fHB3f"

# Pre-compile for speed
_chunk_hdr = struct.Struct(CHUNK_HEADER_FMT)
_ray_rec = struct.Struct(RAY_RECORD_FMT)


@dataclass
class PingHeader:
    ping_id: int
    timestamp: float
    sonar_pos: tuple  # (x, y, z)
    sonar_rot: tuple  # (x, y, z, w)
    num_beams: int
    sub_beams_per_beam: int
    max_range: float
    num_range_bins: int
    swath_angle_deg: float
    vertical_fov_deg: float
    speed_of_sound: float
    source_level_db: float
    pulse_length_s: float
    frequency_hz: float
    sub_beams_h: int
    sub_beams_v: int


@dataclass
class RayRecord:
    beam_index: int
    sub_beam_index: int
    did_hit: bool
    hit_distance: float
    hit_normal: tuple  # (x, y, z)
    ray_dir: tuple     # (x, y, z)
    object_id: int
    material_type: int
    hit_point: tuple   # (x, y, z)


def parse_chunk_header(data: bytes) -> tuple:
    """Returns (pingID, chunkIndex, totalChunks, payloadLength)."""
    return _chunk_hdr.unpack_from(data, 0)


def parse_ping_header(data: bytes) -> PingHeader:
    """Parse 80-byte ping header from reassembled payload."""
    # Manual unpack since the nested tuples need regrouping
    off = 0
    ping_id = struct.unpack_from("<I", data, off)[0]; off += 4
    timestamp = struct.unpack_from("<d", data, off)[0]; off += 8
    pos = struct.unpack_from("<3f", data, off); off += 12
    rot = struct.unpack_from("<4f", data, off); off += 16
    (num_beams, sub_beams_per_beam) = struct.unpack_from("<HH", data, off); off += 4
    max_range = struct.unpack_from("<f", data, off)[0]; off += 4
    num_range_bins = struct.unpack_from("<H", data, off)[0]; off += 2
    swath_angle = struct.unpack_from("<f", data, off)[0]; off += 4
    vert_fov = struct.unpack_from("<f", data, off)[0]; off += 4
    speed = struct.unpack_from("<f", data, off)[0]; off += 4
    src_lvl = struct.unpack_from("<f", data, off)[0]; off += 4
    pulse_len = struct.unpack_from("<f", data, off)[0]; off += 4
    freq = struct.unpack_from("<f", data, off)[0]; off += 4
    sub_h, sub_v = struct.unpack_from("<HH", data, off); off += 4
    # 2 bytes reserved
    return PingHeader(
        ping_id=ping_id, timestamp=timestamp,
        sonar_pos=pos, sonar_rot=rot,
        num_beams=num_beams, sub_beams_per_beam=sub_beams_per_beam,
        max_range=max_range, num_range_bins=num_range_bins,
        swath_angle_deg=swath_angle, vertical_fov_deg=vert_fov,
        speed_of_sound=speed, source_level_db=src_lvl,
        pulse_length_s=pulse_len, frequency_hz=freq,
        sub_beams_h=sub_h, sub_beams_v=sub_v,
    )


def parse_rays_bulk(data: bytes, offset: int, count: int) -> np.ndarray:
    """Parse ray records into a structured numpy array for fast processing.

    Returns structured array with fields:
        beam_index (u2), sub_beam_index (u2), did_hit (u1),
        hit_distance (f4), hit_normal (3×f4), ray_dir (3×f4),
        object_id (u2), material_type (u1), hit_point (3×f4)
    """
    dt = np.dtype([
        ("beam_index", "<u2"),
        ("sub_beam_index", "<u2"),
        ("did_hit", "u1"),
        ("hit_distance", "<f4"),
        ("hit_normal", "<f4", (3,)),
        ("ray_dir", "<f4", (3,)),
        ("object_id", "<u2"),
        ("material_type", "u1"),
        ("hit_point", "<f4", (3,)),
    ])
    raw = data[offset : offset + count * RAY_RECORD_SIZE]
    return np.frombuffer(raw, dtype=dt, count=count)


def build_response(ping_id: int, intensity: np.ndarray) -> bytes:
    """Build response payload: 8-byte header + float32 image data."""
    beams, bins = intensity.shape
    header = struct.pack("<IHH", ping_id, beams, bins)
    return header + intensity.astype("<f4").tobytes()


def chunk_payload(ping_id: int, payload: bytes) -> list[bytes]:
    """Split payload into UDP-safe chunks with headers."""
    total = (len(payload) + CHUNK_PAYLOAD_SIZE - 1) // CHUNK_PAYLOAD_SIZE
    chunks = []
    for i in range(total):
        start = i * CHUNK_PAYLOAD_SIZE
        piece = payload[start : start + CHUNK_PAYLOAD_SIZE]
        hdr = _chunk_hdr.pack(ping_id, i, total, len(piece))
        chunks.append(hdr + piece)
    return chunks
