namespace Metica.Assignment {
    /// <summary>
    /// The assigned group of a device for the experiment in effect.
    /// </summary>
    public enum Group {
        /// <summary>No group this session: no Firebase config had arrived when the group was needed, or deciding failed.
        /// The game runs its own ad code.</summary>
        Unassigned,
        /// <summary>Assigned Metica on.</summary>
        Metica,
        /// <summary>Assigned Metica off: the game runs its own ad code.</summary>
        Baseline
    }

    /// <summary>
    /// Outcome of a Remote Config fetch, as reported by the game.
    /// </summary>
    public enum RemoteConfigStatus {
        /// <summary>
        /// Fetch and activation succeeded. Pass the metica_assignment value if its source is remote, otherwise null
        /// (reported as "missing"). A valid value decides this session's group if the group has not been needed yet.
        /// </summary>
        Success,
        /// <summary>The fetch failed (offline, throttled, error).</summary>
        FetchFailed,
        /// <summary>The fetch succeeded but activation faulted or was cancelled.</summary>
        ActivationFailed
    }

    /// <summary>
    /// Stage of the primary ad network init, as reported by the game.
    /// </summary>
    public enum AdsInitStage {
        Started,
        Succeeded,
        Failed,
        SkippedLowMemory
    }

    /// <summary>
    /// The player's GDPR consent from the game's own prompt (not the TCF ad consent). The library only passes it on in
    /// the consent_status field of its events; it never changes what is sent or the group.
    /// </summary>
    public enum ConsentStatus {
        /// <summary>The game has not set a status. Never treated as given.</summary>
        Unknown,
        Granted,
        Denied
    }

    internal static class Labels {
        public const string Metica = "metica";
        public const string Baseline = "baseline";
        public const string Unassigned = "unassigned";

        public const string ConsentUnknown = "unknown";
        public const string ConsentGranted = "granted";
        public const string ConsentDenied = "denied";

        public static string For(Group group) {
            switch (group) {
                case Group.Metica:
                    return Metica;
                case Group.Baseline:
                    return Baseline;
                default:
                    return Unassigned;
            }
        }

        public static string For(ConsentStatus status) {
            switch (status) {
                case ConsentStatus.Granted:
                    return ConsentGranted;
                case ConsentStatus.Denied:
                    return ConsentDenied;
                default:
                    return ConsentUnknown;
            }
        }
    }
}
