# Protocol v3: Responses to the Integration Questions

2026-11-11 · answers to the questions raised after the **old version (v2)**,
[`shared/PROTOCOL_v2.md`](../shared/PROTOCOL_v2.md), was implemented.
The **new version (v3)** is [`shared/PROTOCOL_v3.md`](../shared/PROTOCOL_v3.md) (draft).

Three findings change the design and are worth reading first:

1. **The old ray formula cannot describe a forward-looking sonar.** In v2, azimuth rotates about the sonar's
   forward axis, starting from straight down. Setting `mountTiltDeg = −90` to look ahead collapses the whole
   fan onto one direction (verified numerically: azimuths −60°, 0° and +60° all gave `(0, 0, 1)`). v3 uses
   a forward-looking-native formula that also covers the down-looking multibeam case (Q8–Q10).
2. **Unity's quaternion is not the quaternion in your MATLAB functions.** Your functions are consistent with
   each other, and they assume NED / FRD frames. Unity is left-handed with Y up. The conversion is
   `q_NED = (−qz, −qx, qy, qw)` (Q7).
3. **"Complete RaycastHit" is best sent as a normals plane, not as the struct.** Most RaycastHit fields are
   meaningless outside the Unity process or on simple colliders (Q1). With Unity's normals in every FRAME,
   `paddingDeg` is no longer needed and is removed (Q3).

Question 14 was not in the list, so these responses skip from Q13 to Q15b.

## Summary

| Question | Answer |
|----------|--------|
| Q1. Send the complete RaycastHit? | Send the surface normal (`RaycastHit.normal`) for every cell, alongside the range and material ID already sent. The rest of the struct is redundant or meaningless outside Unity |
| Q2. Add a magic number prefix? | Yes: `[magic 0x3BA4B839][length][payload]` |
| Q3. What is the optimal `paddingDeg`? | None: `paddingDeg` is removed, because Unity now sends the normals |
| Q4. Add pulse bandwidth to CONFIG? | No. Centre frequency and bandwidth stay fixed in the generator, as agreed at the start of the project. Both can be added to CONFIG if you need them to vary |
| Q5. Multipath and later hits? | Out of scope for v3. FRAMEs carry the first hit only |
| Q6. Add the Table 1 fields to the FRAME header? | Yes, all of them (header grows to 176 bytes). Used now: submerged flag, wave height. The rest are echoed back for logging and later use |
| Q7. Is our Euler ↔ quaternion convention right? | Yes: ZYX, Hamilton, scalar last, body → NED. Unity's pose needs converting to NED / FRD first |
| Q8–9. Are el, az and the ray direction independent of attitude? | Yes: all three are body-frame quantities |
| Q10. Elevation 0 = ahead, −90 = down? | Adopted, for both `el` and `mountTiltDeg` |
| Q11. Object materials? | IDs 64–67: metal, wood, concrete, rubber |
| Q12. Echo the FRAME header in the IMAGE? | Yes, the full 176-byte header |
| Q13. Polar or Cartesian output? | Both, chosen once per session in CONFIG |
| Q15b. Use the S-57 NATSUR seabed classes? | Yes: seabed material ID = NATSUR code (IDs 1–18) |

---

## Q1. Porting the complete RaycastHit

**Recommendation:** send the surface normal for every cell. Don't send the RaycastHit struct itself.

