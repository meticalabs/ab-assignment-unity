using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Threading;

namespace AB.Assignment {
    /// <summary>
    /// State and logic behind <see cref="ABAssignment"/>. One instance per process.
    /// </summary>
    internal sealed class AssignmentEngine {
        internal const string UserIdKey = "metica_assignment_user_id_v1";
        internal const string ConfigKey = "metica_assignment_config_v1";
        internal const string EnrollmentKey = "metica_assignment_v1";
        internal const string DailyKey = "metica_assignment_daily_v1";
        internal const string CustomIdsKey = "metica_assignment_custom_ids_v1";
        internal const string DebugKey = "metica_assignment_debug_v1";
        internal const string ConsentKey = "metica_assignment_consent_v1";

        internal const string AssignmentEvent = "upliftAssignment";
        internal const string ConfigFetchEvent = "upliftConfigFetch";
        internal const string AdsInitEvent = "upliftAdsInit";

        internal const long ConfigFetchTimeoutMs = 10000;
        internal const long AdsInitTimeoutMs = 30000;

        private const int MaxEnrollments = 8;
        private const int MaxErrorLength = 256;

        private readonly IKeyValueStore _store;
        private readonly IClock _clock;
        private readonly EventQueue _queue;
        private readonly MeticaIngestEventSender _sender;
        private readonly string _platform;
        private readonly List<Action<string, IReadOnlyDictionary<string, object>>> _listeners =
            new List<Action<string, IReadOnlyDictionary<string, object>>>();

        private int _mainThreadId = -1;

        // Passed on in consent_status on every event; never changes what is sent.
        private ConsentStatus _consentStatus = ConsentStatus.Unknown;

        // Session: started on first use or before the first scene loads. The group is not decided yet.
        private bool _sessionStarted;
        private string _userId = "";
        private string _sessionId = "";
        private string _sessionError;
        private long _initMs;

        // A valid Firebase value received in this session before the group was decided.
        private AssignmentConfig _remoteThisSession;

        // The group, decided once on first need and fixed for the rest of the session.
        private volatile AssignmentResult _current;
        private DateTime _lastSeenUtcDate;

        private bool _configFetchSent;
        private bool _configFetchTimedOut;
        private bool _configFetchLateSent;

        private bool _adsInitStarted;
        private long _adsInitStartMs;
        private bool _adsInitTimeoutSent;
        private bool _adsInitOutcomeSent;
        // skipped_low_memory was reported: one later Started plus one terminal outcome are still reported.
        private bool _adsInitSkippedRetryAllowed;

        /// <param name="platform">"android" or "ios"; null on other platforms, where events are not sent.</param>
        /// <param name="jitter">Backoff jitter factor in [0.5, 1.0].</param>
        public AssignmentEngine(IKeyValueStore store, IClock clock, IIngestTransport transport, string platform,
            Func<double> jitter) {
            _store = store;
            _clock = clock;
            _platform = platform;
            _queue = new EventQueue(store);
            _sender = new MeticaIngestEventSender(_queue, clock, transport, jitter, () => Labels.For(_consentStatus));
        }

        /// <summary>The Unity main thread, recorded before any scene loads. Unknown (-1) in edit mode.</summary>
        public void SetMainThread(int threadId) {
            _mainThreadId = threadId;
        }

        /// <summary>The generated user id. Starts the session if needed, but never decides the group.</summary>
        public string UserId {
            get {
                if (!EnsureSession()) {
                    return null;
                }
                return _userId;
            }
        }

        /// <summary>The decided assignment, or null while the group has not been needed yet. Never decides it.</summary>
        public AssignmentResult Peek => _current;

