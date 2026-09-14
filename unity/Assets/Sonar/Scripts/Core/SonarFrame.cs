using UnityEngine;

namespace Sonar.Core
{
    /// <summary>
    /// Polar depth + material frame produced by SonarRaycaster, consumed by
    /// SonarNetworkClient. Matches the wire format in shared/PROTOCOL.md.
    ///
    /// Row-major grids: row = elevation index, column = azimuth index.
    /// ranges[i] is the hit range in metres along the ray (0 = no return);
    /// materials[i] is the material type index (see AcousticProperties).
    /// </summary>
    public struct DepthFrame
    {
        public uint pingID;
        public double timestamp;
        public Vector3 sonarPos;
        public Quaternion sonarRot;
        public int width;      // azimuth samples
        public int height;     // elevation samples
        public float[] ranges;
        public byte[] materials;
    }
}
