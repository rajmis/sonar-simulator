using UnityEngine;
using System.Collections.Generic;

public class SonarGPUEmitter : MonoBehaviour
{
    public SonarConfig config;
    public ComputeShader sonarComputeShader;

    [Header("Scene Geometry")]
    public Transform[] cylinderTargets;    // drag JettyPile etc here
    public Transform seabedTransform;

    [Header("Output")]
    public float[,] intensityImage;
    public System.Action<float[,]> OnPingComplete;

    // GPU buffers
    private ComputeBuffer raysBuffer;
    private ComputeBuffer resultsBuffer;
    private ComputeBuffer cylindersBuffer;

    private float pingTimer;

    struct RayInput
    {
        public Vector3 origin;
        public Vector3 direction;
        public float maxRange;
        public int beamIndex;
        public float subBeamWeight;
    }

    struct RayResult
    {
        public float hitDistance;
        public Vector3 hitNormal;
        public float intensity;
        public int beamIndex;
        public float weight;
        public int didHit;
    }

    struct CylinderDef
    {
        public Vector3 center;
        public float radius;
        public float yMin;
        public float yMax;
        public float reflectivity;
    }

    void Start()
    {
        int totalRays = config.TotalRays;
        raysBuffer = new ComputeBuffer(totalRays,
            sizeof(float) * 3 + sizeof(float) * 3 + sizeof(float)
            + sizeof(int) + sizeof(float));
        resultsBuffer = new ComputeBuffer(totalRays,
            sizeof(float) + sizeof(float) * 3 + sizeof(float)
            + sizeof(int) + sizeof(float) + sizeof(int));

        UpdateCylinderBuffer();
    }

    void UpdateCylinderBuffer()
    {
        if (cylinderTargets == null || cylinderTargets.Length == 0) return;

        CylinderDef[] cylinders = new CylinderDef[cylinderTargets.Length];
        for (int i = 0; i < cylinderTargets.Length; i++)
        {
            Transform t = cylinderTargets[i];
            float radius = t.lossyScale.x * 0.5f; // Unity cylinder radius = 0.5 * scaleX
            float halfHeight = t.lossyScale.y;     // Unity cylinder height = 2 * scaleY

            var acoustics = t.GetComponent<AcousticProperties>();

            cylinders[i] = new CylinderDef
            {
                center = new Vector3(t.position.x, 0, t.position.z),
                radius = radius,
                yMin = t.position.y - halfHeight,
                yMax = t.position.y + halfHeight,
                reflectivity = acoustics != null ? acoustics.reflectivity : 0.5f
            };
        }

        cylindersBuffer?.Release();
        cylindersBuffer = new ComputeBuffer(cylinders.Length,
            sizeof(float) * 3 + sizeof(float) * 3 + sizeof(float));
        cylindersBuffer.SetData(cylinders);
    }

    void Update()
    {
        pingTimer += Time.deltaTime;
        float pingInterval = 1f / config.pingRateHz;

        if (pingTimer >= pingInterval)
        {
            pingTimer -= pingInterval;
            ExecutePingGPU();
        }
    }

    private Vector3 GetBeamDirection(int beamIndex, float hOffset, float vOffset)
    {
        float halfSwath = config.swathAngleDeg / 2f;
        float angleStep = config.AngularResolutionDeg;

        // Base horizontal angle + small offset within beam width
        float beamAngle = -halfSwath + (beamIndex * angleStep) + (angleStep / 2f) + hOffset;

        // Build rotation: mount tilt, then fan angle, then vertical offset
        Quaternion tilt = Quaternion.AngleAxis(config.mountTiltDeg, Vector3.right);
        Quaternion fan = Quaternion.AngleAxis(beamAngle, Vector3.forward);
        Quaternion vert = Quaternion.AngleAxis(vOffset, Vector3.right);

        Vector3 localDir = fan * vert * tilt * Vector3.down;
        return transform.TransformDirection(localDir);
    }

    private float BeamPatternWeight(float hOffset, float vOffset)
    {
        float halfBeamWidth = config.AngularResolutionDeg / 2f;
        float halfVFOV = config.verticalFOVDeg / 2f;

        float normH = hOffset / halfBeamWidth;    // -1 to 1
        float normV = vOffset / halfVFOV;          // -1 to 1

        // 2D Gaussian — strongest at centre of beam cone
        return Mathf.Exp(-2f * (normH * normH + normV * normV));
    }

