# Sonar Simulator TCP Protocol Specification: v3

## Overview

This document specifies what Unity sends to the sonar image generator and what it receives back.

- **Out of Unity:** for every ping, a FRAME holding the sonar's pose and, for every ray of a polar grid, the **range**, **material ID** and **surface normal** of the first hit.
- **Back into Unity:** for every FRAME, an IMAGE holding the sonar image for that ping.

Unity (client) connects to the generator (server) on `localhost:9000`, or the IP address where the generator runs. There is one bidirectional TCP connection. Byte order is **little-endian** throughout.

The sonar's settings are not sent over the connection. Unity and the generator each read them from a local config file before the run ([Sonar Configuration](#sonar-configuration)), and they stay fixed for the whole run. After connecting, Unity sends one **FRAME** per ping and receives one **IMAGE** in reply to each. There is no handshake.

---

## Message Framing

Every message in both directions:

```
[uint32 magic = 0x3BA4B839][uint32 payloadLength][payload bytes]
```

- `payloadLength` counts the payload only. It excludes the 8 bytes of magic and length.
- If the magic number is wrong, the receiver logs the error and closes the connection. There is no resynchronisation; Unity reconnects and starts sending FRAMEs again.

Every payload begins with a `uint32 msgType`:

| Direction         | msgType | Message | Sent when               |
|-------------------|---------|---------|-------------------------|
| Unity → Generator | 1       | FRAME   | Every ping              |
| Generator → Unity | 1       | IMAGE   | In reply to every FRAME |

---

## Session Rules

