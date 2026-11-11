using UnityEngine;

public class AcousticProperties : MonoBehaviour
{
    [Range(0f, 1f)]
    public float reflectivity = 0.8f; // 1.0 = perfect reflector
    
    [Range(0f, 1f)]
    public float scatteringCoefficient = 0.2f;
    
    // Indices must match processor/config/materials.yaml
    public enum MaterialType
    {
        Default = 0,
        Steel = 1,
        Concrete = 2,
        Sand = 3,
        Mud = 4,
        Rock = 5,
        Wood = 6,
        Rubber = 7,
        WaterSurface = 8,   // air-water interface seen from below (near-perfect mirror)
    }
    public MaterialType materialType = MaterialType.Concrete;
}