using UnityEngine;

namespace Metica.Assignment {
    /// <summary>
    /// Hidden object that drives the library every frame and on resume, so the game does not have to: it checks the
    /// fetch / ads-init deadlines and the UTC day change, and sends queued events to Metica.
    /// </summary>
    internal sealed class AssignmentDriver : MonoBehaviour {
        /// <summary>Creates the driver in play mode. Does nothing in edit mode (tests drive the library directly).</summary>
        internal static void Create() {
            if (!Application.isPlaying) {
                return;
            }
            var driver = new GameObject("MeticaAssignment");
            driver.hideFlags = HideFlags.HideInHierarchy;
            DontDestroyOnLoad(driver);
            driver.AddComponent<AssignmentDriver>();
        }

        private void Update() {
            MeticaAssignment.Tick();
        }

        private void OnApplicationPause(bool paused) {
            if (paused) {
                MeticaAssignment.OnApplicationPaused();
            } else {
                MeticaAssignment.OnApplicationResumed();
            }
        }

        private void OnApplicationFocus(bool hasFocus) {
            if (!hasFocus) {
                MeticaAssignment.OnApplicationPaused();
            }
        }
    }
}
