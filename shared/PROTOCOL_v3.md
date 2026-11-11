# Sonar Simulator TCP Protocol Specification: v3

## Overview

This document specifies what Unity sends to the sonar image generator and what it receives back.

- **Out of Unity:** for every ping, a FRAME holding the sonar's pose and, for every ray of a polar grid, the
  **range**, **material ID** and **surface normal** of the first hit.
- **Back into Unity:** for every FRAME, an IMAGE holding the sonar image for that ping.

Unity (client) connects to the generator (server) on `localhost:9000`, or the IP address where the generator
runs. There is one bidirectional TCP connection. Byte order is **little-endian** throughout.

Unity first sends a **CONFIG** describing the sonar and waits for a **CONFIG_ACK**. It then sends one
**FRAME** per ping, and receives one **IMAGE** in reply to each.

---

## Message Framing

Every message in both directions:

```
[uint32 magic = 0x3BA4B839][uint32 payloadLength][payload bytes]
```

- `payloadLength` counts the payload only. It excludes the 8 bytes of magic and length.
- If the magic number is wrong, the receiver logs the error and closes the connection. There is no
  resynchronisation; Unity reconnects and starts again with a CONFIG.

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

## Unity → Generator: CONFIG (msgType = 1)

### Payload (52 bytes)

| Offset | Type    | Field             | Description |
|--------|---------|-------------------|-------------|
| 0      | uint32  | msgType           | `1` |
| 4      | uint32  | protocolVersion   | `3` |
| 8      | uint16  | numberOfBeams     | Beams in the output image (e.g. 256) |
| 10     | uint16  | numberOfRangeBins | Range bins in the output image (e.g. 650) |
| 12     | float32 | maxRange          | Metres |
| 16     | float32 | swathAngleDeg     | Horizontal opening of the fan. Must be ≤ 180 when `imgType = 1` |
| 20     | float32 | verticalFOVDeg    | Vertical opening of the fan |
| 24     | float32 | mountTiltDeg      | Boresight elevation in the body frame: 0 = ahead, −90 = down, positive = up ([Ray Geometry](#ray-geometry)) |
| 28     | float32 | speedOfSound      | m/s |
| 32     | float32 | sourceLevel_dB    | dB re 1 µPa at 1 m |
| 36     | float32 | pulseLength_s     | Pulse duration, seconds |
| 40     | uint16  | gridWidth         | Azimuth samples W, the number of ray columns Unity casts (e.g. 512) |
| 42     | uint16  | gridHeight        | Elevation samples H, the number of ray rows Unity casts (e.g. 128) |
| 44     | uint32  | imgType           | `0` = polar, `1` = Cartesian |
| 48     | uint16  | imgWidth          | Cartesian only: pixels across (x, starboard). Send 0 for polar |
| 50     | uint16  | imgHeight         | Cartesian only: pixels along (y, forward). Send 0 for polar |

Centre frequency and pulse bandwidth are **not** in CONFIG. They are fixed, as instructed at the start of the
project. If they need to change per session, both can be added to CONFIG on request.

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

## Unity → Generator: FRAME (msgType = 2)

A FRAME is a 176-byte header followed by three per-cell data planes. Pose and normals must be converted from
Unity's frame first ([Coordinate Frames](#coordinate-frames)), and the rays cast as in [Ray Geometry](#ray-geometry).

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
| 96     | float64×3 | φ, θ, ψ   | rad   | Roll, pitch, yaw relative to NED. Derived from `sonarRot` ([Coordinate Frames](#coordinate-frames)) |
| 120    | float32×3 | sonarPos  | m     | Position in NED from the scene origin |
| 132    | float32×4 | sonarRot  |       | Quaternion (x, y, z, w), body FRD → NED. Authoritative attitude |
| 148    | float32   | waveHgt   | m     | Significant wave height |
| 152    | float32   | waveFrq   | Hz    | Peak wave frequency |
| 156    | float32   | waveDir   | deg   | Direction the waves come **from**, clockwise from true north |
| 160    | float32   | uwNorth   | m/s   | Underwater current, north component |
| 164    | float32   | uwEast    | m/s   | Underwater current, east component |
| 168    | uint32    | flags     |       | Bit 0: sonar submerged (1 = in water). When 0, the returned image contains noise only. Other bits reserved (0) |
| 172    | uint32    | reserved  |       | `0` |

All angles and sizes for the grid come from the current CONFIG. The whole header is echoed back, byte for byte,
in the IMAGE for this ping.

### Coordinate Frames

| Frame | Axes | Notes |
|-------|------|-------|
| World | **NED**: x = north, y = east, z = down; metres from the scene origin | Right-handed |
| Sonar body | **FRD**: x = forward, y = starboard, z = down | Right-handed, fixed to the sonar housing |

Attitude is the unit quaternion `q = (x, y, z, w)`, with the scalar last (Hamilton convention). It rotates
body vectors into the world: `p_world = R(q) · p_body + sonarPos`, with `R = Rz(ψ) · Ry(θ) · Rx(φ)` (ZYX):

```
R(q) = [ 1−2(y²+z²)   2(xy−wz)    2(xz+wy)  ]
       [ 2(xy+wz)    1−2(x²+z²)   2(yz−wx)  ]
       [ 2(xz−wy)    2(yz+wx)    1−2(x²+y²) ]
```

Signs: φ > 0 means the starboard side is down, θ > 0 means the nose is up, and ψ > 0 means turning from north
toward east.

#### Converting from Unity (required on the Unity side)

Unity is left-handed with Y up, so every pose and normal must be converted before it is sent. The scene is
assumed to be laid out with Unity **+X = east** and **+Z = north**. With `p = transform.position` and
`qu = transform.rotation` of the sonar:

```
sonarPos = ( p.z, p.x, −p.y )                           # NED
sonarRot = ( −qu.z, −qu.x, qu.y, qu.w )                 # (x, y, z, w), body FRD → NED

# φ, θ, ψ from sonarRot = (x, y, z, w):
φ = atan2(2(wx+yz), 1−2(x²+y²))
θ = asin(clamp(2(wy−xz), −1, 1))        # clamp: rounding can push the argument past ±1
ψ = atan2(2(wz+xy), 1−2(y²+z²))

# Any direction between Unity local space and body FRD:
FRD   = ( local.z, local.x, −local.y )
local = ( FRD.y, −FRD.z, FRD.x )
```

- Checked numerically against `M · R_unity · Mᵀ` over 10,000 random rotations; the largest error was 0.
- Sanity checks: `Quaternion.Euler(0, 30, 0)` gives ψ = +30°; `Euler(10, 0, 0)` gives θ = −10° (Unity's +X
  rotation turns the nose down); `Euler(0, 0, 10)` gives φ = −10°.
- Do not send Unity's `eulerAngles` as φ, θ, ψ. They use a different order (Z, X, Y) and different signs.
- Near θ = ±90° φ and ψ can't be separated (gimbal lock); `sonarRot` remains exact.
- Zero components can come out as `−0.0`. That is valid; receivers treat it as 0.

### Ray Geometry

Unity casts one ray per grid cell. Cell angles are defined in the **sonar body frame**, so they do not depend
on the sonar's roll, pitch or yaw. The grid covers exactly the swath and the vertical field of view.

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

`Ry(τ)` is a rotation about the body's starboard axis, and τ > 0 raises the boresight. With τ = 0 the fan is
horizontal: a forward-looking sonar. With τ = −90 the fan is a vertical swath across the sonar's track: a
down-looking multibeam. In Unity, convert `dir` to local space with `local = (FRD.y, −FRD.z, FRD.x)` and cast
along `transform.rotation * local`.

Every ray starts at `sonarPos`, the acoustic centre, and `range` is measured from that point. `sonarDep` and
`sonarAlt` are measured from the top and bottom of the housing, so for a housing of height h they differ from
`sonarPos.D` by h/2.

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

### Data Planes

Three planes follow the header in this order, each stored row-major as `H` rows of `W` cells, with row `r` and
column `c` as in [Ray Geometry](#ray-geometry). All three are always present.

| # | Plane    | Type per cell | Bytes/cell | Content |
|---|----------|---------------|-----------:|---------|
| 1 | range    | float32       | 4 | First-hit range along the ray (`RaycastHit.distance`), metres, from `sonarPos`. `0.0` = no hit within maxRange |
| 2 | material | uint8         | 1 | [Material ID](#material-ids) of the surface hit |
| 3 | normal   | int16 × 3     | 6 | Surface normal at the hit (`RaycastHit.normal`), **body frame (FRD)**, encoded as below |

**Cells with no hit.** Where `range = 0`, every plane holds 0. A valid range is finite and in (0, maxRange].

**Normal encoding.** Convert to the body frame, then quantise each component:

```
nLocal = Quaternion.Inverse(transform.rotation) * hit.normal
n      = ( nLocal.z, nLocal.x, −nLocal.y )                  # FRD
v      = clamp( round(n · 32767), −32767, 32767 )           # int16 per component; never −32768
```

The receiver decodes `n = v / 32767`. The normal points out of the surface, toward the side the ray came from.
For the water surface seen from below, it points down.

### Frame size

```
frameLength = 176 + 11·W·H
```

Example, 512 × 128: 176 + 65,536 · 11 = **721,072 bytes**.

### Material IDs

One byte per hit. Every surface a ray can hit must carry an ID, including the seabed and the water surface,
and both must have colliders.

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

Where each pixel lies, for displaying or overlaying the image.

**Polar** (`imgType = 0`): column `j` is a beam, row `i` is a range bin.

```
az_j = −swathAngleDeg/2 + (j + 0.5) · swathAngleDeg/N
r_i  = (i + 0.5) · maxRange/M
```

**Cartesian** (`imgType = 1`): a regular grid in the plane of the fan, with y along the boresight.

```
X   = maxRange · sin(swathAngleDeg/2)
x_j = −X + (j + 0.5) · 2X/N                 # starboard
y_i = (i + 0.5) · maxRange/M                 # forward along the boresight
```

Pixel `(j, i)` shows the point at range `√(x_j² + y_i²)` and azimuth `atan2(x_j, y_i)`. It shows slant range
projected onto the fan plane, not ground range. Pixels are square only when `2X/N = maxRange/M`. Pixels whose
centre lies outside the fan (`|az| > swathAngleDeg/2` or range > maxRange) are `NaN`; display them as
transparent. Polar images never contain `NaN`.

### Intensity scale

Values are on a **fixed scale**, as on a real sonar display. They are not normalised per image:

- Linear in dB over a 60 dB window: `0` is the bottom of the window and `1` is 60 dB above it.
- Range losses are already compensated, so the same seabed appears similarly bright near and far.
- The window is the same for every ping and every session, so the same echo gives the same value on every
  ping. Values can be compared across pings and thresholded, and a strong target does not dim the rest of the image.
- The receiver noise floor is part of the image.

Unity can display the values directly with any colour map, without rescaling.

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
