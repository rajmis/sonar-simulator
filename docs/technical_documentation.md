# NORBIT WBMS Multibeam Sonar Simulator — Technical Documentation

## 1. System Overview

This is a hybrid Unity3D + Python multibeam sonar simulator modelling a NORBIT WBMS. Unity handles real-time raycasting (geometric hit detection) and Python handles all acoustic signal processing (intensity computation, noise modelling, image generation). The two processes communicate over a single TCP connection on localhost port 9000.

**Pipeline per ping cycle:**

```
Unity SonarRaycaster (11,520 rays via batch Physics)
        │
        ▼
Unity SonarNetworkClient (serialise 553 KB, TCP send)
        │
        ▼ TCP localhost:9000
        │
Python SonarTCPServer (parse header + rays)
        │
        ▼
Python process_ping (sonar equation → range binning → shadows → dB normalisation → noise)
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

Unity's role is exclusively geometric: fire rays, detect hits, transmit raw hit data, receive processed image, render it. Unity performs zero acoustic computation.

### 2.1 SonarConfig (ScriptableObject)

All sonar parameters are centralised in a ScriptableObject asset (`DefaultConfig`). Edited in the Unity Inspector.

| Parameter | Default | Description |
|-----------|---------|-------------|
| numberOfBeams | 256 | Beams across the swath |
| swathAngleDeg | 150° | Horizontal field of view |
| verticalFOVDeg | 25° | Vertical beam spread |
| subBeamsH × subBeamsV | 5 × 9 = 45 | Sub-beam grid per beam |
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

Fires rays in a 2D cone pattern matching NORBIT WBMS beam geometry. For each of 256 beams, generates a 5×9 sub-beam grid = 11,520 total rays per ping.

**Beam direction computation (pre-computed on Start in local space):**

1. **Base beam angle:** Evenly distributed across 150° swath, offset by half angular resolution to centre beams within cells.
2. **Sub-beam offsets:** Vertical sub-beams span the 25° vertical FOV. Horizontal sub-beams span the angular resolution width of one beam.
3. **Rotation chain:** Mount tilt (around X) → Fan angle (around Z) → Vertical offset (around X) → applied to `Vector3.down`. This produces the downward-looking fan pattern of hull-mounted multibeam sonars. This rotation chain matches the legacy `SonarGPUEmitter.GetBeamDirection()` exactly.

**Batch raycasting:** Uses Unity's `RaycastCommand` with `NativeArray` allocations. All 11,520 raycasts are scheduled on the Job System for parallel execution. Only colliders on the `SonarTarget` physics layer are detected.

**Hit data collected per ray:** beam index, sub-beam index, did hit (bool), hit distance (m), hit normal (Vector3), ray direction (Vector3), object ID, material type (from `AcousticProperties` component), hit point (Vector3). Misses record `didHit=false` with zeroed fields.

**Output:** Fires `OnPingReady(PingData)` event with all ray data packaged.

### 2.3 AcousticProperties (MonoBehaviour)

Attached to scene objects (JettyPile, Seabed, etc.) on the `SonarTarget` physics layer. Contains a `materialType` enum that indexes into the Python-side YAML material table. The raycaster reads this via `GetComponentInParent<AcousticProperties>()` on hit colliders.

### 2.4 SonarNetworkClient (TCP)

**File:** `Scripts/Network/SonarNetworkClient.cs`

Manages a single bidirectional TCP connection to the Python processor. Replaces the earlier UDP-based SonarSender + SonarReceiver (which suffered from chunk loss due to OS socket buffer overflow at 15 Hz).

**Sending (main thread):** When `OnPingReady` fires, serialises the ping into the binary protocol: 4-byte length prefix + 80-byte ping header + 48 bytes per ray. All little-endian. Total per ping: 4 + 80 + (11,520 × 48) = 553,044 bytes. Sent via `NetworkStream.Write`.

**Receiving (background thread):** A dedicated `Thread` reads length-prefixed responses from the TCP stream. Each response = 4-byte length + 8-byte response header + float32 intensity data (256×650×4 bytes). Completed images are queued into a `ConcurrentQueue` and dequeued on the main thread in `Update()`.

**Connection management:** Connects on `Start()`. Auto-reconnects every 2 seconds if connection drops. The `SocketException` on Play mode stop is expected (background thread's blocking read is interrupted when the socket closes).

**Inspector fields:** `host` (default 127.0.0.1), `port` (default 9000), `config` (SonarConfig), plus read-only status fields `connected` and `pingsProcessed`.

### 2.5 RayHitData / PingData (Structs)

**File:** `Scripts/Core/RayHitData.cs`

`RayHitData` holds per-ray data (matches the 48-byte wire format). `PingData` packages a complete ping: ping ID, timestamp, sonar pose, and the ray array.

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

### 3.2 Ping Payload (Unity → Python)

Total: 80 + (numberOfBeams × subBeamsPerBeam × 48) bytes.

**Ping header (80 bytes):**

| Offset | Type | Field | Description |
|--------|------|-------|-------------|
| 0 | uint32 | pingID | Sequence number |
| 4 | float64 | timestamp | Unity Time.timeAsDouble |
| 12 | float32×3 | sonarPos | World position (x, y, z) |
| 24 | float32×4 | sonarRot | Quaternion (x, y, z, w) |
| 40 | uint16 | numberOfBeams | e.g. 256 |
| 42 | uint16 | subBeamsPerBeam | e.g. 45 |
| 44 | float32 | maxRange | Metres |
| 48 | uint16 | numberOfRangeBins | e.g. 650 |
| 50 | float32 | swathAngleDeg | Horizontal FOV |
| 54 | float32 | verticalFOVDeg | Vertical FOV |
| 58 | float32 | speedOfSound | m/s |
| 62 | float32 | sourceLevel_dB | Source level |
| 66 | float32 | pulseLength_s | Pulse duration |
| 70 | float32 | frequency_Hz | Centre frequency |
| 74 | uint16 | subBeamsH | Sub-beams horizontal |
| 76 | uint16 | subBeamsV | Sub-beams vertical |
| 78 | uint16 | _reserved | Padding to 80 bytes |

**Ray record (48 bytes each, repeated numberOfBeams × subBeamsPerBeam times):**

| Offset | Type | Field | Description |
|--------|------|-------|-------------|
| 0 | uint16 | beamIndex | Parent beam [0, numberOfBeams) |
| 2 | uint16 | subBeamIndex | Sub-beam [0, subBeamsPerBeam) |
| 4 | uint8 | didHit | 1 = hit, 0 = miss |
| 5 | float32 | hitDistance | Range in metres (0 if miss) |
| 9 | float32×3 | hitNormal | Surface normal at hit |
| 21 | float32×3 | rayDir | Normalised ray direction |
| 33 | uint16 | objectID | Target object identifier |
| 35 | uint8 | materialType | Enum index into material table |
| 36 | float32×3 | hitPoint | World-space hit position |

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
```

