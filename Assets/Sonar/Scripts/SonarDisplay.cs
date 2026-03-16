using UnityEngine;
using UnityEngine.UI;

public class SonarDisplay : MonoBehaviour
{
    public SonarEmitter cpuEmitter;        // use one or the other
    public SonarGPUEmitter gpuEmitter;     // use one or the other
    public RawImage displayImage;
    public Material fanMaterial;

    [Header("Colour Mapping")]
    public Gradient colourMap;

    private Texture2D sonarTexture;

    void Start()
    {
        if (cpuEmitter != null)
            cpuEmitter.OnPingComplete += OnPing;
        if (gpuEmitter != null)
            gpuEmitter.OnPingComplete += OnPing;

        if (fanMaterial != null)
            displayImage.material = fanMaterial;
    }

    void OnPing(float[,] intensityImage)
    {
        int beams = intensityImage.GetLength(0);
        int bins  = intensityImage.GetLength(1);

        if (sonarTexture == null || sonarTexture.width != beams
                                 || sonarTexture.height != bins)
        {
            sonarTexture = new Texture2D(beams, bins, TextureFormat.RGBA32, false);
            sonarTexture.filterMode = FilterMode.Bilinear;
            sonarTexture.wrapMode = TextureWrapMode.Clamp;
            displayImage.texture = sonarTexture;

            if (fanMaterial != null)
                fanMaterial.SetTexture("_SonarTex", sonarTexture);
        }

        for (int b = 0; b < beams; b++)
        {
            for (int r = 0; r < bins; r++)
            {
                Color c = colourMap.Evaluate(intensityImage[b, r]);
                sonarTexture.SetPixel(b, r, c);
            }
        }

        sonarTexture.Apply();
    }

    void OnDestroy()
    {
        if (cpuEmitter != null)
            cpuEmitter.OnPingComplete -= OnPing;
        if (gpuEmitter != null)
            gpuEmitter.OnPingComplete -= OnPing;
    }
}