        /// <summary>
        /// Starts the session once: user id, event queue, fetch timer, driver. Returns false only when the first use
        /// happens off the main thread (PlayerPrefs is main-thread only).
        /// </summary>
        public bool EnsureSession() {
            if (_sessionStarted) {
                return true;
            }
            if (_mainThreadId != -1 && Thread.CurrentThread.ManagedThreadId != _mainThreadId) {
                AssignmentLog.Error("The assignment was first used off the main thread; it is not available yet.");
                return false;
            }
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _sessionStarted = true;
            _initMs = _clock.ElapsedMilliseconds;
            _sessionId = Guid.NewGuid().ToString("N");
            try {
                _consentStatus = LoadConsentStatus();
                _userId = LoadOrCreateUserId();
            } catch (Exception e) {
                AssignmentLog.Error("Starting the session failed; the device will be unassigned this session: " + e);
                _sessionError = Truncate(e.GetType().Name + ": " + e.Message);
            }
            // Separate, so a failure above does not leave earlier sessions' events unloaded (and then overwritten).
            try {
                _queue.Load(CompleteOldEvent);
            } catch (Exception e) {
                AssignmentLog.Error("Loading the stored event queue failed: " + e);
            }
            try {
                AssignmentDriver.Create();
            } catch (Exception e) {
                AssignmentLog.Error("Could not create the driver object; deadlines and day changes are not checked: " + e);
            }
            return true;
        }

        /// <summary>
        /// Decides the group on first need and returns it; later calls return the same result. Uses a valid Firebase
        /// value received earlier in this session, otherwise the cached one; with neither, the session is unassigned.
        /// Returns null only when called off the main thread before the group exists.
        /// </summary>
        public AssignmentResult EnsureAssigned() {
            AssignmentResult current = _current;
            if (current != null) {
                return current;
            }
            if (_mainThreadId != -1 && Thread.CurrentThread.ManagedThreadId != _mainThreadId) {
                AssignmentLog.Error("The group was first needed off the main thread; it is not decided yet. " +
                                    "Read Current once on the main thread before any ad call.");
                return null;
            }
            if (!EnsureSession()) {
                return null;
            }
            AssignmentResult result;
            try {
                result = Decide();
            } catch (Exception e) {
                AssignmentLog.Error("Deciding the group failed; the device is unassigned this session: " + e);
                result = AssignmentResult.CreateUnassigned(_userId, _sessionId, AssignmentResult.SourceUnassignedError,
                    Truncate(e.GetType().Name + ": " + e.Message));
            }
            _current = result;
            _lastSeenUtcDate = _clock.UtcNow.Date;
            AssignmentLog.Info("Group " + result.Label + ", route " + RouteText(result.RunMetica) +
                               ", experiment \"" + result.ExperimentId + "\", source \"" + result.ConfigSource +
                               "\", bucket " + result.Bucket + ", user " + result.UserId);
            if (!_sender.IsConfigured) {
                AssignmentLog.Warning("ABAssignment.Initialize has not been called yet; events are queued until it is.");
            }
            try {
                EmitAssignment();
            } catch (Exception e) {
                AssignmentLog.Error("Could not emit " + AssignmentEvent + ": " + e);
            }
            return result;
        }

        public void Initialize(ABAssignmentOptions options) {
            if (!CheckMainThread(nameof(Initialize))) {
                return;
            }
            if (options == null || string.IsNullOrWhiteSpace(options.AppId) || string.IsNullOrWhiteSpace(options.ApiKey)) {
                AssignmentLog.Error("Initialize needs options with a non-empty AppId and ApiKey; call ignored. Events stay queued.");
                return;
            }
            // Throws on an invalid custom domain before anything changes, so a corrected call can follow.
            string baseUrl = CustomDomainValidator.ResolveBaseUrl(options.CustomDomain, MeticaIngestEventSender.ProductionBaseUrl);
            if (_sender.IsConfigured) {
                AssignmentLog.Warning("Initialize was called more than once; the second call is ignored.");
                return;
            }
            if (!EnsureSession()) {
                return;
            }
            _sender.Configure(options, baseUrl, _platform);
            AssignmentLog.Info("Events go to Metica ingestion at " + _sender.BaseUrl + " for app " + options.AppId + ".");
            _sender.TrySend();
        }

        public void AddListener(Action<string, IReadOnlyDictionary<string, object>> listener) {
            if (listener == null || !CheckMainThread("EventLogged")) {
                return;
            }
            _listeners.Add(listener);
        }

