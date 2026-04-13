# Lagoon 3D World — Build Decisions & Status

## Location
- **Site**: Swimming lagoon on one of Singapore's Southern Islands
- **Coordinates**: Origin at 1.2149958°N, 103.8515187°E
- **Purpose**: Sim-to-real sonar comparison — simulated sonar output must match real NORBIT WBMS data collected at this site

## Coordinate System
- **Origin**: Deploy point (sonar deployment location at the T-junction of the pier)
- **Frame**: Local ENU (East-North-Up)
  - Unity X = East (right in satellite view)
  - Unity Z = North (up in satellite view)
  - Unity Y = Up (vertical)
- **Scale**: 1 Unity unit = 1 meter
- **Conversion**: lat/lng → meters using `M_PER_DEG_LAT = 110574.0`, `M_PER_DEG_LNG = 110320.0 × cos(lat)`

## Data Pipeline
```
Google Maps (labels + KML path trace)
    ↓ export .kml
kml_to_unity.py (tools/)
    ↓ outputs:
    ├── lagoon_unity.csv        → Unity TextAsset for LagoonSceneBuilder
    ├── breakwater_vertices.csv → ordered path for ProBuilder tracing
    └── scene_summary.txt       → distances for validation
```

- **Old CSV workflow is deprecated** — KML export captures everything more accurately
- KML auto-detects origin from any placemark named "deploy" or "origin"
- Auto-classifies point types by name pattern (pillar → pillar, jetty → jetty, etc.)
- Breakwater KML path becomes ordered vertices + LineRenderer guide in Unity

## Scene Structure (as of this session)

### Structures Built
| Type | Count | Status |
|------|-------|--------|
| Pillars | 11 (pillar_1 through pillar_11) | ✅ Built as cylinders, need snap-to-seabed |
| Ramp | 1 (jetty_ramp_start → jetty_ramp_end, 14.8m) | ✅ Built, angled from shore to pier |
| Jetty stem | 1 (jetty_base_start → jetty_base_end, 41.8m) | ✅ Built as deck slab |
| Jetty right arm | 1 (jetty_base_right_start → jetty_base_right_end, 18.1m) | ✅ Built as deck slab |
| Gate barrier | 1 (gate_north → gate_south, 131.2m) | ✅ Built as wall mesh |
| Breakwater path | 53 vertices from KML | ⚠️ LineRenderer only — needs ProBuilder mesh |
| Seabed | Bowl-shaped mesh from SeabedGenerator | ✅ Generated |
| Water surface | Plane at Y=0 | ✅ Built |

### Hierarchy
```
LagoonScene
├── Main Camera
├── Directional Light
└── LagoonWorld                    ← LagoonSceneBuilder + SeabedSnapper
    ├── SatelliteReference         ← can be removed, visual reference only
    ├── WaterSurface
    ├── Seabed                     ← SeabedGenerator component
    ├── DeployOrigin
    ├── Pillars
    │   ├── Pillar_pillar_1 ... Pillar_pillar_11
    ├── Ramp
    ├── Jetties
    │   ├── Jetty_jetty_base       ← main stem
    │   └── Jetty_jetty_base_right ← right arm
    ├── Barriers
    │   └── GateBarrier
    └── BreakwaterGuide
        └── BreakwaterPath         ← LineRenderer (guide, not solid)
```

## Pier Structure Decisions
The pier is NOT separate jetties — it's one connected structure:
1. **Ramp**: Sloped surface from beach (high) down to the raised pier (low). ~14.8m long.
2. **Main stem**: Continuous raised deck on pilings running south-to-north through the T-junction. ~41.8m long.
3. **Right arm**: Branches east from the T-junction. ~18.1m long.
- **No auto-generated legs**: `jettyLegSpacing` set to 9999 to suppress generated legs. Real structural support comes from pillar_6, pillar_7, pillar_8, pillar_10 which are already placed at correct positions.

## Pillar Layout
- **pillar_1 through pillar_5**: In-water standalone pillars running into the lagoon from the deploy point. Spaced ~20m apart. Ranges: 22.5m to 101.2m from origin.
- **pillar_6, pillar_7**: Near the pier base/ramp connection. Structural supports.
- **pillar_8**: Near the right arm tip (2.1m from jetty_base_right_end). Structural.
- **pillar_9**: Further out near breakwater_06/07 area. Range 37.7m.
- **pillar_10**: Near the jetty stem north end (5.1m from jetty_base_end). Structural.
- **pillar_11**: Near breakwater_08 and gate_south. Range 22.5m.

## Breakwater & Gate
- **Breakwater**: Continuous rocky seawall forming the lagoon boundary. Traced as a KML path with 53 vertices. Currently a LineRenderer guide — needs to be extruded into a solid mesh in ProBuilder or Blender.
- **Gate**: Only opening in the breakwater wall. 131.2m wide between gate_north and gate_south. The KML path doesn't perfectly close to the gate endpoints — small gap is cosmetic only.
- **Gate connection**: The breakwater curves around the left/bottom of the lagoon AND continues along the top/right edges connecting to both sides of the gate. This was initially missed and added later.

