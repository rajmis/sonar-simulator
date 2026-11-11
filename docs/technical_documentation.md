# NORBIT WBMS Multibeam Sonar Simulator — Technical Documentation

## 1. System Overview

This is a hybrid Unity3D + Python multibeam sonar simulator modelling a NORBIT WBMS. Unity captures a polar depth + material map of the sonar's field of view (geometry only) and Python handles all acoustic signal processing (intensity computation, noise modelling, image generation) — on GPU via PyTorch when available. The two processes communicate over a single TCP connection on localhost port 9000.

**Pipeline per ping cycle:**

```
Unity SonarRaycaster (512×128 polar ray grid via batch Physics)
        │
        ▼
Unity SonarNetworkClient (serialise ~330 KB depth + material frame, TCP send)
        │
        ▼ TCP localhost:9000
        │
Python SonarTCPServer (parse header + range/material planes)
        │
        ▼
Python process_frame (points → normals → sonar equation → binning → dB normalisation → noise; shadows are geometric)
        │
        ▼
Python SonarTCPServer (send 665 KB float32 image)
        │
        ▼ TCP localhost:9000
        │
Unity SonarNetworkClient (receive on background thread, queue to main thread)
        │
        ▼
Unity SonarDisplay (colour map → RGBA32 texture → fan shader render)
```

**Why hybrid?** Unity excels at real-time 3D physics and rendering; Python excels at numerical computation and rapid iteration on signal processing algorithms. The TCP protocol is the stable contract — the Python side can be replaced with Julia or GPU-accelerated processing without changing Unity.

---

## 2. Unity Side

Unity's role is exclusively geometric: capture a depth + material map of the sonar's field of view, transmit it, receive the processed image, render it. Unity performs zero acoustic computation.

### 2.1 SonarConfig (ScriptableObject)

All sonar parameters are centralised in a ScriptableObject asset (`DefaultConfig`). Edited in the Unity Inspector.

| Parameter | Default | Description |
|-----------|---------|-------------|
| numberOfBeams | 256 | Beams across the swath (output image width) |
| swathAngleDeg | 150° | Horizontal field of view |
| verticalFOVDeg | 25° | Vertical beam spread |
| azimuthSamples | 512 | Capture grid columns across swath + padding |
| elevationSamples | 128 | Capture grid rows across vFOV + padding |
| capturePaddingDeg | 2° | Extra grid margin per side (used by Python for normals) |
| subBeamsH × subBeamsV | 5 × 9 = 45 | Legacy emitters only |
| maxRange | 100 m | Maximum detection range |
| numberOfRangeBins | 650 | Range resolution in output image |
| pingRateHz | 15 Hz | Pings per second |
| speedOfSound | 1500 m/s | Speed of sound in water |
| sourceLevel_dB | 220 dB | Source level (re 1 µPa) |
| pulseLength_s | 0.0001 s | Pulse duration |
| frequency_Hz | 400 kHz | Centre frequency |
| beamWidthAlongTrackDeg | 1.5° | Along-track beam width |
| mountTiltDeg / mountRollDeg | 0° | Mounting offsets |

**Derived properties:** `AngularResolutionDeg = swathAngleDeg / numberOfBeams`, `SubBeamsPerBeam = subBeamsH × subBeamsV`, `TotalRays = numberOfBeams × SubBeamsPerBeam`.

**Important:** ScriptableObject assets cache serialised values. When new fields are added to the C# class, existing `.asset` files retain old (zero) values. New fields must be set manually in the Inspector.

### 2.2 SonarRaycaster

**File:** `Scripts/Raycasting/SonarRaycaster.cs`

Captures a polar depth + material map of the sonar's field of view. Rays are cast on a regular azimuth × elevation grid (default 512 × 128) spanning `swathAngleDeg + 2·capturePaddingDeg` horizontally and `verticalFOVDeg + 2·capturePaddingDeg` vertically.

