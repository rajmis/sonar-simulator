# Pipeline v2 — Depth-Map Capture (September 2026)

What changed when the Unity→Python link moved from per-ray records to a
polar depth + material frame. Wire format details live in
[`shared/PROTOCOL.md`](../shared/PROTOCOL.md); inner workings in
[`technical_documentation.md`](technical_documentation.md).

## Why

Unity previously computed per-ray hit records (distance, normal, direction,
hit point, object ID — 48 bytes × 11,520 rays) tied to the beam/sub-beam
structure. Unity now only answers "how far, and what material" on a regular
angular grid. Everything acoustic — including surface normals — is derived
in Python, on the GPU where available. Unity stays a dumb range scanner;
the sonar model can change without touching Unity.

## What flows over the wire (TCP, localhost:9000)

Transport is unchanged: single TCP connection, length-prefixed messages.
(The repo briefly said "UDP" in old docstrings — it has been TCP since the
restructure; the dead UDP chunking helpers are now removed.)

**Unity → Python, per ping (~330 KB at defaults):**

| Part | Size | Content |
|------|------|---------|
| Header | 88 B | pingID, timestamp, pose, sonar params, grid dimensions |
| Range plane | W×H × f32 | Hit range in metres per grid cell, 0 = no return |
| Material plane | W×H × u8 | Material index per cell (→ `materials.yaml`) |

The grid is a **slice + padding** of the sonar fan: azimuth spans
swath + 2·padding (default 150° + 4°), elevation spans vFOV + 2·padding
(default 25° + 4°), sampled 512 × 128. Padding columns/rows are used only
for normal estimation in Python, never binned into beams.

**Python → Unity:** unchanged — 256×650 float32 intensity image, 0–1.

## Unity changes

- `SonarRaycaster` — same class, new internals: casts the polar grid via
  `RaycastCommand`, emits a `DepthFrame` (was: beam/sub-beam `RayHitData[]`).
- `SonarNetworkClient` — serialises the new frame; receive path untouched.
- `SonarConfig` — new fields: `azimuthSamples`, `elevationSamples`,
  `capturePaddingDeg`. Beam/sub-beam fields remain (legacy emitters, and
  `numberOfBeams`/`numberOfRangeBins` still define the output image).
- `AcousticProperties.MaterialType` — enum indices now explicitly match
  `materials.yaml` (Default=0, Steel=1, Concrete=2, Sand=3, Mud=4, Rock=5,
  Wood=6, Rubber=7). **Check tagged objects after upgrading**: previously
  the enum order disagreed with the YAML, so e.g. "Concrete" was processed
  as steel. Serialised values keep their integers — JettyPile (1) now
  *displays* as Steel, which is what Python was already computing.
- Scene wiring survives: class and file names kept, so SampleScene needs no
  re-wiring.

## Python changes

- `processing.py` — rewritten around `process_frame()` in PyTorch
  (`cuda` if available, else CPU). Normals come from central differences on
  the reconstructed point grid; the sonar equation, shadow model, 60 dB
  window, and noise components are numerically the same as v1.
- `protocol.py` — parses frames instead of ray records; UDP-era chunking
  helpers deleted.
- `main.py` — new `--dump-dir` flag saves the latest received frame and
  output image as `.npy` for inspection.
- `environment.yaml` — adds `pytorch` (use a `*cuda12*` build for GPU).

## Testing

```bash
python -m processor.tests.test_pipeline       # processing on synthetic seabed+pile frame
python -m processor.tests.test_tcp_roundtrip  # full server round-trip, no Unity
```

Then the live check: `python -m processor.src.main`, hit Play in
SampleScene, confirm the fan shows the seabed arc, a bright pile return,
and a shadow wedge behind it.

## Deliberate deviations from v1

- Normals are estimated from the depth grid, not taken from `RaycastHit`
  — silhouette-edge pixels assume head-on incidence.
- Grid sampling replaces beam/sub-beam sampling: 512 columns over 150°
  (~1.17 columns/beam at 256 beams) versus 5 horizontal sub-beams per beam
  before. Raise `azimuthSamples` if beams look under-sampled.
- St. John's / Lagoon scene not yet migrated (monopile/SampleScene first).
