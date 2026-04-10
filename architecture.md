# Sonar Simulator Architecture

## Overview

Hybrid Unity3D + Python multibeam sonar simulator modelling a NORBIT WBMS.

```
Unity (raycasting)  ──TCP──▶  Python (acoustic processing)  ──TCP──▶  Unity (display)
                     localhost:9000 (single bidirectional connection)
```

## Data Flow

1. **Unity SonarRaycaster** fires 256×45 = 11,520 rays per ping via batch `RaycastCommand`
2. **Unity SonarNetworkClient** serialises 80-byte header + 48 bytes/ray, sends length-prefixed over TCP
3. **Python SonarTCPServer** reads the complete message (553 KB)
4. **Python process_ping** runs: beam weights → sonar equation → range binning → shadows → dB normalisation → noise
5. **Python** returns length-prefixed float32 intensity image (256×650, normalised 0–1) over same TCP connection
6. **Unity SonarNetworkClient** receives on background thread, queues to main thread
7. **Unity SonarDisplay** applies colour map gradient, renders through fan-view shader

## Key Files

| Path | Purpose |
|------|---------|
| `shared/protocol.md` | Byte-level TCP packet format |
| `processor/src/protocol.py` | Binary parse/build, numpy structured arrays |
| `processor/src/network.py` | TCP server, length-prefixed messages |
| `processor/src/processing.py` | Sonar equation, binning, shadows, normalisation, noise |
| `processor/src/materials.py` | YAML material properties loader |
| `processor/src/main.py` | Entry point: `python -m processor.src.main` |
| `processor/config/norbit_wbms.yaml` | Material & noise config |
| `processor/tests/test_pipeline.py` | Offline end-to-end test |
| `TECHNICAL_DOCUMENTATION.md` | Complete inner workings reference |

## Running

```bash
# Offline test (no Unity needed)
python3 -m processor.tests.test_pipeline

# Full pipeline
python3 -m processor.src.main          # start Python first
# then hit Play in Unity
```

## Adding a New Processing Stage

1. Add function in `processor/src/processing.py`
2. Call it from `process_ping()` at the appropriate point
3. If it needs config, add params to `norbit_wbms.yaml` and load in `materials.py`
4. Test with `python3 -m processor.tests.test_pipeline`
5. Update `TECHNICAL_DOCUMENTATION.md`
