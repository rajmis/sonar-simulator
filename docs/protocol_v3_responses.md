# Protocol v3: Responses to the Integration Questions

2026-11-11 · answers to the questions raised after the **old version (v2)**, [`shared/PROTOCOL_v2.md`](../shared/PROTOCOL_v2.md), was implemented. The **new version (v3)** is [`shared/PROTOCOL_v3.md`](../shared/PROTOCOL_v3.md) (draft). It covers what Unity sends out (pose, and range, material ID and surface normal for every cell) and the image it receives back.

**Updated 2026-11-25:** the CONFIG message and its CONFIG_ACK are removed. Unity and the generator each read the sonar settings from a local config file before the run, the settings stay fixed for the run, and after connecting Unity sends only FRAMEs.

Three findings change the design and are worth reading first:

1. **The old ray formula cannot describe a forward-looking sonar.** In v2, azimuth rotates about the sonar's forward axis, starting from straight down. Setting `mountTiltDeg = −90` to look ahead collapses the whole fan onto one direction (verified numerically: azimuths −60°, 0° and +60° all gave `(0, 0, 1)`). v3 uses a forward-looking-native formula that also covers the down-looking multibeam case (Q8–Q10).
2. **Unity's quaternion is not the quaternion in your MATLAB functions.** Your functions are consistent with each other, and they assume NED / FRD frames. Unity is left-handed with Y up. The conversion is `q_NED = (−qz, −qx, qy, qw)` (Q7).
3. **"Complete RaycastHit" is best sent as a normals plane, not as the struct.** Most RaycastHit fields are meaningless outside the Unity process or on simple colliders ([RaycastHit](#point-raised-sending-the-complete-raycasthit)). With Unity's normals in every FRAME, `paddingDeg` is no longer needed and is removed (Q3).

The complete RaycastHit was raised as a point rather than a question; it is answered first. Questions 2 and 14 were not in the list, so these responses skip them.

## Summary

| Question | Answer |
|----------|--------|
| Point raised: send the complete RaycastHit | Send the surface normal (`RaycastHit.normal`) for every cell, alongside the range and material ID already sent. The rest of the struct is redundant or meaningless outside Unity |
| Q1. Add a magic number prefix? | Yes: `[magic 0x3BA4B839][length][payload]` |
| Q3. What is the optimal `paddingDeg`? | None: `paddingDeg` is removed, because Unity now sends the normals |
| Q4. Add pulse bandwidth to the sonar settings? | No. Centre frequency and bandwidth stay fixed, as instructed at the start of the project |
| Q5. Multipath and later hits? | Out of scope for v3. FRAMEs carry the first hit only |
| Q6. Add the Table 1 fields to the FRAME header? | Yes, all of them, and every IMAGE echoes them back |
| Q7. Is our Euler ↔ quaternion convention right? | Yes: ZYX, Hamilton, scalar last, body → NED. Unity's pose must be converted to NED / FRD first |
| Q8–9. Are el, az and the ray direction independent of attitude? | Yes: all three are body-frame quantities |
| Q10. Elevation 0 = ahead, −90 = down? | Adopted, for both `el` and `mountTiltDeg` |
| Q11. Object materials? | IDs 21–24: metal, wood, concrete, rubber |
| Q12. Echo the FRAME header in the IMAGE? | Yes, the full 176-byte header |
| Q13. Polar or Cartesian output? | Both, chosen in the local config file and fixed for the run |
| Q15b. Use the S-57 NATSUR seabed classes? | Yes: seabed material ID = NATSUR code (IDs 1–18) |

---

## Point raised: sending the complete RaycastHit

**Recommendation:** send the surface normal for every cell. Don't send the RaycastHit struct itself.

We checked what RaycastHit contains in Unity 6000.6.8f1 with a throwaway probe project (see [Feasibility checks](#feasibility-checks)). It is a 48-byte struct with these fields:

| Field | Needed outside Unity? |
|-------|-----------------------|
| `normal` | **Yes.** It gives the exact surface orientation at every hit. Without it, the orientation has to be estimated from range differences between neighbouring cells, which is approximate at object edges and needs extra padding cells around the grid |
| `distance` | Already sent (the range plane) |
| `point` | Redundant: `point = sonarPos + distance · dir` |
| `colliderEntityId` | No. It identifies *which* object was hit, and the raw value is only meaningful inside the running Unity process. What matters is what the object is *made of*, which the material ID already gives |
| `triangleIndex`, `barycentricCoordinate`, `textureCoord(2)`, `lightmapCoord` | No. They are only meaningful on MeshColliders; on primitive colliders the probe returned `triangleIndex = −1` and a texture coordinate of `(0, 0)` |
| `collider`, `transform`, `rigidbody`, `articulationBody` | Object references; they cannot be serialised |

So each cell carries three values: range, material ID and normal. The material ID comes from the collider's material assignment in Unity, not from RaycastHit, and was already in v2.

v3 adds the normal plane as 3 × int16 per cell. Each component is stored as `round(n · 32767)`, which keeps the angle error around 0.003°. The normal is given in the sonar body frame, so the receiver doesn't need the pose to use it. Cost for 512 × 128: +393 KB per frame. That brings the frame to 721 KB, about 87 Mbit/s at 15 Hz, which is fine on localhost or gigabit Ethernet. A float32 × 3 plane would double that cost without adding useful precision.

One thing to know for Unity upgrades: in Unity 6.6, `RaycastHit.colliderInstanceID` is a **compile error** (use `colliderEntityId` instead).

## Q1. Magic number prefix

Yes. v3 frames every message as `[uint32 0x3BA4B839][uint32 payloadLength][payload]`, so on the wire the magic number is the bytes `39 B8 A4 3B`. It goes before the length, so a receiver checks it before trusting the length. On a mismatch the receiver closes the connection rather than trying to resynchronise: on TCP the only realistic causes are the wrong peer or an old (v2) peer.

## Q3. Choosing `paddingDeg`

`paddingDeg` is **removed** in v3. In v2 its only purpose was to provide neighbouring cells beyond the edge of the fan, so that surface normals could be estimated from range differences. Unity now sends the normal from `RaycastHit.normal` for every cell ([RaycastHit](#point-raised-sending-the-complete-raycasthit)), so those extra cells are no longer needed. The grid covers exactly the swath and the vertical field of view, and every cell Unity casts contributes to the image.

For reference, with the v2 default of 2° padding (150° × 25° fan, 512 × 128 grid), about 7 azimuth columns and 9 elevation rows per side were padding: **16% of every frame** was sent but never used in the image.

## Q4. Pulse bandwidth

Not added. The centre frequency and pulse bandwidth stay **fixed**, as instructed at the start of the project, so v3 also removes `frequency_Hz` from the sonar settings.

## Q5. Multipath and later hits

**Out of scope for v3.** Each FRAME carries the first hit of every ray only. The water surface's own direct return is still included: it is a first hit with material ID 20.

## Q6. Table 1 fields in the frame header

All the Table 1 fields are added, and every float64 sits at an 8-byte-aligned offset. Every IMAGE echoes the whole header back byte for byte (Q12), so each image carries its own navigation data.

Two notes for the Unity side:
- φ, θ and ψ must be **derived from `sonarRot`** with the formulas in the spec. The quaternion is the authoritative attitude; the Euler angles are there for logging and MATLAB.
- `sonarDep` is measured from the top of the housing and `sonarAlt` from the bottom, while rays start at the acoustic centre, `sonarPos`.

**Assumptions to confirm** (Table 1 doesn't give units):
- φ, θ and ψ are in **radians**, matching the MATLAB functions.
- `waveDir` is the direction waves come **from**, in degrees clockwise from true north.
- `waveHgt` is the significant wave height.
- `waveFrq` is the peak frequency in Hz.

## Q7. Euler and quaternion convention

**Your three functions are correct and consistent with each other.** We checked them on 10,000 random attitudes:

- Euler → quaternion → Euler round trip: maximum error 2 × 10⁻¹³ rad.
- The matrix in `sonar_to_world` equals `Rz(ψ)·Ry(θ)·Rx(φ)`: maximum error 1 × 10⁻¹⁵.

That makes it the standard aerospace ZYX convention: Hamilton quaternion, scalar last, body → world, roll φ about x, pitch θ about y, yaw ψ about z. v3 adopts exactly this.

Two conditions apply:

1. **The frames must be NED (world) and FRD (body).** Your formulas follow the usual sign convention (φ > 0 starboard down, θ > 0 nose up, ψ > 0 north toward east) only in those frames. Unity is left-handed with Y up, so `transform.rotation` must be converted. Assuming Unity +X = east and +Z = north: `q_NED = (−qz, −qx, qy, qw)` and `pos_NED = (z, x, −y)`. Both were verified exactly against `M·R·Mᵀ`. Sanity checks: Unity `Euler(0, +30, 0)` → ψ = +30°; `Euler(+10, 0, 0)` → θ = −10° (Unity's +X turns the nose down); `Euler(0, 0, +10)` → φ = −10°. The spec section "Converting from Unity" gives the full set, including normals.
2. **Clamp the `asin` argument** in `quaternion_to_euler` to [−1, 1]. Rounding can push it slightly past ±1, which makes MATLAB return a complex number. Near θ = ±90°, φ and ψ can't be separated (gimbal lock), which is one more reason the quaternion is authoritative.

`q` and `−q` describe the same rotation, so compare attitudes with `|q₁·q₂| ≈ 1`, not component by component.

## Q8 and Q9. Are el(r), az(c) and the ray direction independent of attitude?

**Yes.** `el(r)`, `az(c)` and `dir(az, el)` are all defined in the sonar body frame, so they don't change with roll, pitch or yaw. Attitude enters only when converting to the world, `dir_world = R(q) · dir`.

One correction to the wording "the origin is the direction the centre of the sonar is presently pointing". The origin of `az` and `el` is the **sonar head's boresight in the body frame**: the body x-axis tilted by the fixed `mountTiltDeg`. That is where the sonar points in the world only after applying `R(q)`.

In the old version (v2) the angles were also body-frame, but the zero direction was **straight down** and azimuth rotated about the forward axis: a down-looking multibeam geometry. That is why the forward-looking case breaks (finding 1).

## Q10. Elevation convention (0 = ahead, −90 = down)

Adopted. In v3, both `el` and `mountTiltDeg` follow your convention: 0 = ahead, −90 = straight down, positive = up. Azimuth is positive to starboard. The ray direction in the body frame (FRD) becomes:

```
dir(az, el) = Ry(τ) · ( cos el·cos az,  cos el·sin az,  −sin el ),   τ = mountTiltDeg
```

A single formula covers both kinds of sonar. `τ = 0` gives a horizontal forward-looking fan, and `τ = −90` gives a vertical swath across the sonar's track (a down-looking multibeam). Both were checked numerically, and the test vectors (with the matching Unity local directions) are in the spec. Converting an old v2 tilt to the new convention (boresight only): `τ_v3 = −90 − tilt_v2`.

## Q11. Object material IDs

| ID | Material | Examples |
|----|----------|----------|
| 21 | Metal | Shipping container, ship hull, anchor chain |
| 22 | Wood | Wooden boat |
| 23 | Concrete | Pillar |
| 24 | Rubber | Tyre |

ID 0 means no hit. IDs 19 and 25–255 are reserved, for example for seagrass, kelp or fish schools. IDs are append-only: a new material takes a reserved ID, and an assigned ID is never renumbered or reused. Unity only sends the ID; adding a material needs a new ID and nothing else in the protocol.

## Q12. Echoing the FRAME header in IMAGE

Yes. The IMAGE carries the full 176-byte FRAME header byte for byte at offset 16, right after a 16-byte image header. Each image is then self-describing: pose, time and navigation are known without matching ping IDs.

## Q13. Polar or Cartesian output

Yes. The current output is a polar image, one column per beam and one row per range bin. v3 adds a Cartesian image as an alternative.

The choice goes in the **local config file** that Unity and the generator each read before the run (`imgType`, `imgWidth`, `imgHeight`), not in the FRAME, and it stays fixed for the run. The IMAGE repeats `imgType`, `N` and `M` so every image can be read on its own. We kept your proposed layout with one change: a 4-byte reserved field after `N` and `M`, so the echoed header starts on an 8-byte boundary.

Cartesian details to note:
- The Cartesian image lies in the plane of the fan. It shows slant range projected onto that plane, not ground range.
- Pixels outside the fan are `NaN`; display them as transparent.
- Cartesian output requires `swathAngleDeg ≤ 180`.

The spec also makes one existing detail explicit. The data is stored **one column at a time**: index = `j·M + i`, beam (or x) first. That is what Unity reads today, even though the old version's table called beams the "width" of a row-major image.

## Q15b. S-57 NATSUR seabed classes

Yes. **Seabed material ID = NATSUR code**, so Unity sends the S-57 value directly. The codes come from S-57 Ed. 3.1, Appendix A, Chapter 2, attribute NATSUR (code 113), printed pages 2.153–2.154: 1 mud · 2 clay · 3 silt · 4 sand · 5 stone · 6 gravel · 7 pebbles · 8 cobbles · 9 rock · 10 marsh · 11 lava · 12 snow · 13 ice · 14 coral · 15 swamp · 16 bog/moor · 17 shells · 18 boulder.

That puts the seabed at IDs 1–18. The water surface moves from 2 (in v2) to **20**.

S-57 allows a mixed seabed such as "4,1,17" (sand, mud, shells), listed main constituent first. One byte per cell holds only the **main constituent**. S-57 also defines qualifiers, NATQUA (1 fine, 2 medium, 3 coarse, 4 broken, 5 sticky, 6 soft, 7 stiff, 8 volcanic, 9 calcareous, 10 hard). These affect backscatter (fine sand and coarse sand differ by several dB) but are not in v3. If they are needed, a qualifier plane can be added in a later version.

---

## Changes from the old version (v2) to the new version (v3)

| # | Item | Old version (v2) | New version (v3) | Why |
|---|------|------------------|------------------|-----|
| 1 | Message framing | `[length][payload]` | `[magic 0x3BA4B839][length][payload]` | Q1 |
| 2 | Sonar settings | Sent by Unity in a 48-byte CONFIG, answered by a CONFIG_ACK; can change mid-session | **Not sent.** Unity and the generator each read them from a local config file before the run, and they stay fixed for the run. CONFIG and CONFIG_ACK are removed, and FRAME and IMAGE become `msgType` 1 | Review feedback: the settings never change during a run, so the handshake is not needed. Making sure both sides load the correct, matching file is up to the user |
| 3 | `paddingDeg` | CONFIG field; the grid extends past the swath and vertical FOV | **Removed.** The grid covers exactly the swath and vertical FOV | Q3 |
| 4 | Surface normals | Not sent | **Sent by Unity** from `RaycastHit.normal`, in every FRAME (3 × int16, body frame) | RaycastHit |
| 5 | `frequency_Hz` | CONFIG field | **Removed.** Centre frequency and bandwidth are fixed | Q4 |
| 6 | Output image | Polar only | Polar or Cartesian, chosen in the local config file (`imgType`, `imgWidth`, `imgHeight`) | Q13 |
| 7 | Coordinate frames | Unity world (left-handed, Y up); attitude convention unstated | World NED, body FRD, Hamilton quaternion (scalar last), ZYX Euler; Unity converts before sending | Q7 |
| 8 | Ray geometry | Zero direction straight down; azimuth about the forward axis | Elevation 0 = ahead, −90 = down; `mountTiltDeg` uses the same convention | Q8–Q10 |
| 9 | FRAME header | 48 bytes | 176 bytes, with the Table 1 fields | Q6 |
| 10 | FRAME size | `48 + 5·W·H` | `176 + 11·W·H` | RaycastHit, Q6 |
| 11 | Material IDs | 0 default, 1 seabed, 2 water surface, 3–255 objects | 1–18 seabed by NATSUR code, 20 water surface, 21–24 object materials, 0 no hit; 19 and 25–255 reserved (append-only) | Q11, Q15b |
| 12 | IMAGE header | 12 bytes; echoes `pingID` | 192 bytes; echoes the full FRAME header and states `imgType` | Q12, Q13 |
| 13 | IMAGE index order | "Row-major", with beams called the width | Stated explicitly: index = `j·M + i`, one beam at a time (same bytes as before) | Q13 |
| 14 | Valid config values | CONFIG_ACK status 1 for any non-positive value | A per-field valid-values table, including a cap on grid and image size. If its config breaks them or names an `imgType` it doesn't support yet, the generator accepts each connection and closes it right away, logging the reason | v2's rule would reject valid values such as `mountTiltDeg = −20`, so each field now has its own range |
| 15 | Session rules | 6 rules ([listed below](#session-rules-old-and-new)) | 6 rules: drops the 5 CONFIG rules, makes the size check close the connection, and adds local config loading, FRAME/IMAGE only, lock-step exchange, closing on other protocol errors, and sessions with `pingID` wrap-around ([listed below](#session-rules-old-and-new)) | v2 left open how many FRAMEs may be in flight and what happens to a mis-sized FRAME. Lock-step settles the first: Unity waits for each IMAGE before sending the next FRAME, so nothing queues up on either side and every image matches the latest pose. The tightened size check settles the second |
| 16 | Intensity scale | "Normalised 0–1", undefined | A **fixed** scale, the same for every ping ([below](#intensity-scale-old-and-new)) | v2 never said what 0–1 is relative to. A fixed scale matches real sonar displays, and makes values comparable across pings and usable for thresholds |

Unchanged: TCP on port 9000, little-endian byte order, the range and material planes, and the submerged flag.

### Session rules: old and new

The old version (v2) had six session rules:

1. The first message after connecting must be a CONFIG.
2. The generator replies to every CONFIG with exactly one CONFIG_ACK. Unity waits for that ACK before sending any FRAME.
3. If the ACK status is `0` (OK), the new CONFIG applies to every following FRAME until the next accepted CONFIG. If the status is non-zero, the generator discards that CONFIG and keeps the previously accepted one, if any; Unity logs the status, fixes the values and resends.
4. When any config field changes, Unity sends the new CONFIG and waits for its ACK before the first FRAME that uses the new values.
5. A FRAME received before any accepted CONFIG is a protocol error: the generator logs it and closes the connection.
6. The generator checks each FRAME's payload length against the current CONFIG (`48 + 5·W·H`) and rejects frames that do not match.

The new version (v3) has no CONFIG, so rules 1–5 are gone. It has six rules:

1. **Local config.** Before the run, Unity and the generator each load their local config file. The values stay fixed for the whole run and are never sent over the connection.
2. **FRAMEs and IMAGEs only.** After connecting, Unity sends only FRAMEs and the generator sends only IMAGEs.
3. **Size check** (old rule 6, tightened). The generator works out the expected FRAME length from the config (now `176 + 11·W·H`) and never allocates memory from the wire value. A mis-sized FRAME is a protocol error: the generator logs it and **closes the connection**. (v2 said only "rejects", which left open whether the connection survives.)
4. **Lock-step.** After sending a FRAME, Unity waits for its IMAGE before sending the next FRAME. At most one FRAME is ever in flight, and neither side keeps a queue. Messages are processed strictly in order.
5. **Other protocol errors.** An unknown `msgType`, or a `payloadLength` below 4, closes the connection.
6. **Sessions.** A session is one TCP connection. `pingID` increases by 1 per FRAME, wraps modulo 2³², and may restart from any value after a reconnect.

### Intensity scale: old and new

The old version (v2) said only "normalised 0–1", without saying relative to what. If each image were normalised to its own brightest pixel, a strong target entering the view would dim everything else, and the same echo would give different values on different pings. Real sonar displays don't do this: they compensate range losses with a fixed time-varied gain and show the result through a gain and contrast window that changes only when the operator changes it.

The new version (v3) follows the real sonar. Values are linear in dB over a fixed window that is the same for every ping and every session, with range losses already compensated. The same echo gives the same value on every ping, so values can be compared across pings and thresholded, and Unity can display them directly.

## Feasibility checks

Code was written only where feasibility was uncertain. Everything else in v3 is specification only. These checks ran in throwaway projects outside the repository.

| Check | How | Result |
|-------|-----|--------|
| What a "complete RaycastHit" contains | Unity 6000.6.8f1 batch-mode probe, reflection over `RaycastHit` | 48 bytes; field list as in [RaycastHit](#point-raised-sending-the-complete-raycasthit). `colliderInstanceID` is a compile error in 6.6 |
| Cost of a 65,536-ray first-hit batch | Same probe, average of 5 runs | 0.9 ms |
| Euler ↔ quaternion (Q7) | NumPy, 10,000 random attitudes | Consistent; matches ZYX to 1 × 10⁻¹⁵ |
| Unity → NED conversion (Q7) | NumPy, 10,000 random rotations | `(−qz, −qx, qy, qw)`, exact |
| Old ray formula with tilt −90 (Q8–Q10) | NumPy | Every azimuth gives `(0, 0, 1)`: the fan collapses |
| New ray formula | NumPy | Distinct rays; horizontal fan at τ = 0, across-track swath at τ = −90 |

## What changes on the Unity side

1. **Framing:** the magic number. CONFIG and CONFIG_ACK are no longer sent; FRAMEs start straight after connecting. FRAME and IMAGE are now `msgType` 1 (was 2).
2. **Config:** read the sonar settings from a local config file before the run, matching the generator's.
3. **Pose:** convert position, rotation and normals from Unity to NED / FRD; derive φ, θ, ψ from the converted quaternion; fill the Table 1 fields.
4. **Rays:** cast the grid with the new ray formula and no padding, and send the normals plane alongside range and material.
5. **Materials:** tag colliders with the v3 material IDs (NATSUR seabed 1–18, 20 water surface, 21–24 objects).
6. **Lock-step sending:** after sending a FRAME, wait for its IMAGE before sending the next one. No queue.
7. **IMAGE:** read the 192-byte header and, for Cartesian output, display `NaN` pixels as transparent.
