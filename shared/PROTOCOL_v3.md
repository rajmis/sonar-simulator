# Sonar Simulator TCP Protocol Specification: v3 (draft)

Status: **draft for review**, 2026-11-11. This is the **new version (v3)**. It supersedes the **old version (v2)**,
[`PROTOCOL_v2.md`](PROTOCOL_v2.md), and is not wire-compatible with it.
Rationale for each change: [`docs/protocol_v3_responses.md`](../docs/protocol_v3_responses.md).

## Overview

Unity (client) connects to the sonar image generator (server) on `localhost:9000`, or the IP address where the
generator runs. There is one bidirectional TCP connection. Byte order is **little-endian** throughout.

Unity sends a **CONFIG** and waits for a **CONFIG_ACK**. Unity then sends one **FRAME** per ping, and the
generator replies to each with one **IMAGE**.

Each FRAME carries the **first hit** of every ray: range, material and surface normal. Multipath (later hits and
reflections) is out of scope for this version.

### Changes from the old version (v2)

| # | Item | Old version (v2) | New version (v3) |
|---|------|------------------|------------------|
| 1 | Message framing | `[length][payload]` | `[magic 0x3BA4B839][length][payload]` ([Message Framing](#message-framing)) |
| 2 | Protocol version | Not sent | CONFIG carries `protocolVersion = 3`; ACK status `3` if unsupported |
| 3 | CONFIG size | 48 bytes | 52 bytes ([CONFIG](#unity--generator-config-msgtype--1)) |
| 4 | `paddingDeg` | CONFIG field; the grid extends past the swath and vertical FOV so the generator can estimate normals | **Removed.** The grid covers exactly the swath and vertical FOV |
| 5 | Surface normals | Estimated by the generator from range differences | **Sent by Unity** from `RaycastHit.normal`, in every FRAME ([Data Planes](#data-planes)) |
| 6 | `frequency_Hz` | CONFIG field | **Removed.** Centre frequency and bandwidth are fixed in the generator, as agreed at the start of the project |
| 7 | Output image | Polar only | Polar or Cartesian, chosen in CONFIG (`imgType`, `imgWidth`, `imgHeight`) |
| 8 | Coordinate frames | Unity world (left-handed, Y up); attitude convention unstated | World **NED**, body **FRD**, Hamilton quaternion (scalar last), ZYX Euler ([Coordinate Frames](#coordinate-frames)) |
| 9 | Ray geometry | Zero direction straight down; azimuth rotates about the forward axis. A forward-looking fan cannot be described | Elevation 0 = ahead, −90 = down; `mountTiltDeg` uses the same convention ([Ray Geometry](#ray-geometry)) |
| 10 | FRAME header | 48 bytes | 176 bytes: adds navigation and environment fields (Table 1) ([FRAME header](#frame-header-176-bytes)) |
| 11 | FRAME size | `48 + 5·W·H` | `176 + 11·W·H` ([Frame size](#frame-size)) |
| 12 | Material IDs | 0 default, 1 seabed, 2 water surface, 3–255 objects | 0 default, 1–18 seabed by S-57 NATSUR code, 32 seabed unspecified, 33 water surface, 64–67 object materials ([Material IDs](#material-ids)) |
| 13 | IMAGE header | 12 bytes; echoes `pingID` | 192 bytes; echoes the full FRAME header and states `imgType` ([IMAGE](#generator--unity-image-msgtype--2)) |
| 14 | IMAGE index order | "Row-major", with beams called the width | Stated explicitly: index = `j·M + i`, one beam (column) at a time. Same bytes as v2 sent in practice |
| 15 | CONFIG_ACK status | 0 OK, 1 invalid value, 2 malformed | Adds 3 unsupported version, 4 unsupported option; status 1 checked against a [Valid values](#valid-values) table |
| 16 | Session rules | 6 rules | Adds lock-step exchange, strict ordering, size limits, mis-sized FRAME and unknown `msgType` handling, `pingID` wrap ([Session Rules](#session-rules)) |
| 17 | Intensity scale | "Normalised 0–1", undefined | A **fixed** 60 dB window on the TVG-corrected received level, the same for every ping ([Intensity scale](#intensity-scale)) |

---

## Message Framing

Every message in both directions:

```
[uint32 magic = 0x3BA4B839][uint32 payloadLength][payload bytes]
```

- On the wire the magic number is the bytes `39 B8 A4 3B`.
- `payloadLength` counts the payload only. It excludes the 8 bytes of magic and length.
- If the magic number is wrong, the receiver logs the error and closes the connection. There is no
  resynchronisation; the client reconnects and starts again with a CONFIG.

Every payload begins with a `uint32 msgType`:

| Direction         | msgType | Message    | Sent when                                                     |
|-------------------|---------|------------|---------------------------------------------------------------|
| Unity → Generator | 1       | CONFIG     | Right after connecting, and whenever any config field changes |
| Unity → Generator | 2       | FRAME      | Every ping                                                    |
| Generator → Unity | 1       | CONFIG_ACK | In reply to every CONFIG                                      |
| Generator → Unity | 2       | IMAGE      | In reply to every FRAME                                       |

---

## Session Rules

1. The first message after connecting **must** be a CONFIG.
2. The generator replies to every CONFIG with exactly one CONFIG_ACK. Unity waits for that ACK before
   sending any FRAME.
3. If the ACK status is `0` (OK), the new CONFIG applies to every following FRAME until the next accepted
   CONFIG. If the status is non-zero, the generator discards that CONFIG and keeps the previously accepted
   one, if any. Unity logs the status, fixes the values and resends.
4. When any config field changes, Unity sends the new CONFIG and waits for its ACK **before** the first
   FRAME that uses the new values.
5. A FRAME received before any accepted CONFIG is a protocol error. The generator logs it and closes the
   connection.
6. Receivers work out the expected payload length from the active CONFIG ([Frame size](#frame-size)) and
   never allocate memory from the wire value. A FRAME whose `payloadLength` differs from that size is a
   protocol error: the generator logs it and closes the connection.
7. Messages are processed strictly in order. Because Unity sends no FRAME between a CONFIG and its ACK, every
   FRAME belongs to exactly one CONFIG. The exchange is **lock-step**: Unity sends the next FRAME only after
   the IMAGE for the previous one has arrived. Pings that fall due while Unity is waiting are skipped, not
   queued, so the effective ping rate is the lower of the configured rate and the round-trip rate.
8. An unknown `msgType`, or a `payloadLength` below 4, is a protocol error: log and close the connection.
   A CONFIG with the wrong length gets ACK status `2`.
9. A **session** is one TCP connection. `pingID` increases by 1 per FRAME and wraps modulo 2³². It may
   restart from any value after a reconnect. Every new connection starts with a CONFIG.
10. Size limits: `gridWidth · gridHeight ≤ 4,194,304` and image `N · M ≤ 4,194,304`. That caps a FRAME at
    about 46 MB and an IMAGE at about 16 MB. A CONFIG that exceeds either limit gets ACK status `1`.

---

## Coordinate Frames

| Frame | Axes | Notes |
|-------|------|-------|
| World | **NED**: x = north, y = east, z = down; metres from the scene origin | Right-handed |
| Sonar body | **FRD**: x = forward, y = starboard, z = down | Right-handed, fixed to the sonar housing |

Attitude is the unit quaternion `q = (x, y, z, w)`, with the scalar last (Hamilton convention). It rotates
body vectors into the world: `p_world = R(q) · p_body + sonarPos`.

`R(q)` and the Euler angles (roll φ, pitch θ, yaw ψ) follow the ZYX convention, `R = Rz(ψ) · Ry(θ) · Rx(φ)`:

```
q = ( sφ cθ cψ − cφ sθ sψ,      R(q) = [ 1−2(y²+z²)   2(xy−wz)    2(xz+wy)  ]
      cφ sθ cψ + sφ cθ sψ,               [ 2(xy+wz)    1−2(x²+z²)   2(yz−wx)  ]
      cφ cθ sψ − sφ sθ cψ,               [ 2(xz−wy)    2(yz+wx)    1−2(x²+y²) ]
      cφ cθ cψ + sφ sθ sψ )     (c = cos(·/2), s = sin(·/2))

φ = atan2(2(wx+yz), 1−2(x²+y²))
θ = asin(clamp(2(wy−xz), −1, 1))        # clamp: rounding can push the argument past ±1
ψ = atan2(2(wz+xy), 1−2(y²+z²))
```

Signs: φ > 0 means the starboard side is down, θ > 0 means the nose is up, and ψ > 0 means turning from north toward east.
These formulas lose accuracy when θ is near ±90° (gimbal lock). The quaternion is the authoritative attitude.

### Converting from Unity (informative)

Unity is left-handed with Y up. Assume the scene is laid out with Unity +X = east and +Z = north. With
`p` the sonar's Unity world position and `qu` its Unity world rotation:

```
sonarPos (NED) = ( p.z, p.x, −p.y )
sonarRot (NED) = ( −qu.z, −qu.x, qu.y, qu.w )           # (x, y, z, w)
body FRD ← Unity local:   (x, y, z)_FRD = ( local.z, local.x, −local.y )
```

All three were checked numerically against `M · R_unity · Mᵀ` over 10,000 random rotations; the largest error was 0.
Unity's own `eulerAngles` use a different order (Z, X, Y) and different signs. Do not send them as φ, θ, ψ.

---

## Ray Geometry

Grid indices map to angles in the **sonar body frame**. They do not depend on the sonar's roll, pitch or yaw.
The grid covers exactly the swath and the vertical field of view; there is no padding.

```
az(c) = −swathAngleDeg/2  + (c + 0.5)·swathAngleDeg/W     # c = 0…W−1, positive = starboard
el(r) = −verticalFOVDeg/2 + (r + 0.5)·verticalFOVDeg/H    # r = 0…H−1, positive = up
```

`mountTiltDeg` (τ) is the elevation of the sonar head's boresight in the body frame: `0` = ahead,
`−90` = straight down, `+10` = 10° up. Ray direction in the body frame (FRD):

```
dir(az, el) = Ry(τ) · ( cos el·cos az,  cos el·sin az,  −sin el )

            = ( cos τ·cos el·cos az − sin τ·sin el,
                cos el·sin az,
               −sin τ·cos el·cos az − cos τ·sin el )
```

Every ray starts at `sonarPos`, the acoustic centre, and `range` is measured from that point. `sonarDep` and
`sonarAlt` are measured from the top and bottom of the housing, so for a housing of height h they differ from
`sonarPos.D` by h/2.

Here `Ry(τ)` is a rotation about the body's starboard axis, and τ > 0 raises the boresight. With τ = 0 the fan is
horizontal: a forward-looking sonar. With τ = −90 the fan is a vertical swath across the sonar's track: a
down-looking multibeam. World direction: `R(q) · dir`.

Test vectors (computed numerically):

| τ   | az  | el  | dir, FRD (x fwd, y stbd, z down) | Unity local (x right, y up, z fwd) |
|-----|-----|-----|----------------------------------|------------------------------------|
| 0   | 0   | 0   | (+1.0000, +0.0000, +0.0000)      | (+0.0000, +0.0000, +1.0000)        |
| 0   | 30  | 0   | (+0.8660, +0.5000, +0.0000)      | (+0.5000, +0.0000, +0.8660)        |
| 0   | 0   | 10  | (+0.9848, +0.0000, −0.1736)      | (+0.0000, +0.1736, +0.9848)        |
| −30 | 0   | 0   | (+0.8660, +0.0000, +0.5000)      | (+0.0000, −0.5000, +0.8660)        |
| −90 | 0   | 0   | (+0.0000, +0.0000, +1.0000)      | (+0.0000, −1.0000, +0.0000)        |
| −90 | 30  | 0   | (+0.0000, +0.5000, +0.8660)      | (+0.5000, −0.8660, +0.0000)        |
| −20 | −45 | 5   | (+0.6917, −0.7044, +0.1590)      | (−0.7044, −0.1590, +0.6917)        |

---

## Unity → Generator: CONFIG (msgType = 1)

### Payload (52 bytes)

| Offset | Type    | Field             | Description |
|--------|---------|-------------------|-------------|
| 0      | uint32  | msgType           | `1` |
| 4      | uint32  | protocolVersion   | `3` |
| 8      | uint16  | numberOfBeams     | Beams formed by the sonar (e.g. 256) |
| 10     | uint16  | numberOfRangeBins | Range bins (e.g. 650) |
| 12     | float32 | maxRange          | Metres |
| 16     | float32 | swathAngleDeg     | Horizontal opening of the fan. Must be ≤ 180 when `imgType = 1` |
| 20     | float32 | verticalFOVDeg    | Vertical opening of the fan |
| 24     | float32 | mountTiltDeg      | Boresight elevation in the body frame: 0 = ahead, −90 = down, positive = up |
| 28     | float32 | speedOfSound      | m/s |
| 32     | float32 | sourceLevel_dB    | dB re 1 µPa at 1 m |
| 36     | float32 | pulseLength_s     | Pulse duration, seconds |
| 40     | uint16  | gridWidth         | Azimuth samples W (e.g. 512) |
| 42     | uint16  | gridHeight        | Elevation samples H (e.g. 128) |
| 44     | uint32  | imgType           | `0` = polar, `1` = Cartesian |
| 48     | uint16  | imgWidth          | Cartesian only: pixels across (x, starboard). Ignored for polar |
| 50     | uint16  | imgHeight         | Cartesian only: pixels along (y, forward). Ignored for polar |

Centre frequency and pulse bandwidth are **not** in CONFIG. They are fixed in the generator, as agreed at the
start of the project. If they need to change per session, both can be added to CONFIG on request.

### Valid values

The generator answers ACK status `1` if any of these is violated. All float fields must be finite (no NaN or Inf).

| Field(s) | Constraint |
|----------|------------|
| numberOfBeams, numberOfRangeBins, gridWidth, gridHeight | ≥ 1 |
| maxRange, swathAngleDeg, verticalFOVDeg, speedOfSound, pulseLength_s | > 0 |
| swathAngleDeg | ≤ 360 (≤ 180 when imgType = 1) |
| verticalFOVDeg | ≤ 180 |
| mountTiltDeg | −90 … +90 |
| sourceLevel_dB | any finite value |
| imgType | 0 or 1 |
| imgWidth, imgHeight | ≥ 1 when imgType = 1; ignored (send 0) when imgType = 0 |
| gridWidth · gridHeight, and image N · M | ≤ 4,194,304 |

---

## Unity → Generator: FRAME (msgType = 2)

### Frame Header (176 bytes)

Every float64 sits at an 8-byte-aligned offset.

| Offset | Type      | Field     | Unit  | Description |
|--------|-----------|-----------|-------|-------------|
| 0      | uint32    | msgType   |       | `2` |
| 4      | uint32    | pingID    |       | Ping sequence number |
| 8      | float64   | timestamp | s     | Unity `Time.timeAsDouble` |
| 16     | float64   | sonarLat  | deg   | Latitude, WGS-84 decimal degrees |
| 24     | float64   | sonarLon  | deg   | Longitude, WGS-84 decimal degrees |
| 32     | float64   | sonarDep  | m     | Depth: vertical distance from the top of the sonar to the water surface (positive down) |
| 40     | float64   | sonarAlt  | m     | Altitude: vertical distance from the bottom of the sonar to the seabed |
| 48     | float64×3 | U, V, W   | m/s   | Linear velocity in the body frame (surge, sway, heave) |
| 72     | float64×3 | P, Q, R   | rad/s | Angular velocity in the body frame (roll, pitch, yaw rates) |
| 96     | float64×3 | φ, θ, ψ   | rad   | Roll, pitch, yaw relative to NED. Derived from `sonarRot`, for logging only |
| 120    | float32×3 | sonarPos  | m     | Position in NED from the scene origin |
| 132    | float32×4 | sonarRot  |       | Quaternion (x, y, z, w), body FRD → NED ([Coordinate Frames](#coordinate-frames)) |
| 148    | float32   | waveHgt   | m     | Significant wave height |
| 152    | float32   | waveFrq   | Hz    | Peak wave frequency |
| 156    | float32   | waveDir   | deg   | Direction the waves come **from**, clockwise from true north |
| 160    | float32   | uwNorth   | m/s   | Underwater current, north component |
| 164    | float32   | uwEast    | m/s   | Underwater current, east component |
| 168    | uint32    | flags     |       | Bit 0: sonar submerged (1 = in water). When 0, the generator ignores all returns and sends a noise-floor-only image. Other bits reserved (0) |
| 172    | uint32    | reserved  |       | `0` |

All angles and sizes for the grid come from the current CONFIG. A v3 generator uses `flags` and `waveHgt`.
It echoes the other fields back unchanged
([responses, Q6](../docs/protocol_v3_responses.md#q6-table-1-fields-in-the-frame-header)).

### Data Planes

Three planes follow the header in this order, each stored row-major as `H` rows of `W` cells, with row `r` and
column `c` as in [Ray Geometry](#ray-geometry). All three are always present.

| # | Plane    | Type per cell | Bytes/cell | Content |
|---|----------|---------------|-----------:|---------|
| 1 | range    | float32       | 4 | First-hit range along the ray, metres, from `sonarPos`. `0.0` = no return within maxRange |
| 2 | material | uint8         | 1 | [Material ID](#material-ids) of the first hit |
| 3 | normal   | int16 × 3     | 6 | Surface normal at the first hit (`RaycastHit.normal`), **body frame (FRD)**, encoded as described below |

**Cells with no return.** Where `range = 0`, every plane holds 0. A valid range is finite and in (0, maxRange].
Receivers treat any other value (negative, NaN, Inf, or greater than maxRange) as no return.

**Normal encoding.** Each component is `clamp(round(n · 32767), −32767, 32767)`, never −32768, and is decoded
as `n = v / 32767`. The normal points out of the surface, toward the side the ray came from. For the water
surface seen from below, that means the normal points down. In Unity:
`nLocal = Quaternion.Inverse(transform.rotation) * hit.normal`, then `n_FRD = (nLocal.z, nLocal.x, −nLocal.y)`.

The generator reconstructs the hit point as `sonarPos + R(q) · range · dir(az, el)`, so the point itself is not sent.

### Frame size

```
frameLength = 176 + 11·W·H
```

Example, 512 × 128: 176 + 65,536 · 11 = **721,072 bytes**.

---

## Material IDs

One byte per hit. Every raycastable surface must carry an ID, including the seabed and the water surface.

| ID      | Material | Source |
|---------|----------|--------|
| 0       | Default / unassigned | |
| 1–18    | Seabed, **ID = S-57 NATSUR code**: 1 mud, 2 clay, 3 silt, 4 sand, 5 stone, 6 gravel, 7 pebbles, 8 cobbles, 9 rock, 10 marsh, 11 lava, 12 snow, 13 ice, 14 coral, 15 swamp, 16 bog/moor, 17 shells, 18 boulder | [S-57 Ed. 3.1 App. A Ch. 2, NATSUR (code 113), pp. 2.153–2.154](https://iho.int/uploads/user/pubs/standards/s-57/31ApAch2.pdf) |
| 19–31   | Reserved (seabed) | |
| 32      | Seabed, sediment unspecified | |
| 33      | Water surface, seen from below | |
| 34–63   | Reserved (environment) | |
| 64      | Metal (container, hull, anchor chain) | |
| 65      | Wood (wooden boat) | |
| 66      | Concrete (pillar) | |
| 67      | Rubber (tyre) | |
| 68–255  | Reserved for further object materials | |

For a mixed seabed (S-57 lists several NATSUR values, main constituent first), Unity sends the main constituent.
NATSUR codes 10, 12, 13, 15 and 16 are land classes; they are valid but not expected underwater.

---

## Generator → Unity: CONFIG_ACK (msgType = 1)

Sent once per CONFIG, in the order the CONFIGs arrived.

| Offset | Type   | Field   | Description |
|--------|--------|---------|-------------|
| 0      | uint32 | msgType | `1` |
| 4      | uint32 | status  | Result code (below) |

| status | Meaning |
|--------|---------|
| 0      | OK: CONFIG accepted and now active |
| 1      | Invalid value: a field violates [Valid values](#valid-values) |
| 2      | Malformed: payload length is not 52 bytes |
| 3      | Unsupported `protocolVersion` |
| 4      | Unsupported option: an `imgType` that this generator does not implement yet |

---

## Generator → Unity: IMAGE (msgType = 2)

Sent once per FRAME.

### Payload

| Offset | Type      | Field         | Description |
|--------|-----------|---------------|-------------|
| 0      | uint32    | msgType       | `2` |
| 4      | uint32    | imgType       | `0` = polar, `1` = Cartesian (as in the active CONFIG) |
| 8      | uint16    | N             | Columns: `numberOfBeams` (polar) or `imgWidth` (Cartesian) |
| 10     | uint16    | M             | Rows: `numberOfRangeBins` (polar) or `imgHeight` (Cartesian) |
| 12     | uint32    | reserved      | `0` (aligns the echoed header to 8 bytes) |
| 16     | bytes[176]| frameHeader   | The FRAME header of the ping this image answers, byte for byte (includes `pingID`) |
| 192    | float32[] | intensityData | `N·M` values in 0–1 ([Intensity scale](#intensity-scale)). Index = `j·M + i` (all of column `j`, then the next column) |

Total for polar 256 × 650: 192 + 665,600 = **665,792 bytes**.

### Pixel geometry

**Polar** (`imgType = 0`): column `j` is a beam, row `i` is a range bin.

```
az_j = −swathAngleDeg/2 + (j + 0.5) · swathAngleDeg/N
r_i  = (i + 0.5) · maxRange/M
```

**Cartesian** (`imgType = 1`): a regular grid in the plane of the fan (head frame), resampled from the polar
image with bilinear interpolation.

```
X   = maxRange · sin(swathAngleDeg/2)
x_j = −X + (j + 0.5) · 2X/N                 # starboard
y_i = (i + 0.5) · maxRange/M                 # forward along the boresight
value = polar(r = √(x² + y²), az = atan2(x, y)),   NaN outside the fan or beyond maxRange
```

Cartesian pixels are square only when `2X/N = maxRange/M`. A pixel is `NaN` only when its centre is outside
the fan: `|az| > swathAngleDeg/2` or `r > maxRange`. Between the outermost beam or bin centre and the edge of
the fan, sampling clamps to the outermost beam or bin. The display must treat `NaN` as transparent. Polar
images never contain `NaN`.

### Intensity scale

Values are on a **fixed, absolute scale**, as on a real sonar display. They are not normalised per image.

```
L_tvg = RL + TVG(r)                                  # dB; RL = received level, dB re 1 µPa
value = clamp( (L_tvg − floor_dB) / 60, 0, 1 )       # inverse: L_tvg = floor_dB + 60 · value
```

- `RL` is the received level for that beam and range bin, including the receiver noise floor, as a real
  sonar would measure it.
- `TVG(r)` is a time-varied gain that compensates spreading and absorption loss along range, so the same
  seabed looks similar near and far.
- `floor_dB` is the bottom of a 60 dB display window.

`TVG(r)` and `floor_dB` are **generator constants**, fixed in the generator's configuration like the centre
frequency and bandwidth. They are the same for every ping and every session. The same echo therefore gives the
same value on every ping, so values can be compared across pings and thresholded. A strong target does not dim
the rest of the image. If the window or the TVG law needs to vary per session, both can be added to CONFIG on
request.

This defines what an image means, not how it is computed. Any generator, physics-based or learned (for
example a U-Net), must produce images on this scale.

---

## Wire Template (worked example)

This is one complete exchange with realistic values. Every byte below was produced by a reference encoder
written from the tables above and decoded again to check it. "Payload offset" counts from the first byte after
the 8-byte prefix; "message offset" includes the prefix.

### Session sequence

```
Unity                                              Generator
  │ ── CONFIG      39 B8 A4 3B | 34 00 00 00 | 52 B ──────────▶ │  validate, apply
  │ ◀──────────── 39 B8 A4 3B | 08 00 00 00 | 01 00 00 00 00 00 00 00 ── CONFIG_ACK (status 0)
  │ ── FRAME #42   39 B8 A4 3B | B0 00 0B 00 | 721,072 B ─────▶ │  collapse to image
  │ ◀──────────── 39 B8 A4 3B | C0 28 0A 00 | 665,792 B ───── IMAGE #42
  │ ── FRAME #43 …  (sent only after IMAGE #42 arrived; lock-step)
```

### CONFIG: 150° × 25° fan, 512 × 128 grid, polar output

| Offset | Type | Field | Example value | Bytes (hex, as sent) |
|---:|---|---|---|---|
| 0 | uint32 | msgType | 1 | `01 00 00 00` |
| 4 | uint32 | protocolVersion | 3 | `03 00 00 00` |
| 8 | uint16 | numberOfBeams | 256 | `00 01` |
| 10 | uint16 | numberOfRangeBins | 650 | `8A 02` |
| 12 | float32 | maxRange | 30.0 m | `00 00 F0 41` |
| 16 | float32 | swathAngleDeg | 150.0 | `00 00 16 43` |
| 20 | float32 | verticalFOVDeg | 25.0 | `00 00 C8 41` |
| 24 | float32 | mountTiltDeg | −20.0 (20° below ahead) | `00 00 A0 C1` |
| 28 | float32 | speedOfSound | 1500.0 m/s | `00 80 BB 44` |
| 32 | float32 | sourceLevel_dB | 220.0 | `00 00 5C 43` |
| 36 | float32 | pulseLength_s | 0.0001 s | `17 B7 D1 38` |
| 40 | uint16 | gridWidth | 512 | `00 02` |
| 42 | uint16 | gridHeight | 128 | `80 00` |
| 44 | uint32 | imgType | 0 = polar | `00 00 00 00` |
| 48 | uint16 | imgWidth | 0 (ignored, polar) | `00 00` |
| 50 | uint16 | imgHeight | 0 (ignored, polar) | `00 00` |

Complete message (8-byte prefix + 52-byte payload):

```
00000000  39 B8 A4 3B 34 00 00 00  01 00 00 00 03 00 00 00
00000010  00 01 8A 02 00 00 F0 41  00 00 16 43 00 00 C8 41
00000020  00 00 A0 C1 00 80 BB 44  00 00 5C 43 17 B7 D1 38
00000030  00 02 80 00 00 00 00 00  00 00 00 00
```

### CONFIG_ACK: accepted

```
39 B8 A4 3B  08 00 00 00  01 00 00 00  00 00 00 00
magic        length = 8   msgType = 1  status = 0 (OK)
```

### FRAME #42: header

Pose: the sonar is at Unity position (10, −3, 25) with rotation `Quaternion.Euler(0, 30, 0)`, heading
030°, level, moving forward at 1.5 m/s while turning at 5°/s. The housing is 0.2 m tall, so with its acoustic
centre at D = 3.0 m, the top is 2.9 m deep (`sonarDep`) and the bottom is 6.95 m above the seabed (`sonarAlt`).
The seabed is therefore at D = 10.05 m.

| Offset | Type | Field | Example value | Bytes (hex, as sent) |
|---:|---|---|---|---|
| 0 | uint32 | msgType | 2 | `02 00 00 00` |
| 4 | uint32 | pingID | 42 | `2A 00 00 00` |
| 8 | float64 | timestamp | 12.345678 s | `B4 E4 F1 B4 FC B0 28 40` |
| 16 | float64 | sonarLat | 1.2650° | `3D 0A D7 A3 70 3D F4 3F` |
| 24 | float64 | sonarLon | 103.8200° | `14 AE 47 E1 7A F4 59 40` |
| 32 | float64 | sonarDep | 2.9 m | `33 33 33 33 33 33 07 40` |
| 40 | float64 | sonarAlt | 6.95 m | `CD CC CC CC CC CC 1B 40` |
| 48 | float64 | U | 1.5 m/s | `00 00 00 00 00 00 F8 3F` |
| 56 | float64 | V | 0.0 | `00 00 00 00 00 00 00 00` |
| 64 | float64 | W | 0.0 | `00 00 00 00 00 00 00 00` |
| 72 | float64 | P | 0.0 | `00 00 00 00 00 00 00 00` |
| 80 | float64 | Q | 0.0 | `00 00 00 00 00 00 00 00` |
| 88 | float64 | R | 0.087266 rad/s (5°/s) | `87 44 E7 4A 18 57 B6 3F` |
| 96 | float64 | φ | −0.0 rad | `00 00 00 00 00 00 00 80` |
| 104 | float64 | θ | 0.0 rad | `00 00 00 00 00 00 00 00` |
| 112 | float64 | ψ | 0.523599 rad (30°) | `65 73 2D 38 52 C1 E0 3F` |
| 120 | float32×3 | sonarPos (N, E, D) | (25.0, 10.0, 3.0) m | `00 00 C8 41 00 00 20 41 00 00 40 40` |
| 132 | float32×4 | sonarRot (x, y, z, w) | (−0.0, −0.0, 0.258819, 0.965926) | `00 00 00 80 00 00 00 80 EE 83 84 3E EA 46 77 3F` |
| 148 | float32 | waveHgt | 0.5 m | `00 00 00 3F` |
| 152 | float32 | waveFrq | 0.125 Hz | `00 00 00 3E` |
| 156 | float32 | waveDir | 225.0° (from the south-west) | `00 00 61 43` |
| 160 | float32 | uwNorth | 0.2 m/s | `CD CC 4C 3E` |
| 164 | float32 | uwEast | −0.1 m/s | `CD CC CC BD` |
| 168 | uint32 | flags | 1 = submerged | `01 00 00 00` |
| 172 | uint32 | reserved | 0 | `00 00 00 00` |

`−0.0` (sign bit only, `… 80`) is what the Unity → NED conversion `(−qz, −qx, qy, qw)` produces for zero
components. Receivers must treat it as 0. Don't compare floats bit for bit.

Prefix + header as sent (payloadLength `B0 00 0B 00` = 721,072):

```
00000000  39 B8 A4 3B B0 00 0B 00  02 00 00 00 2A 00 00 00
00000010  B4 E4 F1 B4 FC B0 28 40  3D 0A D7 A3 70 3D F4 3F
00000020  14 AE 47 E1 7A F4 59 40  33 33 33 33 33 33 07 40
00000030  CD CC CC CC CC CC 1B 40  00 00 00 00 00 00 F8 3F
00000040  00 00 00 00 00 00 00 00  00 00 00 00 00 00 00 00
00000050  00 00 00 00 00 00 00 00  00 00 00 00 00 00 00 00
00000060  87 44 E7 4A 18 57 B6 3F  00 00 00 00 00 00 00 80
00000070  00 00 00 00 00 00 00 00  65 73 2D 38 52 C1 E0 3F
00000080  00 00 C8 41 00 00 20 41  00 00 40 40 00 00 00 80
00000090  00 00 00 80 EE 83 84 3E  EA 46 77 3F 00 00 00 3F
000000A0  00 00 00 3E 00 00 61 43  CD CC 4C 3E CD CC CC BD
000000B0  01 00 00 00 00 00 00 00
```

### FRAME #42: data planes

| Plane | Payload offset | Size | Index of cell (r, c) |
|---|---:|---:|---|
| range (float32) | 176 | 262,144 | `176 + 4·(r·W + c)` |
| material (uint8) | 262,320 | 65,536 | `262,320 + (r·W + c)` |
| normal (int16 × 3) | 327,856 | 393,216 | `327,856 + 6·(r·W + c)` |
| (end) | 721,072 | | |

One cell, row 64 and column 300, is a ray hitting the flat sand seabed below:

| Quantity | Value |
|---|---|
| az(300), el(64) | 13.0371°, 0.0977° |
| dir (FRD), with τ = −20° | (0.9161, 0.2256, 0.3316) |
| range | (10.05 − 3.0) m vertical / 0.3316 = 21.26 m |

| Plane | Payload offset | Value | Bytes |
|---|---:|---|---|
| range | 132,448 | 21.26 | `7B 14 AA 41` |
| material | 295,388 | 4 (NATSUR sand) | `04` |
| normal | 526,264 | (0, 0, −1) → (0, 0, −32767). The seabed faces up, and because the sonar is level in this example, up is −z in FRD | `00 00 00 00 01 80` |

A cell with no return has range `00 00 00 00`, material `00` and normal `00 00 00 00 00 00`.

### IMAGE #42: polar, 256 beams × 650 range bins

| Payload offset | Type | Field | Example value | Bytes |
|---:|---|---|---|---|
| 0 | uint32 | msgType | 2 | `02 00 00 00` |
| 4 | uint32 | imgType | 0 = polar | `00 00 00 00` |
| 8 | uint16 | N | 256 beams | `00 01` |
| 10 | uint16 | M | 650 range bins | `8A 02` |
| 12 | uint32 | reserved | 0 | `00 00 00 00` |
| 16 | bytes[176] | frameHeader | FRAME #42 header, byte for byte | (as above, from `02 00 00 00 2A 00 00 00 …`) |
| 192 | float32[N·M] | intensityData | `[beam j, bin i]` at `192 + 4·(j·650 + i)` | |

```
00000000  39 B8 A4 3B C0 28 0A 00  02 00 00 00 00 00 00 00     prefix (length 665,792), msgType, imgType
00000010  00 01 8A 02 00 00 00 00  02 00 00 00 2A 00 00 00     N, M, reserved, echoed header starts
…                                                              echoed header continues to payload 192
000000C8  CD CC CC 3D 8F C2 F5 3D  …                           [beam 0, bin 0] = 0.10, [beam 0, bin 1] = 0.12
00000AF0  52 B8 9E 3E  …                                       [beam 1, bin 0] = 0.31 (payload 2,792)
```

---

## Constants

```
HOST  = "127.0.0.1"           # or the IP address where the generator runs
PORT  = 9000
MAGIC = 0x3BA4B839            # wire bytes 39 B8 A4 3B
PROTOCOL_VERSION = 3

# Unity → Generator
MSG_CONFIG = 1
MSG_FRAME  = 2
# Generator → Unity
MSG_CONFIG_ACK = 1
MSG_IMAGE      = 2

PREFIX_SIZE       = 8          # magic + payloadLength
CONFIG_SIZE       = 52
FRAME_HEADER_SIZE = 176
FRAME_CELL_SIZE   = 11         # range 4 + material 1 + normal 6
CONFIG_ACK_SIZE   = 8
IMAGE_HEADER_SIZE = 192        # 16 + echoed FRAME header

ACK_OK                 = 0
ACK_INVALID_VALUE      = 1
ACK_MALFORMED          = 2
ACK_UNSUPPORTED_VER    = 3
ACK_UNSUPPORTED_OPTION = 4

IMG_POLAR     = 0
IMG_CARTESIAN = 1

FLAG_SUBMERGED = 1 << 0

MAT_DEFAULT         = 0
MAT_SEABED_NATSUR   = 1..18    # ID = S-57 NATSUR code
MAT_SEABED_UNKNOWN  = 32
MAT_WATER_SURFACE   = 33
MAT_METAL           = 64
MAT_WOOD            = 65
MAT_CONCRETE        = 66
MAT_RUBBER          = 67

NORMAL_SCALE = 32767           # int16 snorm
```
