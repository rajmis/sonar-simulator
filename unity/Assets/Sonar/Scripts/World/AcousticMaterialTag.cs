using UnityEngine;

/// <summary>
/// Tags a GameObject with an acoustic material type for sonar simulation.
/// The materialType string maps to entries in acoustic_materials.yaml,
/// which the Python-side processing uses to look up reflectivity,
/// scattering, and other acoustic properties.
///
/// Attach this to every mesh in the lagoon scene that the sonar
/// raycaster can hit.
/// </summary>
public class AcousticMaterialTag : MonoBehaviour
{
    [Tooltip("Material type key matching acoustic_materials.yaml")]
    public string materialType = "concrete";

    [Tooltip("Optional override: reflectivity (0-1). -1 means use YAML default.")]
    [Range(-1f, 1f)]
    public float reflectivityOverride = -1f;

    [Tooltip("Optional override: scattering coefficient (0-1). -1 means use YAML default.")]
    [Range(-1f, 1f)]
    public float scatteringOverride = -1f;

    [Tooltip("Optional notes about this specific object")]
    [TextArea(2, 4)]
    public string notes = "";

    /// <summary>
    /// Returns the material type string for this object.
    /// Used by the sonar raycaster when building hit data packets.
    /// </summary>
    public string GetMaterialType() => materialType;

    /// <summary>
    /// Returns true if this object has per-instance overrides
    /// that should take precedence over the YAML lookup.
    /// </summary>
    public bool HasOverrides() => reflectivityOverride >= 0 || scatteringOverride >= 0;

    /// <summary>
    /// Packs this tag's data into a format suitable for the
    /// Unity→Python hit data export.
    /// </summary>
    public string ToHitDataString()
    {
        if (HasOverrides())
        {
            return $"{materialType}|r={reflectivityOverride:F3}|s={scatteringOverride:F3}";
        }
        return materialType;
    }

    private void OnDrawGizmosSelected()
    {
        // Show material type label in Scene view when selected
        #if UNITY_EDITOR
        var style = new GUIStyle();
        style.normal.textColor = GetMaterialColor();
        style.fontSize = 12;
        style.fontStyle = FontStyle.Bold;
        UnityEditor.Handles.Label(
            transform.position + Vector3.up * 0.5f,
            $"[{materialType}]",
            style
        );
        #endif
    }

    private Color GetMaterialColor()
    {
        return materialType switch
        {
            "sand_mud" => new Color(0.9f, 0.8f, 0.5f),
            "rock_rubble" => new Color(0.6f, 0.5f, 0.4f),
            "concrete" => Color.gray,
            "concrete_cylinder" => Color.gray,
            "concrete_wall" => new Color(0.7f, 0.7f, 0.7f),
            "metal" => Color.cyan,
            "wood" => new Color(0.6f, 0.4f, 0.2f),
            "water_surface" => Color.blue,
            "coral_rubble" => new Color(1f, 0.6f, 0.4f),
            _ => Color.white
        };
    }
}