**Grid direction computation (pre-computed on Start in local space):**

1. **Cell centres:** Row `r` maps to elevation `-elSpan/2 + (r + 0.5)·elSpan/H`; column `c` maps to azimuth `-azSpan/2 + (c + 0.5)·azSpan/W`.
2. **Rotation chain:** Fan angle (around Z) → Elevation (around X) → Mount tilt (around X) → applied to `Vector3.down`. Same downward-looking fan convention as the legacy `SonarGPUEmitter.GetBeamDirection()`.

**Batch raycasting:** Uses Unity's `RaycastCommand` with `NativeArray` allocations; all grid rays are scheduled on the Job System for parallel execution. Only colliders on the `SonarTarget` physics layer are detected.

**Data collected per grid cell:** hit range in metres (float32, `0` = no return) and material type (uint8, from `AcousticProperties` via a collider → material cache). Nothing else — no normals, hit points, or per-ray records; Python reconstructs geometry from the range grid.

**Output:** Fires `OnFrameReady(DepthFrame)` event with the two planes packaged.

### 2.3 AcousticProperties (MonoBehaviour)

Attached to scene objects (JettyPile, Seabed, etc.) on the `SonarTarget` physics layer. Contains a `materialType` enum with explicit indices matching `materials.yaml`: Default = 0, Steel = 1, Concrete = 2, Sand = 3, Mud = 4, Rock = 5, Wood = 6, Rubber = 7, WaterSurface = 8. The raycaster reads this via `GetComponentInParent<AcousticProperties>()` on hit colliders (cached per collider).

### 2.4 SonarNetworkClient (TCP)

**File:** `Scripts/Network/SonarNetworkClient.cs`

Manages a single bidirectional TCP connection to the Python processor. Replaces the earlier UDP-based SonarSender + SonarReceiver (which suffered from chunk loss due to OS socket buffer overflow at 15 Hz).

**Sending (main thread):** When `OnFrameReady` fires, serialises the frame into the binary protocol: 4-byte length prefix + 88-byte frame header + range plane (W·H float32) + material plane (W·H uint8). All little-endian; the planes are bulk-copied with `Buffer.BlockCopy`. Total per ping at 512×128: 4 + 88 + 262,144 + 65,536 = 327,772 bytes. Sent via `NetworkStream.Write`.

**Receiving (background thread):** A dedicated `Thread` reads length-prefixed responses from the TCP stream. Each response = 4-byte length + 8-byte response header + float32 intensity data (256×650×4 bytes). Completed images are queued into a `ConcurrentQueue` and dequeued on the main thread in `Update()`.

