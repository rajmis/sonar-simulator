# Protocol v3: Responses to the Integration Questions

2026-11-11 · answers to the questions raised after [`shared/PROTOCOL_v2.md`](../shared/PROTOCOL_v2.md) was implemented.
The resulting spec is [`shared/PROTOCOL_v3.md`](../shared/PROTOCOL_v3.md) (draft).

Three findings change the design and are worth reading first:

1. **The v2 ray formula cannot describe a forward-looking sonar.** In v2, azimuth rotates about the sonar's
   forward axis, starting from straight down. Setting `mountTiltDeg = −90` to look ahead collapses the whole
   fan onto one direction (verified numerically: azimuths −60°, 0° and +60° all gave `(0, 0, 1)`). v3 uses
   a forward-looking-native formula that also covers the down-looking multibeam case (Q8–Q10).
2. **Unity's quaternion is not the quaternion in your MATLAB functions.** Your functions are consistent with
   each other, and they assume NED / FRD frames. Unity is left-handed with Y up. The conversion is
   `q_NED = (−qz, −qx, qy, qw)` (Q7).
3. **"Complete RaycastHit" is best sent as a normals plane, not as the struct.** Most RaycastHit fields are
   meaningless outside the Unity process or on simple colliders (Q1).

Question 14 was not in the list, so these responses skip from Q13 to Q15b.

## Summary

| Q   | Question | Answer | v3 change |
|-----|----------|--------|-----------|
| 1   | Send the complete RaycastHit | Yes in substance: send the surface normal (and optionally an object ID), not the 48-byte struct | New `normal` and `objectId` planes |
| 2   | Magic number prefix | Yes | `[magic][length][payload]` |
| 3   | Optimal `paddingDeg` | `0` once normals are sent. Otherwise `max(swath/(W−2), vFOV/(H−2))`, about 0.3° for the defaults | Guidance only |
| 4   | Pulse bandwidth in CONFIG | Yes. It sets range resolution `c/(2B)` | `bandwidth_Hz` |
| 5   | Multipath / later hits | First-hit planes cannot encode them, and the generator cannot infer them. Unity casts one specular bounce | Optional `bounceRange` / `bounceMaterial` planes |
| 6   | Table 1 fields | Yes, all of them (124 bytes). Used now: depth, waves. Later: velocities. Logged only: lat/lon, altitude, currents | FRAME header grows to 176 bytes |
| 7   | Euler ↔ quaternion convention | Your formulas are correct and consistent (ZYX, scalar last, body → NED). Unity needs converting | Pose defined in NED / FRD |
| 8–9 | Are el, az and ray direction independent of attitude? | Yes: they are body-frame quantities | Stated explicitly |
| 10  | Elevation 0 = ahead, −90 = down | Adopted, for `mountTiltDeg` and `el` alike | New ray formula |
| 11  | Object materials | IDs 64–67: metal, wood, concrete, rubber | Material table |
| 12  | Echo the FRAME header in IMAGE | Yes | IMAGE carries the 176-byte header |
| 13  | Polar or Cartesian output | Yes, chosen in CONFIG (session-wide) rather than per frame | `imgType`, `imgWidth`, `imgHeight` |
| 15b | S-57 NATSUR seabed classes | Yes: seabed material ID = NATSUR code | IDs 1–18 |

---

## Q1. Porting the complete RaycastHit

**Recommendation:** send the surface normal for every cell, plus an optional object ID. Don't send the
RaycastHit struct itself.

