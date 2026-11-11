using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Sonar.Network;

namespace Sonar.EditorTools
{
    /// <summary>
    /// Headless end-to-end smoke test: opens SampleScene, enters play mode,
    /// and waits until the Python processor has returned intensity images.
    ///
    /// Run with the Python processor already listening on localhost:9000:
    ///   Unity -batchmode -nographics -projectPath unity \
    ///         -executeMethod Sonar.EditorTools.HeadlessPipelineTest.Run
    ///
    /// Exits 0 on success (≥3 images received within 60 s), 1 otherwise.
    /// </summary>
    public static class HeadlessPipelineTest
    {
        private const string Flag = "HPT_ACTIVE";
        private const string StartKey = "HPT_START";
        private const int RequiredPings = 3;
        private const float TimeoutSeconds = 60f;

        public static void Run()
        {
            SessionState.SetBool(Flag, true);
            EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
            EditorApplication.EnterPlaymode();
        }

        // Re-registers the poll loop after the play-mode domain reload.
        [InitializeOnLoadMethod]
        private static void OnDomainReload()
        {
            if (!SessionState.GetBool(Flag, false))
                return;
            SessionState.SetFloat(StartKey, (float)EditorApplication.timeSinceStartup);
            EditorApplication.update += Poll;
        }

        private static void Poll()
        {
            float start = SessionState.GetFloat(StartKey, 0f);
            float elapsed = (float)EditorApplication.timeSinceStartup - start;

            if (!EditorApplication.isPlaying)
            {
                if (elapsed > TimeoutSeconds)
                    Finish(false, -1, elapsed, "play mode never started");
                return;
            }

            int pings = CountPings();
            if (pings >= RequiredPings)
                Finish(true, pings, elapsed, "ok");
            else if (elapsed > TimeoutSeconds)
                Finish(false, pings, elapsed, "timeout waiting for images");
        }

        private static int CountPings()
        {
            var client = Object.FindFirstObjectByType<SonarNetworkClient>();
            if (client == null) return -1;
            var field = typeof(SonarNetworkClient).GetField("pingsProcessed",
                BindingFlags.NonPublic | BindingFlags.Instance);
            return (int)field.GetValue(client);
        }

        private static void Finish(bool ok, int pings, float elapsed, string why)
        {
            EditorApplication.update -= Poll;
            SessionState.SetBool(Flag, false);
            Debug.Log($"[HeadlessPipelineTest] {(ok ? "PASS" : "FAIL")} — " +
                      $"{pings} images received in {elapsed:F1} s ({why})");
            EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