    private void ExecutePingGPU()
    {
        int totalRays = config.TotalRays;
        int numBins = config.numberOfRangeBins;

        RayInput[] rays = new RayInput[totalRays];
        float halfVFOV = config.verticalFOVDeg / 2f;
        float halfBeamWidth = config.AngularResolutionDeg / 2f;

        for (int b = 0; b < config.numberOfBeams; b++)
        {
            for (int sv = 0; sv < config.subBeamsV; sv++)
            {
                for (int sh = 0; sh < config.subBeamsH; sh++)
                {
                    // Vertical offset across vFOV
                    float vOffset = Mathf.Lerp(-halfVFOV, halfVFOV,
                        (sv + 0.5f) / config.subBeamsV);

                    // Horizontal offset within beam width
                    float hOffset = Mathf.Lerp(-halfBeamWidth, halfBeamWidth,
                        (sh + 0.5f) / config.subBeamsH);

                    int idx = b * config.SubBeamsPerBeam + sv * config.subBeamsH + sh;
                    rays[idx] = new RayInput
                    {
                        origin = transform.position,
                        direction = GetBeamDirection(b, hOffset, vOffset),
                        maxRange = config.maxRange,
                        beamIndex = b,
                        subBeamWeight = BeamPatternWeight(hOffset, vOffset)
                    };
                }
            }
        }

        raysBuffer.SetData(rays);

        int kernel = sonarComputeShader.FindKernel("CSMain");
        sonarComputeShader.SetBuffer(kernel, "_Rays", raysBuffer);
        sonarComputeShader.SetBuffer(kernel, "_Results", resultsBuffer);
        sonarComputeShader.SetBuffer(kernel, "_Cylinders", cylindersBuffer);
        sonarComputeShader.SetInt("_NumCylinders", cylinderTargets.Length);
        sonarComputeShader.SetFloat("_SeabedY", seabedTransform.position.y);

        var seabedAcoustics = seabedTransform.GetComponent<AcousticProperties>();
        sonarComputeShader.SetFloat("_SeabedReflectivity",
            seabedAcoustics != null ? seabedAcoustics.reflectivity : 0.3f);

        int threadGroups = Mathf.CeilToInt(totalRays / 256f);
        sonarComputeShader.Dispatch(kernel, threadGroups, 1, 1);

        RayResult[] results = new RayResult[totalRays];
        resultsBuffer.GetData(results);

        BuildIntensityImage(results, numBins);
    }

    private void BuildIntensityImage(RayResult[] results, int numBins)
    {
        intensityImage = new float[config.numberOfBeams, numBins];
        float[] weightSums = new float[config.numberOfBeams];
        int[] firstHitBin = new int[config.numberOfBeams];

        for (int b = 0; b < config.numberOfBeams; b++)
            firstHitBin[b] = numBins;

        // Weighted accumulation across sub-beams
        for (int i = 0; i < results.Length; i++)
        {
            RayResult r = results[i];
            if (r.didHit == 0) continue;

            int b = r.beamIndex;
            int hitBin = Mathf.FloorToInt(r.hitDistance / config.RangeBinSize);
            hitBin = Mathf.Clamp(hitBin, 0, numBins - 1);

            if (hitBin < firstHitBin[b])
                firstHitBin[b] = hitBin;

            float weightedIntensity = r.intensity * r.weight;

            // Spread across bins
            int spreadBins = 8;
            for (int s = -spreadBins; s <= spreadBins; s++)
            {
                int bin = hitBin + s;
                if (bin >= 0 && bin < numBins)
                {
                    float falloff = Mathf.Exp(-0.5f * (s * s));
                    float val = weightedIntensity * falloff;
                    intensityImage[b, bin] = Mathf.Max(intensityImage[b, bin], val);
                }
            }
        }

        // ===== POST-PROCESSING =====
        AddShadows(intensityImage, firstHitBin);
        AddNoise(intensityImage, firstHitBin);

        OnPingComplete?.Invoke(intensityImage);
    }

