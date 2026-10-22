using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AB.Assignment.Tests {
    /// <summary>Delivery to Metica ingestion: request shape, queue, retries, host fallback and listeners.</summary>
    public class IngestTests : AssignmentTestBase {
        private const string ProdEvents = "https://api-global-gateway.prod-eu.metica.com/ingest/v1/events";
        private const string RelayEvents = "https://relay.prod-eu.metica.com/ingest/v1/events";

        private static readonly Regex LowercaseUuidV4 =
            new Regex("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$");

        // ---- request shape ----

        [Test]
        public void BodyHasOnlyTheMinimalFields() {
            var e = new QueuedEvent {
                Name = "upliftAssignment",
                EventId = "134e4d62-421c-437a-975d-b3b5545ddfbe",
                EventTimeMs = 1792592101176L,
                UserId = "6c0f2b0e9d6a4f7f8a0c1d2e3f405162",
                Payload = new Dictionary<string, object> {
                    { "experiment_id", "studio_mygame-20261016-50" },
                    { "group", "metica" },
                    { "metica_route", true },
                    { "metica_percentage", 50.0 },
                    { "config_source", "remote_this_session" },
                    { "session_id", "18e09451d68345aeacfe1f44cfdbb6e1" },
                    { "activity_date_utc", "2026-10-21" },
                    { "fetch_latency_ms", 812L },
                    { "error", null }
                }
            };
            string body = MeticaIngestEventSender.BuildBody(new List<QueuedEvent> { e }, "test-app-id", Android, "granted");
            const string golden =
                "{\"events\":[{\"eventType\":\"upliftAssignment\",\"eventId\":\"134e4d62-421c-437a-975d-b3b5545ddfbe\"," +
                "\"appId\":\"test-app-id\",\"eventTime\":1792592101176,\"userId\":\"6c0f2b0e9d6a4f7f8a0c1d2e3f405162\"," +
                "\"platform\":\"android\",\"customPayload\":{\"experiment_id\":\"studio_mygame-20261016-50\",\"group\":\"metica\",\"metica_route\":true," +
                "\"metica_percentage\":50.0,\"config_source\":\"remote_this_session\",\"session_id\":\"18e09451d68345aeacfe1f44cfdbb6e1\"," +
                "\"activity_date_utc\":\"2026-10-21\",\"fetch_latency_ms\":812,\"error\":null,\"consent_status\":\"granted\"}}]}";
            Assert.AreEqual(golden, body);
        }

        [Test]
        public void ProductionRequestHasTheMinimalUrlAndFields() {
            string user = FindUserId(ExperimentA, 5000, Group.Metica);
            SetUserId(user);
            AssignmentResult a = StartSessionCached(ConfigA());

            Assert.AreEqual(1, Transport.Requests.Count);
            FakeRequest request = Transport.Last;
            Assert.AreEqual(ProdEvents + "?appId=testApp&userId=" + user + "&platform=android", request.Url);
            Assert.AreEqual(ApiKey, request.ApiKey);
            JObject e = (JObject)request.Events[0];
            CollectionAssert.AreEqual(new[] {
                "eventType", "eventId", "appId", "eventTime", "userId", "platform", "customPayload"
            }, e.Properties().Select(p => p.Name).ToArray(), "no device or SDK version fields");
            Assert.AreEqual("upliftAssignment", (string)e["eventType"]);
            StringAssert.IsMatch(LowercaseUuidV4.ToString(), (string)e["eventId"]);
            Assert.AreEqual(AppId, (string)e["appId"]);
            Assert.AreEqual(JTokenType.Integer, e["eventTime"].Type);
            Assert.AreEqual(1792144800000L, (long)e["eventTime"]);
            Assert.AreEqual(a.UserId, (string)e["userId"]);
            Assert.AreEqual(ABAssignment.UserId, (string)e["userId"], "the id the game gives the Metica SDK");
            Assert.AreEqual("metica", (string)e["customPayload"]["group"]);
            Assert.AreEqual(JTokenType.Float, e["customPayload"]["metica_percentage"].Type);
            Assert.AreEqual("unknown", (string)e["customPayload"]["consent_status"]);
        }

        [Test]
        public void CustomDomainIsNormalizedAndUsedAsTheOnlyHost() {
            Transport.AutoStatus = null;
            NewBareProcess();
            ABAssignment.Initialize(new ABAssignmentOptions(AppId, ApiKey) { CustomDomain = " Events.Example.COM/ " });
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, "offline");
            const string customEvents = "https://events.example.com/ingest/v1/events";
            StringAssert.StartsWith(customEvents + "?appId=testApp&", Transport.Last.Url);
            Assert.AreEqual(ApiKey, Transport.Last.ApiKey);

            Transport.Last.Fail(timedOut: true);
            for (int i = 0; i < 3; i++) {
                Clock.Advance(TimeSpan.FromMinutes(10));
                ABAssignment.Tick();
                Transport.Last.Fail();
            }
            Assert.AreEqual(4, Transport.Requests.Count);
            Assert.IsTrue(Transport.Requests.All(r => r.Url.StartsWith(customEvents, StringComparison.Ordinal)),
                "a custom domain has no relay");
            CollectionAssert.IsEmpty(Errors);
        }

        [Test]
        public void CustomDomainKeepsThePortAndScheme() {
            NewBareProcess();
            ABAssignment.Initialize(new ABAssignmentOptions(AppId, ApiKey) { CustomDomain = "http://localhost:8080" });
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, "offline");
            StringAssert.StartsWith("http://localhost:8080/ingest/v1/events?appId=testApp&", Transport.Last.Url);
            Assert.IsTrue(Warnings.Any(w => w.Contains("cleartext http")), "http is warned about");
        }

        [Test]
        public void InvalidCustomDomainThrowsAndSendsNothingAndACorrectedCallWorks() {
            NewBareProcess();
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, "offline");
            int queued = StoredQueue().Count;
            Assert.AreEqual(1, queued, "logged before Initialize");
            Warnings.Clear();

            var e = Assert.Throws<ArgumentException>(() => ABAssignment.Initialize(
                new ABAssignmentOptions(AppId, ApiKey) { CustomDomain = "https://events.example.com/metica" }));
            Assert.AreEqual("customDomain must not contain a path, was '/metica': 'https://events.example.com/metica'", e.Message);
            StringAssert.DoesNotContain(ApiKey, e.Message);

            Clock.Advance(TimeSpan.FromMinutes(10));
            ABAssignment.Tick();
            ABAssignment.FlushEvents();
            Assert.AreEqual(0, Transport.Requests.Count, "no fallback to production, nothing sent");
            Assert.AreEqual(queued, StoredQueue().Count, "the queue is unchanged");
            CollectionAssert.IsEmpty(Warnings, "not treated as a second Initialize");

            ABAssignment.Initialize(new ABAssignmentOptions(AppId, ApiKey) { CustomDomain = "events.example.com" });
            Assert.AreEqual(1, Transport.Requests.Count);
            StringAssert.StartsWith("https://events.example.com/ingest/v1/events?appId=testApp&", Transport.Last.Url);
            Assert.AreEqual(0, StoredQueue().Count);
        }

        [Test]
        public void EachCustomDomainRejectRuleThrowsFromInitialize() {
            string[,] cases = {
                { "api customer.com", "customDomain must not contain whitespace" },
                { "api.metica\u3002com", "customDomain must be ASCII only" },
                { "ftp://api.customer.com", "customDomain scheme must be https or http" },
                { "https://", "customDomain is not a valid host" },
                { "api_customer.com", "customDomain host contains invalid characters" },
                // Mono's Uri already refuses a leading dot and an empty label, before the explicit checks.
                { ".api.customer.com", "customDomain is not a valid host" },
                { "api..customer.com", "customDomain is not a valid host" },
                { "https://user:pass@api.customer.com", "customDomain must not contain credentials" },
                { "https://api.customer.com?env=test", "customDomain must not contain a query string" },
                { "https://api.customer.com#frag", "customDomain must not contain a fragment" },
                { "https://api.customer.com/metica", "customDomain must not contain a path" },
                { "api.customer.com:0", "customDomain port must be between 1 and 65535" }
            };
            NewBareProcess();
            for (int i = 0; i < cases.GetLength(0); i++) {
                string raw = cases[i, 0];
                var e = Assert.Throws<ArgumentException>(() => ABAssignment.Initialize(
                    new ABAssignmentOptions(AppId, ApiKey) { CustomDomain = raw }), "raw: '" + raw + "'");
                StringAssert.StartsWith(cases[i, 1], e.Message, "raw: '" + raw + "'");
                StringAssert.DoesNotContain(ApiKey, e.Message);
            }
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, "offline");
            Assert.AreEqual(0, Transport.Requests.Count, "still not initialised, nothing sent");
        }

        [Test]
        public void BlankCustomDomainUsesProductionWithoutAnError() {
            foreach (string blank in new[] { null, "", "   ", "\t\n" }) {
                Transport.Requests.Clear();
                NewBareProcess();
                ABAssignment.Initialize(new ABAssignmentOptions(AppId, ApiKey) { CustomDomain = blank });
                ABAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, "offline");
                StringAssert.StartsWith(ProdEvents + "?appId=testApp&", Transport.Last.Url);
            }
            CollectionAssert.IsEmpty(Errors);
        }

        [Test]
        public void CustomDomainEqualToProductionKeepsTheRelayAsInTheSdk() {
            Transport.AutoStatus = null;
            NewBareProcess();
            ABAssignment.Initialize(new ABAssignmentOptions(AppId, ApiKey) {
                CustomDomain = "api-global-gateway.prod-eu.metica.com"
            });
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, "offline");
            StringAssert.StartsWith(ProdEvents, Transport.Last.Url);
            Transport.Last.Fail(timedOut: true);
            Clock.Advance(TimeSpan.FromMinutes(10));
            ABAssignment.Tick();
            StringAssert.StartsWith(RelayEvents, Transport.Last.Url);
        }

        [Test]
        public void QueryValuesAreEncodedLikeTheSdk() {
            Assert.AreEqual("a%20b*-_.%7E%2F%C3%A9", MeticaIngestEventSender.Encode(" a b*-_.~/é "));
        }

        // ---- validation ----

        [Test]
        public void EventNamesMustBeCamelCaseLetters() {
            var payload = new Dictionary<string, object>();
            Assert.IsTrue(EventValidation.TryValidate("upliftAssignment", payload, 1, out _));
            Assert.IsTrue(EventValidation.TryValidate("upliftAdsInit", payload, 1, out _));
            Assert.IsFalse(EventValidation.TryValidate("uplift_assignment", payload, 1, out _));
            Assert.IsFalse(EventValidation.TryValidate("UpliftAssignment", payload, 1, out _));
            Assert.IsFalse(EventValidation.TryValidate("uplift2", payload, 1, out _));
            Assert.IsFalse(EventValidation.TryValidate("", payload, 1, out _));
        }

        [Test]
        public void PayloadMustBeFlatAndAtMostOneHundredKeys() {
            var ok = new Dictionary<string, object> { { "s", "x" }, { "b", true }, { "l", 1L }, { "d", 1.5 }, { "n", null } };
            Assert.IsTrue(EventValidation.TryValidate("upliftAssignment", ok, 1, out _));
            var nested = new Dictionary<string, object> { { "o", new Dictionary<string, object>() } };
            Assert.IsFalse(EventValidation.TryValidate("upliftAssignment", nested, 1, out _));
            var array = new Dictionary<string, object> { { "a", new List<object>() } };
            Assert.IsFalse(EventValidation.TryValidate("upliftAssignment", array, 1, out _));
            var nan = new Dictionary<string, object> { { "d", double.NaN } };
            Assert.IsFalse(EventValidation.TryValidate("upliftAssignment", nan, 1, out _));
            var many = new Dictionary<string, object>();
            for (int i = 0; i < 99; i++) {
                many["k" + i] = i;
            }
            Assert.IsTrue(EventValidation.TryValidate("upliftAssignment", many, 1, out _), "99 + consent_status");
            many["k99"] = 99;
            Assert.IsFalse(EventValidation.TryValidate("upliftAssignment", many, 1, out _));
        }

        [Test]
        public void LongStringsAreCutToTwoHundredCharactersLikeTheSdk() {
            string capped = EventValidation.Cap(new string('a', 300));
            Assert.AreEqual(200, capped.Length);
            Assert.AreEqual(new string('a', 186) + "...[truncated]", capped);
            Assert.AreEqual(new string('a', 200), EventValidation.Cap(new string('a', 200)));

            Transport.AutoStatus = 202;
            NewProcess();
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, new string('e', 250));
            string sent = (string)Transport.Last.Events[0]["customPayload"]["error"];
            Assert.AreEqual(200, sent.Length);
            Assert.AreEqual(sent, Sink.Sent[0].Payload["error"], "the listener gets the same capped value");
        }

        // ---- queue: ack, retry, drop ----

        [Test]
        public void EventsStayQueuedUntilIngestionAnswers2xx() {
            Transport.AutoStatus = null;
            StartSessionCached(ConfigA());
            Assert.AreEqual(1, Transport.Requests.Count);
            Assert.AreEqual(1, StoredQueue().Count, "in flight, still persisted");
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, "offline");
            Assert.AreEqual(1, Transport.Requests.Count, "one request at a time");
            Assert.AreEqual(2, StoredQueue().Count);

            Transport.Requests[0].Answer(202);
            Assert.AreEqual(2, Transport.Requests.Count, "the next batch goes out after the answer");
            Assert.AreEqual(1, StoredQueue().Count);
            Transport.Requests[1].Answer(200);
            Assert.AreEqual(0, StoredQueue().Count);
        }

        [Test]
        public void ServerErrorsRetryWithExponentialBackoffAndKeepTheEventId() {
            Transport.AutoStatus = 500;
            StartSessionCached(ConfigA());
            string eventId = (string)Transport.Last.Events[0]["eventId"];
            Assert.AreEqual(1, Transport.Requests.Count);

            Clock.Advance(TimeSpan.FromMilliseconds(1999));
            ABAssignment.Tick();
            Assert.AreEqual(1, Transport.Requests.Count, "2 s after the first failure");
            Clock.Advance(TimeSpan.FromMilliseconds(1));
            ABAssignment.Tick();
            Assert.AreEqual(2, Transport.Requests.Count);

            Clock.Advance(TimeSpan.FromMilliseconds(3999));
            ABAssignment.Tick();
            Assert.AreEqual(2, Transport.Requests.Count, "4 s after the second failure");
            Transport.AutoStatus = 202;
            Clock.Advance(TimeSpan.FromMilliseconds(1));
            ABAssignment.Tick();
            Assert.AreEqual(3, Transport.Requests.Count);
            Assert.AreEqual(eventId, (string)Transport.Last.Events[0]["eventId"]);
            Assert.AreEqual(0, StoredQueue().Count);
        }

        [Test]
        public void BackoffIsCappedAtFiveMinutes() {
            Assert.AreEqual(2000, MeticaIngestEventSender.BackoffMs(1, 1.0));
            Assert.AreEqual(1000, MeticaIngestEventSender.BackoffMs(1, 0.5));
            Assert.AreEqual(256000, MeticaIngestEventSender.BackoffMs(8, 1.0));
            Assert.AreEqual(300000, MeticaIngestEventSender.BackoffMs(9, 1.0));
            Assert.AreEqual(300000, MeticaIngestEventSender.BackoffMs(1000, 1.0));
        }

        [Test]
        public void TimeoutAndTooManyRequestsAreRetried() {
            Assert.IsFalse(MeticaIngestEventSender.IsPermanentFailure(408));
            Assert.IsFalse(MeticaIngestEventSender.IsPermanentFailure(429));
            Assert.IsFalse(MeticaIngestEventSender.IsPermanentFailure(500));
            Assert.IsFalse(MeticaIngestEventSender.IsPermanentFailure(302));
            Assert.IsTrue(MeticaIngestEventSender.IsPermanentFailure(400));
            Assert.IsTrue(MeticaIngestEventSender.IsPermanentFailure(401));
            Assert.IsTrue(MeticaIngestEventSender.IsPermanentFailure(403));

            Transport.AutoStatus = 429;
            StartSessionCached(ConfigA());
            Assert.AreEqual(1, StoredQueue().Count);
        }

        [Test]
        public void ClientErrorsDropTheBatchWithAnErrorAndSendTheRest() {
            Transport.AutoStatus = null;
            StartSessionCached(ConfigA());
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, "offline");
            Transport.Requests[0].Answer(401);
            Assert.AreEqual(1, Errors.Count(m => m.Contains("HTTP 401")));
            Assert.AreEqual(2, Transport.Requests.Count, "the next event is sent at once");
            Assert.AreEqual("upliftConfigFetch", (string)Transport.Last.Events[0]["eventType"]);
            Transport.Last.Answer(400);
            Assert.AreEqual(0, StoredQueue().Count);
        }

        [Test]
        public void BatchesHoldAtMostTenEventsInOrder() {
            Transport.AutoStatus = null;
            NewBareProcess();
            CacheConfig(ConfigA());
            Assert.IsNotNull(ABAssignment.Current);
            for (int i = 0; i < 12; i++) {
                ABAssignment.AddCustomUserId("login", "id-" + i);
            }
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, "offline");
            Assert.AreEqual(14, StoredQueue().Count);

            ABAssignment.Initialize(new ABAssignmentOptions(AppId, ApiKey));
            Assert.AreEqual(1, Transport.Requests.Count);
            Assert.AreEqual(10, Transport.Last.Events.Count);
            Assert.AreEqual("upliftAssignment", (string)Transport.Last.Events[0]["eventType"]);
            Assert.AreEqual("login=id-0", (string)Transport.Last.Events[1]["customPayload"]["custom_user_ids"]);
            Transport.Last.Answer(202);
            Assert.AreEqual(4, Transport.Last.Events.Count);
            Assert.AreEqual("login=id-9", (string)Transport.Last.Events[0]["customPayload"]["custom_user_ids"]);
            Assert.AreEqual("upliftConfigFetch", (string)Transport.Last.Events[3]["eventType"]);
        }

        [Test]
        public void EventsBeforeInitializeAreQueuedAndSentOnInitialize() {
            NewBareProcess();
            CacheConfig(ConfigA());
            Assert.IsNotNull(ABAssignment.Current);
            Assert.AreEqual(0, Transport.Requests.Count);
            Assert.IsTrue(Warnings.Any(m => m.Contains("Initialize has not been called")));
            ABAssignment.Initialize(new ABAssignmentOptions(AppId, ApiKey));
            Assert.AreEqual(1, Transport.Requests.Count);
            Assert.AreEqual(0, StoredQueue().Count);
        }

        [Test]
        public void InitializeWithoutAppIdOrKeyIsRejected() {
            NewBareProcess();
            ABAssignment.Initialize(new ABAssignmentOptions(AppId, ""));
            ABAssignment.Initialize(new ABAssignmentOptions(null, ApiKey));
            ABAssignment.Initialize(null);
            Assert.AreEqual(3, Errors.Count);
            CacheConfig(ConfigA());
            Assert.IsNotNull(ABAssignment.Current);
            Assert.AreEqual(0, Transport.Requests.Count);
            Assert.AreEqual(1, StoredQueue().Count);
        }

        [Test]
        public void QueueSurvivesRestartsAndDropsTheOldestBeyondTheLimit() {
            Transport.AutoStatus = 503;
            StartSessionCached(ConfigA());
            string firstId = (string)Transport.Last.Events[0]["eventId"];
            for (int i = 0; i < EventQueue.Capacity + 2; i++) {
                StartSession();
            }
            Assert.AreEqual(EventQueue.Capacity, StoredQueue().Count);
            Assert.IsTrue(Warnings.Any(m => m.Contains("queue is full")));

            Transport.AutoStatus = 202;
            Transport.Requests.Clear();
            StartSession();
            List<JObject> sent = Transport.AllEvents();
            Assert.AreEqual(EventQueue.Capacity + 1, sent.Count, "the queue, then this session's event");
            Assert.IsFalse(sent.Any(e => (string)e["eventId"] == firstId));
            Assert.AreEqual(sent.Count, sent.Select(e => (string)e["eventId"]).Distinct().Count());
            Assert.AreEqual(0, StoredQueue().Count);
        }

        [Test]
        public void ARestartResendsAnInFlightBatchWithTheSameIds() {
            Transport.AutoStatus = null;
            StartSessionCached(ConfigA());
            string eventId = (string)Transport.Last.Events[0]["eventId"];
            long eventTime = (long)Transport.Last.Events[0]["eventTime"];

            // The app is killed before the answer arrives.
            Clock.Advance(TimeSpan.FromMinutes(5));
            Transport.AutoStatus = 202;
            NewProcess();
            JObject resent = (JObject)Transport.Last.Events[0];
            Assert.AreEqual(eventId, (string)resent["eventId"]);
            Assert.AreEqual(eventTime, (long)resent["eventTime"]);
            Assert.AreEqual(0, StoredQueue().Count);
        }

        [Test]
        public void QueueStoredByTheOldVersionIsSent() {
            Store.Values[EventQueue.StoreKey] =
                "[{\"name\":\"upliftConfigFetch\",\"payload\":{\"rc_received\":false,\"fetch_status\":\"timeout\"," +
                "\"fetch_latency_ms\":10000,\"session_id\":\"old\",\"activity_date_utc\":\"2026-10-15\"}}]";
            SetUserId("user-0");
            NewProcess();
            JObject e = (JObject)Transport.Last.Events[0];
            Assert.AreEqual("upliftConfigFetch", (string)e["eventType"]);
            StringAssert.IsMatch(LowercaseUuidV4.ToString(), (string)e["eventId"]);
            Assert.AreEqual("user-0", (string)e["userId"]);
            Assert.AreEqual(10000L, (long)e["customPayload"]["fetch_latency_ms"]);
            Assert.AreEqual("android", (string)e["platform"]);
            Assert.AreEqual(0, StoredQueue().Count);
        }

        [Test]
        public void LongStringsInAnOldQueueAreCappedBeforeSending() {
            Store.Values[EventQueue.StoreKey] =
                "[{\"name\":\"upliftConfigFetch\",\"payload\":{\"fetch_status\":\"fetch_failed\",\"error\":\"" +
                new string('e', 256) + "\"}}]";
            NewProcess();
            string sent = (string)Transport.Last.Events[0]["customPayload"]["error"];
            Assert.AreEqual(200, sent.Length);
            StringAssert.EndsWith(EventValidation.TruncationSuffix, sent);
        }

        [Test]
        public void DeviceFieldsStoredBy100AreNotSent() {
            Store.Values[EventQueue.StoreKey] =
                "[{\"name\":\"upliftConfigFetch\",\"payload\":{\"fetch_status\":\"success\"}," +
                "\"id\":\"134e4d62-421c-437a-975d-b3b5545ddfbe\",\"time\":1792592101176,\"user\":\"u1\"," +
                "\"env\":{\"appVersion\":\"1.0.34\",\"platform\":\"android\",\"deviceModel\":\"CPH2487\"}}]";
            NewProcess();
            JObject e = (JObject)Transport.Requests[0].Events[0];
            Assert.AreEqual("134e4d62-421c-437a-975d-b3b5545ddfbe", (string)e["eventId"], "same event, same id");
            CollectionAssert.AreEqual(new[] {
                "eventType", "eventId", "appId", "eventTime", "userId", "platform", "customPayload"
            }, e.Properties().Select(p => p.Name).ToArray());
        }

        [Test]
        public void PauseSendsAtOnceInsteadOfWaitingForTheBackoff() {
            Transport.AutoStatus = 503;
            StartSessionCached(ConfigA());
            Transport.AutoStatus = 202;
            ABAssignment.Tick();
            Assert.AreEqual(1, Transport.Requests.Count);
            ABAssignment.OnApplicationPaused();
            Assert.AreEqual(2, Transport.Requests.Count);
            Assert.AreEqual(0, StoredQueue().Count);
        }

        [Test]
        public void UnsupportedPlatformLogsAndDiscardsInsteadOfSending() {
            Platform = null;
            StartSessionCached(ConfigA());
            Assert.AreEqual(0, Transport.Requests.Count);
            Assert.AreEqual(0, StoredQueue().Count);
            Assert.AreEqual(1, Assignments().Count, "listeners still get the event");
        }

        // ---- host fallback (production only) ----

        [Test]
        public void TwoUnreachableSendsMoveToTheRelayAndRelay5xxMovesBack() {
            Transport.AutoStatus = null;
            StartSessionCached(ConfigA());
            Transport.Last.Fail();
            Clock.Advance(TimeSpan.FromMinutes(10));
            ABAssignment.Tick();
            StringAssert.StartsWith(ProdEvents, Transport.Last.Url, "one failure does not move the route");
            Transport.Last.Fail();
            Clock.Advance(TimeSpan.FromMinutes(10));
            ABAssignment.Tick();
            StringAssert.StartsWith(RelayEvents, Transport.Last.Url);

            Transport.Last.Answer(502);
            Clock.Advance(TimeSpan.FromMinutes(10));
            ABAssignment.Tick();
            StringAssert.StartsWith(RelayEvents, Transport.Last.Url);
            Transport.Last.Answer(502);
            Clock.Advance(TimeSpan.FromMinutes(10));
            ABAssignment.Tick();
            StringAssert.StartsWith(ProdEvents, Transport.Last.Url, "two relay 5xx count as unreachable");
            Transport.Last.Answer(202);
            Assert.AreEqual(0, StoredQueue().Count);
        }

        [Test]
        public void AStallMovesToTheRelayAtOnce() {
            Transport.AutoStatus = null;
            StartSessionCached(ConfigA());
            Transport.Last.Fail(timedOut: true);
            Clock.Advance(TimeSpan.FromMinutes(10));
            ABAssignment.Tick();
            StringAssert.StartsWith(RelayEvents, Transport.Last.Url);
        }

        [Test]
        public void Ga5xxDoesNotMoveTheRoute() {
            Transport.AutoStatus = null;
            StartSessionCached(ConfigA());
            for (int i = 0; i < 3; i++) {
                Transport.Last.Answer(503);
                Clock.Advance(TimeSpan.FromMinutes(10));
                ABAssignment.Tick();
            }
            Assert.AreEqual(4, Transport.Requests.Count);
            Assert.IsTrue(Transport.Requests.All(r => r.Url.StartsWith(ProdEvents, StringComparison.Ordinal)));
        }

        // ---- listeners ----

        [Test]
        public void ListenerGetsAllThreeEventsAndMeticaGetsThemToo() {
            StartSessionCached(ConfigA());
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, "offline");
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            ABAssignment.ReportAdsInit(AdsInitStage.Succeeded);
            CollectionAssert.AreEqual(new[] { "upliftAssignment", "upliftConfigFetch", "upliftAdsInit" },
                Sink.Sent.Select(e => e.Name).ToArray());
            CollectionAssert.AreEqual(new[] { "upliftAssignment", "upliftConfigFetch", "upliftAdsInit" },
                Transport.AllEvents().Select(e => (string)e["eventType"]).ToArray());
            Assert.AreEqual("unknown", Sink.Sent[0].Payload["consent_status"]);
        }

        [Test]
        public void ListenerCopyIsReadOnly() {
            IReadOnlyDictionary<string, object> received = null;
            NewProcess();
            ABAssignment.EventLogged += (name, payload) => received = payload;
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, "offline");
            Assert.IsNotNull(received);
            Assert.IsFalse(received is Dictionary<string, object>, "not castable back to a mutable dictionary");
            Assert.Throws<NotSupportedException>(() => ((IDictionary<string, object>)received)["fetch_status"] = "x");
        }

        [Test]
        public void ThrowingListenerIsLoggedAndBlocksNeitherMeticaNorOtherListeners() {
            NewBareProcess();
            ABAssignment.EventLogged += (name, payload) => throw new InvalidOperationException("listener bug");
            ABAssignment.EventLogged += Sink.OnEvent;
            ABAssignment.Initialize(new ABAssignmentOptions(AppId, ApiKey));
            CacheConfig(ConfigA());
            Assert.IsNotNull(ABAssignment.Current);
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, "offline");
            Assert.AreEqual(2, Errors.Count(m => m.Contains("listener threw")));
            Assert.AreEqual(2, Sink.Sent.Count);
            Assert.AreEqual(2, Transport.AllEvents().Count);
            Assert.AreEqual(0, StoredQueue().Count);
        }

        [Test]
        public void ListenerMayCallTheLibraryFromItsCallback() {
            NewProcess();
            CacheConfig(ConfigA());
            bool linked = false;
            ABAssignment.EventLogged += (name, payload) => {
                if (!linked) {
                    linked = true;
                    ABAssignment.AddCustomUserId("login", "from-listener");
                }
            };
            Assert.IsNotNull(ABAssignment.Current);
            CollectionAssert.AreEqual(new[] { "upliftAssignment", "upliftAssignment" },
                Transport.AllEvents().Select(e => (string)e["eventType"]).ToArray());
            Assert.AreEqual("login=from-listener", (string)Transport.AllEvents()[1]["customPayload"]["custom_user_ids"]);
            Assert.AreEqual(2, Sink.Sent.Count);
            Assert.AreEqual(0, StoredQueue().Count);
            Assert.AreEqual(0, Errors.Count);
        }

        [Test]
        public void RemovedListenerGetsNothing() {
            NewProcess();
            ABAssignment.EventLogged -= Sink.OnEvent;
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, "offline");
            Assert.AreEqual(0, Sink.Sent.Count);
            Assert.AreEqual(1, Transport.Requests.Count);
        }

