# Protocol v3: Generator Notes (internal)

**Internal. Not for the other team.** The shared docs, [`shared/PROTOCOL_v3.md`](../shared/PROTOCOL_v3.md) and
[`protocol_v3_responses.md`](protocol_v3_responses.md), cover only what Unity sends and receives. This file
keeps the generator-side details that were removed from them.

The current Python generator (`processor/src/`) is structural placeholder code and will be replaced by a U-Net.
These notes describe what any generator must do to meet the v3 contract, not how today's code does it.

## Using the FRAME

- **Hit points:** `sonarPos + R(q) · range · dir(az, el)`. The point is not sent.
- **Normals:** decode `n = v / 32767` (FRD). Use them directly; no normal estimation and no padding cells.
- **Footprint spread:** estimating how far each cell's footprint spreads in range still uses neighbouring
  cells. At the grid edges, use one-sided differences.
- **No-hit cells:** treat any range that is not finite and in (0, maxRange] as no return.

## Table 1 fields

| Field | Use |
|-------|-----|
| `flags` bit 0 | **Now.** When 0, return a noise-only image |
| `waveHgt` | **Now.** Roughness of the water surface's direct return (material 33): a rougher surface scatters more energy back toward the sonar |
| `waveFrq`, `waveDir` | Later: time-varying surface roughness |
| `U V W P Q R` | Later: platform motion during a ping. At 30 m the round trip is 40 ms; a 10°/s yaw rate smears returns by 0.4°, about 70% of the 0.59° beam spacing. Doppler at 400 kHz and 2 m/s is about 1.1 kHz |
| `sonarDep` | Not needed to form the image: the surface return is already in the range plane, and `flags` is authoritative for "in water" |
| `φ θ ψ`, `sonarLat`, `sonarLon`, `sonarAlt`, `uwNorth`, `uwEast` | Not used; echoed back |

## Fixed constants (not in CONFIG)

- **Centre frequency and bandwidth:** fixed, as instructed at the start of the project. Range resolution
  `δr = c / (2B)` sets the width of the range blur on each return. For a CW pulse `B ≈ 1/τ` (100 µs → 10 kHz →
  7.5 cm); an FM chirp with matched filtering has `B ≫ 1/τ`.
- **Intensity window:** `floor_dB` and the time-varied gain (TVG) law.

## Intensity scale

The spec promises a fixed 60 dB window with range losses compensated. Concretely:

```
L_tvg = RL + TVG(r)                                  # dB; RL = received level, dB re 1 µPa, incl. noise floor
value = clamp( (L_tvg − floor_dB) / 60, 0, 1 )       # inverse: L_tvg = floor_dB + 60 · value
```

- The usual imaging-sonar default is `TVG(r) = 20·log10 r + 2α·r`.
- Today's code (`processing.py`) instead normalises each image to its own peak (`ceiling_db = image_db.max()`),
  which breaks the contract and must go.
- For the U-Net: training targets must be on this fixed scale, or the model learns per-ping rescaling.

## Cartesian output

- Resample from the polar image with bilinear interpolation at `r = √(x² + y²)`, `az = atan2(x, y)`.
- Pixels whose centre is outside the fan (`|az| > swath/2` or `r > maxRange`) are `NaN`.
- Between the outermost beam or bin centre and the fan edge, clamp to the outermost beam or bin.
- Until Cartesian is implemented, answer `imgType = 1` with ACK status `4`.

## Implementing v3 in the generator

1. Framing (magic, version), 52-byte CONFIG parsing and validation, ACK codes 0–4.
2. FRAME parsing: 176-byte header, `176 + 11·W·H` size check, three planes.
3. New ray formula without padding.
4. Use Unity's normals instead of estimating them.
5. Material table re-indexed to the v3 IDs (steel → metal 64, sand → NATSUR 4, water surface → 33, ...).
6. IMAGE with the 192-byte header and the echoed FRAME header.
7. Fixed intensity scale with TVG, replacing per-image normalisation.
8. Cartesian resampling.
9. Later: motion compensation using U–R, wave-driven surface roughness.

## Unity editor version (our machine)

The installed editor is **6000.6.8f1**, but the project is pinned to **6000.3.10f1**, and must stay there.
Opening the project in 6.6 upgrades it. Install 6000.3.10f1 alongside 6.6 (`unityhub` → Installs).