    // Same AddShadows and AddNoise methods as before
    private void AddShadows(float[,] image, int[] firstHitBin)
    {
        int beams = image.GetLength(0);
        int bins = image.GetLength(1);

        for (int b = 0; b < beams; b++)
        {
            int shadowStart = firstHitBin[b] + 12;
            for (int r = shadowStart; r < bins; r++)
                image[b, r] = 0f;
        }
    }

    private void AddNoise(float[,] image, int[] firstHitBin)
    {
        int beams = image.GetLength(0);
        int bins = image.GetLength(1);

        float pingOffset = Random.Range(0f, 1000f);

        for (int b = 0; b < beams; b++)
        {
            for (int r = 0; r < bins; r++)
            {
                float rangeM = r * config.RangeBinSize;
                float rangeRatio = rangeM / config.maxRange;

                bool inShadow = (r >= firstHitBin[b] + 12);
                bool beforeHit = (r < firstHitBin[b]);

                if (inShadow)
                {
                    float faintNoise = Random.Range(0f, 0.015f);
                    image[b, r] = Mathf.Clamp01(image[b, r] + faintNoise);
                    continue;
                }

                float t = rangeRatio * 100f + pingOffset;
                float bandNoise = Mathf.Abs(
                    Mathf.Sin(t * 1.7f) * 0.015f +
                    Mathf.Sin(t * 3.3f + 42f) * 0.01f +
                    Mathf.Sin(t * 7.1f + 17f) * 0.008f +
                    Mathf.Sin(t * 13.9f + 83f) * 0.005f);

                float receiverNoise = Random.Range(0.01f, 0.04f);
                float gaussNoise = RandomGaussian(0f, 0.02f);

                float volumeScatter = 0f;
                if (beforeHit)
                    volumeScatter = Random.Range(0.02f, 0.08f)
                                    * (1.0f - 0.3f * rangeRatio);

                float speckle = 0f;
                if (Random.value < 0.003f)
                    speckle = Random.Range(0.08f, 0.2f);

                image[b, r] = Mathf.Clamp01(image[b, r] + bandNoise
                    + receiverNoise + gaussNoise + volumeScatter + speckle);
            }
        }
    }

    private float RandomGaussian(float mean, float stdDev)
    {
        float u1 = 1f - Random.value;
        float u2 = 1f - Random.value;
        float z = Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Sin(2f * Mathf.PI * u2);
        return mean + stdDev * z;
    }

    void OnDestroy()
    {
        raysBuffer?.Release();
        resultsBuffer?.Release();
        cylindersBuffer?.Release();
    }

    [Header("Debug")]
    public bool showBeams = true;
    public bool showSubBeams = false;  // warning: draws many rays

    void OnDrawGizmos()
    {
        if (!showBeams || config == null) return;

        float halfVFOV = config.verticalFOVDeg / 2f;
        float halfBeamWidth = config.AngularResolutionDeg / 2f;

        for (int b = 0; b < config.numberOfBeams; b += 32)
        {
            if (showSubBeams)
            {
                for (int sv = 0; sv < config.subBeamsV; sv++)
                {
                    for (int sh = 0; sh < config.subBeamsH; sh++)
                    {
                        float vOffset = Mathf.Lerp(-halfVFOV, halfVFOV,
                            (sv + 0.5f) / config.subBeamsV);
                        float hOffset = Mathf.Lerp(-halfBeamWidth, halfBeamWidth,
                            (sh + 0.5f) / config.subBeamsH);

                        Vector3 dir = GetBeamDirection(b, hOffset, vOffset);
                        Gizmos.color = new Color(0, 0.3f, 1f, 0.15f);
                        Gizmos.DrawRay(transform.position, dir * config.maxRange);
                    }
                }
            }

            Vector3 centreDir = GetBeamDirection(b, 0f, 0f);
            Gizmos.color = Color.yellow;
            Gizmos.DrawRay(transform.position, centreDir * config.maxRange);
        }

        Vector3 leftDir = GetBeamDirection(0, 0f, 0f);
        Vector3 rightDir = GetBeamDirection(config.numberOfBeams - 1, 0f, 0f);
        Gizmos.color = Color.red;
        Gizmos.DrawRay(transform.position, leftDir * config.maxRange);
        Gizmos.DrawRay(transform.position, rightDir * config.maxRange);
    }
}