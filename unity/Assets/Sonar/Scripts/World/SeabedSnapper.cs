using UnityEngine;

/// <summary>
/// Snaps objects to the seabed mesh surface using raycasting downward.
/// Adjusts pillar heights so they extend from the seabed up to their
/// configured height above water.
///
/// Usage: Attach to LagoonWorld, assign the Seabed object, then run
/// "Snap All Pillars to Seabed" from the context menu.
/// </summary>
public class SeabedSnapper : MonoBehaviour
{
    [Tooltip("The Seabed GameObject with a MeshCollider")]
    public GameObject seabedObject;

    [Tooltip("How far above water surface pillar tops should extend")]
    public float pillarTopAboveWater = 0.5f;

    [Tooltip("Water surface Y level")]
    public float waterSurfaceY = 0f;

    [ContextMenu("Snap All Pillars to Seabed")]
    public void SnapAllPillars()
    {
        if (seabedObject == null)
        {
            Debug.LogError("Assign the Seabed GameObject first.");
            return;
        }

        var seabedCollider = seabedObject.GetComponent<MeshCollider>();
        if (seabedCollider == null)
        {
            Debug.LogError("Seabed object needs a MeshCollider.");
            return;
        }

        // Find the Pillars parent
        var pillarsParent = transform.Find("Pillars");
        if (pillarsParent == null)
        {
            Debug.LogError("No 'Pillars' child found under " + gameObject.name);
            return;
        }

        int snapped = 0;
        foreach (Transform pillar in pillarsParent)
        {
            float seabedY = GetSeabedY(pillar.position.x, pillar.position.z, seabedCollider);

            // Pillar is a Unity cylinder: height = scale.y * 2
            float totalHeight = (waterSurfaceY + pillarTopAboveWater) - seabedY;
            float halfHeight = totalHeight / 2f;
            float centerY = seabedY + halfHeight;

            // Preserve XZ scale (radius), adjust Y scale (height)
            Vector3 scale = pillar.localScale;
            scale.y = halfHeight; // Cylinder height = 2 * scale.y
            pillar.localScale = scale;

            // Reposition center
            Vector3 pos = pillar.position;
            pos.y = centerY;
            pillar.position = pos;

            snapped++;
        }

        Debug.Log($"Snapped {snapped} pillars to seabed.");
    }

    [ContextMenu("Snap Jetty Legs to Seabed")]
    public void SnapJettyLegs()
    {
        if (seabedObject == null)
        {
            Debug.LogError("Assign the Seabed GameObject first.");
            return;
        }

        var seabedCollider = seabedObject.GetComponent<MeshCollider>();
        if (seabedCollider == null)
        {
            Debug.LogError("Seabed object needs a MeshCollider.");
            return;
        }

        int snapped = 0;
        // Search through Jetties, Piers, Ramp for any Leg objects
        string[] parents = { "Jetties", "Piers", "Ramp", "Platform" };
        foreach (string parentName in parents)
        {
            var parent = transform.Find(parentName);
            if (parent == null) continue;

            foreach (var leg in parent.GetComponentsInChildren<Transform>())
            {
                if (!leg.name.Contains("Leg") && !leg.name.Contains("RampLeg")) continue;

                float seabedY = GetSeabedY(leg.position.x, leg.position.z, seabedCollider);

                // Leg top should stay where it is (supporting the deck)
                float legTopY = leg.position.y + leg.localScale.y; // Cylinder top
                float totalHeight = legTopY - seabedY;
                float halfHeight = totalHeight / 2f;

                Vector3 scale = leg.localScale;
                scale.y = halfHeight;
                leg.localScale = scale;

                Vector3 pos = leg.position;
                pos.y = seabedY + halfHeight;
                leg.position = pos;

                snapped++;
            }
        }

        Debug.Log($"Snapped {snapped} jetty/ramp legs to seabed.");
    }

    private float GetSeabedY(float worldX, float worldZ, MeshCollider collider)
    {
        // Raycast downward from above to find the seabed surface
        Ray ray = new Ray(new Vector3(worldX, 100f, worldZ), Vector3.down);

        if (collider.Raycast(ray, out RaycastHit hit, 200f))
        {
            return hit.point.y;
        }

        // Fallback: return a default depth
        Debug.LogWarning($"Seabed raycast missed at ({worldX:F1}, {worldZ:F1}). Using -4m fallback.");
        return -4f;
    }
}