#pragma warning disable 618 // the obsolete sink API is what these tests cover
        [Test]
        public void ObsoleteSinkIsIgnoredAndCannotStopOrReplaceMeticaDelivery() {
            var notReady = new TestSink { IsReady = false };
            var throwing = new TestSink { Throws = true };
            NewProcess();
            ABAssignment.SetEventSink(notReady);
            ABAssignment.SetEventSink(throwing);
            CacheConfig(ConfigA());
            Assert.IsNotNull(ABAssignment.Current);
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, "offline");
            Assert.AreEqual(0, notReady.Calls + throwing.Calls, "a 0.1.0 sink would send every event a second time");
            Assert.AreEqual(2, Transport.AllEvents().Count, "Metica got every event");
            Assert.AreEqual(2, Sink.Sent.Count, "listeners are unaffected");
            Assert.AreEqual(2, Errors.Count(m => m.Contains("SetEventSink is obsolete")));
            Assert.AreEqual(0, StoredQueue().Count);
        }

        private sealed class TestSink : IAssignmentEventSink {
            public int Calls;
            public bool Throws;
            public bool IsReady { get; set; } = true;

            public void Send(string eventName, IReadOnlyDictionary<string, object> payload) {
                Calls++;
                if (Throws) {
                    throw new InvalidOperationException("transport down");
                }
            }
        }
#pragma warning restore 618

        private List<object> StoredQueue() {
            if (!Store.Values.TryGetValue(EventQueue.StoreKey, out string stored)) {
                return new List<object>();
            }
            Assert.IsTrue(Json.TryParse(stored, out object root, out string error), error);
            return (List<object>)root;
        }
    }
}
