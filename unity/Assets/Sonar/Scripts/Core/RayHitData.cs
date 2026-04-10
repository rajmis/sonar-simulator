using UnityEngine;

namespace Sonar.Core
{
    /// <summary>
    /// Raw ray hit data produced by SonarRaycaster, consumed by SonarSender.
    /// Matches the 48-byte per-ray wire format in shared/protocol.md.
    /// </summary>
    public struct RayHitData
    {
        public ushort beamIndex;
        public ushort subBeamIndex;
        public bool didHit;
        public float hitDistance;
        public Vector3 hitNormal;
        public Vector3 rayDir;
        public ushort objectID;
        public byte materialType;
        public Vector3 hitPoint;
    }

    /// <summary>
    /// Complete ping data ready for network transmission.
    /// </summary>
    public struct PingData
    {
        public uint pingID;
        public double timestamp;
        public Vector3 sonarPos;
        public Quaternion sonarRot;
        public RayHitData[] rays;
    }
}
