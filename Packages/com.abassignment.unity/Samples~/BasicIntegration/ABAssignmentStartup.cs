using System.Collections.Generic;
using AB.Assignment;
using UnityEngine;

namespace ABAssignmentSample {
    /// <summary>
    /// App start: gives the package your Metica app id and API key. It runs before the first scene loads, so there is
    /// nothing to add to a scene.
    /// </summary>
    public static class ABAssignmentStartup {
        // Your Metica credentials (the same values you give the Metica SDK).
        public const string MeticaAppId = "YOUR_METICA_APP_ID";
        public const string MeticaApiKey = "YOUR_METICA_API_KEY";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void OnBeforeSceneLoad() {
            ABAssignment.Initialize(new ABAssignmentOptions(MeticaAppId, MeticaApiKey));

            // Optional: a copy of each event for your own analytics. Metica gets the event either way.
            ABAssignment.EventLogged += OnEventLogged;
        }

        private static void OnEventLogged(string eventName, IReadOnlyDictionary<string, object> payload) {
            // Replace with your analytics call, for example YourAnalytics.LogEvent(eventName, copy).
            var copy = new Dictionary<string, object>(payload);
            Debug.Log("Assignment event " + eventName + " with " + copy.Count + " fields");
        }

        /// <summary>Call this when the player answers your own GDPR prompt (skip it if your game has none).</summary>
        public static void OnGdprAnswered(bool accepted) {
            if (accepted) {
                ABAssignment.SetConsentStatus(ConsentStatus.Granted);
            } else {
                ABAssignment.SetConsentStatus(ConsentStatus.Denied);
            }
        }
    }
}