---

## 4. Python Acoustic Processing Pipeline

The Python processor is the acoustic heart of the simulator. It transforms raw ray hit data into a realistic sonar intensity image through five processing stages.

### 4.1 Module Overview

| Module | Responsibility |
|--------|---------------|
| `main.py` | Entry point, TCP server loop, orchestrates parsing → processing → response |
| `network.py` | TCP server with length-prefixed message framing |
| `protocol.py` | Binary serialisation/deserialisation, numpy structured array parsing |
| `processing.py` | Sonar equation, range binning, shadows, normalisation, noise |
| `materials.py` | YAML config loader, material property lookup arrays |

### 4.2 Protocol Parsing (protocol.py)

`parse_ping_header()` extracts the 80-byte header into a `PingHeader` dataclass using `struct.unpack_from`. `parse_rays_bulk()` parses all ray records in one call using a numpy structured dtype — this gives zero-copy, vectorised access to all 11,520 rays without Python-level loops. Fields are accessed as `rays["hit_distance"]`, `rays["beam_index"]`, etc.

`build_response()` packs the ping ID and image dimensions into an 8-byte header, appends the float32 image bytes.

### 4.3 Material Properties (materials.py)

Loaded from `config/norbit_wbms.yaml` on startup. Each material type defines:

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

- **Reflectivity** (0–1): fraction of incident energy reflected specularly. Steel is highly reflective (0.95); sand is weakly reflective (0.20).
- **Scattering coefficient** (0–1): fraction of incident energy scattered diffusely. Higher for rough surfaces (sand 0.35, concrete 0.25).
- **Absorption** (dB/m): energy absorbed per metre of travel through the material. Negligible for hard surfaces; significant for mud and rubber.

Properties are pre-built into numpy lookup arrays indexed by material type, enabling vectorised access across all rays: `materials.reflectivity[mat_types]`.

The YAML file also defines noise parameters: `receiver_noise_floor_db`, `volume_scattering_db_per_m`, `speckle_std`, `band_noise_num_bands`, `band_noise_amplitude`.

### 4.4 Processing Pipeline (processing.py)

Entry point: `process_ping(header, rays, materials) → float32 array (256×650)`.

#### Stage 1: Beam Pattern Weights

A 2D Gaussian beam pattern is computed for the sub-beam grid (5×9). Each sub-beam receives a weight based on its distance from the beam centre using `exp(-(h² + v²) / (2σ²))` with σ=0.5. Weights are normalised to sum to 1.0 per beam, ensuring consistent total energy regardless of grid resolution.