        public void RemoveListener(Action<string, IReadOnlyDictionary<string, object>> listener) {
            if (listener == null || !CheckMainThread("EventLogged")) {
                return;
            }
            _listeners.Remove(listener);
        }


        public void SetConsentStatus(ConsentStatus status) {
            if (!CheckMainThread(nameof(SetConsentStatus)) || !EnsureSession()) {
                return;
            }
            if (status != ConsentStatus.Unknown && status != ConsentStatus.Granted && status != ConsentStatus.Denied) {
                AssignmentLog.Error("Unknown ConsentStatus " + status + "; call ignored.");
                return;
            }
            _consentStatus = status;
            if (status == ConsentStatus.Unknown) {
                _store.DeleteKey(ConsentKey);
            } else {
                _store.SetString(ConsentKey, Labels.For(status));
            }
            _store.Save();
            AssignmentLog.Info("Consent status " + Labels.For(status) + "; it is sent with every event from now on.");
        }

        public void AddCustomUserId(string source, string id) {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(id)) {
                AssignmentLog.Error("AddCustomUserId needs a non-empty source and id; call ignored.");
                return;
            }
            if (!CheckMainThread(nameof(AddCustomUserId)) || !EnsureSession()) {
                return;
            }
            bool changed = StoreCustomUserId(source, id);
            // Before the group is decided the pair is only stored; the first upliftAssignment carries it.
            if (changed && _current != null) {
                EmitAssignment();
            }
        }

        public void ReportRemoteConfig(RemoteConfigStatus status, string valueOrNull, string errorOrNull) {
            if (!CheckMainThread(nameof(ReportRemoteConfig)) || !EnsureSession()) {
                return;
            }
            string fetchStatus;
            string error = errorOrNull;
            switch (status) {
                case RemoteConfigStatus.Success:
                    if (string.IsNullOrEmpty(valueOrNull)) {
                        fetchStatus = "missing";
                    } else if (AssignmentConfig.TryParse(valueOrNull, out AssignmentConfig config, out string invalidReason)) {
                        CacheConfig(valueOrNull);
                        fetchStatus = "success";
                        if (_current == null) {
                            _remoteThisSession = config;
                            AssignmentLog.Info("Remote config \"" + config.ExperimentId +
                                               "\" received before the group was needed; it applies to this session.");
                        } else {
                            AssignmentLog.Info("Cached remote config \"" + config.ExperimentId +
                                               "\"; the group is already decided, so it applies from the next session.");
                        }
                    } else {
                        fetchStatus = "invalid";
                        error = invalidReason;
                        AssignmentLog.Warning("Remote config value is invalid and was ignored: " + invalidReason);
                    }
                    break;
                case RemoteConfigStatus.FetchFailed:
                    fetchStatus = "fetch_failed";
                    break;
                case RemoteConfigStatus.ActivationFailed:
                    fetchStatus = "activation_failed";
                    break;
                default:
                    AssignmentLog.Error("Unknown RemoteConfigStatus " + status + "; call ignored.");
                    return;
            }

            if (_configFetchSent) {
                if (!_configFetchTimedOut || _configFetchLateSent) {
                    AssignmentLog.Info("Fetch result " + fetchStatus + " was not sent: " + ConfigFetchEvent +
                                       " was already sent this session.");
                    return;
                }
                _configFetchLateSent = true;
            }
            _configFetchSent = true;
            EmitConfigFetch(fetchStatus, error);
        }

