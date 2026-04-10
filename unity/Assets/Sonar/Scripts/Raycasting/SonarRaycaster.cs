using System;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Sonar.Core;

namespace Sonar.Raycasting
{
    /// <summary>
    /// Fires rays in a 2D cone pattern per beam and collects raw hit data.
    /// All acoustic processing is handled by the Python processor.
    /// 
    /// Attach to the sonar GameObject. Requires a SonarConfig reference.
    /// </summary>
    public class SonarRaycaster : MonoBehaviour
    {
        [Header("Configuration")]
        public SonarConfig config;

        [Header("Physics")]
        [Tooltip("Layer mask for sonar targets")]
        public LayerMask targetLayer;

        /// <summary>Fired when a ping's ray data is ready for transmission.</summary>
        public event Action<PingData> OnPingReady;

        private uint _pingCounter;
        private float _pingTimer;

        // Pre-allocated arrays
        private Vector3[] _rayDirs;
        private int[] _beamIndices;
        private int[] _subBeamIndices;

        private void Start()
        {
            if (config == null)
            {
                Debug.LogError("SonarRaycaster: SonarConfig not assigned!");
                enabled = false;
                return;
            }

            PrecomputeRayDirections();
        }

        private void Update()
        {
            _pingTimer += Time.deltaTime;
            float pingInterval = 1f / config.pingRateHz;

            if (_pingTimer >= pingInterval)
            {
                _pingTimer -= pingInterval;
                ExecutePing();
            }
        }

        /// <summary>
        /// Pre-compute ray directions in local space for all beams × sub-beams.
        /// Directions are relative to the sonar's forward (Z+) axis.
        /// </summary>
        private void PrecomputeRayDirections()
        {
            int totalRays = config.TotalRays;
            _rayDirs = new Vector3[totalRays];
            _beamIndices = new int[totalRays];
            _subBeamIndices = new int[totalRays];

            float halfVFOV = config.verticalFOVDeg / 2f;
            float halfBeamWidth = config.AngularResolutionDeg / 2f;

            int idx = 0;
            for (int beam = 0; beam < config.numberOfBeams; beam++)
            {
                float halfSwath = config.swathAngleDeg / 2f;
                float angleStep = config.AngularResolutionDeg;
                float beamAngle = -halfSwath + (beam * angleStep) + (angleStep / 2f);

                for (int sv = 0; sv < config.subBeamsV; sv++)
                {
                    float vOffset = Mathf.Lerp(-halfVFOV, halfVFOV,
                        (sv + 0.5f) / config.subBeamsV);

                    for (int sh = 0; sh < config.subBeamsH; sh++)
                    {
                        float hOffset = Mathf.Lerp(-halfBeamWidth, halfBeamWidth,
                            (sh + 0.5f) / config.subBeamsH);

                        // Match legacy SonarGPUEmitter beam geometry exactly
                        Quaternion tilt = Quaternion.AngleAxis(config.mountTiltDeg, Vector3.right);
                        Quaternion fan = Quaternion.AngleAxis(beamAngle + hOffset, Vector3.forward);
                        Quaternion vert = Quaternion.AngleAxis(vOffset, Vector3.right);

                        Vector3 localDir = fan * vert * tilt * Vector3.down;

                        _rayDirs[idx] = localDir;
                        _beamIndices[idx] = beam;
                        _subBeamIndices[idx] = sv * config.subBeamsH + sh;
                        idx++;
                    }
                }
            }
        }

        private void ExecutePing()
        {
            _pingCounter++;
            int totalRays = config.numberOfBeams * config.SubBeamsPerBeam;

            Transform t = transform;
            Vector3 sonarPos = t.position;
            Quaternion sonarRot = t.rotation;

            // Batch raycast using RaycastCommand for parallel execution
            var commands = new NativeArray<RaycastCommand>(totalRays, Allocator.TempJob);
            var results = new NativeArray<RaycastHit>(totalRays, Allocator.TempJob);

            var queryParams = new QueryParameters(targetLayer, false, QueryTriggerInteraction.Ignore, false);

            for (int i = 0; i < totalRays; i++)
            {
                Vector3 worldDir = sonarRot * _rayDirs[i];
                commands[i] = new RaycastCommand(sonarPos, worldDir, queryParams, config.maxRange);
            }

            // Schedule batch raycast on job system
            JobHandle handle = RaycastCommand.ScheduleBatch(commands, results, 256);
            handle.Complete();

            // Collect results
            var rays = new RayHitData[totalRays];
            for (int i = 0; i < totalRays; i++)
            {
                RaycastHit hit = results[i];
                bool didHit = hit.collider != null;

                rays[i] = new RayHitData
                {
                    beamIndex = (ushort)_beamIndices[i],
                    subBeamIndex = (ushort)_subBeamIndices[i],
                    didHit = didHit,
                    hitDistance = didHit ? hit.distance : 0f,
                    hitNormal = didHit ? hit.normal : Vector3.zero,
                    rayDir = sonarRot * _rayDirs[i],
                    objectID = didHit ? (ushort)(hit.collider.gameObject.GetInstanceID() & 0xFFFF) : (ushort)0,
                    materialType = didHit ? GetMaterialType(hit.collider) : (byte)0,
                    hitPoint = didHit ? hit.point : Vector3.zero,
                };
            }

            commands.Dispose();
            results.Dispose();

            var ping = new PingData
            {
                pingID = _pingCounter,
                timestamp = Time.timeAsDouble,
                sonarPos = sonarPos,
                sonarRot = sonarRot,
                rays = rays,
            };

            OnPingReady?.Invoke(ping);
        }

        private byte GetMaterialType(Collider col)
        {
            var props = col.GetComponentInParent<AcousticProperties>();
            if (props != null)
                return (byte)props.materialType;
            return 0; // default
        }
    }
}
