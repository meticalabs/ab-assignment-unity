namespace AB.Assignment {
    /// <summary>
    /// The assigned group of a device for the experiment in effect.
    /// </summary>
    public enum Group {
        /// <summary>No group this session: no Firebase config had arrived when the group was needed, or deciding failed.
        /// The game runs its own ad code. In client-managed mode: the game has not reported a group yet.</summary>
        Unassigned,
        /// <summary>Assigned Metica on.</summary>
        Metica,
        /// <summary>Assigned Metica off: the game runs its own ad code.</summary>
        Baseline
    }

    /// <summary>
    /// Who decides the group this session (<see cref="ABAssignment.SetAssignmentMode"/>). Not persisted.
    /// </summary>
    public enum AssignmentMode {
        /// <summary>Default: the library decides the group from the Firebase value (or cache) on first need.</summary>
        Automatic,
        /// <summary>The game decides the group itself and reports it with
        /// <see cref="ABAssignment.ReportClientAssignment"/>; the library never decides or changes it.</summary>
        ClientManaged
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
