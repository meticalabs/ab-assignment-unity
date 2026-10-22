using System;
using Firebase.Extensions;
using Firebase.RemoteConfig;
using AB.Assignment;

namespace ABAssignmentSample {
    /// <summary>
    /// Firebase Remote Config: tells the package how the fetch ended. If your game already fetches Remote Config, add
    /// the three ReportRemoteConfig calls to that code instead of fetching a second time.
    /// </summary>
    public static class ABAssignmentRemoteConfig {
        private const string ParameterName = "metica_assignment";

        /// <summary>Call as early as possible, once Firebase is ready (after CheckAndFixDependenciesAsync).</summary>
        public static void Fetch() {
            FirebaseRemoteConfig rc = FirebaseRemoteConfig.DefaultInstance;
            rc.FetchAsync(TimeSpan.Zero).ContinueWithOnMainThread(fetchTask => {
                if (fetchTask.IsFaulted || fetchTask.IsCanceled || rc.Info.LastFetchStatus != LastFetchStatus.Success) {
                    ABAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, "fetch failed");
                    return;
                }
                rc.ActivateAsync().ContinueWithOnMainThread(activateTask => {
                    if (activateTask.IsFaulted || activateTask.IsCanceled) {
                        ABAssignment.ReportRemoteConfig(RemoteConfigStatus.ActivationFailed, null, "activate failed");
                        return;
                    }
                    // Pass the value only if it came from the server (never an in-app default).
                    ConfigValue value = rc.GetValue(ParameterName);
                    string remoteValue = null;
                    if (value.Source == ValueSource.RemoteValue) {
                        remoteValue = value.StringValue;
                    }
                    ABAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, remoteValue, null);

                    // Your other Remote Config handlers go after this line.
                });
            });
        }
    }
}