        public void ReportAdsInit(AdsInitStage stage, string errorOrNull,
            string meticaInternalGroup, bool? forcedHoldout) {
            if (!CheckMainThread(nameof(ReportAdsInit)) || EnsureAssigned() == null) {
                return;
            }
            if (stage == AdsInitStage.Started) {
                if (_adsInitStarted && !_adsInitSkippedRetryAllowed) {
                    AssignmentLog.Warning("Ads init Started was reported twice; the second report is ignored.");
                    return;
                }
                // A Started after skipped_low_memory opens a new attempt: its outcome and 30 s timeout count from here.
                _adsInitSkippedRetryAllowed = false;
                _adsInitOutcomeSent = false;
                _adsInitTimeoutSent = false;
                _adsInitStarted = true;
                _adsInitStartMs = _clock.ElapsedMilliseconds;
                return;
            }
            if (_adsInitOutcomeSent) {
                AssignmentLog.Warning("Ads init outcome " + stage + " was ignored: an outcome was already reported this session.");
                return;
            }
            string outcome;
            switch (stage) {
                case AdsInitStage.Succeeded:
                    outcome = "success";
                    break;
                case AdsInitStage.Failed:
                    outcome = "failed";
                    break;
                case AdsInitStage.SkippedLowMemory:
                    outcome = "skipped_low_memory";
                    break;
                default:
                    AssignmentLog.Error("Unknown AdsInitStage " + stage + "; call ignored.");
                    return;
            }
            Dictionary<string, object> payload = AdsInitPayload(outcome);
            if (stage == AdsInitStage.Succeeded) {
                if (meticaInternalGroup != null) {
                    payload["metica_internal_group"] = meticaInternalGroup;
                }
                if (forcedHoldout.HasValue) {
                    payload["forced_holdout"] = forcedHoldout.Value;
                }
            }
            if (stage == AdsInitStage.Failed && !string.IsNullOrEmpty(errorOrNull)) {
                payload["error"] = Truncate(errorOrNull);
            }
            _adsInitOutcomeSent = true;
            // skipped_low_memory is provisional: one later Started plus one outcome may still be reported.
            _adsInitSkippedRetryAllowed = stage == AdsInitStage.SkippedLowMemory;
            Emit(AdsInitEvent, payload);
        }

        public void FlushEvents() {
            if (!CheckMainThread(nameof(FlushEvents))) {
                return;
            }
            _sender.TrySend();
        }

        public void Tick() {
            if (!_sessionStarted || !CheckMainThread(nameof(Tick))) {
                return;
            }
            long now = _clock.ElapsedMilliseconds;
            if (!_configFetchSent && now - _initMs >= ConfigFetchTimeoutMs) {
                _configFetchSent = true;
                _configFetchTimedOut = true;
                EmitConfigFetch("timeout", null);
            }
            if (_adsInitStarted && !_adsInitOutcomeSent && !_adsInitTimeoutSent &&
                now - _adsInitStartMs >= AdsInitTimeoutMs) {
                _adsInitTimeoutSent = true;
                Emit(AdsInitEvent, AdsInitPayload("pending_timeout"));
            }
            if (_current != null) {
                CheckDayChange();
            }
            _sender.TrySend();
        }

        public void OnApplicationResumed() {
            if (!_sessionStarted || !CheckMainThread(nameof(OnApplicationResumed))) {
                return;
            }
            if (_current != null) {
                CheckDayChange();
            }
            _sender.TrySend();
        }

        /// <summary>The app is going to the background or lost focus: send what is queued now.</summary>
        public void OnApplicationPaused() {
            if (!_sessionStarted || !CheckMainThread(nameof(OnApplicationPaused))) {
                return;
            }
            _sender.SendNow();
        }

