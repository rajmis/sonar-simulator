using UnityEngine;
using Sonar.Raycasting;
using Sonar.Network;

namespace Sonar.Core
{
    /// <summary>
    /// Orchestrator: wires SonarNetworkClient.OnImageReady → SonarDisplay.
    /// 
    /// Attach to the sonar GameObject alongside SonarRaycaster and SonarNetworkClient.
    /// </summary>
    [RequireComponent(typeof(SonarRaycaster))]
    [RequireComponent(typeof(SonarNetworkClient))]
    public class SonarBridge : MonoBehaviour
    {
        [Header("Display")]
        [Tooltip("The SonarDisplay component (can be on a different GameObject)")]
        public SonarDisplay display;

        private SonarNetworkClient _network;

        private void Start()
        {
            _network = GetComponent<SonarNetworkClient>();

            if (display == null)
            {
                display = FindFirstObjectByType<SonarDisplay>();
                if (display == null)
                {
                    Debug.LogError("SonarBridge: No SonarDisplay found!");
                    return;
                }
            }

            _network.OnImageReady += HandleImageReady;
            Debug.Log("SonarBridge: pipeline wired — Raycaster → Network → [Python] → Display");
        }

        private void OnDestroy()
        {
            if (_network != null)
                _network.OnImageReady -= HandleImageReady;
        }

        private void HandleImageReady(uint pingID, Texture2D texture)
        {
            Debug.Log($"[SonarBridge] received ping {pingID}, display={display != null}");
            display.UpdateFromTexture(texture);
        }
    }
}
