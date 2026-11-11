# Sonar Simulator TCP Protocol Specification

## Overview

Unity (client) connects to Python (server) on `localhost:9000`.
Single bidirectional TCP connection. All messages are length-prefixed.
Byte order: **little-endian** throughout.

Unity sends a **polar depth + material frame** per ping; Python collapses it
into a beam × range-bin intensity image and sends it back.

---

## Message Framing

Every message in both directions:

```
[4-byte uint32: payload length][payload bytes]
```

TCP guarantees ordered, reliable delivery. No chunking or reassembly needed.

---

## Unity → Python: Depth Frame

### Frame Header (88 bytes)

| Offset | Type      | Field            | Description                                      |
|--------|-----------|------------------|--------------------------------------------------|
| 0      | uint32    | pingID           | Ping sequence number                             |
| 4      | float64   | timestamp        | Unity Time.timeAsDouble                          |
| 12     | float32×3 | sonarPos         | World-space position (x, y, z)                   |
| 24     | float32×4 | sonarRot         | Quaternion (x, y, z, w)                          |
| 40     | uint16    | numberOfBeams    | Output image width (e.g. 256)                    |
| 42     | uint16    | numberOfRangeBins| Output image height (e.g. 650)                   |
| 44     | float32   | maxRange         | Metres                                           |
| 48     | float32   | swathAngleDeg    | Horizontal FOV of the sonar fan                  |
| 52     | float32   | verticalFOVDeg   | Vertical opening of the fan slice                |
| 56     | float32   | paddingDeg       | Grid extends this far beyond swath/vFOV per side |
| 60     | float32   | mountTiltDeg     | Mount tilt applied about the sonar X axis        |
| 64     | float32   | speedOfSound     | m/s                                              |
| 68     | float32   | sourceLevel_dB   | Sonar source level                               |
| 72     | float32   | pulseLength_s    | Pulse duration in seconds                        |
| 76     | float32   | frequency_Hz     | Centre frequency                                 |
| 80     | uint16    | gridWidth        | Azimuth samples W (e.g. 512)                     |
| 82     | uint16    | gridHeight       | Elevation samples H (e.g. 128)                   |
| 84     | uint32    | flags            | Bit 0: sonar submerged (1 = in water). When 0, Python ignores all returns and sends a noise-floor-only image. Other bits reserved. |

### Range Plane (W × H × float32)

Immediately after the header. Row-major, `H` rows of `W` floats.

- Row `r` = elevation index; elevation of a row centre is
  `el = -(verticalFOVDeg/2 + paddingDeg) + (r + 0.5) · elSpan/H`
  where `elSpan = verticalFOVDeg + 2·paddingDeg`.
- Column `c` = azimuth index; azimuth of a column centre is
  `az = -(swathAngleDeg/2 + paddingDeg) + (c + 0.5) · azSpan/W`
  where `azSpan = swathAngleDeg + 2·paddingDeg`.
- Value = ray hit **range in metres** along the ray (not perpendicular depth).
  `0.0` = no return within maxRange.

Ray direction for a cell, in the sonar's local frame (Unity convention):
`dir(az, el) = AngleAxis(az, forward) · AngleAxis(el, right) · AngleAxis(mountTiltDeg, right) · down`

This matches the legacy beam geometry: azimuth fans about the sonar's
forward (Z+) axis starting from straight down.

### Material Plane (W × H × uint8)

Immediately after the range plane. Same row-major ordering.
Value = material type index into `processor/config/materials.yaml`
(0 = default). Meaningless where the range plane is 0.

Total payload for 512×128: 88 + 262,144 + 65,536 = 327,768 bytes.

---

## Python → Unity: Intensity Image

### Response Payload

| Offset | Type      | Field         | Description                            |
|--------|-----------|---------------|----------------------------------------|
| 0      | uint32    | pingID        | Echoed ping ID                         |
| 4      | uint16    | beams         | Image width (256)                      |
| 6      | uint16    | rangeBins     | Image height (650)                     |
| 8      | float32[] | intensityData | Row-major float32, normalised 0–1      |

Total payload: 8 + (256 × 650 × 4) = 665,608 bytes.

---

## Beam Mapping (informative)

Python maps grid columns to output beams by azimuth value:

```
beamStep = swathAngleDeg / numberOfBeams
beam     = floor((az + swathAngleDeg/2) / beamStep)
```

Columns whose azimuth falls outside `±swathAngleDeg/2` (the padding region)
are used only for normal estimation, never binned.

---

## Constants

```
HOST = "127.0.0.1"
PORT = 9000
FRAME_HEADER_SIZE = 88
RESPONSE_HEADER_SIZE = 8
```