Centre sub-beams contribute most strongly; edge sub-beams taper off, modelling the real beam pattern of a multibeam sonar element.

#### Stage 2: Sonar Equation

For each ray that hit a target, the sonar equation computes echo level in dB:

```
EL = SL − 2×TL + TS
```

Where:

- **SL** (Source Level) = 220 dB — transmitted acoustic power
- **TL** (Transmission Loss) = 20·log₁₀(range) + α·range — spherical spreading plus material absorption, applied twice (two-way path)
- **TS** (Target Strength) = 10·log₁₀(reflectivity × cos(θ) + scattering) — where θ is the grazing angle between ray direction and surface normal. Steep incidence produces stronger returns.

The grazing angle is computed as `|dot(rayDir, hitNormal)|`, clipped to [1e-6, 1.0].

The dB result is converted to linear intensity: `10^(EL/10)`, then multiplied by the beam pattern weight for that sub-beam.

All operations are fully vectorised across 11,520 rays using numpy — no Python loops.

#### Stage 3: Range Binning with Gaussian Spread

Each hit is mapped to a range bin: `bin = int(distance / bin_width)`. To model the finite pulse length and create realistic target extent, each hit is spread across ±8 neighbouring bins using a Gaussian envelope: `weight = exp(-0.5 × offset²)`.

Bin accumulation uses `np.maximum.at` (not `np.add.at`). This matches the legacy renderer's `Mathf.Max` approach and prevents stripe artifacts from overlapping pulse envelopes. Maximum blending produces clean, solid target returns.

#### Stage 4: Acoustic Shadows

For each beam, the minimum hit bin across all sub-beams is found using `np.minimum.at`. Everything beyond `first_hit_bin + 12` bins is zeroed. This models the physics that acoustic energy cannot penetrate past a solid target — the region behind a pillar is dark. The 12-bin offset accounts for pulse envelope spread around the hit point.

#### Stage 5: dB Normalisation with Fixed Dynamic Range

The raw intensity image spans an enormous dynamic range (potentially 10¹⁷). To map to displayable 0–1:

```python
image_dB = 10 × log₁₀(intensity)
ceiling_dB = max(image_dB)  # strongest return this ping
floor_dB = ceiling_dB - 60  # 60 dB display window
normalised = (image_dB - floor_dB) / 60
```

**Why fixed dynamic range?** Per-ping max normalisation (`image /= max_val`) causes the seabed to vanish when a strong nearby target dominates. A fixed 60 dB window ensures both strong targets (pillar at close range) and weaker returns (seabed) remain visible simultaneously — matching how real sonar displays work.

### 4.5 Noise Model

Noise is added after dB normalisation, in 0–1 space. Noise is applied only to the **active region** (before the acoustic shadow per beam), keeping shadow zones nearly black.

**Active region noise:**

- **Volume scattering:** Simulates acoustic scattering by suspended particles in the water column. Applied only to bins *before* the first target hit. Amplitude 0.02–0.08, decreasing with range (clearer water at depth).
- **Band noise:** Multiple sinusoidal interference patterns at random frequencies (3 bands). Random frequency multipliers and phases, with per-ping random offset for temporal variation. Simulates electrical and environmental noise.
- **Receiver noise:** Uniform random noise (0.01–0.04) per pixel. Models electronic noise floor.
- **Gaussian noise:** Additive N(0, 0.02). General system noise.
- **Speckle:** 0.3% of pixels get a bright spike (0.08–0.2). Models constructive interference speckle characteristic of coherent sonar imaging.

**Shadow zone noise:** Extremely faint (0–0.005). Models residual multipath and ambient noise. Nearly black but not perfectly zero.

---

## 5. Configuration

### 5.1 Unity Side (SonarConfig ScriptableObject)

Edited in the Unity Inspector. Controls beam geometry, range, timing, and acoustic transmission parameters. Changes take effect on next Play.

### 5.2 Python Side (norbit_wbms.yaml)

Edited in any text editor at `processor/config/norbit_wbms.yaml`. Controls material acoustic properties (reflectivity, scattering, absorption per material type) and noise model parameters. Reloaded by restarting the Python processor — no Unity restart needed.

### 5.3 Consistency Requirements

The `materialType` enum values must match between `AcousticProperties` components in Unity and the YAML material indices. If Unity sends `materialType=1`, Python must have index `1` defined in the YAML.

---

## 6. Codebase Structure