        public void SetDebugOverride(Group? group) {
            if (!CheckMainThread(nameof(SetDebugOverride))) {
                return;
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (group.HasValue) {
                _store.SetString(DebugKey, Labels.For(group.Value));
                AssignmentLog.Info("Debug override set to " + Labels.For(group.Value) + "; it applies from the next session.");
            } else {
                _store.DeleteKey(DebugKey);
                AssignmentLog.Info("Debug override cleared; this applies from the next session.");
            }
            _store.Save();
#else
            AssignmentLog.Warning("SetDebugOverride is ignored outside development builds.");
#endif
        }

        private AssignmentResult Decide() {
            if (_sessionError != null) {
                return AssignmentResult.CreateUnassigned(_userId, _sessionId, AssignmentResult.SourceUnassignedError,
                    _sessionError);
            }
            AssignmentConfig config;
            string source;
            if (_remoteThisSession != null) {
                config = _remoteThisSession;
                source = AssignmentResult.SourceRemoteThisSession;
            } else if (TryLoadCachedConfig(out AssignmentConfig cached)) {
                config = cached;
                source = AssignmentResult.SourceCachedRemote;
            } else {
                return AssignmentResult.CreateUnassigned(_userId, _sessionId, AssignmentResult.SourceUnassignedWaiting,
                    "no Firebase config received before the group was needed");
            }

            int bucket = Bucketing.Bucket(config.ExperimentId, _userId);
            Group group = Bucketing.GroupFor(bucket, config.MeticaPercentageBasisPoints);
            // The kill switch wins: with enabled false the debug override cannot route to Metica.
            bool runMetica = config.Enabled && ApplyDebugOverride(group == Group.Metica);
            bool isNew = RecordEnrollment(config.ExperimentId);
            return new AssignmentResult(group, runMetica, config.Enabled, config.ExperimentId,
                config.MeticaPercentageBasisPoints, bucket, _userId, _sessionId, source, isNew, null);
        }

        private bool ApplyDebugOverride(bool runMetica) {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            string value = _store.GetString(DebugKey);
            if (value == Labels.Metica) {
                AssignmentLog.Warning("Debug override active: routing to Metica.");
                return true;
            }
            if (value == Labels.Baseline || value == Labels.Unassigned) {
                AssignmentLog.Warning("Debug override active: not routing to Metica.");
                return false;
            }
#endif
            return runMetica;
        }

        private ConsentStatus LoadConsentStatus() {
            string stored = _store.GetString(ConsentKey);
            if (stored == Labels.ConsentGranted) {
                return ConsentStatus.Granted;
            }
            if (stored == Labels.ConsentDenied) {
                return ConsentStatus.Denied;
            }
            return ConsentStatus.Unknown;
        }

        private string LoadOrCreateUserId() {
            string userId = _store.GetString(UserIdKey);
            if (!string.IsNullOrEmpty(userId)) {
                return userId;
            }
            userId = Guid.NewGuid().ToString("N");
            _store.SetString(UserIdKey, userId);
            _store.Save();
            return userId;
        }

        private bool TryLoadCachedConfig(out AssignmentConfig config) {
            config = null;
            string stored = _store.GetString(ConfigKey);
            if (string.IsNullOrEmpty(stored)) {
                return false;
            }
            if (!Json.TryParse(stored, out object root, out _) ||
                !(root is Dictionary<string, object> fields) ||
                !fields.TryGetValue("value", out object value) ||
                !(value is string json)) {
                AssignmentLog.Warning("Cached remote config is unreadable and was ignored.");
                return false;
            }
            if (!AssignmentConfig.TryParse(json, out config, out string error)) {
                AssignmentLog.Warning("Cached remote config is invalid (" + error + ") and was ignored.");
                return false;
            }
            return true;
        }

        private void CacheConfig(string json) {
            var stored = new Dictionary<string, object> {
                { "value", json },
                { "received_at_utc", _clock.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) }
            };
            _store.SetString(ConfigKey, Json.Write(stored));
            _store.Save();
        }

        /// <summary>Returns true if this is the first session of the device under the experiment.</summary>
        private bool RecordEnrollment(string experimentId) {
            var records = new List<object>();
            string stored = _store.GetString(EnrollmentKey);
            if (!string.IsNullOrEmpty(stored)) {
                if (Json.TryParse(stored, out object root, out _) && root is List<object> list) {
                    records = list;
                } else {
                    AssignmentLog.Warning("Stored enrolment records are unreadable and were reset.");
                }
            }
            foreach (object record in records) {
                if (record is List<object> pair && pair.Count == 2 && pair[0] is string id && id == experimentId) {
                    return false;
                }
            }
            records.Add(new List<object> {
                experimentId,
                _clock.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            });
            while (records.Count > MaxEnrollments) {
                records.RemoveAt(0);
            }
            _store.SetString(EnrollmentKey, Json.Write(records));
            _store.Save();
            return true;
        }

