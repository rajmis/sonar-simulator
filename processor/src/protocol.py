"""
Byte-level protocol definitions for the Unity ↔ Python TCP link.
See shared/PROTOCOL.md for the full specification.

Unity sends a polar depth + material frame per ping; Python replies with
a beam × range-bin intensity image.
"""

import struct
from dataclasses import dataclass

import numpy as np

# Network
HOST = "127.0.0.1"
PORT = 9000

# Sizes
FRAME_HEADER_SIZE = 88
RESPONSE_HEADER_SIZE = 8


@dataclass
class FrameHeader:
    ping_id: int
    timestamp: float
    sonar_pos: tuple  # (x, y, z)
    sonar_rot: tuple  # (x, y, z, w)
    num_beams: int
    num_range_bins: int
    max_range: float
    swath_angle_deg: float
    vertical_fov_deg: float
    padding_deg: float
    mount_tilt_deg: float
    speed_of_sound: float
    source_level_db: float
    pulse_length_s: float
    frequency_hz: float
    grid_width: int   # azimuth samples W
    grid_height: int  # elevation samples H


def parse_frame_header(data: bytes) -> FrameHeader:
    """Parse the 88-byte frame header."""
    off = 0
    ping_id = struct.unpack_from("<I", data, off)[0]; off += 4
    timestamp = struct.unpack_from("<d", data, off)[0]; off += 8
    pos = struct.unpack_from("<3f", data, off); off += 12
    rot = struct.unpack_from("<4f", data, off); off += 16
    num_beams, num_range_bins = struct.unpack_from("<HH", data, off); off += 4
    (max_range, swath, vfov, padding, tilt,
     speed, src_lvl, pulse_len, freq) = struct.unpack_from("<9f", data, off); off += 36
    grid_w, grid_h = struct.unpack_from("<HH", data, off); off += 4
    # 4 bytes reserved
    return FrameHeader(
        ping_id=ping_id, timestamp=timestamp,
        sonar_pos=pos, sonar_rot=rot,
        num_beams=num_beams, num_range_bins=num_range_bins,
        max_range=max_range, swath_angle_deg=swath,
        vertical_fov_deg=vfov, padding_deg=padding,
        mount_tilt_deg=tilt, speed_of_sound=speed,
        source_level_db=src_lvl, pulse_length_s=pulse_len,
        frequency_hz=freq, grid_width=grid_w, grid_height=grid_h,
    )


def parse_frame(payload: bytes) -> tuple[FrameHeader, np.ndarray, np.ndarray]:
    """Parse a full frame payload.

    Returns (header, ranges, materials) where ranges is float32 (H, W)
    with 0 = no return, and materials is uint8 (H, W).
    Row = elevation index, column = azimuth index.
    """
    header = parse_frame_header(payload)
    w, h = header.grid_width, header.grid_height
    cells = w * h

    off = FRAME_HEADER_SIZE
    ranges = np.frombuffer(payload, dtype="<f4", count=cells, offset=off)
    off += cells * 4
    materials = np.frombuffer(payload, dtype="u1", count=cells, offset=off)

    return header, ranges.reshape(h, w), materials.reshape(h, w)


def build_frame(header: FrameHeader, ranges: np.ndarray,
                materials: np.ndarray) -> bytes:
    """Build a frame payload (used by tests and offline tools —
    mirrors the Unity-side serialisation)."""
    hdr = struct.pack(
        "<Id3f4fHH9fHHI",
        header.ping_id, header.timestamp,
        *header.sonar_pos, *header.sonar_rot,
        header.num_beams, header.num_range_bins,
        header.max_range, header.swath_angle_deg, header.vertical_fov_deg,
        header.padding_deg, header.mount_tilt_deg, header.speed_of_sound,
        header.source_level_db, header.pulse_length_s, header.frequency_hz,
        header.grid_width, header.grid_height, 0,
    )
    assert len(hdr) == FRAME_HEADER_SIZE
    return (hdr
            + ranges.astype("<f4").tobytes()
            + materials.astype("u1").tobytes())


def build_response(ping_id: int, intensity: np.ndarray) -> bytes:
    """Build response payload: 8-byte header + float32 image data."""
    beams, bins = intensity.shape
    header = struct.pack("<IHH", ping_id, beams, bins)
    return header + intensity.astype("<f4").tobytes()
