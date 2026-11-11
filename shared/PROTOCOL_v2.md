# Sonar Simulator TCP Protocol Specification: v2

## Overview

Unity (client) connects to the sonar image generator (server) on `localhost:9000` (or the IP addr where the server is running). Single bidirectional TCP connection. All messages are length-prefixed. Byte order: **little-endian** throughout.

Unity sends a **CONFIG** describing the sonar and the generator acknowledges it with a **CONFIG_ACK**. Unity then sends a **polar depth + material FRAME** per ping; the generator collapses each frame into a beam × range-bin intensity **IMAGE** and sends it back.

Materials cover everything a ray can hit, not only objects in view: the **seabed** and the **water surface** have their own reserved material IDs and must be raycastable in Unity. Whether the sonar is in the water is reported separately, per ping, by the submerged flag in the FRAME header. See [Material Plane](#material-plane-w--h--uint8) and [Session Rules](#session-rules) for details.

---

## Message Framing

Every message in both directions:

```
[4-byte uint32: payload length][payload bytes]
```

TCP guarantees ordered, reliable delivery. No chunking or reassembly needed.

Every payload, in both directions, begins with a `uint32 msgType`:

| Direction         | msgType | Message    | Sent when                                              |
|-------------------|---------|------------|--------------------------------------------------------|
| Unity → Generator | 1       | CONFIG     | Right after connecting, and whenever any config field changes |
| Unity → Generator | 2       | FRAME      | Every ping                                             |
| Generator → Unity | 1       | CONFIG_ACK | In reply to every CONFIG                               |
| Generator → Unity | 2       | IMAGE      | In reply to every FRAME                                |

---

## Session Rules

1. The first message after connecting **must** be a CONFIG.
2. The generator replies to every CONFIG with exactly one CONFIG_ACK. Unity waits for that ACK before sending any FRAME.
3. If the ACK status is `0` (OK), the new CONFIG applies to every FRAME that follows, until the next accepted CONFIG. If the status is non-zero, the generator discards that CONFIG and keeps the previous accepted one (if any); Unity should log the status, fix the values and resend.
4. When any config field changes, Unity sends the new CONFIG and waits for its ACK **before** the first FRAME that uses the new values.
5. A FRAME received before any accepted CONFIG is a protocol error: the generator logs it and closes the connection.
6. The generator validates each FRAME's payload length against the current CONFIG (`48 + W·H·5`) and rejects frames that do not match.

---

## Unity → Generator: CONFIG (msgType = 1)

### Payload (48 bytes)

| Offset | Type      | Field            | Description                                      |
|--------|-----------|------------------|--------------------------------------------------|
| 0      | uint32    | msgType          | `1`                                              |
| 4      | uint16    | numberOfBeams    | Output image width (e.g. 256)                    |
| 6      | uint16    | numberOfRangeBins| Output image height (e.g. 650)                   |
| 8      | float32   | maxRange         | Metres                                           |
| 12     | float32   | swathAngleDeg    | Horizontal FOV of the sonar fan                  |
| 16     | float32   | verticalFOVDeg   | Vertical opening of the fan slice                |
| 20     | float32   | paddingDeg       | Grid extends this far beyond swath/vFOV per side |
| 24     | float32   | mountTiltDeg     | Mount tilt applied about the sonar X axis        |
| 28     | float32   | speedOfSound     | m/s                                              |
| 32     | float32   | sourceLevel_dB   | Sonar source level                               |
| 36     | float32   | pulseLength_s    | Pulse duration in seconds                        |
| 40     | float32   | frequency_Hz     | Centre frequency                                 |
| 44     | uint16    | gridWidth        | Azimuth samples W (e.g. 512)                     |
| 46     | uint16    | gridHeight       | Elevation samples H (e.g. 128)                   |

---

## Unity → Generator: FRAME (msgType = 2)

### Frame Header (48 bytes)

| Offset | Type      | Field            | Description                                      |
|--------|-----------|------------------|--------------------------------------------------|
| 0      | uint32    | msgType          | `2`                                              |
| 4      | uint32    | pingID           | Ping sequence number                             |
| 8      | float64   | timestamp        | Unity Time.timeAsDouble                          |
| 16     | float32×3 | sonarPos         | World-space position (x, y, z)                   |
| 28     | float32×4 | sonarRot         | Quaternion (x, y, z, w)                          |
| 44     | uint32    | flags            | Bit 0: sonar submerged (1 = in water). When 0, the generator ignores all returns and sends a noise-floor-only image. Other bits reserved. |

`W`, `H` and all angles below come from the current CONFIG.

### Range Plane (W × H × float32)

Immediately after the frame header. Row-major, `H` rows of `W` floats.

- Row `r` = elevation index; elevation of a row centre is `el = -(verticalFOVDeg/2 + paddingDeg) + (r + 0.5) · elSpan/H` where `elSpan = verticalFOVDeg + 2·paddingDeg`.
- Column `c` = azimuth index; azimuth of a column centre is `az = -(swathAngleDeg/2 + paddingDeg) + (c + 0.5) · azSpan/W` where `azSpan = swathAngleDeg + 2·paddingDeg`.
- Value = ray hit **range in metres** along the ray (not perpendicular depth). `0.0` = no return within maxRange.

Ray direction for a cell, in the sonar's local frame (Unity convention): `dir(az, el) = AngleAxis(az, forward) · AngleAxis(el, right) · AngleAxis(mountTiltDeg, right) · down`

Azimuth fans about the sonar's forward (Z+) axis, starting from straight down.

### Material Plane (W × H × uint8)

Immediately after the range plane. Same row-major ordering. Value = material ID of whatever the ray hit. Meaningless where the range plane is 0.

Every surface a ray can hit must carry a material ID, including the environment itself, not only objects in view. The seabed and the water surface are materials like any other and must be raycastable (e.g. have colliders) so they appear in both the range and material planes.

| ID    | Material                                               |
|-------|--------------------------------------------------------|
| 0     | Default / unassigned                                   |
| 1     | Seabed                                                 |
| 2     | Water surface (seen from below)                        |
| 3–255 | Object materials, indexed into the generator's material table |

Whether the sonar is in water is not a material; it is reported per ping by `flags` bit 0 in the frame header.

Total payload for 512×128: 48 + 262,144 + 65,536 = 327,728 bytes.

---

## Generator → Unity: CONFIG_ACK (msgType = 1)

Sent once per CONFIG, in the same order the CONFIGs arrived.

### Payload (8 bytes)

| Offset | Type      | Field         | Description                            |
|--------|-----------|---------------|----------------------------------------|
| 0      | uint32    | msgType       | `1`                                    |
| 4      | uint32    | status        | Result code (see below)                |

| status | Meaning                                                                 |
|--------|-------------------------------------------------------------------------|
| 0      | OK: CONFIG accepted and now active                                      |
| 1      | Invalid value: a count is zero, or an angle/range/physical value is non-positive or out of range |
| 2      | Malformed: payload length is not 48 bytes                               |

---

## Generator → Unity: IMAGE (msgType = 2)

Sent once per FRAME.

### Payload

| Offset | Type      | Field         | Description                            |
|--------|-----------|---------------|----------------------------------------|
| 0      | uint32    | msgType       | `2`                                    |
| 4      | uint32    | pingID        | Echoed ping ID                         |
| 8      | uint16    | beams         | Image width (256)                      |
| 10     | uint16    | rangeBins     | Image height (650)                     |
| 12     | float32[] | intensityData | Row-major float32, normalised 0–1      |

Total payload: 12 + (256 × 650 × 4) = 665,612 bytes.

---

## Beam Mapping (informative)

The generator maps grid columns to output beams by azimuth value:

```
beamStep = swathAngleDeg / numberOfBeams
beam     = floor((az + swathAngleDeg/2) / beamStep)
```

Columns whose azimuth falls outside `±swathAngleDeg/2` (the padding region) are used only for normal estimation, never binned.

---

## Constants

```
HOST = "127.0.0.1" (or the IP addr where the server is running)
PORT = 9000
# Unity → Generator
MSG_CONFIG = 1
MSG_FRAME  = 2
# Generator → Unity
MSG_CONFIG_ACK = 1
MSG_IMAGE      = 2

CONFIG_SIZE = 48
FRAME_HEADER_SIZE = 48
CONFIG_ACK_SIZE = 8
IMAGE_HEADER_SIZE = 12

ACK_OK = 0
ACK_INVALID_VALUE = 1
ACK_MALFORMED = 2

MAT_DEFAULT = 0
MAT_SEABED = 1
MAT_WATER_SURFACE = 2
FLAG_SUBMERGED = 1 << 0
```
