using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Metica.Assignment.Tests {
    public sealed class FakeStore : IKeyValueStore {
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        public readonly int OwnerThreadId = Thread.CurrentThread.ManagedThreadId;
        public bool AccessedFromOtherThread;
        /// <summary>Reading this key throws, to simulate a storage failure.</summary>
        public string ThrowOnKey;

        public string GetString(string key) {
            CheckThread();
            if (key == ThrowOnKey) {
                throw new InvalidOperationException("storage failure");
            }
            if (Values.TryGetValue(key, out string value)) {
                return value;
            }
            return null;
        }

        public void SetString(string key, string value) {
            CheckThread();
            Values[key] = value;
        }

        public void DeleteKey(string key) {
            CheckThread();
            Values.Remove(key);
        }

        public void Save() {
            CheckThread();
        }

        private void CheckThread() {
            if (Thread.CurrentThread.ManagedThreadId != OwnerThreadId) {
                AccessedFromOtherThread = true;
            }
        }
    }

    public sealed class FakeClock : IClock {
        public DateTime UtcNow { get; set; }
        public long ElapsedMilliseconds { get; set; }

        public FakeClock(DateTime utcNow) {
            UtcNow = utcNow;
        }

        public void Advance(TimeSpan by) {
            UtcNow += by;
            ElapsedMilliseconds += (long)by.TotalMilliseconds;
        }
    }

    public sealed class SentEvent {
        public readonly string Name;
        public readonly Dictionary<string, object> Payload;

        public SentEvent(string name, Dictionary<string, object> payload) {
            Name = name;
            Payload = payload;
        }
    }

    /// <summary>An EventLogged listener that records every event.</summary>
    public sealed class FakeListener {
        public readonly List<SentEvent> Sent = new List<SentEvent>();

        public void OnEvent(string eventName, IReadOnlyDictionary<string, object> payload) {
            var copy = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> pair in payload) {
                copy[pair.Key] = pair.Value;
            }
            Sent.Add(new SentEvent(eventName, copy));
        }

        public List<SentEvent> Named(string eventName) {
            return Sent.FindAll(e => e.Name == eventName);
        }
    }

    internal sealed class FakeRequest {
        public string Url;
        public string ApiKey;
        public string Body;
        public Action<IngestResponse> Complete;

        /// <summary>The events in the body.</summary>
        public JArray Events => (JArray)JObject.Parse(Body)["events"];

        public void Answer(long status) {
            Complete(new IngestResponse { Status = status });
        }

        public void Fail(bool timedOut = false) {
            Complete(new IngestResponse { Error = "network down", TimedOut = timedOut });
        }
    }

    /// <summary>
    /// Records every POST. With <see cref="AutoStatus"/> set, answers at once with that status; with null, the request
    /// stays pending until the test answers it.
    /// </summary>
    internal sealed class FakeTransport : IIngestTransport {
        public readonly List<FakeRequest> Requests = new List<FakeRequest>();
        public long? AutoStatus = 202;

        public void Post(string url, string apiKey, string body, Action<IngestResponse> onComplete) {
            var request = new FakeRequest { Url = url, ApiKey = apiKey, Body = body, Complete = onComplete };
            Requests.Add(request);
            if (AutoStatus.HasValue) {
                request.Answer(AutoStatus.Value);
            }
        }

        public FakeRequest Last => Requests[Requests.Count - 1];

        /// <summary>Every event sent in any request, in order.</summary>
        public List<JObject> AllEvents() {
            var events = new List<JObject>();
            foreach (FakeRequest request in Requests) {
                foreach (JToken e in request.Events) {
                    events.Add((JObject)e);
                }
            }
            return events;
        }
    }

    /// <summary>
    /// Base fixture: fake storage, clock, listener, transport and captured logs. Each StartSession call is a new cold start over the
    /// same storage; the first read of Current decides the group.
    /// </summary>
    public abstract class AssignmentTestBase {
        protected const string ExperimentA = "studio_mygame-20261016-50";
        protected const string ExperimentB = "studio_mygame-20261020-90";
        protected static readonly DateTime Start = new DateTime(2026, 10, 16, 10, 0, 0, DateTimeKind.Utc);

        protected const string AppId = "testApp";
        protected const string ApiKey = "test-api-key";

        /// <summary>Device fields as an Android device would give them.</summary>
        protected static readonly Dictionary<string, object> AndroidEnvironment = new Dictionary<string, object> {
            { "appVersion", "1.0.34" },
            { "platform", "android" },
            { "osVersion", "16" },
            { "buildNumber", "BP2A.250605.015" },
            { "deviceModel", "CPH2487" },
            { "deviceManufacturer", "OnePlus" },
            { "deviceType", "phone" },
            { "deviceMemory", 7226L },
            { "locale", "en-US" },
            { "localTimezone", "Europe/Ljubljana" },
            { "country", "si" }
        };

        protected FakeStore Store;
        protected FakeClock Clock;
        protected FakeListener Sink;
        private protected FakeTransport Transport;
        protected Dictionary<string, object> Environment;
        protected List<string> Errors;
        protected List<string> Warnings;
        private Action<LogType, string> _previousHandler;

        [SetUp]
        public void SetUpBase() {
            Store = new FakeStore();
            Clock = new FakeClock(Start);
            Sink = new FakeListener();
            Transport = new FakeTransport();
            Environment = AndroidEnvironment;
            Errors = new List<string>();
            Warnings = new List<string>();
            _previousHandler = AssignmentLog.Handler;
            AssignmentLog.Handler = (type, message) => {
                lock (Errors) {
                    if (type == LogType.Error) {
                        Errors.Add(message);
                    } else if (type == LogType.Warning) {
                        Warnings.Add(message);
                    }
                }
            };
        }

        [TearDown]
        public void TearDownBase() {
            AssignmentLog.Handler = _previousHandler;
            MeticaAssignment.ResetForTests(new FakeStore(), new FakeClock(Start), new FakeTransport(), AndroidEnvironment);
        }

        protected static string Config(string experimentId, string percentage, bool enabled) {
            string enabledText = "false";
            if (enabled) {
                enabledText = "true";
            }
            return "{\"experiment_id\":\"" + experimentId + "\",\"metica_percentage\":" + percentage +
                   ",\"enabled\":" + enabledText + "}";
        }

        protected static string ConfigA(bool enabled = true) {
            return Config(ExperimentA, "50", enabled);
        }

        protected static string ConfigB(bool enabled = true) {
            return Config(ExperimentB, "90", enabled);
        }

        /// <summary>A new process over the same storage, not initialized and without the listener.</summary>
        protected void NewBareProcess() {
            MeticaAssignment.ResetForTests(Store, Clock, Transport, Environment);
        }

        /// <summary>
        /// A new process over the same storage, initialized for production with the listener subscribed. Nothing is
        /// decided yet.
        /// </summary>
        protected void NewProcess() {
            NewBareProcess();
            MeticaAssignment.EventLogged += Sink.OnEvent;
            MeticaAssignment.Initialize(new MeticaAssignmentOptions(AppId, ApiKey));
        }

        /// <summary>
        /// A new cold start: new process, optionally a valid Firebase value reported before the group is needed, then
        /// the first read of Current decides the group.
        /// </summary>
        protected AssignmentResult StartSession(string remoteBeforeNeed = null) {
            NewProcess();
            if (remoteBeforeNeed != null) {
                MeticaAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, remoteBeforeNeed, null);
            }
            return MeticaAssignment.Current;
        }

        /// <summary>Writes a remote value to the cache, as if a previous session had received it.</summary>
        protected void CacheConfig(string json) {
            Store.Values[AssignmentEngine.ConfigKey] = Json.Write(new Dictionary<string, object> {
                { "value", json },
                { "received_at_utc", "2026-10-15T00:00:00Z" }
            });
        }

        /// <summary>A cold start assigned from a cached value; emits only the upliftAssignment event.</summary>
        protected AssignmentResult StartSessionCached(string json) {
            CacheConfig(json);
            return StartSession();
        }

        protected void SetUserId(string userId) {
            Store.Values[AssignmentEngine.UserIdKey] = userId;
        }

        /// <summary>First id of the form user-N whose group under the experiment is the wanted one.</summary>
        protected static string FindUserId(string experimentId, int percentageBasisPoints, Group wanted) {
            for (int i = 0; i < 100000; i++) {
                string id = "user-" + i;
                if (Bucketing.GroupFor(Bucketing.Bucket(experimentId, id), percentageBasisPoints) == wanted) {
                    return id;
                }
            }
            throw new InvalidOperationException("no user id found");
        }

        protected List<SentEvent> Assignments() {
            return Sink.Named(AssignmentEngine.AssignmentEvent);
        }

        protected List<SentEvent> ConfigFetches() {
            return Sink.Named(AssignmentEngine.ConfigFetchEvent);
        }

        protected List<SentEvent> AdsInits() {
            return Sink.Named(AssignmentEngine.AdsInitEvent);
        }
    }
}
