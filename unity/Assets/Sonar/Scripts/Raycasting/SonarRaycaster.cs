using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Sonar.Core;

namespace Sonar.Raycasting
{
    /// <summary>
    /// Captures a polar depth + material map of the sonar's field of view.
    ///
    /// Rays are cast on a regular (azimuth × elevation) grid covering the
    /// swath plus padding on every side. Each grid cell records the hit
    /// range and the material type of the object hit — nothing else.
    /// All acoustic processing happens in the Python processor.
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

        /// <summary>Fired when a frame's depth/material data is ready for transmission.</summary>
        public event Action<DepthFrame> OnFrameReady;

        private uint _pingCounter;
        private float _pingTimer;

        // Local-space grid ray directions, row-major (row = elevation, col = azimuth)
        private Vector3[] _rayDirs;

        // Cache collider → material type so we don't walk the hierarchy per ray
        private readonly Dictionary<Collider, byte> _materialCache = new();

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
        /// Pre-compute local-space ray directions on the capture grid.
        /// Same angular convention as the legacy beam geometry:
        /// azimuth fans about the forward (Z+) axis starting from straight down.
        /// </summary>
        private void PrecomputeRayDirections()
        {
            int w = config.azimuthSamples;
            int h = config.elevationSamples;
            _rayDirs = new Vector3[w * h];

            float azSpan = config.swathAngleDeg + 2f * config.capturePaddingDeg;
            float elSpan = config.verticalFOVDeg + 2f * config.capturePaddingDeg;

            Quaternion tilt = Quaternion.AngleAxis(config.mountTiltDeg, Vector3.right);

            for (int row = 0; row < h; row++)
            {
                float el = -elSpan / 2f + (row + 0.5f) * elSpan / h;
                Quaternion vert = Quaternion.AngleAxis(el, Vector3.right);

                for (int col = 0; col < w; col++)
                {
                    float az = -azSpan / 2f + (col + 0.5f) * azSpan / w;
                    Quaternion fan = Quaternion.AngleAxis(az, Vector3.forward);

                    _rayDirs[row * w + col] = fan * vert * tilt * Vector3.down;
                }
            }
        }

        private void ExecutePing()
        {
            _pingCounter++;
            int w = config.azimuthSamples;
            int h = config.elevationSamples;
            int total = w * h;

            Transform t = transform;
            Vector3 sonarPos = t.position;
            Quaternion sonarRot = t.rotation;

            var commands = new NativeArray<RaycastCommand>(total, Allocator.TempJob);
            var results = new NativeArray<RaycastHit>(total, Allocator.TempJob);

            var queryParams = new QueryParameters(targetLayer, false, QueryTriggerInteraction.Ignore, false);

            for (int i = 0; i < total; i++)
            {
                Vector3 worldDir = sonarRot * _rayDirs[i];
                commands[i] = new RaycastCommand(sonarPos, worldDir, queryParams, config.maxRange);
            }

            JobHandle handle = RaycastCommand.ScheduleBatch(commands, results, 256);
            handle.Complete();

            var ranges = new float[total];
            var materials = new byte[total];

            for (int i = 0; i < total; i++)
            {
                RaycastHit hit = results[i];
                if (hit.collider != null)
                {
                    ranges[i] = hit.distance;
                    materials[i] = GetMaterialType(hit.collider);
                }
                // else: ranges[i] stays 0 = no return
            }

            commands.Dispose();
            results.Dispose();

            var frame = new DepthFrame
            {
                pingID = _pingCounter,
                timestamp = Time.timeAsDouble,
                sonarPos = sonarPos,
                sonarRot = sonarRot,
                width = w,
                height = h,
                ranges = ranges,
                materials = materials,
            };

            OnFrameReady?.Invoke(frame);
        }

        private byte GetMaterialType(Collider col)
        {
            if (_materialCache.TryGetValue(col, out byte cached))
                return cached;

            var props = col.GetComponentInParent<AcousticProperties>();
            byte mat = props != null ? (byte)props.materialType : (byte)0;
            _materialCache[col] = mat;
            return mat;
        }
    }
}
