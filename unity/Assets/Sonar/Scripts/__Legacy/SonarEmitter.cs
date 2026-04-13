using UnityEngine;

public class SonarEmitter : MonoBehaviour
{
    public SonarConfig config;

    [Header("Debug")]
    public bool showBeams = true;

    void OnDrawGizmos()
    {
        if (!showBeams || config == null) return;

        // Fan edges in red
        Vector3 leftDir = GetBeamDirection(0);
        Vector3 rightDir = GetBeamDirection(config.numberOfBeams - 1);
        Gizmos.color = Color.red;
        Gizmos.DrawRay(transform.position, leftDir * config.maxRange);
        Gizmos.DrawRay(transform.position, rightDir * config.maxRange);

        // Every 4th beam in yellow
        for (int b = 0; b < config.numberOfBeams; b += 4)
        {
            Vector3 dir = GetBeamDirection(b);
            Gizmos.color = Color.yellow;
            Gizmos.DrawRay(transform.position, dir * config.maxRange);
        }
    }

    [Header("Output")]
    public float[,] intensityImage; // [beamIndex, rangeBinIndex]

    private float pingTimer;

    void Update()
    {
        pingTimer += Time.deltaTime;
        float pingInterval = 1f / config.pingRateHz;

        if (pingTimer >= pingInterval)
        {
            pingTimer -= pingInterval;
            ExecutePing();
        }
    }

    private Vector3 GetBeamDirection(int beamIndex)
    {
        float halfSwath = config.swathAngleDeg / 2f;
        float angleStep = config.AngularResolutionDeg;

        // Beam angle relative to centre: -halfSwath to +halfSwath
        float beamAngle = -halfSwath + (beamIndex * angleStep) + (angleStep / 2f);

        // Apply mount tilt (rotation around the sonar's local X axis)
        // and beam angle (rotation around the sonar's local Z/forward axis)
        Quaternion tilt = Quaternion.AngleAxis(config.mountTiltDeg, Vector3.right);
        Quaternion fan = Quaternion.AngleAxis(beamAngle, Vector3.forward);

        // Base direction is straight down (-Y in Unity)
        Vector3 localDir = fan * tilt * Vector3.down;

        // Transform to world space
        return transform.TransformDirection(localDir);
    }


    private void ExecutePing()
    {
        int numBins = config.numberOfRangeBins;
        intensityImage = new float[config.numberOfBeams, numBins];
        int sonarLayerMask = LayerMask.GetMask("SonarTarget");

        // Store first hit bin per beam (-1 means no hit)
        int[] firstHitBin = new int[config.numberOfBeams];

        for (int b = 0; b < config.numberOfBeams; b++)
        {
            firstHitBin[b] = numBins; // default: no hit, noise fills everything
            Vector3 dir = GetBeamDirection(b);
            Debug.DrawRay(transform.position, dir * config.maxRange, Color.red, 0.1f);
            if (Physics.Raycast(transform.position, dir, out RaycastHit hit,
                                config.maxRange, sonarLayerMask))
            {
                int hitBin = Mathf.FloorToInt(hit.distance / config.RangeBinSize);
                hitBin = Mathf.Clamp(hitBin, 0, numBins - 1);
                firstHitBin[b] = hitBin;

                float intensity = ComputeIntensity(hit, dir);

                // Write the return with spread
                int spreadBins = 8;
                for (int s = -spreadBins; s <= spreadBins; s++)
                {
                    int bin = hitBin + s;
                    if (bin >= 0 && bin < numBins)
                    {
                        float falloff = Mathf.Exp(-0.5f * (s * s));
                        intensityImage[b, bin] = Mathf.Max(intensityImage[b, bin],
                                                            intensity * falloff);
                    }
                }
            }
        }

        // ===== POST-PROCESSING =====
        // Comment out either line to disable that effect
        AddShadows(intensityImage, firstHitBin);
        AddNoise(intensityImage, firstHitBin);



        OnPingComplete?.Invoke(intensityImage);
    }

    // ===== ACOUSTIC SHADOW =====
    private void AddShadows(float[,] image, int[] firstHitBin)
    {
        int beams = image.GetLength(0);
        int bins = image.GetLength(1);

        for (int b = 0; b < beams; b++)
        {
            int shadowStart = firstHitBin[b] + 12;
            for (int r = shadowStart; r < bins; r++)
            {
                // Kill acoustic returns but leave the bin open for noise
                image[b, r] = 0f;
            }
        }
    }

    // ===== NOISE MODEL =====
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
                    // Very faint electronic noise only — no acoustic energy here
                    float faintNoise = Random.Range(0f, 0.015f);
                    image[b, r] = Mathf.Clamp01(image[b, r] + faintNoise);
                    continue;
                }

                // Range-dependent band noise — multiple overlapping frequencies
                // for uncorrelated, natural-looking variation
                float t = rangeRatio * 100f + pingOffset;
                float band1 = Mathf.Sin(t * 1.7f) * 0.015f;
                float band2 = Mathf.Sin(t * 3.3f + 42f) * 0.01f;
                float band3 = Mathf.Sin(t * 7.1f + 17f) * 0.008f;
                float band4 = Mathf.Sin(t * 13.9f + 83f) * 0.005f;
                float bandNoise = Mathf.Abs(band1 + band2 + band3 + band4);

                // Per-bin random noise — uncorrelated
                float receiverNoise = Random.Range(0.01f, 0.04f);

                // Gaussian jitter
                float gaussNoise = RandomGaussian(0f, 0.02f);

                // Volume scattering — only in water column before hit
                float volumeScatter = 0f;
                if (beforeHit)
                {
                    volumeScatter = Random.Range(0.02f, 0.08f)
                                    * (1.0f - 0.3f * rangeRatio);
                }

                // Speckle — rare bright pixels
                float speckle = 0f;
                if (Random.value < 0.003f)
                    speckle = Random.Range(0.08f, 0.2f);

                image[b, r] = Mathf.Clamp01(image[b, r] + bandNoise
                                             + receiverNoise + gaussNoise
                                             + volumeScatter + speckle);
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

    public System.Action<float[,]> OnPingComplete;
    private float ComputeIntensity(RaycastHit hit, Vector3 beamDir)
    {
        // Transmission loss (two-way, spherical spreading + absorption)
        float range = hit.distance;
        float absorptionCoeff = 0.01f; // dB/m, frequency-dependent
        float TL = 40f * Mathf.Log10(Mathf.Max(range, 0.1f))
                 + 2f * absorptionCoeff * range;

        // Backscatter strength from surface properties
        float BS = 0f;
        var acousticProps = hit.collider.GetComponent<AcousticProperties>();
        if (acousticProps != null)
        {
            // Lambert's law backscatter
            float grazingAngle = Mathf.Acos(
                Mathf.Abs(Vector3.Dot(beamDir, hit.normal)));
            BS = 10f * Mathf.Log10(
                acousticProps.reflectivity * Mathf.Sin(grazingAngle) + 0.001f);
        }

        // Sonar equation: EL = SL - 2*TL + BS
        float SL = 200f; // Source level (dB re 1µPa @ 1m) — typical WBMS
        float EL = SL - TL + BS; // simplified (one-way TL already doubled above)

        // Normalise to 0–1 for visualisation
        // float normIntensity = Mathf.InverseLerp(80f, 200f, EL);
        float normIntensity = Mathf.InverseLerp(100f, 170f, EL);
        return Mathf.Clamp01(normIntensity);
    }

}