        /// <summary>Stores the pair; returns false if it was already stored with the same id.</summary>
        private bool StoreCustomUserId(string source, string id) {
            Dictionary<string, string> ids = LoadCustomIds();
            if (ids.TryGetValue(source, out string existing) && existing == id) {
                return false;
            }
            ids[source] = id;
            var stored = new Dictionary<string, object>(ids.Count);
            foreach (KeyValuePair<string, string> pair in ids) {
                stored[pair.Key] = pair.Value;
            }
            _store.SetString(CustomIdsKey, Json.Write(stored));
            _store.Save();
            return true;
        }

        private Dictionary<string, string> LoadCustomIds() {
            var ids = new Dictionary<string, string>();
            string stored = _store.GetString(CustomIdsKey);
            if (string.IsNullOrEmpty(stored)) {
                return ids;
            }
            if (!Json.TryParse(stored, out object root, out _) || !(root is Dictionary<string, object> fields)) {
                AssignmentLog.Warning("Stored custom user ids are unreadable and were ignored.");
                return ids;
            }
            foreach (KeyValuePair<string, object> pair in fields) {
                if (pair.Value is string id) {
                    ids[pair.Key] = id;
                }
            }
            return ids;
        }

        /// <summary>source=id pairs, URL-encoded, sorted by source, comma separated. Empty when none.</summary>
        internal static string EncodeCustomIds(Dictionary<string, string> ids) {
            var sources = new List<string>(ids.Keys);
            sources.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            foreach (string source in sources) {
                if (sb.Length > 0) {
                    sb.Append(',');
                }
                sb.Append(Uri.EscapeDataString(source)).Append('=').Append(Uri.EscapeDataString(ids[source]));
            }
            return sb.ToString();
        }

        private void CheckDayChange() {
            DateTime today = _clock.UtcNow.Date;
            if (today == _lastSeenUtcDate) {
                return;
            }
            _lastSeenUtcDate = today;
            if (_store.GetString(DailyKey) == DailyMarker(_current.ExperimentId, FormatDate(today))) {
                return;
            }
            EmitAssignment();
        }

        private void EmitAssignment() {
            AssignmentResult r = _current;
            string date = FormatDate(_clock.UtcNow);
            var payload = new Dictionary<string, object> {
                { "experiment_id", r.ExperimentId },
                { "group", r.Label },
                { "metica_route", r.RunMetica }
            };
            if (r.Group != Group.Unassigned) {
                payload["metica_percentage"] = PercentageValue(r.MeticaPercentageBasisPoints);
            }
            // remote_this_session / cached_remote when assigned; unassigned_waiting / unassigned_error when not.
            payload["config_source"] = r.ConfigSource;
            payload["session_id"] = r.SessionId;
            payload["activity_date_utc"] = date;
            string customIds = EncodeCustomIds(LoadCustomIds());
            if (customIds.Length > 0) {
                payload["custom_user_ids"] = customIds;
            }
            if (r.Error != null) {
                payload["error"] = r.Error;
            }
            _store.SetString(DailyKey, DailyMarker(r.ExperimentId, date));
            Emit(AssignmentEvent, payload);
        }

        private void EmitConfigFetch(string fetchStatus, string error) {
            var payload = new Dictionary<string, object> {
                { "rc_received", fetchStatus == "success" },
                { "fetch_status", fetchStatus },
                { "fetch_latency_ms", _clock.ElapsedMilliseconds - _initMs },
                { "session_id", _sessionId },
                { "activity_date_utc", FormatDate(_clock.UtcNow) }
            };
            if (!string.IsNullOrEmpty(error)) {
                payload["error"] = Truncate(error);
            }
            Emit(ConfigFetchEvent, payload);
        }

