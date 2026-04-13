using UnityEngine;
using System.Collections.Generic;
using System.IO;
using System.Globalization;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Builds the lagoon 3D world from a CSV of real-world coordinates.
/// Attach to an empty GameObject in the scene, assign the CSV and satellite texture,
/// then use the context menu or inspector buttons to build.
///
/// CSV format: point_id,type,unity_x,unity_z,notes
/// (Run the Python lat/lng converter first to get unity_x/unity_z in meters)
/// </summary>
public class LagoonSceneBuilder : MonoBehaviour
{
    [Header("Data Sources")]
    [Tooltip("CSV file with columns: point_id, type, unity_x, unity_z, notes")]
    public TextAsset pointsCsv;

    [Tooltip("Satellite screenshot to use as ground reference")]
    public Texture2D satelliteImage;

    [Header("Satellite Reference Quad")]
    [Tooltip("Real-world width of the satellite image in meters (East-West extent)")]
    public float satelliteWidthMeters = 250f;

    [Tooltip("Real-world height of the satellite image in meters (North-South extent)")]
    public float satelliteHeightMeters = 300f;

    [Tooltip("Offset of image center from lagoon center, in meters (East, North)")]
    public Vector2 satelliteOffset = Vector2.zero;

    [Header("Lagoon Geometry Defaults")]
    public float waterSurfaceY = 0f;
    public float seabedDepth = -3f;
    public float breakwaterHeight = 1.5f;
    public float breakwaterWidth = 10f;

    [Header("Pillar Defaults")]
    public float pillarRadius = 0.25f;
    public float pillarHeightAboveWater = 0.5f;

    [Header("Ramp Defaults")]
    [Tooltip("Height where the ramp starts on shore (above water)")]
    public float rampShoreHeight = 2.0f;
    [Tooltip("Height where the ramp meets the platform (above water)")]
    public float rampPlatformHeight = 1.0f;
    public float rampWidth = 3.5f;
    public float rampThickness = 0.25f;

    [Header("Platform Defaults (T-shaped jetty)")]
    public float platformDeckHeight = 1.0f;
    public float platformDeckWidth = 3.5f;
    public float platformDeckThickness = 0.3f;
    public float platformLegRadius = 0.15f;
    public float platformLegSpacing = 4f;

    [Header("Jetty Defaults (simple base-end spans)")]
    public float jettyDeckHeight = 1.0f;
    public float jettyDeckWidth = 3.5f;
    public float jettyDeckThickness = 0.3f;
    public float jettyLegRadius = 0.15f;
    public float jettyLegSpacing = 4f;

    // Parsed points
    private List<LagoonPoint> parsedPoints = new List<LagoonPoint>();

    private struct LagoonPoint
    {
        public string id;
        public string type;
        public float x; // East (Unity X)
        public float z; // North (Unity Z)
        public string notes;
    }

    [ContextMenu("1. Build Reference Layer")]
    public void BuildReferenceLayer()
    {
        // Create satellite reference quad
        if (satelliteImage == null)
        {
            Debug.LogWarning("No satellite image assigned. Skipping reference layer.");
            return;
        }

        var refQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        refQuad.name = "SatelliteReference";
        refQuad.transform.SetParent(transform);
        refQuad.transform.position = new Vector3(
            satelliteOffset.x,
            waterSurfaceY + 0.01f, // Just above water
            satelliteOffset.y
        );
        refQuad.transform.rotation = Quaternion.Euler(90, 0, 0); // Lay flat
        refQuad.transform.localScale = new Vector3(
            satelliteWidthMeters,
            satelliteHeightMeters,
            1f
        );

        // Apply unlit material with satellite texture
        var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        mat.mainTexture = satelliteImage;
        mat.SetFloat("_Surface", 1); // Transparent
        mat.SetFloat("_Blend", 0);
        mat.color = new Color(1, 1, 1, 0.7f); // Semi-transparent
        refQuad.GetComponent<Renderer>().material = mat;

        // Disable collider — reference only
        DestroyImmediate(refQuad.GetComponent<Collider>());

        Debug.Log($"Reference quad placed: {satelliteWidthMeters}m × {satelliteHeightMeters}m");
    }