## Seabed Profile
Not a simple centered bowl — asymmetric cross-section based on real-site knowledge:

```
West (breakwater) ←————————————————→ East (gate mouth)

Shore → steep drop → TROUGH (6m) → gradual rise → PIER (4m) → rise → SILL (3m) → gate
```

### SeabedGenerator Parameters
| Parameter | Value | Reasoning |
|-----------|-------|-----------|
| edgeDepth | 2m | Shallow at breakwater base |
| troughDepth | 6m | Measured deepest point |
| pierDepth | 4m | Measured at deploy point |
| sillDepth | 3m | Rise at gate mouth opening |
| troughX | -40 | Halfway between pillar_4 (-19.8) and breakwater_04 (-59.8) |
| troughZ | 82 | Same halfway calculation |
| troughRadius | 40m | Width of deepest zone |
| bowlCurvature | 1.5 | Gradual bowl profile |
| gateNorth | (18.41, 135.43) | From scene summary |
| gateSouth | (52.05, 8.63) | From scene summary |
| gridCellSize | 2m | Balance of detail vs performance |

## Acoustic Material Tags
Every mesh gets an `AcousticMaterialTag` component with a `materialType` string that maps to `acoustic_materials.yaml` in `processor/config/`. The Python-side processing uses this for reflectivity, scattering coefficients, and impedance calculations.

| Material Type | Used For |
|---------------|----------|
| sand_mud | Seabed |
| rock_rubble | Breakwater wall (pending) |
| concrete | Jetty deck slabs |
| concrete_cylinder | Pillars, jetty legs |
| concrete_wall | Gate barrier |
| water_surface | Water plane at Y=0 |
| metal | Gate hardware (if modeled) |

## File Locations
```
sonar-simulator/
├── tools/
│   ├── kml_to_unity.py              ← KML converter
│   └── latlon_to_unity.py           ← legacy CSV converter (backup)
├── processor/
│   └── config/
│       └── acoustic_materials.yaml  ← material properties for Python processing
└── unity/
    └── Assets/
        ├── Scenes/
        │   └── Lagoon/
        │       ├── LagoonScene.unity
        │       ├── lagoon.kml
        │       ├── lagoon_unity.csv
        │       ├── breakwater_vertices.csv
        │       ├── scene_summary.txt
        │       └── satellite_ref.png
        └── Sonar/
            └── Scripts/
                └── World/
                    ├── LagoonSceneBuilder.cs
                    ├── AcousticMaterialTag.cs
                    ├── SeabedGenerator.cs
                    └── SeabedSnapper.cs
```

## What's Done
- [x] Google Maps labeling of all structures (30+ placemarks)
- [x] KML export + breakwater path tracing
- [x] kml_to_unity.py converter pipeline
- [x] Scene summary validation — all distances sanity-checked
- [x] LagoonSceneBuilder auto-builds pillars, ramp, jetty, barriers, breakwater guide
- [x] SeabedGenerator with asymmetric depth profile
- [x] AcousticMaterialTag system for sonar processing
- [x] acoustic_materials.yaml with Singapore tropical water properties

## What's Next
- [ ] **Snap pillars to seabed** — run SeabedSnapper to adjust pillar heights to the bowl-shaped seabed
- [ ] **Build breakwater mesh** — trace over LineRenderer guide in ProBuilder, extrude from seabed to above water, tag as rock_rubble
- [ ] **Adjust ramp/jetty heights** — verify they align with the new seabed surface
- [ ] **Connect sonar emitter** — place SonarHead prefab at deploy origin, configure for NORBIT WBMS parameters
- [ ] **First simulated ping** — fire rays into the scene, export hit data
- [ ] **Unity↔Python pipeline** — send hit data to Python processor, get back intensity image
- [ ] **Sim-to-real comparison** — compare simulated fan-view against real sonar data from this site
- [ ] **Iterate** — adjust geometry and material properties based on comparison results

## Naming Conventions
- Placemarks in Google Maps use `snake_case` (e.g., `pillar_01`, `jetty_base_start`)
- Pair matching: builder strips `_start`/`_end` or `_base`/`_end` suffixes to find matching pairs
- Breakwater path vertices from KML are auto-named `bw_path_000`, `bw_path_001`, etc.
- Unity GameObjects follow the CSV point_id with type prefix (e.g., `Pillar_pillar_1`)

## Lessons Learned
1. **KML > CSV**: Tracing paths in Google Maps and exporting KML is far more accurate and faster than manually entering lat/lng coordinates
2. **Satellite image alignment is hard**: Screenshot-based ground truth has rotation and projection distortion. The structures from KML coordinates are the real ground truth — the satellite quad is just a visual aid
3. **Typos propagate**: Name mismatches (`brekwater_08`, `jett_base_right_start`) cause classification failures. Always validate the scene summary for `unknown` types.
4. **Seabed must come first**: All vertical positioning depends on the seabed surface. Build it before placing any structures.
5. **Auto-generated legs vs real supports**: Don't auto-generate pier support legs when the real structural supports (pillars) are already placed as separate objects at their measured positions.
