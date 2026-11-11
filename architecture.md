# Sonar Simulator Architecture

## Overview

Hybrid Unity3D + Python multibeam sonar simulator modelling a NORBIT WBMS.

```
Unity (depth-map capture)  ──TCP──▶  Python (acoustic collapse, GPU)  ──TCP──▶  Unity (display)
                            localhost:9000 (single bidirectional connection)
```

## Data Flow

1. **Unity SonarRaycaster** casts a polar ray grid (default 512×128 = 65,536 rays)
   covering swath + padding × vFOV + padding via batch `RaycastCommand`
2. Each grid cell records only **hit range** (float32, 0 = miss) and
   **material ID** (uint8)
3. **Unity SonarNetworkClient** serialises 88-byte header + range plane +
   material plane (~330 KB), sends length-prefixed over TCP
4. **Python SonarTCPServer** reads the complete frame
5. **Python process_frame** (PyTorch, CUDA when available) runs:
   point reconstruction → normals from depth gradients → sonar equation →
   beam/range binning → dB normalisation → noise (shadows are geometric:
   occluded surfaces are absent from the depth map)
6. **Python** returns length-prefixed float32 intensity image (256×650,
   normalised 0–1) over the same TCP connection
7. **Unity SonarNetworkClient** receives on background thread, queues to main thread
8. **Unity SonarDisplay** applies colour map gradient, renders through fan-view shader

## Key Files

| Path | Purpose |
|------|---------|
| `shared/PROTOCOL.md` | Byte-level TCP packet format |
| `processor/src/protocol.py` | Binary parse/build of depth frames |
| `processor/src/network.py` | TCP server, length-prefixed messages |
| `processor/src/processing.py` | Torch frame collapse: normals, sonar equation, binning, noise |
| `processor/src/materials.py` | YAML material properties loader |
| `processor/src/main.py` | Entry point: `python -m processor.src.main` |
| `processor/config/materials.yaml` | Material & noise config |
| `processor/tests/test_pipeline.py` | Offline end-to-end processing test |
| `processor/tests/test_tcp_roundtrip.py` | TCP round-trip test (plays Unity's role) |
| `docs/technical_documentation.md` | Complete inner workings reference |

## Running

```bash
# Offline tests (no Unity needed)
python3 -m processor.tests.test_pipeline
python3 -m processor.tests.test_tcp_roundtrip

# Full pipeline
python3 -m processor.src.main          # start Python first
# then hit Play in Unity (SampleScene)
```

## Adding a New Processing Stage

1. Add a stage in `processor/src/processing.py` and call it from `process_frame()`
2. If it needs config, add params to `materials.yaml` and load in `materials.py`
3. Test with `python3 -m processor.tests.test_pipeline`
4. Update `docs/technical_documentation.md`