    [ContextMenu("2. Build Water Surface")]
    public void BuildWaterSurface()
    {
        var water = GameObject.CreatePrimitive(PrimitiveType.Plane);
        water.name = "WaterSurface";
        water.transform.SetParent(transform);
        water.transform.position = new Vector3(0, waterSurfaceY, 0);
        water.transform.localScale = new Vector3(30, 1, 30); // 300m × 300m

        // Tag for acoustic processing
        var tag = water.AddComponent<AcousticMaterialTag>();
        tag.materialType = "water_surface";

        // Simple blue transparent material
        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.SetFloat("_Surface", 1);
        mat.color = new Color(0.1f, 0.3f, 0.5f, 0.3f);
        water.GetComponent<Renderer>().material = mat;

        Debug.Log("Water surface created at Y=0");
    }

    [ContextMenu("3. Build Seabed (use SeabedGenerator instead)")]
    public void BuildSeabed()
    {
        Debug.Log("SKIPPED: Flat seabed is deprecated. Use SeabedGenerator component instead.\n" +
                  "1. Create empty child 'Seabed' under LagoonWorld\n" +
                  "2. Add SeabedGenerator component\n" +
                  "3. Right-click → 'Generate Seabed'");
    }

    [ContextMenu("4. Parse CSV and Build Structures")]
    public void ParseAndBuild()
    {
        if (pointsCsv == null)
        {
            Debug.LogError("No CSV file assigned!");
            return;
        }

        ParseCSV();
        BuildFromParsedPoints();
    }

    private void ParseCSV()
    {
        parsedPoints.Clear();
        var lines = pointsCsv.text.Split('\n');

        // Detect header to find column positions
        var header = lines[0].Trim().Split(',');
        int idCol = 0, typeCol = 1, xCol = 2, zCol = 3, notesCol = 4;

        // Handle KML-generated CSV which has: point_id, type, unity_x, unity_z, lat, lng, notes
        for (int c = 0; c < header.Length; c++)
        {
            string h = header[c].Trim().ToLower();
            if (h == "point_id") idCol = c;
            else if (h == "type") typeCol = c;
            else if (h == "unity_x") xCol = c;
            else if (h == "unity_z") zCol = c;
            else if (h == "notes") notesCol = c;
        }

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

            var cols = line.Split(',');
            if (cols.Length < 4) continue;

            parsedPoints.Add(new LagoonPoint
            {
                id = cols[idCol].Trim(),
                type = cols[typeCol].Trim(),
                x = float.Parse(cols[xCol].Trim(), CultureInfo.InvariantCulture),
                z = float.Parse(cols[zCol].Trim(), CultureInfo.InvariantCulture),
                notes = cols.Length > notesCol ? cols[notesCol].Trim() : ""
            });
        }