1. Before the run, Unity and the generator each load their local config file ([Sonar Configuration](#sonar-configuration)). The values stay fixed for the whole run and are never sent over the connection.
2. After connecting, Unity sends only FRAMEs and the generator sends only IMAGEs.
3. The generator works out the expected FRAME payload length from the config ([Frame size](#frame-size)) and never allocates memory from the wire value. A FRAME whose `payloadLength` differs from that size is a protocol error: the generator logs it and closes the connection.
4. The exchange is **lock-step**: after sending a FRAME, Unity waits for its IMAGE before sending the next FRAME. At most one FRAME is ever in flight, and neither side keeps a queue. Messages are processed strictly in order.
5. An unknown `msgType`, or a `payloadLength` below 4, is a protocol error: log and close the connection.
6. A **session** is one TCP connection. `pingID` increases by 1 per FRAME and wraps modulo 2³². It may restart from any value after a reconnect.

---

## Sonar Configuration

Unity and the generator each read the sonar configuration from a local config file at startup. It is never sent over the connection and stays fixed for the whole run. To change any value, stop both sides, update the config and restart.

It is the user's responsibility to make sure both sides load the correct, matching config. The protocol cannot fully check this. Rule 3 of [Session Rules](#session-rules) catches only a different number of grid cells (`gridWidth · gridHeight`); a grid with the same number of cells, such as 512 × 128 against 256 × 256, passes. Every IMAGE carries `imgType`, `N` and `M`, which Unity can compare with its own config if it wants. Any other difference, such as in `maxRange` or `swathAngleDeg`, goes undetected and gives wrong images without any error. Using a copy of the same config file on both sides avoids this.

The table below is **an example** of a sonar configuration, listing the fields this document's formulas use. The full configuration may contain more fields, such as speed of sound, source level or pulse length, depending on what the generator needs.

| Field             | Type    | Description |
|-------------------|---------|-------------|
| numberOfBeams     | uint16  | Beams in the polar image (e.g. 256) |
| numberOfRangeBins | uint16  | Range bins in the polar image (e.g. 650) |
| maxRange          | float32 | Metres |
| swathAngleDeg     | float32 | Horizontal opening of the fan |
| verticalFOVDeg    | float32 | Vertical opening of the fan |
| mountTiltDeg      | float32 | Boresight elevation in the body frame: 0 = ahead, −90 = down, positive = up ([Ray Geometry](#ray-geometry)) |
| gridWidth         | uint16  | Azimuth samples W, the number of ray columns Unity casts (e.g. 512) |
| gridHeight        | uint16  | Elevation samples H, the number of ray rows Unity casts (e.g. 128) |
| imgType           | uint32  | `0` = polar, `1` = Cartesian |
| imgWidth          | uint16  | Cartesian only: pixels across (x, starboard). 0 for polar |
| imgHeight         | uint16  | Cartesian only: pixels along (y, forward). 0 for polar |

Centre frequency and pulse bandwidth are **not** configurable. They are fixed, as instructed at the start of the project.

### Valid values

The config must satisfy these constraints, and all float fields must be finite (no NaN or Inf). If the generator's config breaks any of them, or names an `imgType` the generator does not implement yet, the generator accepts each connection and closes it right away, logging the reason.

| Field(s) | Constraint |
|----------|------------|
| numberOfBeams, numberOfRangeBins, gridWidth, gridHeight | ≥ 1 |
| maxRange, swathAngleDeg, verticalFOVDeg | > 0 |
| swathAngleDeg | ≤ 360 (≤ 180 when imgType = 1) |
| verticalFOVDeg | ≤ 180 |
| mountTiltDeg | −90 … +90 |
| imgType | 0 or 1 |
| imgWidth, imgHeight | ≥ 1 when imgType = 1; ignored (set 0) when imgType = 0 |
| gridWidth · gridHeight, and image N · M | ≤ 2048 × 2048 cells |

---

## Unity → Generator: FRAME (msgType = 1)

A FRAME is a 176-byte header followed by three per-cell data planes. Pose and normals must be converted from Unity's frame first ([Coordinate Frames](#coordinate-frames)), and the rays cast as in [Ray Geometry](#ray-geometry).

### Frame Header (176 bytes)

Every float64 sits at an 8-byte-aligned offset.

| Offset | Type      | Field     | Unit  | Description |
|--------|-----------|-----------|-------|-------------|
| 0      | uint32    | msgType   |       | `1` |
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

All angles and sizes for the grid come from the [Sonar Configuration](#sonar-configuration). The whole header is echoed back, byte for byte, in the IMAGE for this ping.

### Coordinate Frames

| Frame | Axes | Notes |
|-------|------|-------|
| World | **NED**: x = north, y = east, z = down; metres from the scene origin | Right-handed |
| Sonar body | **FRD**: x = forward, y = starboard, z = down | Right-handed, fixed to the sonar housing |

Attitude is the unit quaternion `q = (x, y, z, w)`, with the scalar last (Hamilton convention). It rotates body vectors into the world: `p_world = R(q) · p_body + sonarPos`, with `R = Rz(ψ) · Ry(θ) · Rx(φ)` (ZYX):

```
R(q) = [ 1−2(y²+z²)   2(xy−wz)    2(xz+wy)  ]
       [ 2(xy+wz)    1−2(x²+z²)   2(yz−wx)  ]
       [ 2(xz−wy)    2(yz+wx)    1−2(x²+y²) ]
```

Signs: φ > 0 means the starboard side is down, θ > 0 means the nose is up, and ψ > 0 means turning from north toward east.

#### Converting from Unity (required on the Unity side)

Unity is left-handed with Y up, so every pose and normal must be converted before it is sent. The scene is assumed to be laid out with Unity **+X = east** and **+Z = north**. With `p = transform.position` and `qu = transform.rotation` of the sonar:

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
- Sanity checks: `Quaternion.Euler(0, 30, 0)` gives ψ = +30°; `Euler(10, 0, 0)` gives θ = −10° (Unity's +X rotation turns the nose down); `Euler(0, 0, 10)` gives φ = −10°.
- Do not send Unity's `eulerAngles` as φ, θ, ψ. They use a different order (Z, X, Y) and different signs.
- Near θ = ±90° φ and ψ can't be separated (gimbal lock); `sonarRot` remains exact.
- Zero components can come out as `−0.0`. That is valid; receivers treat it as 0.

### Ray Geometry

Unity casts one ray per grid cell. Cell angles are defined in the **sonar body frame**, so they do not depend on the sonar's roll, pitch or yaw. The grid covers exactly the swath and the vertical field of view.

```
az(c) = −swathAngleDeg/2  + (c + 0.5)·swathAngleDeg/W     # c = 0…W−1, positive = starboard
el(r) = −verticalFOVDeg/2 + (r + 0.5)·verticalFOVDeg/H    # r = 0…H−1, positive = up
```

`mountTiltDeg` (τ) is the elevation of the sonar head's boresight in the body frame: `0` = ahead, `−90` = straight down, `+10` = 10° up. Ray direction in the body frame (FRD):

```
dir(az, el) = Ry(τ) · ( cos el·cos az,  cos el·sin az,  −sin el )

            = ( cos τ·cos el·cos az − sin τ·sin el,
                cos el·sin az,
               −sin τ·cos el·cos az − cos τ·sin el )
```

`Ry(τ)` is a rotation about the body's starboard axis, and τ > 0 raises the boresight. With τ = 0 the fan is horizontal: a forward-looking sonar. With τ = −90 the fan is a vertical swath across the sonar's track: a down-looking multibeam. In Unity, convert `dir` to local space with `local = (FRD.y, −FRD.z, FRD.x)` and cast along `transform.rotation * local`.

Every ray starts at `sonarPos`, the acoustic centre, and `range` is measured from that point. `sonarDep` and `sonarAlt` are measured from the top and bottom of the housing, so for a housing of height h they differ from `sonarPos.D` by h/2.

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

Three planes follow the header in this order, each stored row-major as `H` rows of `W` cells, with row `r` and column `c` as in [Ray Geometry](#ray-geometry). All three are always present.

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

The receiver decodes `n = v / 32767`. The normal points out of the surface, toward the side the ray came from. For the water surface seen from below, it points down.

### Frame size

```
frameLength = 176 + 11·W·H
```

### Material IDs

One byte per hit. Every surface a ray can hit must carry an ID, including the seabed and the water surface, and both must have colliders.

| ID      | Material | Source |
|---------|----------|--------|
| 0       | No hit (cells with `range = 0`); never assigned to a material | |
| 1–18    | Seabed, **ID = S-57 NATSUR code**: 1 mud, 2 clay, 3 silt, 4 sand, 5 stone, 6 gravel, 7 pebbles, 8 cobbles, 9 rock, 10 marsh, 11 lava, 12 snow, 13 ice, 14 coral, 15 swamp, 16 bog/moor, 17 shells, 18 boulder | [S-57 Ed. 3.1 App. A Ch. 2, NATSUR (code 113), pp. 2.153–2.154](https://iho.int/uploads/user/pubs/standards/s-57/31ApAch2.pdf) |
| 19      | Reserved | |
| 20      | Water surface, seen from below | |
| 21      | Metal (container, hull, anchor chain) | |
| 22      | Wood (wooden boat) | |
| 23      | Concrete (pillar) | |
| 24      | Rubber (tyre) | |
| 25–255  | Reserved, for example seagrass, kelp, fish school | |

For a mixed seabed (S-57 lists several NATSUR values, main constituent first), Unity sends the main constituent. NATSUR codes 10, 12, 13, 15 and 16 are land classes; they are valid but not expected underwater.

**IDs are append-only.** A new material takes a reserved ID (never 0). An assigned ID is never renumbered or reused, even if its material is withdrawn. The ID table stays the same for the whole run.

---

## Generator → Unity: IMAGE (msgType = 1)

Sent once per FRAME.

### Payload

| Offset | Type      | Field         | Description |
|--------|-----------|---------------|-------------|
| 0      | uint32    | msgType       | `1` |
| 4      | uint32    | imgType       | `0` = polar, `1` = Cartesian (as in the [Sonar Configuration](#sonar-configuration)) |
| 8      | uint16    | N             | Columns: `numberOfBeams` (polar) or `imgWidth` (Cartesian) |
| 10     | uint16    | M             | Rows: `numberOfRangeBins` (polar) or `imgHeight` (Cartesian) |
| 12     | uint32    | reserved      | `0` (aligns the echoed header to 8 bytes) |
| 16     | bytes[176]| frameHeader   | The FRAME header of the ping this image answers, byte for byte (includes `pingID`) |
| 192    | float32[] | intensityData | `N·M` values in 0–1 ([Intensity scale](#intensity-scale)). Index = `j·M + i` (all of column `j`, then the next column) |

Total: `192 + 4·N·M`.

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

Pixel `(j, i)` shows the point at range `√(x_j² + y_i²)` and azimuth `atan2(x_j, y_i)`. It shows slant range projected onto the fan plane, not ground range. Pixels are square only when `2X/N = maxRange/M`. Pixels whose centre lies outside the fan (`|az| > swathAngleDeg/2` or range > maxRange) are `NaN`; display them as transparent. Polar images never contain `NaN`.

### Intensity scale

Values are on a **fixed scale**, as on a real sonar display. They are not normalised per image:

- Linear in dB over a fixed window: `0` is the bottom of the window and `1` is the top.
- Range losses are already compensated, so the same seabed appears similarly bright near and far.
- The window is the same for every ping and every session, so the same echo gives the same value on every ping. Values can be compared across pings and thresholded, and a strong target does not dim the rest of the image.
- The receiver noise floor is part of the image.

Unity can display the values directly with any colour map, without rescaling.

---

## Constants

```
HOST  = "127.0.0.1"           # or the IP address where the generator runs
PORT  = 9000
MAGIC = 0x3BA4B839            # wire bytes 39 B8 A4 3B

MSG_FRAME = 1                  # Unity → Generator
MSG_IMAGE = 1                  # Generator → Unity

PREFIX_SIZE       = 8          # magic + payloadLength
FRAME_HEADER_SIZE = 176
FRAME_CELL_SIZE   = 11         # range 4 + material 1 + normal 6
IMAGE_HEADER_SIZE = 192        # 16 + echoed FRAME header

IMG_POLAR     = 0
IMG_CARTESIAN = 1

FLAG_SUBMERGED = 1 << 0

MAT_SEABED_NATSUR   = 1..18    # ID = S-57 NATSUR code
MAT_WATER_SURFACE   = 20
MAT_METAL           = 21
MAT_WOOD            = 22
MAT_CONCRETE        = 23
MAT_RUBBER          = 24

NORMAL_SCALE = 32767           # int16 snorm
```
