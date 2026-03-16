using UnityEngine;

[CreateAssetMenu(fileName = "SonarConfig", menuName = "Sonar/Config")]
public class SonarConfig : ScriptableObject
{
    [Header("Beam Configuration")]
    public int numberOfBeams = 256;
    public float swathAngleDeg = 150f;       // hFOV

    [Header("Beam Pattern")]
    public float verticalFOVDeg = 25f;
    public int subBeamsH = 3;     // horizontal samples within beam width
    public int subBeamsV = 3;     // vertical samples within vFOV
                                  // Total sub-beams per beam = subBeamsH * subBeamsV

    [Header("Range Configuration")]
    public float maxRange = 100f;
    public int numberOfRangeBins = 650;

    [Header("Timing")]
    public float pingRateHz = 15f;

    [Header("Beam Shape")]
    public float beamWidthAlongTrackDeg = 1.5f;

    [Header("Mounting")]
    public float mountTiltDeg = 0f;
    public float mountRollDeg = 0f;

    // Derived
    public float AngularResolutionDeg => swathAngleDeg / numberOfBeams;
    public float RangeBinSize => maxRange / numberOfRangeBins;
    public int SubBeamsPerBeam => subBeamsH * subBeamsV;
    public int TotalRays => numberOfBeams * SubBeamsPerBeam;
}