        Debug.Log($"Parsed {parsedPoints.Count} points from CSV");
    }

    private void BuildFromParsedPoints()
    {
        // Group points by type — any type string is valid
        var grouped = new Dictionary<string, List<LagoonPoint>>();

        foreach (var pt in parsedPoints)
        {
            string key = pt.type.ToLower();
            if (!grouped.ContainsKey(key))
                grouped[key] = new List<LagoonPoint>();
            grouped[key].Add(pt);
        }

        // --- Deploy origin marker ---
        if (grouped.ContainsKey("origin"))
        {
            var originParent = new GameObject("DeployOrigin");
            originParent.transform.SetParent(transform);
            foreach (var pt in grouped["origin"])
            {
                CreateDeployMarker(pt, originParent.transform);
            }
        }

        // --- Known auto-build types ---

        // Pillars → cylinders
        if (grouped.ContainsKey("pillar"))
        {
            var parent = new GameObject("Pillars");
            parent.transform.SetParent(transform);
            foreach (var p in grouped["pillar"])
                CreatePillar(p, parent.transform);
        }

        // Ramp → angled surface from shore down to platform
        if (grouped.ContainsKey("ramp"))
        {
            var parent = new GameObject("Ramp");
            parent.transform.SetParent(transform);
            BuildRampFromPairs(grouped["ramp"], parent.transform);
        }

        // Platform → T-shaped deck with arms radiating from a center point
        if (grouped.ContainsKey("platform"))
        {
            var parent = new GameObject("Platform");
            parent.transform.SetParent(transform);
            BuildPlatform(grouped["platform"], parent.transform);
        }

        // Jetties → deck + legs from base-end pairs
        if (grouped.ContainsKey("jetty"))
        {
            var parent = new GameObject("Jetties");
            parent.transform.SetParent(transform);
            BuildJettiesFromPairs(grouped["jetty"], parent.transform);
        }

        // Piers → same construction as jetties
        if (grouped.ContainsKey("pier"))
        {
            var parent = new GameObject("Piers");
            parent.transform.SetParent(transform);
            BuildJettiesFromPairs(grouped["pier"], parent.transform);
        }

        // Barriers → wall segments from point pairs
        if (grouped.ContainsKey("barrier"))
        {
            var parent = new GameObject("Barriers");
            parent.transform.SetParent(transform);
            BuildBarriersFromPairs(grouped["barrier"], parent.transform);
        }

        // Breakwater → guide markers + connected path line if dense
        if (grouped.ContainsKey("breakwater"))
        {
            var parent = new GameObject("BreakwaterGuide");
            parent.transform.SetParent(transform);

            // Separate path vertices (from KML, ordered) from manually placed points
            var pathVerts = new List<LagoonPoint>();
            var manualVerts = new List<LagoonPoint>();

            foreach (var pt in grouped["breakwater"])
            {
                if (pt.id.StartsWith("bw_path_"))
                    pathVerts.Add(pt);
                else
                    manualVerts.Add(pt);
            }

            // Place manual markers (brown)
            foreach (var pt in manualVerts)
                CreateMarkerSphere(pt, parent.transform, new Color(0.6f, 0.4f, 0.2f));

            if (pathVerts.Count > 0)
            {
                // KML path: connect vertices with a LineRenderer
                var lineObj = new GameObject("BreakwaterPath");
                lineObj.transform.SetParent(parent.transform);

                var lr = lineObj.AddComponent<LineRenderer>();
                lr.positionCount = pathVerts.Count;
                lr.startWidth = 0.5f;
                lr.endWidth = 0.5f;
                lr.useWorldSpace = true;
                lr.loop = false;

                // Simple unlit material
                var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                mat.color = new Color(0.8f, 0.5f, 0.2f, 0.8f);
                lr.material = mat;

                for (int i = 0; i < pathVerts.Count; i++)
                {
                    var pt = pathVerts[i];
                    lr.SetPosition(i, new Vector3(pt.x, waterSurfaceY + 0.3f, pt.z));
                }

                Debug.Log($"Breakwater KML path: {pathVerts.Count} vertices connected. " +
                          "Use this as a guide to extrude the seawall mesh in ProBuilder or Blender.");
            }
            else
            {
                Debug.Log($"Placed {manualVerts.Count} breakwater guide markers. " +
                          "Trace these in ProBuilder or Blender to create the seawall mesh.");
            }
        }

        // Wall segments → same as jetties (deck + legs from base-end pairs)
        if (grouped.ContainsKey("wall"))
        {
            var parent = new GameObject("Walls");
            parent.transform.SetParent(transform);
            BuildBarriersFromPairs(grouped["wall"], parent.transform);
        }

        // --- Unknown types → labeled marker spheres ---
        var knownTypes = new HashSet<string>
        {
            "origin", "pillar", "jetty", "pier", "barrier", "breakwater",
            "wall", "reference", "ramp", "platform"
        };

        foreach (var kvp in grouped)
        {
            if (knownTypes.Contains(kvp.Key)) continue;

            var parent = new GameObject($"Markers_{kvp.Key}");
            parent.transform.SetParent(transform);
            foreach (var pt in kvp.Value)
                CreateMarkerSphere(pt, parent.transform, Color.yellow);

            Debug.Log($"Unknown type '{kvp.Key}': placed {kvp.Value.Count} marker spheres. " +
                      "Replace these with proper geometry manually.");
        }

        // Summary
        int total = parsedPoints.Count;
        Debug.Log($"Built {total} points across {grouped.Count} types: " +
                  string.Join(", ", grouped.Keys));
    }

    private void CreateDeployMarker(LagoonPoint pt, Transform parent)
    {
        // Visible marker at the sonar deployment origin
        var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.name = $"DeployPoint_{pt.id}";
        marker.transform.SetParent(parent);
        marker.transform.position = new Vector3(pt.x, waterSurfaceY + 0.5f, pt.z);
        marker.transform.localScale = Vector3.one * 0.5f;

        DestroyImmediate(marker.GetComponent<Collider>()); // Non-physical marker

        var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        mat.color = Color.green;
        marker.GetComponent<Renderer>().material = mat;
    }

    private void CreateMarkerSphere(LagoonPoint pt, Transform parent, Color color)
    {
        var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.name = $"Marker_{pt.id}";
        marker.transform.SetParent(parent);
        marker.transform.position = new Vector3(pt.x, waterSurfaceY + 0.3f, pt.z);
        marker.transform.localScale = Vector3.one * 0.4f;

        DestroyImmediate(marker.GetComponent<Collider>());

        var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        mat.color = color;
        marker.GetComponent<Renderer>().material = mat;
    }

    private void BuildBarriersFromPairs(List<LagoonPoint> points, Transform parent)
    {
        // Build wall/barrier segments from consecutive pairs
        // If points have _start/_end or _north/_south suffixes, pair them
        // Otherwise, build segments between consecutive points
        var starts = new Dictionary<string, LagoonPoint>();
        var ends = new Dictionary<string, LagoonPoint>();
        var unpaired = new List<LagoonPoint>();

        foreach (var pt in points)
        {
            string key = pt.id
                .Replace("_start", "").Replace("_end", "")
                .Replace("_north", "").Replace("_south", "")
                .Replace("_left", "").Replace("_right", "");

            if (pt.id.EndsWith("_start") || pt.id.EndsWith("_north") || pt.id.EndsWith("_left"))
                starts[key] = pt;
            else if (pt.id.EndsWith("_end") || pt.id.EndsWith("_south") || pt.id.EndsWith("_right"))
                ends[key] = pt;
            else
                unpaired.Add(pt);
        }

        // Build from matched pairs
        foreach (var kvp in starts)
        {
            if (ends.ContainsKey(kvp.Key))
                CreateBarrier(kvp.Value, ends[kvp.Key], parent);
        }

        // Build from consecutive unpaired points
        for (int i = 0; i < unpaired.Count - 1; i += 2)
        {
            CreateBarrier(unpaired[i], unpaired[i + 1], parent);
        }
    }

    private void BuildRampFromPairs(List<LagoonPoint> points, Transform parent)
    {
        // Find the two ends of the ramp
        // Supports naming: shore/water, start/end, high/low
        LagoonPoint? startEnd = null; // Shore / high end
        LagoonPoint? waterEnd = null; // Water / low end

        foreach (var pt in points)
        {
            string id = pt.id.ToLower();
            if (id.Contains("shore") || id.Contains("start") || id.Contains("high"))
                startEnd = pt;
            else if (id.Contains("water") || id.Contains("end") || id.Contains("low") || id.Contains("platform"))
                waterEnd = pt;
        }

        // Fallback: first point is shore end, second is water end
        if (startEnd == null && points.Count >= 1) startEnd = points[0];
        if (waterEnd == null && points.Count >= 2) waterEnd = points[1];

        if (startEnd == null || waterEnd == null)
        {
            Debug.LogWarning("Ramp needs at least 2 points (start/shore and end/water).");
            return;
        }

        CreateRamp(startEnd.Value, waterEnd.Value, parent);
    }

    private void CreateRamp(LagoonPoint shoreEnd, LagoonPoint waterEnd, Transform parent)
    {
        var rampObj = new GameObject("RampStructure");
        rampObj.transform.SetParent(parent);

        // Shore end is higher, water/platform end is lower
        Vector3 shorePos = new Vector3(shoreEnd.x, rampShoreHeight, shoreEnd.z);
        Vector3 waterPos = new Vector3(waterEnd.x, rampPlatformHeight, waterEnd.z);
        Vector3 midpoint = (shorePos + waterPos) / 2f;

        float horizDist = Vector3.Distance(
            new Vector3(shoreEnd.x, 0, shoreEnd.z),
            new Vector3(waterEnd.x, 0, waterEnd.z)
        );
        float heightDiff = rampShoreHeight - rampPlatformHeight;
        float slopeLength = Mathf.Sqrt(horizDist * horizDist + heightDiff * heightDiff);

        // Rotation: yaw to face the right direction, pitch for the slope
        float yaw = Mathf.Atan2(waterPos.x - shorePos.x, waterPos.z - shorePos.z) * Mathf.Rad2Deg;
        float pitch = Mathf.Atan2(heightDiff, horizDist) * Mathf.Rad2Deg;

        // Ramp deck (angled cube)
        var deck = GameObject.CreatePrimitive(PrimitiveType.Cube);
        deck.name = "RampDeck";
        deck.transform.SetParent(rampObj.transform);
        deck.transform.position = midpoint;
        deck.transform.rotation = Quaternion.Euler(pitch, yaw, 0);
        deck.transform.localScale = new Vector3(rampWidth, rampThickness, slopeLength);

        var deckTag = deck.AddComponent<AcousticMaterialTag>();
        deckTag.materialType = "concrete";

        var deckMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        deckMat.color = new Color(0.65f, 0.63f, 0.60f, 1f);
        deck.GetComponent<Renderer>().material = deckMat;

        // Ramp underside support legs (at shore end and water end)
        float shoreUnderHeight = rampShoreHeight - seabedDepth;
        CreateRampLeg(shorePos, shoreUnderHeight, rampObj.transform);

        float waterUnderHeight = rampPlatformHeight - seabedDepth;
        CreateRampLeg(waterPos, waterUnderHeight, rampObj.transform);

        // Intermediate legs
        int midLegs = Mathf.Max(0, Mathf.FloorToInt(horizDist / platformLegSpacing) - 1);
        for (int i = 1; i <= midLegs; i++)
        {
            float t = (float)i / (midLegs + 1);
            Vector3 legXZ = Vector3.Lerp(shorePos, waterPos, t);
            float legTopY = Mathf.Lerp(rampShoreHeight, rampPlatformHeight, t);
            float legH = legTopY - seabedDepth;
            CreateRampLeg(new Vector3(legXZ.x, legTopY, legXZ.z), legH, rampObj.transform);
        }

        Debug.Log($"Ramp built: {slopeLength:F1}m long, " +
                  $"{heightDiff:F1}m drop, slope {Mathf.Atan2(heightDiff, horizDist) * Mathf.Rad2Deg:F1}°");
    }

    private void CreateRampLeg(Vector3 topPos, float height, Transform parent)
    {
        var leg = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        leg.name = "RampLeg";
        leg.transform.SetParent(parent);
        leg.transform.position = new Vector3(topPos.x, topPos.y - height / 2f, topPos.z);
        leg.transform.localScale = new Vector3(
            platformLegRadius * 2,
            height / 2f,
            platformLegRadius * 2
        );

        var tag = leg.AddComponent<AcousticMaterialTag>();
        tag.materialType = "concrete_cylinder";

        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.color = new Color(0.5f, 0.5f, 0.5f, 1f);
        leg.GetComponent<Renderer>().material = mat;
    }

    private void BuildPlatform(List<LagoonPoint> points, Transform parent)
    {
        // Platform uses a hub-and-spoke model:
        //   - One point with "center" in its id is the hub
        //   - All other points are arm endpoints
        //   - A deck span is built from center to each endpoint

        LagoonPoint? center = null;
        var arms = new List<LagoonPoint>();

        foreach (var pt in points)
        {
            if (pt.id.Contains("center") || pt.id.Contains("junction") || pt.id.Contains("hub"))
                center = pt;
            else
                arms.Add(pt);
        }

        if (center == null)
        {
            Debug.LogWarning("Platform needs a point with 'center' in its id for the hub.");
            // Fallback: use first point as center
            if (points.Count >= 1)
            {
                center = points[0];
                arms = points.GetRange(1, points.Count - 1);
            }
            else return;
        }

        // Build a deck span from center to each arm tip
        var platformObj = new GameObject("TPlatform");
        platformObj.transform.SetParent(parent);

        foreach (var arm in arms)
        {
            CreatePlatformArm(center.Value, arm, platformObj.transform);
        }

        Debug.Log($"T-Platform built: center at ({center.Value.x:F1}, {center.Value.z:F1}) " +
                  $"with {arms.Count} arms");
    }

    private void CreatePlatformArm(LagoonPoint center, LagoonPoint tip, Transform parent)
    {
        string armName = tip.id.Replace("platform_", "");
        var armObj = new GameObject($"Arm_{armName}");
        armObj.transform.SetParent(parent);

        Vector3 centerPos = new Vector3(center.x, platformDeckHeight, center.z);
        Vector3 tipPos = new Vector3(tip.x, platformDeckHeight, tip.z);
        Vector3 midpoint = (centerPos + tipPos) / 2f;
        float length = Vector3.Distance(centerPos, tipPos);
        float angle = Mathf.Atan2(tipPos.x - centerPos.x, tipPos.z - centerPos.z) * Mathf.Rad2Deg;

        // Deck slab
        var deck = GameObject.CreatePrimitive(PrimitiveType.Cube);
        deck.name = $"Deck_{armName}";
        deck.transform.SetParent(armObj.transform);
        deck.transform.position = midpoint;
        deck.transform.rotation = Quaternion.Euler(0, angle, 0);
        deck.transform.localScale = new Vector3(
            platformDeckWidth,
            platformDeckThickness,
            length
        );

        var deckTag = deck.AddComponent<AcousticMaterialTag>();
        deckTag.materialType = "concrete";

        var deckMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        deckMat.color = new Color(0.7f, 0.7f, 0.7f, 1f);
        deck.GetComponent<Renderer>().material = deckMat;

        // Support legs along this arm
        int legCount = Mathf.Max(2, Mathf.FloorToInt(length / platformLegSpacing) + 1);
        float legHeight = platformDeckHeight - seabedDepth;

        for (int i = 0; i < legCount; i++)
        {
            float t = (float)i / (legCount - 1);
            Vector3 legPos = Vector3.Lerp(centerPos, tipPos, t);
            legPos.y = seabedDepth + legHeight / 2f;

            var leg = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            leg.name = $"Leg_{armName}_{i}";
            leg.transform.SetParent(armObj.transform);
            leg.transform.position = legPos;
            leg.transform.localScale = new Vector3(
                platformLegRadius * 2,
                legHeight / 2f,
                platformLegRadius * 2
            );

            var legTag = leg.AddComponent<AcousticMaterialTag>();
            legTag.materialType = "concrete_cylinder";

            var legMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            legMat.color = new Color(0.5f, 0.5f, 0.5f, 1f);
            leg.GetComponent<Renderer>().material = legMat;
        }
    }

    private void CreatePillar(LagoonPoint pt, Transform parent)
    {
        float height = Mathf.Abs(seabedDepth) + pillarHeightAboveWater;

        var pillar = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pillar.name = $"Pillar_{pt.id}";
        pillar.transform.SetParent(parent);
        pillar.transform.position = new Vector3(
            pt.x,
            seabedDepth + height / 2f,
            pt.z
        );
        pillar.transform.localScale = new Vector3(
            pillarRadius * 2,
            height / 2f, // Cylinder height is 2× scale.y
            pillarRadius * 2
        );

        var tag = pillar.AddComponent<AcousticMaterialTag>();
        tag.materialType = "concrete_cylinder";

        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.color = new Color(0.6f, 0.6f, 0.6f, 1f);
        pillar.GetComponent<Renderer>().material = mat;
    }

    private void BuildJettiesFromPairs(List<LagoonPoint> points, Transform parent)
    {
        // Find base-end pairs. Supports suffixes:
        //   _base/_end  (e.g. jetty_stem_base + jetty_stem_end)
        //   _start/_end (e.g. jetty_base_start + jetty_base_end)
        var starts = new Dictionary<string, LagoonPoint>();
        var ends = new Dictionary<string, LagoonPoint>();

        foreach (var pt in points)
        {
            // Strip the suffix to get the pairing key
            string id = pt.id;
            string key = id;
            bool isStart = false;
            bool isEnd = false;

            if (id.EndsWith("_base"))   { key = id.Substring(0, id.Length - 5); isStart = true; }
            else if (id.EndsWith("_start")) { key = id.Substring(0, id.Length - 6); isStart = true; }
            else if (id.EndsWith("_end"))   { key = id.Substring(0, id.Length - 4); isEnd = true; }

            if (isStart) starts[key] = pt;
            else if (isEnd) ends[key] = pt;
        }

        foreach (var kvp in starts)
        {
            if (!ends.ContainsKey(kvp.Key)) continue;
            CreateJettySpan(kvp.Key, kvp.Value, ends[kvp.Key], parent);
        }

        // Log unpaired ends
        foreach (var kvp in ends)
        {
            if (!starts.ContainsKey(kvp.Key))
            {
                Debug.LogWarning($"Jetty endpoint '{kvp.Value.id}' has no matching " +
                                 $"_base or _start point (expected '{kvp.Key}_base' or '{kvp.Key}_start'). " +
                                 "Add the missing start point to build this span.");
            }
        }
    }

    private void CreateJettySpan(string name, LagoonPoint basePt, LagoonPoint endPt, Transform parent)
    {
        var jettyObj = new GameObject($"Jetty_{name}");
        jettyObj.transform.SetParent(parent);

        // Calculate span
        Vector3 basePos = new Vector3(basePt.x, jettyDeckHeight, basePt.z);
        Vector3 endPos = new Vector3(endPt.x, jettyDeckHeight, endPt.z);
        Vector3 midpoint = (basePos + endPos) / 2f;
        float length = Vector3.Distance(basePos, endPos);
        float angle = Mathf.Atan2(endPos.x - basePos.x, endPos.z - basePos.z) * Mathf.Rad2Deg;

        // Deck
        var deck = GameObject.CreatePrimitive(PrimitiveType.Cube);
        deck.name = "Deck";
        deck.transform.SetParent(jettyObj.transform);
        deck.transform.position = midpoint;
        deck.transform.rotation = Quaternion.Euler(0, angle, 0);
        deck.transform.localScale = new Vector3(
            jettyDeckWidth,
            jettyDeckThickness,
            length
        );

        var deckTag = deck.AddComponent<AcousticMaterialTag>();
        deckTag.materialType = "concrete";

        var deckMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        deckMat.color = new Color(0.7f, 0.7f, 0.7f, 1f);
        deck.GetComponent<Renderer>().material = deckMat;

        // Support legs
        int legCount = Mathf.Max(2, Mathf.FloorToInt(length / jettyLegSpacing) + 1);
        float legHeight = jettyDeckHeight - seabedDepth;

        for (int i = 0; i < legCount; i++)
        {
            float t = (float)i / (legCount - 1);
            Vector3 legPos = Vector3.Lerp(basePos, endPos, t);
            legPos.y = seabedDepth + legHeight / 2f;

            var leg = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            leg.name = $"Leg_{i}";
            leg.transform.SetParent(jettyObj.transform);
            leg.transform.position = legPos;
            leg.transform.localScale = new Vector3(
                jettyLegRadius * 2,
                legHeight / 2f,
                jettyLegRadius * 2
            );

            var legTag = leg.AddComponent<AcousticMaterialTag>();
            legTag.materialType = "concrete_cylinder";

            var legMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            legMat.color = new Color(0.5f, 0.5f, 0.5f, 1f);
            leg.GetComponent<Renderer>().material = legMat;
        }
    }

    private void CreateBarrier(LagoonPoint pt1, LagoonPoint pt2, Transform parent)
    {
        Vector3 pos1 = new Vector3(pt1.x, 0, pt1.z);
        Vector3 pos2 = new Vector3(pt2.x, 0, pt2.z);
        Vector3 midpoint = (pos1 + pos2) / 2f;
        float length = Vector3.Distance(pos1, pos2);
        float angle = Mathf.Atan2(pos2.x - pos1.x, pos2.z - pos1.z) * Mathf.Rad2Deg;
        float height = Mathf.Abs(seabedDepth) + breakwaterHeight;

        var barrier = GameObject.CreatePrimitive(PrimitiveType.Cube);
        barrier.name = "GateBarrier";
        barrier.transform.SetParent(parent);
        barrier.transform.position = new Vector3(
            midpoint.x,
            seabedDepth + height / 2f,
            midpoint.z
        );
        barrier.transform.rotation = Quaternion.Euler(0, angle, 0);
        barrier.transform.localScale = new Vector3(0.3f, height, length);

        var tag = barrier.AddComponent<AcousticMaterialTag>();
        tag.materialType = "concrete_wall";

        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.color = new Color(0.55f, 0.55f, 0.55f, 1f);
        barrier.GetComponent<Renderer>().material = mat;
    }

    [ContextMenu("5. Remove Reference Layer")]
    public void RemoveReferenceLayer()
    {
        var refQuad = transform.Find("SatelliteReference");
        if (refQuad != null)
        {
            DestroyImmediate(refQuad.gameObject);
            Debug.Log("Satellite reference removed.");
        }
    }

    [ContextMenu("Clear All Built Objects")]
    public void ClearAll()
    {
        while (transform.childCount > 0)
        {
            DestroyImmediate(transform.GetChild(0).gameObject);
        }
        Debug.Log("All built objects cleared.");
    }
}
