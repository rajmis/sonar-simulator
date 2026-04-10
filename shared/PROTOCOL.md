# Sonar Simulator TCP Protocol Specification

## Overview

Unity (client) connects to Python (server) on `localhost:9000`.
Single bidirectional TCP connection. All messages are length-prefixed.
Byte order: **little-endian** throughout.

---

## Message Framing

Every message in both directions:

```
[4-byte uint32: payload length][payload bytes]
```

TCP guarantees ordered, reliable delivery. No chunking or reassembly needed.

---

## Unity → Python: Ping Payload

### Ping Header (80 bytes)

| Offset | Type      | Field            | Description                          |
|--------|-----------|------------------|--------------------------------------|
| 0      | uint32    | pingID           | Ping sequence number                 |
| 4      | float64   | timestamp        | Unity Time.timeAsDouble              |
| 12     | float32×3 | sonarPos         | World-space position (x, y, z)       |
| 24     | float32×4 | sonarRot         | Quaternion (x, y, z, w)              |
| 40     | uint16    | numberOfBeams    | e.g. 256                             |
| 42     | uint16    | subBeamsPerBeam  | e.g. 45                              |
| 44     | float32   | maxRange         | Metres                               |
| 48     | uint16    | numberOfRangeBins| e.g. 650                             |
| 50     | float32   | swathAngleDeg    | Horizontal FOV                       |
| 54     | float32   | verticalFOVDeg   | Vertical FOV                         |
| 58     | float32   | speedOfSound     | m/s                                  |
| 62     | float32   | sourceLevel_dB   | Sonar source level                   |
| 66     | float32   | pulseLength_s    | Pulse duration in seconds            |
| 70     | float32   | frequency_Hz     | Centre frequency                     |
| 74     | uint16    | subBeamsH        | Sub-beams across horizontal          |
| 76     | uint16    | subBeamsV        | Sub-beams across vertical            |
| 78     | uint16    | _reserved        | Padding to 80 bytes                  |

### Ray Records (48 bytes each)

Immediately after header. Count = `numberOfBeams × subBeamsPerBeam`.

| Offset | Type      | Field         | Description                          |
|--------|-----------|---------------|--------------------------------------|
| 0      | uint16    | beamIndex     | Parent beam index [0, numberOfBeams) |
| 2      | uint16    | subBeamIndex  | Sub-beam index [0, subBeamsPerBeam)  |
| 4      | uint8     | didHit        | 1 = hit, 0 = miss                   |
| 5      | float32   | hitDistance    | Range in metres (0 if miss)          |
| 9      | float32×3 | hitNormal     | Surface normal at hit (0,0,0 if miss)|
| 21     | float32×3 | rayDir        | Normalised ray direction             |
| 33     | uint16    | objectID      | Target object identifier             |
| 35     | uint8     | materialType  | Enum index into material table       |
| 36     | float32×3 | hitPoint      | World-space hit position             |

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

## Constants

```
HOST = "127.0.0.1"
PORT = 9000
PING_HEADER_SIZE = 80
RAY_RECORD_SIZE = 48
RESPONSE_HEADER_SIZE = 8
```