We checked what RaycastHit contains in Unity 6000.6.8f1 with a throwaway probe project (see
[Feasibility checks](#feasibility-checks)). It is a 48-byte struct with these fields:

| Field | Useful to the generator? |
|-------|--------------------------|
| `normal` | **Yes.** It replaces normals estimated from depth differences. Those are approximate at edges (a cell at a silhouette edge is currently treated as hit head-on) and need padding cells |
| `distance` | Already sent (the range plane) |
| `point` | Redundant: `point = sonarPos + distance · dir` |
| `colliderEntityId` | Only as a stable object label. The raw value is specific to the running Unity process, so Unity maps it to its own `uint16` ID |
| `triangleIndex`, `barycentricCoordinate`, `textureCoord(2)`, `lightmapCoord` | No. They are only meaningful on MeshColliders; on primitive colliders the probe returned `triangleIndex = −1` and a texture coordinate of `(0, 0)` |
| `collider`, `transform`, `rigidbody`, `articulationBody` | Object references; they cannot be serialised |

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

With the normals plane (Q1), padding is no longer needed: **set it to 0**. The generator's other use of
neighbouring cells, estimating each cell's range footprint, already handles grid edges with one-sided differences.

Without normals, your formula has the right idea, but it is circular: `elSpan` and `azSpan` already include the
padding. Asking for one neighbour cell on each side, `p ≥ (span + 2p)/N`, solves to:

```
paddingDeg = max( swathAngleDeg / (W − 2),  verticalFOVDeg / (H − 2) )
```

For the current defaults (150° × 25°, 512 × 128) that is max(0.294°, 0.198°) = **0.3°**, so 2.7% of the
cells are padding. The current default of 2° gives about 7 azimuth columns and 9 elevation rows of padding
per side, so **16% of the frame** is never binned.

## Q4. Pulse bandwidth

Yes, and v3 adds `bandwidth_Hz` to CONFIG. Bandwidth, not pulse length, sets range resolution: `δr = c / (2B)`.

- For a continuous-wave (CW) pulse, `B ≈ 1/τ`. With the current 100 µs pulse that is 10 kHz, so 7.5 cm. Send `0` and the generator uses `1/τ`.
- For an FM chirp with matched filtering, `B` can be far larger than `1/τ`, so the two must be sent separately.
  τ still matters for signal energy (and therefore SNR).

The generator will use `δr` as the width of the range blur it applies to each return (currently a fixed 1 bin).

## Q5. Multipath and later hits

The range and material planes hold the **first hit only**, and the generator has no scene geometry, so it
cannot infer later bounces. The one exception is the image method for a flat surface, and even that needs the
geometry seen *through* the mirror, which the first-hit planes do not contain.

v3 defines an optional bounce, which Unity enables with `planeMask` bit 2:

- From each first hit, Unity casts the **specular reflection** of the ray (`Vector3.Reflect(dir, hit.normal)`),
  limited to `maxRange − range`.
- It sends `bounceRange` (first hit → second hit) and `bounceMaterial`.
- The generator places that energy at range `range + bounceRange` (out-and-back along the same path), weakened
  by the reflectivity of the first surface. A water-surface first hit (material 33) is the important case.

Cost (probe, 65,536 rays): **6–7 ms** per frame, including building the rays in plain C#. That fits within a
15 Hz ping budget, and could be made faster with Unity's Burst compiler. The generator replies with ACK status 4 to
bit 2 until it models multipath, so Unity can't enable it by accident.

`RaycastCommand` with `maxHits > 1` is not a substitute. It returns hits *through* objects along the same
straight ray, not reflections, and at most one hit per collider. The probe also found the hits **unsorted**
on all 4,044 rays that had two hits.

We agree multipath is minor except near the surface, and the surface's own direct return is already covered by
its material ID. It also matters in confined spaces such as quay walls, tanks and harbour basins, which is why
the plane is defined now rather than later.

## Q6. Table 1 fields in the frame header

All the Table 1 fields are added (124 bytes), and every float64 sits at an 8-byte-aligned offset.

| Field | Generator use |
|-------|---------------|
| `sonarDep` | **Now.** Distance to the surface for surface multipath and surface-return gating. The `flags` submerged bit stays the authoritative "in water" signal |
| `waveHgt` | **Now.** Surface roughness: a rougher surface reflects less specularly and scatters more back toward the sonar |
| `waveFrq`, `waveDir` | Later: time-varying surface roughness. Logged for now |
| `U V W P Q R` | Later: platform motion during a ping. At 30 m the round trip is 40 ms. A 10°/s yaw rate smears returns by 0.4°, about 70% of the 0.59° beam spacing. Doppler at 400 kHz and 2 m/s is about 1.1 kHz, about 11% of a 10 kHz bandwidth |
| `φ θ ψ` | Not used: the quaternion is authoritative. Kept for logging and MATLAB, and must be derived from `sonarRot` |
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

In v2 the angles were also body-frame, but the zero direction was **straight down** and azimuth rotated
about the forward axis: a down-looking multibeam geometry. That is why `SampleScene` pitches the whole sonar
transform to −75° instead of using the tilt setting, and why the forward-looking case breaks (finding 1).

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
live in the generator's material table (`processor/config/materials.yaml`), not in the protocol, so adding
a material doesn't change the protocol.

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
`j·M + i`, beam (or x) first. That is what Python sends and Unity reads today, even though v2's table called
beams the "width" of a row-major image.

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
later as another `planeMask` bit.

---

## Feasibility checks

Code was written only where feasibility was uncertain. Everything else in v3 is specification only. These
checks ran in throwaway projects outside the repository, so **no repository code changed**.

| Check | How | Result |
|-------|-----|--------|
| What a "complete RaycastHit" contains (Q1) | Unity 6000.6.8f1 batch-mode probe, reflection over `RaycastHit` | 48 bytes; field list as in Q1. `colliderInstanceID` is a compile error in 6.6 |
| Cost of a 65,536-ray first-hit batch | Same probe, average of 5 runs | 0.9 ms |
| `maxHits > 1` as multipath (Q5) | Same probe, `maxHits = 4` | One hit per collider; hits unsorted on all 4,044 two-hit rays; no reflections |
| One specular bounce (Q5) | Same probe | 6–7 ms for 65,536 rays, including building the rays in plain C# |
| Euler ↔ quaternion (Q7) | NumPy, 10,000 random attitudes | Consistent; matches ZYX to 1 × 10⁻¹⁵ |
| Unity → NED conversion (Q7) | NumPy, 10,000 random rotations | `(−qz, −qx, qy, qw)`, exact |
| v2 ray formula with tilt −90 (Q8–Q10) | NumPy | Every azimuth gives `(0, 0, 1)`: the fan collapses |
| v3 ray formula | NumPy | Distinct rays; horizontal fan at τ = 0, across-track swath at τ = −90 |

## Independent review

A second model (Fable 5.1) reviewed the spec independently, recomputing every byte of the wire template and
every formula. It confirmed all layout sums, hex bytes, offsets, conversions and test vectors. It also
found gaps in the spec text, all now fixed in `PROTOCOL_v3.md`:

| Finding | Fix |
|---------|-----|
| ACK status 1 ("non-positive value") would reject the spec's own example (padding 0, bandwidth 0, tilt −20) | Per-field [valid values](../shared/PROTOCOL_v3.md#valid-values) table |
| A mis-sized FRAME had no defined outcome, and lengths had no upper bound | Protocol error (close the connection); expected size computed from CONFIG; grid and image ≤ 4,194,304 cells |
| Frames in flight and the timing of a mid-stream CONFIG were implicit | Strict ordering and lock-step: one FRAME at a time, the next sent only after its IMAGE arrives; pings due while waiting are skipped, never queued |
| Ray origin was undefined, and the example's depth and altitude didn't agree | Rays start at `sonarPos`; example uses a 0.2 m housing, so the range is 21.26 m |
| No-hit cells, reserved `planeMask` bits, `pingID`, "session", unknown `msgType` | All defined in the spec |
| Normal encoding: −32768, the Unity conversion, the water-surface direction | Clamp to ±32767; `Quaternion.Inverse(rot) * hit.normal` → FRD; the surface normal points down |
| The meaning of "normalised 0–1" | [Intensity scale](../shared/PROTOCOL_v3.md#intensity-scale): a 60 dB window below each image's peak |

## Implementing v3

None of this is implemented yet. In rough order:

1. **Generator:** framing (magic, version), the new CONFIG and FRAME parsing, the new ray formula, using
   the normals plane when present, material table re-indexed to the v3 IDs (steel → metal 64, sand → NATSUR 4, ...),
   echoed header, Cartesian resampling, and ACK codes 3 and 4.
2. **Unity:** NED / FRD conversion of pose and normals, normals and object-ID planes, Table 1 fields,
   material IDs, the new ray formula in `SonarRaycaster`, and lock-step sending (today Unity sends FRAMEs
   without waiting for the IMAGE).
3. **Later:** the bounce plane (Q5), motion compensation using U–R (Q6), wave-driven surface roughness.

One more Unity note: the installed editor is **6000.6.8f1**, but the project is pinned to **6000.3.10f1**.
Opening it in 6.6 upgrades the project. Install 6000.3.10f1 alongside 6.6 (`unityhub` → Installs), or decide to
upgrade deliberately.