We checked what RaycastHit contains in Unity 6000.6.8f1 with a throwaway probe project (see
[Feasibility checks](#feasibility-checks)). It is a 48-byte struct with these fields:

| Field | Useful to the generator? |
|-------|--------------------------|
| `normal` | **Yes.** It replaces normals estimated from depth differences. Those are approximate at edges (a cell at a silhouette edge is currently treated as hit head-on) and need padding cells |
| `distance` | Already sent (the range plane) |
| `point` | Redundant: `point = sonarPos + distance · dir` |
| `colliderEntityId` | No. It identifies *which* object was hit, and the raw value is only meaningful inside the running Unity process. What the generator needs is what the object is *made of*, which the material ID already gives |
| `triangleIndex`, `barycentricCoordinate`, `textureCoord(2)`, `lightmapCoord` | No. They are only meaningful on MeshColliders; on primitive colliders the probe returned `triangleIndex = −1` and a texture coordinate of `(0, 0)` |
| `collider`, `transform`, `rigidbody`, `articulationBody` | Object references; they cannot be serialised |

So each cell carries three values: range, material ID and normal. The material ID comes from the collider's
material assignment in Unity, not from RaycastHit, and was already in v2.

v3 adds the normal plane as 3 × int16 per cell. Each component is stored as `round(n · 32767)`, which keeps
the angle error around 0.003°. The normal is given in the sonar body frame, so the generator doesn't need the
pose to use it. Cost for 512 × 128: +393 KB per frame. That brings the frame to 721 KB, about 87 Mbit/s at 15 Hz,
which is fine on localhost or gigabit Ethernet. A float32 × 3 plane would double that cost without adding
useful precision.

One thing to know for Unity upgrades: in Unity 6.6, `RaycastHit.colliderInstanceID` is a **compile error**
(use `colliderEntityId` instead). The repository doesn't use it today.

## Q2. Magic number prefix

Yes. v3 frames every message as `[uint32 0x3BA4B839][uint32 payloadLength][payload]`, so on the wire the
magic number is the bytes `39 B8 A4 3B`. It goes before the length, so a receiver checks it before
trusting the length. On a mismatch the receiver closes the connection rather than trying to resynchronise:
on TCP the only realistic causes are the wrong peer or a protocol-version mismatch. For the same reason
CONFIG now also carries `protocolVersion = 3`.

## Q3. Choosing `paddingDeg`

`paddingDeg` is **removed** in v3. In v2 its only purpose was to give the generator neighbouring cells beyond
the edge of the fan, so it could estimate surface normals from range differences. Unity now sends the normal
from `RaycastHit.normal` for every cell (Q1), so those extra cells are no longer needed. The grid covers
exactly the swath and the vertical field of view, and every cell contributes to the image.

The generator still looks at neighbouring cells for one thing: estimating how far each cell's footprint spreads
in range. At the grid edges it uses one-sided differences, so it needs no padding.

For reference, with the v2 default of 2° padding (150° × 25° fan, 512 × 128 grid), about 7 azimuth columns
and 9 elevation rows per side were padding: **16% of every frame** was sent but never binned.

## Q4. Pulse bandwidth

Not added. The centre frequency and pulse bandwidth stay **fixed in the generator**, as in the configuration
agreed at the start of the project, so v3 also removes `frequency_Hz` from CONFIG. If you need either to
change per session (for example, to simulate a different sonar or an FM chirp), tell us and both will be added
to CONFIG.

For background: bandwidth, not pulse length, sets range resolution, `δr = c / (2B)`. For a continuous-wave
pulse `B ≈ 1/τ`; for an FM chirp with matched filtering `B` can be far larger than `1/τ`.

## Q5. Multipath and later hits

**Out of scope for v3.** Each FRAME carries the first hit of every ray only, and the generator does not
model multipath. The water surface's own direct return is still covered: it is a first hit with material
ID 33.

## Q6. Table 1 fields in the frame header

All the Table 1 fields are added (124 bytes), and every float64 sits at an 8-byte-aligned offset.

| Field | Generator use |
|-------|---------------|
| `waveHgt` | **Now.** Roughness of the water surface's direct return (material 33): a rougher surface scatters more energy back toward the sonar |
| `waveFrq`, `waveDir` | Later: time-varying surface roughness. Echoed for now |
| `U V W P Q R` | Later: platform motion during a ping. At 30 m the round trip is 40 ms. A 10°/s yaw rate smears returns by 0.4°, about 70% of the 0.59° beam spacing |
| `φ θ ψ` | Not used: the quaternion is authoritative. Kept for logging and MATLAB, and must be derived from `sonarRot` |
| `sonarDep` | Not needed to form the image: the surface return is already in the range plane, and the `flags` submerged bit is the authoritative "in water" signal. Echoed back |
| `sonarLat`, `sonarLon`, `sonarAlt` | Not needed to form the image; altitude is already in the range plane. Echoed back for georeferencing and labelling |
| `uwNorth`, `uwEast` | No acoustic effect at these ranges. Echoed back |

**Assumptions to confirm** (Table 1 doesn't give units):
- φ, θ and ψ are in **radians**, matching the MATLAB functions.
- `waveDir` is the direction waves come **from**, in degrees clockwise from true north.
- `waveHgt` is the significant wave height.
- `waveFrq` is the peak frequency in Hz.

## Q7. Euler and quaternion convention

**Your three functions are correct and consistent with each other.** We checked them on 10,000 random attitudes:

- Euler → quaternion → Euler round trip: maximum error 2 × 10⁻¹³ rad.
- The matrix in `sonar_to_world` equals `Rz(ψ)·Ry(θ)·Rx(φ)`: maximum error 1 × 10⁻¹⁵.

That makes it the standard aerospace ZYX convention: Hamilton quaternion, scalar last, body → world, roll φ
about x, pitch θ about y, yaw ψ about z. v3 adopts exactly this.

Two conditions apply:

1. **The frames must be NED (world) and FRD (body).** Your formulas follow the usual sign convention (φ > 0
   starboard down, θ > 0 nose up, ψ > 0 north toward east) only in those frames. Unity is left-handed with
   Y up, so `transform.rotation` must be converted. Assuming Unity +X = east and +Z = north:
   `q_NED = (−qz, −qx, qy, qw)` and `pos_NED = (z, x, −y)`. Both were verified exactly against `M·R·Mᵀ`.
   Sanity checks: Unity `Euler(0, +30, 0)` → ψ = +30°; `Euler(+10, 0, 0)` → θ = −10° (Unity's +X turns the
   nose down); `Euler(0, 0, +10)` → φ = −10°.
2. **Clamp the `asin` argument** in `quaternion_to_euler` to [−1, 1]. Rounding can push it slightly past ±1,
   which makes MATLAB return a complex number. Near θ = ±90°, φ and ψ can't be separated (gimbal lock), which is
   one more reason the quaternion is authoritative.

`q` and `−q` describe the same rotation, so compare attitudes with `|q₁·q₂| ≈ 1`, not component by component.

## Q8 and Q9. Are el(r), az(c) and the ray direction independent of attitude?

**Yes.** `el(r)`, `az(c)` and `dir(az, el)` are all defined in the sonar body frame, so they don't change
with roll, pitch or yaw. Attitude enters only when converting to the world, `dir_world = R(q) · dir`.

One correction to the wording "the origin is the direction the centre of the sonar is presently pointing". The
origin of `az` and `el` is the **sonar head's boresight in the body frame**: the body x-axis tilted by the fixed
`mountTiltDeg`. That is where the sonar points in the world only after applying `R(q)`.

In the old version (v2) the angles were also body-frame, but the zero direction was **straight down** and
azimuth rotated about the forward axis: a down-looking multibeam geometry. That is why the forward-looking case
breaks (finding 1).

## Q10. Elevation convention (0 = ahead, −90 = down)

Adopted. In v3, both `el` and `mountTiltDeg` follow your convention: 0 = ahead, −90 = straight down,
positive = up. Azimuth is positive to starboard. The ray direction in the body frame (FRD) becomes:

```
dir(az, el) = Ry(τ) · ( cos el·cos az,  cos el·sin az,  −sin el ),   τ = mountTiltDeg
```

A single formula covers both kinds of sonar. `τ = 0` gives a horizontal forward-looking fan, and
`τ = −90` gives a vertical swath across the sonar's track (a down-looking multibeam). Both were checked
numerically, and the test vectors are in the spec. Converting an old v2 tilt to the new convention
(boresight only): `τ_v3 = −90 − tilt_v2`.

## Q11. Object material IDs

| ID | Material | Examples |
|----|----------|----------|
| 64 | Metal | Shipping container, ship hull, anchor chain |
| 65 | Wood | Wooden boat |
| 66 | Concrete | Pillar |
| 67 | Rubber | Tyre |

IDs 68–255 are reserved for further materials. The acoustic parameters (reflectivity, scattering, absorption)
live in the generator's material table, not in the protocol, so adding a material doesn't change the protocol.

## Q12. Echoing the FRAME header in IMAGE

Yes. The IMAGE carries the full 176-byte FRAME header byte for byte at offset 16, right after a 16-byte image
header. Each image is then self-describing: pose, time and navigation are known without matching ping IDs.
The cost is 176 bytes on a 665 KB image.

## Q13. Polar or Cartesian output

Yes. The current output is a polar image, one column per beam and one row per range bin. v3 adds
Cartesian output, sampled from the polar image with bilinear interpolation. This costs very little in the
generator, which already holds the polar image as a GPU tensor.

The choice goes in **CONFIG** (`imgType`, `imgWidth`, `imgHeight`), not the FRAME. Output type rarely changes
mid-session, and changing it through CONFIG goes through the existing ACK handshake. The IMAGE repeats
`imgType`, `N` and `M` so every image can be read on its own. We kept your proposed layout with one change: a
4-byte reserved field after `N` and `M`, so the echoed header starts on an 8-byte boundary.

Cartesian details to note:
- The Cartesian image lies in the plane of the fan. It shows slant range projected onto that plane, not
  ground range.
- Pixels outside the fan are `NaN`.
- Cartesian output requires `swathAngleDeg ≤ 180`.

The spec also makes one existing detail explicit. The data is stored **one column at a time**: index =
`j·M + i`, beam (or x) first. That is what the generator sends and Unity reads today, even though the old
version's table called beams the "width" of a row-major image.

## Q15b. S-57 NATSUR seabed classes

Yes. **Seabed material ID = NATSUR code**, so Unity sends the S-57 value directly. The codes come from
S-57 Ed. 3.1, Appendix A, Chapter 2, attribute NATSUR (code 113), printed pages 2.153–2.154:
1 mud · 2 clay · 3 silt · 4 sand · 5 stone · 6 gravel · 7 pebbles · 8 cobbles · 9 rock · 10 marsh · 11 lava ·
12 snow · 13 ice · 14 coral · 15 swamp · 16 bog/moor · 17 shells · 18 boulder.

That puts the seabed at IDs 1–18. The water surface moves from 2 (in v2) to **33**, and a sediment-unspecified seabed is **32**.

S-57 allows a mixed seabed such as "4,1,17" (sand, mud, shells), listed main constituent first. One byte per
cell holds only the **main constituent**. S-57 also defines qualifiers, NATQUA (1 fine, 2 medium, 3 coarse,
4 broken, 5 sticky, 6 soft, 7 stiff, 8 volcanic, 9 calcareous, 10 hard). These affect backscatter (fine sand
and coarse sand differ by several dB) but are not in v3. If they are needed, a qualifier plane can be added
in a later version.

---

## Changes from the old version (v2) to the new version (v3)

| # | Item | Old version (v2) | New version (v3) | Why |
|---|------|------------------|------------------|-----|
| 1 | Message framing | `[length][payload]` | `[magic 0x3BA4B839][length][payload]` | Q2 |
| 2 | Protocol version | Not sent | CONFIG carries `protocolVersion = 3`; ACK status `3` if unsupported | Q2 |
| 3 | CONFIG size | 48 bytes | 52 bytes | |
| 4 | `paddingDeg` | CONFIG field; the grid extends past the swath and vertical FOV | **Removed.** The grid covers exactly the swath and vertical FOV | Q3 |
| 5 | Surface normals | Estimated by the generator from range differences | **Sent by Unity** from `RaycastHit.normal`, in every FRAME (3 × int16, body frame) | Q1 |
| 6 | `frequency_Hz` | CONFIG field | **Removed.** Centre frequency and bandwidth are fixed in the generator | Q4 |
| 7 | Output image | Polar only | Polar or Cartesian, chosen in CONFIG (`imgType`, `imgWidth`, `imgHeight`) | Q13 |
| 8 | Coordinate frames | Unity world (left-handed, Y up); attitude convention unstated | World NED, body FRD, Hamilton quaternion (scalar last), ZYX Euler | Q7 |
| 9 | Ray geometry | Zero direction straight down; azimuth about the forward axis | Elevation 0 = ahead, −90 = down; `mountTiltDeg` uses the same convention | Q8–Q10 |
| 10 | FRAME header | 48 bytes | 176 bytes, with the Table 1 fields | Q6 |
| 11 | FRAME size | `48 + 5·W·H` (327,728 bytes for 512 × 128) | `176 + 11·W·H` (721,072 bytes for 512 × 128) | Q1, Q6 |
| 12 | Material IDs | 0 default, 1 seabed, 2 water surface, 3–255 objects | 0 default, 1–18 seabed by NATSUR code, 32 seabed unspecified, 33 water surface, 64–67 object materials | Q11, Q15b |
| 13 | IMAGE header | 12 bytes; echoes `pingID` | 192 bytes; echoes the full FRAME header and states `imgType` | Q12, Q13 |
| 14 | IMAGE index order | "Row-major", with beams called the width | Stated explicitly: index = `j·M + i`, one beam at a time (same bytes as before) | Q13 |
| 15 | CONFIG_ACK status | 0 OK, 1 invalid value, 2 malformed | Adds 3 unsupported version and 4 unsupported option; status 1 checked against a per-field valid-values table | |
| 16 | Session rules | 6 rules | Adds the lock-step exchange (one FRAME at a time; skipped pings are not queued), strict ordering, size limits, closing on a mis-sized FRAME or unknown `msgType`, and `pingID` wrap-around | |
| 17 | Intensity scale | "Normalised 0–1" | Defined: a 60 dB window below each image's peak | |

Unchanged: TCP on port 9000, little-endian byte order, the four message types and their `msgType` values, the
CONFIG → CONFIG_ACK → FRAME → IMAGE sequence, the range and material planes, and the submerged flag.

## Feasibility checks

Code was written only where feasibility was uncertain. Everything else in v3 is specification only. These
checks ran in throwaway projects outside the repository, so **no repository code changed**.

| Check | How | Result |
|-------|-----|--------|
| What a "complete RaycastHit" contains (Q1) | Unity 6000.6.8f1 batch-mode probe, reflection over `RaycastHit` | 48 bytes; field list as in Q1. `colliderInstanceID` is a compile error in 6.6 |
| Cost of a 65,536-ray first-hit batch | Same probe, average of 5 runs | 0.9 ms |
| Euler ↔ quaternion (Q7) | NumPy, 10,000 random attitudes | Consistent; matches ZYX to 1 × 10⁻¹⁵ |
| Unity → NED conversion (Q7) | NumPy, 10,000 random rotations | `(−qz, −qx, qy, qw)`, exact |
| Old ray formula with tilt −90 (Q8–Q10) | NumPy | Every azimuth gives `(0, 0, 1)`: the fan collapses |
| New ray formula | NumPy | Distinct rays; horizontal fan at τ = 0, across-track swath at τ = −90 |
| Wire template in the spec | Reference encoder, then decoded again | Every byte, offset and size matches the tables |

## Implementing v3

None of this is implemented yet. In rough order:

1. **Generator:** framing (magic, version), the new CONFIG and FRAME parsing, the new ray formula without
   padding, using Unity's normals instead of estimating them, material table re-indexed to the v3 IDs
   (steel → metal 64, sand → NATSUR 4, ...), echoed header, Cartesian resampling, and ACK codes 3 and 4.
2. **Unity:** NED / FRD conversion of pose and normals, the normals plane, Table 1 fields, material IDs, the
   new ray formula in `SonarRaycaster`, and lock-step sending (today Unity sends FRAMEs without waiting for
   the IMAGE).
3. **Later:** motion compensation using U–R (Q6), wave-driven surface roughness.

One more Unity note: the installed editor is **6000.6.8f1**, but the project is pinned to **6000.3.10f1**.
Opening it in 6.6 upgrades the project. Install 6000.3.10f1 alongside 6.6 (`unityhub` → Installs), and keep
the project on 6000.3.10f1.
