using UnityEngine;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Generates a lagoon seabed mesh with an asymmetric depth profile:
///   - Shallow at breakwater edges (taper)
///   - Deepest trough offset toward the breakwater wall
///   - Intermediate depth at the pier/deploy area
///   - A sill (rise) at the gate/mouth opening
///
/// Uses the breakwater LineRenderer as the lagoon boundary.
/// Run LagoonSceneBuilder first, then "Generate Seabed" from context menu.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class SeabedGenerator : MonoBehaviour
{
    [Header("Depth Profile")]
    [Tooltip("Depth at the breakwater edges (meters below water surface)")]
    public float edgeDepth = 2f;

    [Tooltip("Maximum depth at the trough (meters below water surface)")]
    public float troughDepth = 6f;

    [Tooltip("Depth at the deploy origin / pier area (meters below water surface)")]
    public float pierDepth = 4f;

    [Tooltip("How sharply depth increases from edge to trough (1=linear, 2=gradual, 0.5=steep)")]
    [Range(0.3f, 3f)]
    public float bowlCurvature = 1.5f;

    [Header("Trough Position (Unity XZ coordinates)")]
    [Tooltip("X coordinate of deepest point — halfway between pillar_4 and breakwater wall")]
    public float troughX = -40f;

    [Tooltip("Z coordinate of deepest point")]
    public float troughZ = 82f;

    [Tooltip("How wide the trough zone is (meters). Larger = broader deep area.")]
    public float troughRadius = 40f;

    [Header("Gate Sill (rise at the mouth opening)")]
    [Tooltip("Depth at the gate sill (shallower than surroundings)")]
    public float sillDepth = 3f;

    [Tooltip("How far the sill effect extends into the lagoon (meters)")]
    public float sillReach = 25f;

    [Tooltip("Position of gate_north (Unity XZ). Read from scene summary.")]
    public Vector2 gateNorth = new Vector2(18.41f, 135.43f);

    [Tooltip("Position of gate_south (Unity XZ). Read from scene summary.")]
    public Vector2 gateSouth = new Vector2(52.05f, 8.63f);

    [Header("Shore Transition")]
    [Tooltip("Depth at the very edge of shore (meters below water)")]
    public float shoreDepth = 0.3f;

    [Tooltip("Width of the shore-to-seabed transition outside the breakwater (meters)")]
    public float shoreTransitionWidth = 8f;

    [Header("Mesh Resolution")]
    [Tooltip("Grid cell size in meters — smaller = smoother, heavier")]
    public float gridCellSize = 2f;

    [Tooltip("Extend mesh beyond breakwater by this many meters")]
    public float boundaryPadding = 15f;

    [Header("Data Source")]
    [Tooltip("Optional: assign breakwater_vertices.csv directly")]
    public TextAsset breakwaterCsv;

    // Internal state
    private List<Vector2> boundaryPoints = new List<Vector2>();
    private Vector2 gateMidpoint;
    private Vector2 gateDir;
    private float gateLength;

    [ContextMenu("Generate Seabed")]
    public void GenerateSeabed()
    {
        if (!CollectBoundaryPoints())
        {
            Debug.LogError("No breakwater boundary found. Run LagoonSceneBuilder first.");
            return;
        }

        // Gate geometry
        gateMidpoint = (gateNorth + gateSouth) / 2f;
        gateDir = (gateSouth - gateNorth).normalized;
        gateLength = Vector2.Distance(gateNorth, gateSouth);

        // Mesh bounds
        float minX = boundaryPoints.Min(p => p.x) - boundaryPadding;
        float maxX = boundaryPoints.Max(p => p.x) + boundaryPadding;
        float minZ = boundaryPoints.Min(p => p.y) - boundaryPadding;
        float maxZ = boundaryPoints.Max(p => p.y) + boundaryPadding;

        int gridW = Mathf.CeilToInt((maxX - minX) / gridCellSize) + 1;
        int gridH = Mathf.CeilToInt((maxZ - minZ) / gridCellSize) + 1;

        var vertices = new Vector3[gridW * gridH];
        var uvs = new Vector2[gridW * gridH];
        var colors = new Color[gridW * gridH]; // Depth visualization

        float actualMaxDepth = 0f;

        for (int z = 0; z < gridH; z++)
        {
            for (int x = 0; x < gridW; x++)
            {
                int idx = z * gridW + x;
                float worldX = minX + x * gridCellSize;
                float worldZ = minZ + z * gridCellSize;

                float depth = CalculateDepth(worldX, worldZ);
                vertices[idx] = new Vector3(worldX, -depth, worldZ);
                uvs[idx] = new Vector2((float)x / (gridW - 1), (float)z / (gridH - 1));

                // Color: lighter = shallow, darker = deep
                float t = Mathf.InverseLerp(0, troughDepth, depth);
                colors[idx] = Color.Lerp(
                    new Color(0.82f, 0.75f, 0.58f), // Sandy shallow
                    new Color(0.35f, 0.32f, 0.25f), // Dark deep
                    t
                );

                if (depth > actualMaxDepth) actualMaxDepth = depth;
            }
        }

        // Triangles
        var triangles = new List<int>();
        for (int z = 0; z < gridH - 1; z++)
        {
            for (int x = 0; x < gridW - 1; x++)
            {
                int tl = z * gridW + x;
                int tr = tl + 1;
                int bl = (z + 1) * gridW + x;
                int br = bl + 1;

                triangles.Add(tl); triangles.Add(bl); triangles.Add(tr);
                triangles.Add(tr); triangles.Add(bl); triangles.Add(br);
            }
        }

        // Build mesh
        var mesh = new Mesh();
        mesh.name = "LagoonSeabed";
        if (vertices.Length > 65535)
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

        mesh.vertices = vertices;
        mesh.triangles = triangles.ToArray();
        mesh.uv = uvs;
        mesh.colors = colors;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        // Apply
        GetComponent<MeshFilter>().mesh = mesh;

        var mr = GetComponent<MeshRenderer>();
        if (mr.sharedMaterial == null)
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.color = new Color(0.72f, 0.65f, 0.48f);
            mr.material = mat;
        }

        var mc = GetComponent<MeshCollider>();
        if (mc == null) mc = gameObject.AddComponent<MeshCollider>();
        mc.sharedMesh = mesh;

        var tag = GetComponent<AcousticMaterialTag>();
        if (tag == null) tag = gameObject.AddComponent<AcousticMaterialTag>();
        tag.materialType = "sand_mud";

        Debug.Log($"Seabed generated: {gridW}x{gridH} grid ({vertices.Length} verts), " +
                  $"depth range: {shoreDepth:F1}m — {actualMaxDepth:F1}m, " +
                  $"trough at ({troughX:F0}, {troughZ:F0})");
    }

    private float CalculateDepth(float worldX, float worldZ)
    {
        Vector2 point = new Vector2(worldX, worldZ);

        // 1. Distance to nearest boundary segment
        float distToBoundary = DistToBoundary(point);
        bool inside = IsInsidePolygon(point, boundaryPoints);

        // Outside the lagoon — slope up to shore
        if (!inside)
        {
            float t = Mathf.Clamp01(distToBoundary / shoreTransitionWidth);
            return Mathf.Lerp(edgeDepth, shoreDepth, t);
        }

        // 2. Inside the lagoon — asymmetric depth profile

        // How far inside are we? (normalized 0=edge, 1=deep inside)
        float maxInternalDist = MaxInternalDistance();
        float inwardFactor = Mathf.Clamp01(distToBoundary / maxInternalDist);
        inwardFactor = Mathf.Pow(inwardFactor, bowlCurvature);

        // 3. Proximity to the deepest trough
        Vector2 troughPos = new Vector2(troughX, troughZ);
        float distToTrough = Vector2.Distance(point, troughPos);
        // Gaussian-like falloff: 1.0 at trough, fading to 0 at troughRadius
        float troughFactor = Mathf.Exp(-(distToTrough * distToTrough) /
                                        (2f * troughRadius * troughRadius));

        // 4. Gate sill effect — reduce depth near the gate opening
        float distToGateLine = DistToSegment(point, gateNorth, gateSouth);
        float sillFactor = 1f; // 1 = no sill effect
        if (distToGateLine < sillReach)
        {
            float sillT = 1f - Mathf.Clamp01(distToGateLine / sillReach);
            sillT = sillT * sillT; // Quadratic falloff — strongest right at the gate
            sillFactor = 1f - sillT;
        }

        // 5. Combine:
        // - Base: edge→pier depth based on how far inside we are
        // - Trough: additional deepening near the trough position
        // - Sill: reduction near the gate

        float baseDepth = Mathf.Lerp(edgeDepth, pierDepth, inwardFactor);
        float troughBonus = (troughDepth - pierDepth) * troughFactor * inwardFactor;
        float depth = baseDepth + troughBonus;

        // Apply sill: lerp toward sillDepth near the gate
        depth = Mathf.Lerp(sillDepth, depth, sillFactor);

        // Clamp to valid range
        return Mathf.Clamp(depth, shoreDepth, troughDepth);
    }

    // ---- Geometry utilities ----

    private float MaxInternalDistance()
    {
        // Centroid's distance to nearest boundary — approximates max internal dist
        Vector2 centroid = Vector2.zero;
        foreach (var p in boundaryPoints) centroid += p;
        centroid /= boundaryPoints.Count;

        float minDist = float.MaxValue;
        for (int i = 0; i < boundaryPoints.Count; i++)
        {
            int next = (i + 1) % boundaryPoints.Count;
            float d = DistToSegment(centroid, boundaryPoints[i], boundaryPoints[next]);
            if (d < minDist) minDist = d;
        }
        return minDist;
    }

    private float DistToBoundary(Vector2 point)
    {
        float minDist = float.MaxValue;
        for (int i = 0; i < boundaryPoints.Count; i++)
        {
            int next = (i + 1) % boundaryPoints.Count;
            float d = DistToSegment(point, boundaryPoints[i], boundaryPoints[next]);
            if (d < minDist) minDist = d;
        }
        return minDist;
    }

    private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSq = ab.sqrMagnitude;
        if (lenSq < 0.001f) return Vector2.Distance(p, a);
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq);
        return Vector2.Distance(p, a + t * ab);
    }

    private static bool IsInsidePolygon(Vector2 point, List<Vector2> polygon)
    {
        bool inside = false;
        int count = polygon.Count;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            if (((polygon[i].y > point.y) != (polygon[j].y > point.y)) &&
                (point.x < (polygon[j].x - polygon[i].x) * (point.y - polygon[i].y) /
                 (polygon[j].y - polygon[i].y) + polygon[i].x))
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private bool CollectBoundaryPoints()
    {
        boundaryPoints.Clear();

        // Look for BreakwaterGuide LineRenderer among siblings/children
        Transform searchRoot = transform.parent ?? transform;
        var guide = FindChildRecursive(searchRoot, "BreakwaterGuide");

        if (guide != null)
        {
            var lr = guide.GetComponentInChildren<LineRenderer>();
            if (lr != null && lr.positionCount > 2)
            {
                for (int i = 0; i < lr.positionCount; i++)
                {
                    Vector3 pos = lr.GetPosition(i);
                    boundaryPoints.Add(new Vector2(pos.x, pos.z));
                }
                Debug.Log($"Boundary: {boundaryPoints.Count} points from LineRenderer");
                return true;
            }

            // Fallback: marker positions
            foreach (Transform child in guide)
            {
                boundaryPoints.Add(new Vector2(child.position.x, child.position.z));
            }
            if (boundaryPoints.Count > 2)
            {
                Debug.Log($"Boundary: {boundaryPoints.Count} points from markers");
                return true;
            }
        }

        // Fallback: CSV
        if (breakwaterCsv != null)
        {
            var lines = breakwaterCsv.text.Split('\n');
            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;
                var cols = line.Split(',');
                if (cols.Length >= 3)
                {
                    float x = float.Parse(cols[1].Trim(),
                        System.Globalization.CultureInfo.InvariantCulture);
                    float z = float.Parse(cols[2].Trim(),
                        System.Globalization.CultureInfo.InvariantCulture);
                    boundaryPoints.Add(new Vector2(x, z));
                }
            }
            if (boundaryPoints.Count > 2)
            {
                Debug.Log($"Boundary: {boundaryPoints.Count} points from CSV");
                return true;
            }
        }

        return false;
    }

    private static Transform FindChildRecursive(Transform parent, string name)
    {
        for (int i = 0; i < parent.childCount; i++)
        {
            var child = parent.GetChild(i);
            if (child.name == name) return child;
            var found = FindChildRecursive(child, name);
            if (found != null) return found;
        }
        return null;
    }

    [ContextMenu("Clear Seabed")]
    public void ClearSeabed()
    {
        var mf = GetComponent<MeshFilter>();
        if (mf != null) mf.mesh = null;
        var mc = GetComponent<MeshCollider>();
        if (mc != null) mc.sharedMesh = null;
        Debug.Log("Seabed cleared.");
    }
}
