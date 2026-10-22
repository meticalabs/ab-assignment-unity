using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace Metica.Assignment {
    /// <summary>
    /// Sends queued events to Metica ingestion the way the Metica SDK sends custom events, so they land in the same
    /// tables: POST {base}/ingest/v1/events with an X-API-KEY header and a {"events":[...]} body of at most 10 events.
    /// One request at a time, oldest events first. An event leaves the queue on a 2xx answer, or on a 4xx other than 408
    /// and 429 (ingestion rejected it; retrying cannot help). Anything else keeps it queued and retries with exponential
    /// backoff capped at 5 minutes. On Metica production the relay host takes over when the main host is unreachable
    /// or stalls; a custom domain has no relay. Main thread only.
    /// </summary>
    internal sealed class MeticaIngestEventSender {
        internal const string ProductionBaseUrl = "https://api-global-gateway.prod-eu.metica.com";
        internal const string RelayBaseUrl = "https://relay.prod-eu.metica.com";

        internal const int MaxBatchSize = 10;
        internal const long MaxBackoffMs = 300000;

        // Two, as in the SDK, so one bad request on a working path does not move the route.
        private const int UnreachableSendsBeforeSwitch = 2;

        private sealed class Host {
            public readonly string Id;
            public readonly string BaseUrl;

            public Host(string id, string baseUrl) {
                Id = id;
                BaseUrl = baseUrl;
            }
        }

        private readonly EventQueue _queue;
        private readonly IClock _clock;
        private readonly IIngestTransport _transport;
        private readonly Func<double> _jitter;
        private readonly Func<string> _consentStatus;

        private string _appId;
        private string _apiKey;
        private string _platform;
        private List<Host> _hosts;
        private int _hostIndex;
        private int _unreachableSends;

        private bool _inFlight;
        private int _failedSends;
        private long _nextAttemptMs;

        /// <param name="jitter">Returns a factor in [0.5, 1.0] applied to each backoff delay.</param>
        /// <param name="consentStatus">The consent label to put in consent_status at send time.</param>
        public MeticaIngestEventSender(EventQueue queue, IClock clock, IIngestTransport transport, Func<double> jitter,
            Func<string> consentStatus) {
            _queue = queue;
            _clock = clock;
            _transport = transport;
            _jitter = jitter;
            _consentStatus = consentStatus;
        }

        public bool IsConfigured => _hosts != null;

        /// <summary>The base URL events go to first.</summary>
        public string BaseUrl => _hosts[0].BaseUrl;

        /// <param name="platform">"android" or "ios"; null on any other platform (editor, desktop), where events are
        /// logged and discarded instead of sent.</param>
        public void Configure(MeticaAssignmentOptions options, string platform) {
            _appId = options.AppId;
            _apiKey = options.ApiKey;
            _platform = platform;
            _hosts = HostsFor(options);
            _hostIndex = 0;
            if (platform == null) {
                AssignmentLog.Warning("Events are not sent from this platform (only Android and iOS); they are logged and discarded.");
            }
        }

        /// <summary>Sends the next batch if one is waiting, nothing is in flight and the backoff has passed.</summary>
        public void TrySend() {
            if (!IsConfigured || _inFlight || _queue.Count == 0 || _clock.ElapsedMilliseconds < _nextAttemptMs) {
                return;
            }
            List<QueuedEvent> batch = _queue.PeekBatch(MaxBatchSize);
            var ids = new HashSet<string>();
            foreach (QueuedEvent e in batch) {
                ids.Add(e.EventId);
            }
            string body = BuildBody(batch, _appId, _platform, _consentStatus());
            if (_platform == null) {
                AssignmentLog.Info("Not sent on this platform: " + body);
                _queue.Remove(ids);
                TrySend();
                return;
            }
            Host host = _hosts[_hostIndex];
            string url = BuildUrl(host, _appId, batch[0].UserId, _platform);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            AssignmentLog.Info("POST " + url + " " + body);
#endif
            _inFlight = true;
            try {
                _transport.Post(url, _apiKey, body, response => OnResponse(host, ids, batch.Count, response));
            } catch (Exception e) {
                AssignmentLog.Error("Could not start the ingestion request: " + e);
                OnResponse(host, ids, batch.Count, new IngestResponse { Error = e.Message });
            }
        }

        /// <summary>The app is going to the background: send now rather than after the backoff.</summary>
        public void SendNow() {
            _nextAttemptMs = 0;
            TrySend();
        }

        private void OnResponse(Host host, HashSet<string> ids, int count, IngestResponse response) {
            _inFlight = false;
            if (response.Status > 0) {
                OnAnswered(host, response.Status);
            } else if (response.TimedOut) {
                // UnityWebRequest does not tell a connect timeout from a stalled upload, so any timeout counts as a
                // stall and moves to the next host at once; the SDK moves only on a stall after connecting.
                SwitchHost(host, "stalled");
            } else {
                OnUnreachable(host);
            }

            if (response.Status >= 200 && response.Status < 300) {
                _queue.Remove(ids);
                _failedSends = 0;
                _nextAttemptMs = 0;
                AssignmentLog.Info("Sent " + count + " event(s) to Metica (HTTP " + response.Status + ", route " + host.Id + ").");
                TrySend();
                return;
            }
            if (IsPermanentFailure(response.Status)) {
                _queue.Remove(ids);
                _failedSends = 0;
                _nextAttemptMs = 0;
                AssignmentLog.Error("Metica ingestion rejected " + count + " event(s) with HTTP " + response.Status +
                                    "; they were dropped. Check the app id and API key.");
                TrySend();
                return;
            }
            _failedSends++;
            long delay = BackoffMs(_failedSends, _jitter());
            _nextAttemptMs = _clock.ElapsedMilliseconds + delay;
            string reason = response.Error;
            if (response.Status > 0) {
                reason = "HTTP " + response.Status;
            }
            // Info, not Warning: offline players retry for a long time, and games may report warnings as errors.
            AssignmentLog.Info("Sending " + count + " event(s) failed (" + reason + "); retrying in " + delay + " ms.");
        }

        // Host choice, as in the SDK's IngestHostSelector: an answer from the main host resets the count; a 5xx from
        // the relay counts as unreachable because the relay answers 502 when its own call to the backend fails.
        private void OnAnswered(Host host, long status) {
            if (_hostIndex != 0 && status >= 500 && status <= 599) {
                OnUnreachable(host);
                return;
            }
            _unreachableSends = 0;
        }

        private void OnUnreachable(Host host) {
            _unreachableSends++;
            if (_unreachableSends >= UnreachableSendsBeforeSwitch) {
                SwitchHost(host, "unreachable");
            }
        }

        private void SwitchHost(Host from, string reason) {
            if (_hosts.Count == 1) {
                return;
            }
            _hostIndex = (_hostIndex + 1) % _hosts.Count;
            _unreachableSends = 0;
            AssignmentLog.Info("Ingestion host " + from.Id + " " + reason + "; switching to " + _hosts[_hostIndex].Id + ".");
        }

        /// <summary>4xx except 408 and 429: ingestion rejected the request and a retry cannot help.</summary>
        internal static bool IsPermanentFailure(long status) {
            return status >= 400 && status <= 499 && status != 408 && status != 429;
        }

        /// <summary>2^failedSends seconds times the jitter factor, capped at 5 minutes.</summary>
        internal static long BackoffMs(int failedSends, double jitter) {
            double exponential = 1000.0 * Math.Pow(2, Math.Min(failedSends, 30));
            return (long)(Math.Min(exponential, MaxBackoffMs) * jitter);
        }

        // As the SDK's IngestHostSelector.hostsFor: only Metica production gets the relay behind it. A custom domain is
        // a single host, unless it resolves to the production URL itself.
        private static List<Host> HostsFor(MeticaAssignmentOptions options) {
            string baseUrl = CustomDomainValidator.ResolveBaseUrl(options.CustomDomain, ProductionBaseUrl);
            if (baseUrl == ProductionBaseUrl) {
                return new List<Host> { new Host("ga", ProductionBaseUrl), new Host("relay", RelayBaseUrl) };
            }
            return new List<Host> { new Host("primary", baseUrl) };
        }

        private static string BuildUrl(Host host, string appId, string userId, string platform) {
            return host.BaseUrl + "/ingest/v1/events?appId=" + Encode(appId) + "&userId=" + Encode(userId) +
                   "&platform=" + platform;
        }

        /// <summary>
        /// The request body. Each event has only what the uplift analysis needs: eventType (the event name), eventId,
        /// appId, eventTime (epoch ms), userId, platform and customPayload (with consent_status).
        /// </summary>
        internal static string BuildBody(List<QueuedEvent> batch, string appId, string platform, string consentStatus) {
            var events = new List<Dictionary<string, object>>(batch.Count);
            foreach (QueuedEvent e in batch) {
                var payload = new Dictionary<string, object>(e.Payload);
                payload["consent_status"] = consentStatus;
                var item = new Dictionary<string, object> {
                    { "eventType", e.Name },
                    { "eventId", e.EventId },
                    { "appId", appId },
                    { "eventTime", e.EventTimeMs },
                    { "userId", e.UserId },
                    { "platform", platform },
                    { "customPayload", payload }
                };
                events.Add(item);
            }
            return JsonConvert.SerializeObject(new Dictionary<string, object> { { "events", events } }, Formatting.None);
        }

        /// <summary>The SDK's query encoding: trimmed, RFC 3986 percent-encoding, safe set A-Z a-z 0-9 - _ . *.</summary>
        internal static string Encode(string value) {
            var sb = new StringBuilder();
            foreach (byte b in Encoding.UTF8.GetBytes((value ?? "").Trim())) {
                char c = (char)b;
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                    c == '-' || c == '_' || c == '.' || c == '*') {
                    sb.Append(c);
                } else {
                    sb.Append('%').Append(b.ToString("X2"));
                }
            }
            return sb.ToString();
        }
    }
}