**Connection management:** Connects on `Start()`. Auto-reconnects every 2 seconds if connection drops. The `SocketException` on Play mode stop is expected (background thread's blocking read is interrupted when the socket closes).

**Inspector fields:** `host` (default 127.0.0.1), `port` (default 9000), `config` (SonarConfig), plus read-only status fields `connected` and `pingsProcessed`.

### 2.5 DepthFrame (Struct)

**File:** `Scripts/Core/SonarFrame.cs`

`DepthFrame` packages one capture: `pingID`, `timestamp`, `sonarPos`, `sonarRot`, grid `width`/`height`, and the two row-major planes `ranges[]` (float32) and `materials[]` (uint8).

### 2.6 SonarBridge (Orchestrator)

**File:** `Scripts/Core/SonarBridge.cs`

Wires `SonarNetworkClient.OnImageReady` → `SonarDisplay.UpdateFromTexture()`. Uses `RequireComponent` to enforce that `SonarRaycaster` and `SonarNetworkClient` are on the same GameObject. Auto-finds `SonarDisplay` via `FindFirstObjectByType` if not manually assigned.

### 2.7 SonarDisplay

**File:** `Scripts/Display/SonarDisplay.cs`

Two input paths:

- **Legacy path:** `OnPing(float[,])` callback from `SonarEmitter` / `SonarGPUEmitter`. Receives pre-computed 0–1 intensity values.
- **Hybrid path:** `UpdateFromTexture(Texture2D)` called by `SonarBridge`. Receives a `TextureFormat.RFloat` texture from the network client.

Both paths apply the same `colourMap` Gradient (dark blue → bright yellow) to convert intensity to RGBA, write to a `TextureFormat.RGBA32` texture, and set it on the fan material's `_SonarTex` property.

**Fan shader:** `SonarFanView.shader` performs polar UV projection to render the rectangular intensity image as a fan-shaped sonar display with range rings and fan outline.

### 2.8 Legacy Code

**Folder:** `Scripts/__Legacy/`

Contains `SonarEmitter.cs` (CPU raycasting + full processing) and `SonarGPUEmitter.cs` (compute shader raycasting + processing). Preserved as reference implementations documenting beam geometry, noise model, and shadow logic. Not actively used but remain compilable. The `SonarBeamPattern.compute` shader is in `Shaders/`.

---

## 3. TCP Communication Protocol

All communication uses length-prefixed binary messages over a single TCP connection. Python listens on port 9000; Unity connects as client. Byte order is little-endian throughout.

### 3.1 Message Framing

Every message in both directions: `[4-byte uint32 payload length][payload bytes]`. TCP guarantees ordered, reliable delivery. No chunking or reassembly needed.

### 3.2 Depth Frame Payload (Unity → Python)

Total: 88 + (W·H × 4) + (W·H × 1) bytes.

**Frame header (88 bytes):**

| Offset | Type | Field | Description |
|--------|------|-------|-------------|
| 0 | uint32 | pingID | Sequence number |
| 4 | float64 | timestamp | Unity Time.timeAsDouble |
| 12 | float32×3 | sonarPos | World position (x, y, z) |
| 24 | float32×4 | sonarRot | Quaternion (x, y, z, w) |
| 40 | uint16 | numberOfBeams | Output image width (e.g. 256) |
| 42 | uint16 | numberOfRangeBins | Output image height (e.g. 650) |
| 44 | float32 | maxRange | Metres |
| 48 | float32 | swathAngleDeg | Horizontal FOV of the sonar fan |
| 52 | float32 | verticalFOVDeg | Vertical opening of the fan slice |
| 56 | float32 | paddingDeg | Grid extends this far beyond swath/vFOV per side |
| 60 | float32 | mountTiltDeg | Mount tilt about the sonar X axis |
| 64 | float32 | speedOfSound | m/s |
| 68 | float32 | sourceLevel_dB | Source level |
| 72 | float32 | pulseLength_s | Pulse duration |
| 76 | float32 | frequency_Hz | Centre frequency |
| 80 | uint16 | gridWidth | Azimuth samples W (e.g. 512) |
| 82 | uint16 | gridHeight | Elevation samples H (e.g. 128) |
| 84 | uint32 | flags | Bit 0: sonar submerged (1 = in water); when 0, Python returns a noise-floor-only image. Unity sets it from `SonarConfig.useWaterLevel` / `waterLevelY`. |

**Range plane (W × H × float32):** Row-major, row = elevation index, column = azimuth index. Value = hit range in metres along the ray; `0.0` = no return within maxRange.

**Material plane (W × H × uint8):** Same ordering. Value = material type index into `materials.yaml`. Meaningless where range = 0.

See `shared/PROTOCOL.md` for cell-centre angle formulas and the ray direction convention.

### 3.3 Response Payload (Python → Unity)

Total: 8 + (beams × rangeBins × 4) bytes.

| Offset | Type | Field | Description |
|--------|------|-------|-------------|
| 0 | uint32 | pingID | Echoed ping ID |
| 4 | uint16 | beams | Image width (256) |
| 6 | uint16 | rangeBins | Image height (650) |
| 8 | float32[] | intensityData | Row-major float32, normalised 0–1 |

### 3.4 Network Constants

```
HOST = "127.0.0.1"
PORT = 9000 (Python listens, Unity connects)
FRAME_HEADER_SIZE = 88
RESPONSE_HEADER_SIZE = 8
```

---

## 4. Python Acoustic Processing Pipeline

The Python processor is the acoustic heart of the simulator. It collapses each polar depth/material frame into a realistic sonar intensity image. All heavy stages run in PyTorch on the `DEVICE` constant — CUDA when available, CPU otherwise.

### 4.1 Module Overview

| Module | Responsibility |
|--------|---------------|
| `main.py` | Entry point, TCP server loop, orchestrates parsing → processing → response; `--dump-dir` saves the latest frame + image as `.npy` |
| `network.py` | TCP server with length-prefixed message framing |
| `protocol.py` | Binary serialisation/deserialisation (`parse_frame`, `build_frame`, `build_response`) |
| `processing.py` | `process_frame`: points → normals → sonar equation → binning → normalisation → noise |
| `materials.py` | YAML config loader, material property lookup arrays |

### 4.2 Protocol Parsing (protocol.py)

`parse_frame()` extracts the 88-byte header into a `FrameHeader` dataclass and returns the range and material planes as zero-copy numpy views reshaped to (H, W). `build_frame()` mirrors the Unity-side serialisation for tests and offline tools.

`build_response()` packs the ping ID and image dimensions into an 8-byte header, appends the float32 image bytes.

### 4.3 Material Properties (materials.py)

Loaded from `config/materials.yaml` on startup. Each material type defines:

| Material | Index | Reflectivity | Scattering | Absorption (dB/m) |
|----------|-------|-------------|------------|-------------------|
| Default | 0 | 0.30 | 0.10 | 0.001 |
| Steel | 1 | 0.95 | 0.05 | 0.000 |
| Concrete | 2 | 0.70 | 0.25 | 0.000 |
| Sand | 3 | 0.20 | 0.35 | 0.002 |
| Mud | 4 | 0.10 | 0.15 | 0.003 |
| Rock | 5 | 0.60 | 0.30 | 0.000 |
| Wood | 6 | 0.40 | 0.20 | 0.005 |
| Rubber | 7 | 0.05 | 0.02 | 0.010 |
| Water surface | 8 | 0.95 | 0.05 | 0.000 |

- **Reflectivity** (0–1): fraction of incident energy reflected specularly. Steel is highly reflective (0.95); sand is weakly reflective (0.20).
- **Scattering coefficient** (0–1): fraction of incident energy scattered diffusely. Higher for rough surfaces (sand 0.35, concrete 0.25).
- **Absorption** (dB/m): energy absorbed per metre of travel through the material. Negligible for hard surfaces; significant for mud and rubber.

Properties are pre-built into numpy lookup arrays indexed by material type, enabling vectorised access across all rays: `materials.reflectivity[mat_types]`.

The YAML file also defines noise parameters: `receiver_noise_floor_db`, `volume_scattering_db_per_m`, `speckle_std`, `band_noise_num_bands`, `band_noise_amplitude`.

### 4.4 Processing Pipeline (processing.py)

Entry point: `process_frame(header, ranges, material_ids, materials) → float32 array (256×650)`.

Grid geometry that depends only on the header — ray directions, column → beam mapping, beam-pattern weights — is cached across pings in `_FrameGeometry`.

#### Stage 1: Point Reconstruction

Each grid cell's 3D point in the sonar frame is `range × dir(az, el)`, where the direction grid replicates the Unity rotation chain analytically: `dir = (cos t·sin az, −cos t·cos az, −sin t)` with `t = el + mountTilt`.

#### Stage 2: Surface Normals

Normals come from central differences of the point grid along azimuth and elevation (`cross(dP/daz, dP/del)`), with edge replication at grid borders. A normal is only *reliable* where the cell and its four neighbours all have returns; at silhouette edges and isolated pixels the incidence is assumed head-on (1.0). Incidence is `|dot(dir, normal)|`, clipped to [1e-6, 1.0].

#### Stage 3: Sonar Equation

For each cell with a return, echo level in dB (unchanged from the ray pipeline):

```
EL = SL − 2×TL + TS
```

- **SL** (Source Level) = 220 dB — transmitted acoustic power
- **TL** (Transmission Loss) = 20·log₁₀(range) + α·range — spherical spreading plus material absorption, applied twice (two-way path)
- **TS** (Target Strength) = 10·log₁₀(reflectivity × incidence + scattering) — steep incidence produces stronger returns

The dB result is converted to linear intensity: `10^(EL/10)`.

#### Stage 4: Beam Pattern Weighting

Each cell is weighted by a 2D Gaussian in normalised beam coordinates (σ = 0.5): elevation normalised to the vertical FOV half-angle, azimuth normalised to the cell's position within its beam. Same shape as the legacy sub-beam weighting; padding rows/columns are attenuated naturally.

#### Stage 5: Max-Binning with Footprint-Aware Splatting

Each cell maps to `(beam, bin)`: beam by azimuth value (padding columns are excluded), bin by `int(range / bin_width)`. A grid pixel covers a surface patch whose range extent grows with grazing angle — on a flat seabed one elevation row can span many range bins. Each return is therefore splatted flat across its own footprint (half-extent from one-sided range differences to its neighbours, capped at 32 bins) with a Gaussian pulse tail (σ = 1 bin) beyond. One-sided differences keep footprints from bridging depth discontinuities, so silhouettes and shadows stay crisp. If the far field still shows range banding, raise `elevationSamples`.

Accumulation uses `scatter_reduce(amax)` — the torch equivalent of the previous `np.maximum.at`. Maximum blending prevents stripe artifacts from overlapping envelopes.

#### Acoustic Shadows (not a stage)

There is no explicit shadow pass. The depth map only contains the first surface along each ray, so anything occluded — the seabed behind a pile, the far side of objects — contributes no returns and comes out dark naturally. (The legacy pipeline zeroed everything beyond `first_return + 12` bins per beam; with depth-map input that rule is redundant for true shadows and wrong for forward-looking geometries, where it would erase targets beyond the near seabed.)

#### Stage 6: dB Normalisation with Fixed Dynamic Range

The raw intensity image spans an enormous dynamic range (potentially 10¹⁷). To map to displayable 0–1:

```python
image_dB = 10 × log₁₀(intensity)
ceiling_dB = max(image_dB)  # strongest return this ping
floor_dB = ceiling_dB - 60  # 60 dB display window
normalised = (image_dB - floor_dB) / 60
```

**Why fixed dynamic range?** Per-ping max normalisation (`image /= max_val`) causes the seabed to vanish when a strong nearby target dominates. A fixed 60 dB window ensures both strong targets (pillar at close range) and weaker returns (seabed) remain visible simultaneously — matching how real sonar displays work.

### 4.5 Noise Model

Noise is added after dB normalisation, in 0–1 space (same components as the legacy pipeline, now generated in torch on the processing device). The noise floor applies across the whole image; shadow regions stay visibly darker because they carry no signal, only floor.

- **Volume scattering:** Simulates acoustic scattering by suspended particles in the water column. Applied only to bins *before* the first return per beam. Amplitude 0.02–0.08, decreasing with range (clearer water at depth).
- **Band noise:** Multiple sinusoidal interference patterns at random frequencies (3 bands). Random frequency multipliers and phases, with per-ping random offset for temporal variation. Simulates electrical and environmental noise.
- **Receiver noise:** Uniform random noise (0.01–0.04) per pixel. Models electronic noise floor.
- **Gaussian noise:** Additive N(0, 0.02). General system noise.
- **Speckle:** 0.3% of pixels get a bright spike (0.08–0.2). Models constructive interference speckle characteristic of coherent sonar imaging.

---

## 5. Configuration

### 5.1 Unity Side (SonarConfig ScriptableObject)

Edited in the Unity Inspector. Controls beam geometry, range, timing, and acoustic transmission parameters. Changes take effect on next Play.

### 5.2 Python Side (materials.yaml)

Edited in any text editor at `processor/config/materials.yaml`. Controls material acoustic properties (reflectivity, scattering, absorption per material type) and noise model parameters. Reloaded by restarting the Python processor — no Unity restart needed.

### 5.3 Consistency Requirements

The `materialType` enum values must match between `AcousticProperties` components in Unity and the YAML material indices. If Unity sends `materialType=1`, Python must have index `1` defined in the YAML.

---

## 6. Codebase Structure

```
sonar-simulator/
├── ARCHITECTURE.md              # High-level architecture overview
├── environment.yaml             # Conda environment (Python 3.11, numpy, pyyaml, opencv, pytorch)
│
├── shared/
│   └── protocol.md              # Byte-level TCP packet format specification
│
├── unity/Assets/Sonar/
│   ├── Scripts/
│   │   ├── Core/
│   │   │   ├── SonarConfig.cs        # ScriptableObject — all sonar parameters
│   │   │   ├── AcousticProperties.cs # MonoBehaviour — per-object material type
│   │   │   ├── SonarFrame.cs         # DepthFrame struct (range + material planes)
│   │   │   └── SonarBridge.cs        # Orchestrator wiring Network → Display
│   │   ├── Raycasting/
│   │   │   └── SonarRaycaster.cs     # Polar grid capture via batch RaycastCommand
│   │   ├── Network/
│   │   │   └── SonarNetworkClient.cs # TCP client (send + receive on one connection)
│   │   ├── Display/
│   │   │   └── SonarDisplay.cs       # Colour mapping + texture output
│   │   └── __Legacy/
│   │       ├── SonarEmitter.cs       # CPU reference implementation
│   │       └── SonarGPUEmitter.cs    # GPU reference implementation
│   ├── Shaders/
│   │   ├── SonarFanView.shader       # Polar UV fan projection
│   │   └── SonarBeamPattern.compute  # Legacy compute shader
│   └── Materials/
│       └── (sonar fan material)
│
├── processor/
│   ├── config/
│   │   └── materials.yaml     # Material properties + noise parameters
│   ├── src/
│   │   ├── __init__.py
│   │   ├── main.py              # Entry point: python -m processor.src.main
│   │   ├── network.py           # TCP server, length-prefixed messages
│   │   ├── protocol.py          # Binary parse/build (frame header + planes)
│   │   ├── processing.py        # Torch frame collapse: points → normals → image
│   │   └── materials.py         # YAML loader, vectorised lookup arrays
│   └── tests/
│       ├── generate_fixture.py  # Synthetic depth frame (seabed + steel pile)
│       ├── test_pipeline.py     # Offline end-to-end processing test
│       ├── test_tcp_roundtrip.py# Full TCP round-trip test (plays Unity's role)
│       └── fixtures/
│           └── test_frame.bin   # Generated test data (optional)
│
└── tools/                       # (planned)
    ├── record_ping.py           # Record live pings to .bin files
    ├── replay_ping.py           # Replay recorded pings through processor
    └── visualise_raw.py         # matplotlib visualisation of raw images
```

---

## 7. Performance

| Metric | Value |
|--------|-------|
| Capture grid | 512 × 128 = 65,536 rays per ping |
| Frame payload (Unity → Python) | ~330 KB |
| Response payload (Python → Unity) | 665 KB |
| Python processing time | 60–80 ms per ping (torch on CPU, measured offline) |
| Target ping rate | 15 Hz (66 ms budget) |
| Packet loss (TCP) | 0% |
| Output image | 256 × 650, float32, normalised 0–1 |

CPU torch processing sits at the edge of the 66 ms budget; CUDA is expected to be substantially faster but has not been benchmarked yet. Throughput is currently not a priority — the pipeline targets dataset generation, not real-time display. Lower `pingRateHz` if the display lags.

---

## 8. Running the System

### 8.1 Environment Setup

```bash
conda env create -f environment.yaml
conda activate sonar-sim
```

The processor requires PyTorch (in `environment.yaml`). For a CUDA build on conda-forge, install e.g. `pytorch=*=*cuda12*`; the processor logs the device it selected on startup.

### 8.2 Offline Testing (No Unity Required)

```bash
cd sonar-simulator
python3 -m processor.tests.test_pipeline       # processing on a synthetic frame
python3 -m processor.tests.test_tcp_roundtrip  # full TCP round-trip, plays Unity's role
```

Validates parsing, geometry reconstruction, sonar equation, binning, geometric shadowing, and noise on synthetic data (forward-looking seabed + pile scene).

### 8.3 Full Pipeline

1. Start Python: `python3 -m processor.src.main`
2. Wait for: `TCP server listening on 127.0.0.1:9000 — waiting for Unity...`
3. Hit Play in Unity. Console logs `SonarNetworkClient: connected to 127.0.0.1:9000`
4. Python logs `Unity connected` followed by `Ping N processed in X ms`
5. Fan display renders real-time sonar returns from scene geometry

Stop Play mode first; Python detects disconnect and waits for reconnection.

---

## 9. Key Design Decisions

### Polar Ray Grid over Camera Render
The depth map is captured with a regular azimuth × elevation ray grid rather than a rendered depth camera. A perspective camera cannot cover the 150° swath in one frustum, and a URP material-ID pass would need override-material renderer-feature machinery; the ray grid gives uniform angular sampling, maps columns directly to beams, and keeps Unity code minimal. The GPU workload lives in the Python torch collapse instead.

### TCP over UDP
Initial UDP implementation used chunked datagrams (60 KB each, 10 chunks per ping). OS socket buffers (~416 KB) consistently overflowed, losing 3/10 chunks. TCP eliminated packet loss entirely with simpler code (no chunking, no reassembly, no stale ping GC).

### Fixed Dynamic Range over Per-Ping Normalisation
Per-ping `image /= max_val` caused the seabed to vanish when a strong nearby pillar dominated. A fixed 60 dB window matches real sonar display behaviour — all targets visible regardless of relative intensity.

### Maximum Blending over Additive Blending
`np.maximum.at` instead of `np.add.at` for range bin accumulation matches the legacy `Mathf.Max` approach. Prevents stripe artifacts from overlapping pulse envelopes.

### Noise in Normalised Space
Noise applied after dB normalisation (in 0–1 space) ensures perceptually consistent levels regardless of target strength.

### Geometric Shadows over an Explicit Shadow Stage
The legacy pipeline zeroed everything beyond the first return per beam. With depth-map input, occlusion is already encoded — shadowed regions contribute no returns — and the legacy rule would erase legitimate targets beyond the near seabed in forward-looking geometries. Shadows now emerge purely from geometry; the noise floor covers the whole image.

### Legacy Preservation
Original CPU and GPU emitters kept in `__Legacy/` as compilable reference. They document beam geometry, noise model, and shadow logic that the hybrid architecture reimplements.

---

## 10. Adding a New Processing Stage

1. Add function in `processor/src/processing.py`
2. Call it from `process_frame()` at the appropriate point in the pipeline
3. If it needs configuration, add parameters to `materials.yaml` and load them in `materials.py`
4. Test offline: `python3 -m processor.tests.test_pipeline`
5. Update this document

## 11. Adding a New Material Type

1. Add enum value to `AcousticProperties.materialType` in Unity
2. Add corresponding entry in `processor/config/materials.yaml` with matching index
3. Assign the material type to scene objects via the AcousticProperties component
4. Python picks it up automatically on next restart