        /// <summary>network = the primary network this session's route starts: metica when routing to Metica, else applovin.</summary>
        private Dictionary<string, object> AdsInitPayload(string outcome) {
            string network = "applovin";
            if (_current.RunMetica) {
                network = "metica";
            }
            long elapsed = 0;
            if (_adsInitStarted) {
                elapsed = _clock.ElapsedMilliseconds - _adsInitStartMs;
            }
            return new Dictionary<string, object> {
                { "session_id", _current.SessionId },
                { "experiment_id", _current.ExperimentId },
                { "group", _current.Label },
                { "network", network },
                { "outcome", outcome },
                { "elapsed_ms", elapsed },
                { "activity_date_utc", FormatDate(_clock.UtcNow) }
            };
        }

        /// <summary>
        /// Queues the event for Metica, then gives a copy to the listeners. A listener can neither change nor stop
        /// delivery to Metica.
        /// </summary>
        private void Emit(string eventName, Dictionary<string, object> payload) {
            // consent_status is added at send time, so it counts towards the key limit here.
            if (!EventValidation.TryValidate(eventName, payload, 1, out string error)) {
                AssignmentLog.Error("Event " + eventName + " is invalid and was dropped: " + error);
                return;
            }
            EventValidation.CapStrings(payload);
            var e = new QueuedEvent {
                Name = eventName,
                EventId = Guid.NewGuid().ToString("D"),
                EventTimeMs = ToUnixMs(_clock.UtcNow),
                UserId = _userId,
                Payload = payload
            };
            _queue.Enqueue(e);
            NotifyListeners(eventName, e.Payload);
            _sender.TrySend();
        }

        /// <summary>
        /// Each listener gets a read-only copy with consent_status as it is now. A listener that throws is logged and
        /// skipped; the others and the Metica queue are not affected.
        /// </summary>
        private void NotifyListeners(string eventName, Dictionary<string, object> payload) {
            if (_listeners.Count == 0) {
                return;
            }
            var values = new Dictionary<string, object>(payload) { ["consent_status"] = Labels.For(_consentStatus) };
            var copy = new ReadOnlyDictionary<string, object>(values);
            // A listener may add or remove listeners; iterate over a snapshot.
            foreach (Action<string, IReadOnlyDictionary<string, object>> listener in _listeners.ToArray()) {
                try {
                    listener(eventName, copy);
                } catch (Exception ex) {
                    AssignmentLog.Error("An EventLogged listener threw for " + eventName + "; Metica delivery is not affected: " + ex);
                }
            }
        }

        /// <summary>
        /// An event stored by 0.1.0 has only a name and payload. It gets a new id, the current time and the user.
        /// </summary>
        private void CompleteOldEvent(QueuedEvent e) {
            e.EventId = Guid.NewGuid().ToString("D");
            e.EventTimeMs = ToUnixMs(_clock.UtcNow);
            e.UserId = _userId;
            // 0.1.0 cut errors at 256 characters; ingestion would accept a longer string and drop it later.
            EventValidation.CapStrings(e.Payload);
        }

        private static long ToUnixMs(DateTime utc) {
            return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        /// <summary>
        /// State changes and PlayerPrefs access are main-thread only. In edit mode the main thread is not known until
        /// the first use.
        /// </summary>
        private bool CheckMainThread(string api) {
            if (_mainThreadId == -1 || Thread.CurrentThread.ManagedThreadId == _mainThreadId) {
                return true;
            }
            AssignmentLog.Error(api + " must be called on the main thread; call ignored.");
            return false;
        }

        /// <summary>Always a double (50 is sent as 50.0), so the event field always has one type.</summary>
        private static double PercentageValue(int basisPoints) {
            return basisPoints / 100.0;
        }

        private static string DailyMarker(string experimentId, string date) {
            return experimentId + "|" + date;
        }

        private static string FormatDate(DateTime utc) {
            return utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static string RouteText(bool runMetica) {
            if (runMetica) {
                return "metica";
            }
            return "own";
        }

        private static string Truncate(string text) {
            if (text == null || text.Length <= MaxErrorLength) {
                return text;
            }
            return text.Substring(0, MaxErrorLength);
        }
    }
}