```
sonar-simulator/
├── ARCHITECTURE.md              # High-level architecture overview
├── environment.yaml             # Conda environment (Python 3.11, numpy, pyyaml, opencv)
│
├── shared/
│   └── protocol.md              # Byte-level TCP packet format specification
│
├── unity/Assets/Sonar/
│   ├── Scripts/
│   │   ├── Core/
│   │   │   ├── SonarConfig.cs        # ScriptableObject — all sonar parameters
│   │   │   ├── AcousticProperties.cs # MonoBehaviour — per-object material type
│   │   │   ├── RayHitData.cs         # Data structs (RayHitData, PingData)
│   │   │   └── SonarBridge.cs        # Orchestrator wiring Network → Display
│   │   ├── Raycasting/
│   │   │   └── SonarRaycaster.cs     # Beam pattern + batch RaycastCommand
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
│   │   └── norbit_wbms.yaml     # Material properties + noise parameters
│   ├── src/
│   │   ├── __init__.py
│   │   ├── main.py              # Entry point: python -m processor.src.main
│   │   ├── network.py           # TCP server, length-prefixed messages
│   │   ├── protocol.py          # Binary parse/build, numpy structured arrays
│   │   ├── processing.py        # Sonar equation → binning → shadows → noise
│   │   └── materials.py         # YAML loader, vectorised lookup arrays
│   └── tests/
│       ├── generate_fixture.py  # Synthetic ping generator
│       ├── test_pipeline.py     # Offline end-to-end test
│       └── fixtures/
│           └── test_ping.bin    # Generated test data
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
| Rays per ping | 11,520 (256 × 45) |
| Ping payload (Unity → Python) | 553 KB |
| Response payload (Python → Unity) | 665 KB |
| Python processing time | 3–9 ms per ping |
| Target ping rate | 15 Hz (66 ms budget) |
| Packet loss (TCP) | 0% |
| Output image | 256 × 650, float32, normalised 0–1 |

The system runs well within the 66 ms per-ping budget. Python processing under 10 ms leaves ample headroom for Unity raycasting, rendering, and TCP round-trip.

---

## 8. Running the System

### 8.1 Environment Setup

```bash
conda env create -f environment.yaml
conda activate sonar-sim
```

### 8.2 Offline Testing (No Unity Required)

```bash
cd sonar-simulator
python3 -m processor.tests.test_pipeline
```

Validates parsing, sonar equation, range binning, shadows, and noise on synthetic data.

### 8.3 Full Pipeline

1. Start Python: `python3 -m processor.src.main`
2. Wait for: `TCP server listening on 127.0.0.1:9000 — waiting for Unity...`
3. Hit Play in Unity. Console logs `SonarNetworkClient: connected to 127.0.0.1:9000`
4. Python logs `Unity connected` followed by `Ping N processed in X ms`
5. Fan display renders real-time sonar returns from scene geometry

Stop Play mode first; Python detects disconnect and waits for reconnection.

---

## 9. Key Design Decisions

### TCP over UDP
Initial UDP implementation used chunked datagrams (60 KB each, 10 chunks per ping). OS socket buffers (~416 KB) consistently overflowed, losing 3/10 chunks. TCP eliminated packet loss entirely with simpler code (no chunking, no reassembly, no stale ping GC).

### Fixed Dynamic Range over Per-Ping Normalisation
Per-ping `image /= max_val` caused the seabed to vanish when a strong nearby pillar dominated. A fixed 60 dB window matches real sonar display behaviour — all targets visible regardless of relative intensity.

### Maximum Blending over Additive Blending
`np.maximum.at` instead of `np.add.at` for range bin accumulation matches the legacy `Mathf.Max` approach. Prevents stripe artifacts from overlapping pulse envelopes.

### Noise in Normalised Space
Noise applied after dB normalisation (in 0–1 space) ensures perceptually consistent levels regardless of target strength. Active-region-only application keeps shadow zones clean.

### Noise Restricted to Active Region
Bulk noise (receiver, band, speckle) is masked to active bins only (before shadow start per beam). Shadow zones receive only trace noise (0–0.005), producing nearly black regions behind targets and below seabed.

### Legacy Preservation
Original CPU and GPU emitters kept in `__Legacy/` as compilable reference. They document beam geometry, noise model, and shadow logic that the hybrid architecture reimplements.

---

## 10. Adding a New Processing Stage

1. Add function in `processor/src/processing.py`
2. Call it from `process_ping()` at the appropriate point in the pipeline
3. If it needs configuration, add parameters to `norbit_wbms.yaml` and load them in `materials.py`
4. Test offline: `python3 -m processor.tests.test_pipeline`
5. Update this document

## 11. Adding a New Material Type

1. Add enum value to `AcousticProperties.materialType` in Unity
2. Add corresponding entry in `processor/config/norbit_wbms.yaml` with matching index
3. Assign the material type to scene objects via the AcousticProperties component
4. Python picks it up automatically on next restart
