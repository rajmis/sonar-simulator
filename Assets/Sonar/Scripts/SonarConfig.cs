using UnityEngine;

[CreateAssetMenu(fileName = "SonarConfig", menuName = "Sonar/Config")]
public class SonarConfig : ScriptableObject
{
    [Header("Beam Configuration")]
    public int numberOfBeams = 256;
    public float swathAngleDeg = 150f;

    [Header("Range Configuration")]
    public float maxRange = 100f;
    public int numberOfRangeBins = 650;  // fixed by hardware

    [Header("Timing")]
    public float pingRateHz = 15f;

    [Header("Beam Shape")]
    public float beamWidthAlongTrackDeg = 1.5f;

    [Header("Mounting")]
    public float mountTiltDeg = 0f;
    public float mountRollDeg = 0f;

    // Derived
    public float AngularResolutionDeg => swathAngleDeg / numberOfBeams;
    public float RangeBinSize => maxRange / numberOfRangeBins;  // ~0.1538 m per bin
}