using UnityEngine;

public class AcousticProperties : MonoBehaviour
{
    [Range(0f, 1f)]
    public float reflectivity = 0.8f; // 1.0 = perfect reflector
    
    [Range(0f, 1f)]
    public float scatteringCoefficient = 0.2f;
    
    public enum MaterialType { Steel, Concrete, Wood, Sand, Mud, Rock }
    public MaterialType materialType = MaterialType.Concrete;
}