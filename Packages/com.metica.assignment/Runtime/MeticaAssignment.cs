using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Metica.Assignment {
    /// <summary>
    /// Entry point. Call <see cref="Initialize"/> once at startup with the game's Metica app id and API key; the library
    /// then sends its events to Metica ingestion itself. The session (user id, event queue) starts before the first
    /// scene loads; the group is decided on first need (reading <see cref="Current"/>, <see cref="TagPlacement"/> or
    /// <see cref="ReportAdsInit"/>) from a Firebase value received earlier in the session, else the cached one. With
    /// neither, the session is unassigned (the game runs its own ad code) and a later value applies from the next session.
    /// Threading: every call that changes state is main-thread only; calls from other threads are logged as errors and
    /// ignored. A decided <see cref="Current"/> and <see cref="TagPlacement"/> are safe from any thread.
    /// </summary>
    public static class MeticaAssignment {
        private static AssignmentEngine _engine = CreateEngine();

        /// <summary>
        /// This session's assignment. The first read on the main thread decides the group, which then stays fixed for
        /// the session. Read it only when the game needs the group (for example at ads init), so a Firebase value that
        /// arrives in the meantime can still decide it. Never null on the main thread.
        /// </summary>
        public static AssignmentResult Current => _engine.EnsureAssigned();

        /// <summary>
        /// The generated user id (stable for the install). Available at once and does not decide the group, so pass it
        /// to SDKs at startup. Main thread on first use.
        /// </summary>
        public static string UserId => _engine.UserId;

        /// <summary>The assignment if the group has already been decided this session; never decides it.</summary>
        public static bool TryGetCurrent(out AssignmentResult current) {
            current = _engine.Peek;
            return current != null;
        }

        /// <summary>
        /// Starts sending the library's events (upliftAssignment, upliftConfigFetch, upliftAdsInit) to Metica ingestion.
        /// Call it once, as early as possible (before the first scene loads is best). Events logged before it are kept
        /// in a persisted queue and sent once it is called. Main thread.
        /// </summary>
        public static void Initialize(MeticaAssignmentOptions options) {
            _engine.Initialize(options);
        }

        /// <summary>
        /// Optional: receives a read-only copy of every event when it is logged (event name and flat payload, including
        /// consent_status as it is at that moment; Metica gets the value when the event is sent), for example to forward
        /// it to the game's own analytics. Called on the main thread. It does
        /// not replace delivery to Metica: the event is already queued for Metica, and a listener that throws is logged
        /// and skipped. Subscribe on the main thread.
        /// </summary>
        public static event Action<string, IReadOnlyDictionary<string, object>> EventLogged {
            add => _engine.AddListener(value);
            remove => _engine.RemoveListener(value);
        }

        /// <summary>
        /// Obsolete and ignored, apart from a warning. Events always go to Metica through the built-in sender (see
        /// <see cref="Initialize"/>). A 0.1.0 sink was the transport to Metica, so passing it copies would send every
        /// event twice; to forward copies to your own analytics, subscribe to <see cref="EventLogged"/>.
        /// </summary>
        [Obsolete("Ignored. Events always go to Metica: call MeticaAssignment.Initialize. To forward copies to your own analytics, subscribe to MeticaAssignment.EventLogged.")]
        public static void SetEventSink(IAssignmentEventSink sink) {
            // An error, not a warning: a 0.1.0 integration that only calls this sends nothing until Initialize is added.
            AssignmentLog.Error("SetEventSink is obsolete and the sink is ignored. Events go to Metica only through " +
                                "the built-in sender: call MeticaAssignment.Initialize, or they stay queued. Use " +
                                "EventLogged for copies.");
        }

        /// <summary>
        /// Sets the player's GDPR consent status from the game's own prompt (not the TCF ad consent). Persisted across
        /// sessions; <see cref="ConsentStatus.Unknown"/> until the game sets it. Every event carries the value at send
        /// time in consent_status. It never changes what is sent or the group; the backend handles consent.
        /// Main thread; any time.
        /// </summary>
        public static void SetConsentStatus(ConsentStatus status) {
            _engine.SetConsentStatus(status);
        }

        /// <summary>
        /// "&lt;label&gt;|&lt;placement&gt;", e.g. "metica|MissionComplete". Null or empty placement gives
        /// "&lt;label&gt;|none". Already tagged input is returned unchanged. The label is the assigned group; the first
        /// call on the main thread decides it if needed. Safe from any thread once decided.
        /// </summary>
        public static string TagPlacement(string gamePlacement) {
            string label = Labels.Unassigned;
            AssignmentResult current = Current;
            if (current != null) {
                label = current.Label;
            }
            if (string.IsNullOrEmpty(gamePlacement)) {
                return label + "|none";
            }
            if (IsTagged(gamePlacement)) {
                return gamePlacement;
            }
            return label + "|" + gamePlacement;
        }

        /// <summary>
        /// Links a game-provided id (for example a login id) to the generated user id. Main thread; any time. Never
        /// changes the group, the bucket or the generated id. A new or changed pair sends one extra upliftAssignment;
        /// every daily upliftAssignment carries all pairs.
        /// </summary>
        public static void AddCustomUserId(string source, string id) {
            _engine.AddCustomUserId(source, id);
        }

        /// <summary>
        /// Reports the Remote Config fetch outcome. For <see cref="RemoteConfigStatus.Success"/> pass the
        /// metica_assignment value when its source is remote (not an in-app default), otherwise null: null or empty is
        /// reported as "missing". A valid value is cached; it decides this session's group if the group has not been
        /// needed yet, otherwise it applies from the next session. Main thread.
        /// </summary>
        public static void ReportRemoteConfig(RemoteConfigStatus status, string valueOrNull, string errorOrNull) {
            _engine.ReportRemoteConfig(status, valueOrNull, errorOrNull);
        }

        /// <summary>
        /// Reports the init of the primary ad network this session routes to (Metica when
        /// <see cref="AssignmentResult.RunMetica"/>, otherwise the game's own MAX code). Report
        /// <see cref="AdsInitStage.Started"/> just before starting it, then exactly one outcome.
        /// <paramref name="meticaInternalGroup"/> and <paramref name="forcedHoldout"/> are recorded only for
        /// <see cref="AdsInitStage.Succeeded"/>, <paramref name="errorOrNull"/> only for <see cref="AdsInitStage.Failed"/>.
        /// Decides the group if it has not been decided yet. Main thread.
        /// </summary>
        public static void ReportAdsInit(AdsInitStage stage, string errorOrNull = null,
            string meticaInternalGroup = null, bool? forcedHoldout = null) {
            _engine.ReportAdsInit(stage, errorOrNull, meticaInternalGroup, forcedHoldout);
        }

        /// <summary>Sends queued events if nothing is in flight and no backoff is pending. The driver also does this every frame.</summary>
        internal static void FlushEvents() {
            _engine.FlushEvents();
        }

        /// <summary>Called every frame by <see cref="AssignmentDriver"/>: deadlines, UTC date, queued events.</summary>
        internal static void Tick() {
            _engine.Tick();
        }

        /// <summary>Called by <see cref="AssignmentDriver"/> when the app returns from the background.</summary>
        internal static void OnApplicationResumed() {
            _engine.OnApplicationResumed();
        }

        /// <summary>Called by <see cref="AssignmentDriver"/> when the app goes to the background or loses focus.</summary>
        internal static void OnApplicationPaused() {
            _engine.OnApplicationPaused();
        }

        /// <summary>
        /// Development builds and the editor only: forces the route (not the group or label) from the next session.
        /// Null clears it. Ignored in release builds.
        /// </summary>
        public static void SetDebugOverride(Group? group) {
            _engine.SetDebugOverride(group);
        }

        /// <summary>
        /// Validates a metica_assignment config value with the same rules the library applies at runtime.
        /// Use it to check a value before publishing it.
        /// </summary>
        public static bool IsValidConfig(string json, out string error) {
            return AssignmentConfig.TryParse(json, out _, out error);
        }

        /// <summary>
        /// Starts a new process-like state over the given storage, clock, transport and device fields, with no backoff
        /// jitter. Tests only.
        /// </summary>
        internal static void ResetForTests(IKeyValueStore store, IClock clock, IIngestTransport transport, string platform) {
            _engine = new AssignmentEngine(store, clock, transport, platform, () => 1.0);
        }

        // Runs first in play mode, on the main thread. Also resets statics when domain reload is disabled.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForPlayMode() {
            _engine = CreateEngine();
            _engine.SetMainThread(Thread.CurrentThread.ManagedThreadId);
        }

        // Starts the session before the first scene loads: the user id exists before any game code starts an SDK, and
        // the fetch timer runs from app start. The group is not decided here.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void StartSessionOnLoad() {
            _engine.EnsureSession();
        }

        private static AssignmentEngine CreateEngine() {
            var random = new System.Random();
            return new AssignmentEngine(new PlayerPrefsStore(), new SystemClock(), new UnityWebRequestTransport(),
                CurrentPlatform(), () => 0.5 + 0.5 * random.NextDouble());
        }

        // Events are sent from Android and iOS players only.
        private static string CurrentPlatform() {
#if UNITY_ANDROID && !UNITY_EDITOR
            return "android";
#elif UNITY_IOS && !UNITY_EDITOR
            return "ios";
#else
            return null;
#endif
        }

        private static bool IsTagged(string placement) {
            return placement.StartsWith(Labels.Metica + "|", StringComparison.Ordinal) ||
                   placement.StartsWith(Labels.Baseline + "|", StringComparison.Ordinal) ||
                   placement.StartsWith(Labels.Unassigned + "|", StringComparison.Ordinal);
        }
    }
}
