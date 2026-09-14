using UnityEngine;

[CreateAssetMenu(fileName = "SonarConfig", menuName = "Sonar/Config")]
public class SonarConfig : ScriptableObject
{
    [Header("Beam Configuration")]
    public int numberOfBeams = 256;
    public float swathAngleDeg = 150f;       // hFOV

    [Header("Beam Pattern")]
    public float verticalFOVDeg = 25f;
    public int subBeamsH = 5;     // horizontal samples within beam width
    public int subBeamsV = 9;     // vertical samples within vFOV
                                  // Total sub-beams per beam = subBeamsH * subBeamsV

    [Header("Depth-Map Capture")]
    public int azimuthSamples = 512;      // grid columns across swath + padding
    public int elevationSamples = 128;    // grid rows across vFOV + padding
    public float capturePaddingDeg = 2f;  // extra margin per side, used by Python for normals

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

    [Header("Acoustics (used by Python processor)")]
    public float speedOfSound = 1500f;
    public float sourceLevel_dB = 220f;
    public float pulseLength_s = 0.0001f;
    public float frequency_Hz = 400000f;

    // Derived
    public float AngularResolutionDeg => swathAngleDeg / numberOfBeams;
    public float RangeBinSize => maxRange / numberOfRangeBins;
    public int SubBeamsPerBeam => subBeamsH * subBeamsV;
    public int TotalRays => numberOfBeams * SubBeamsPerBeam;
}
