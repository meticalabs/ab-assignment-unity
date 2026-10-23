using System;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AB.Assignment.Tests {
    public class EventTests : AssignmentTestBase {
        // ---- upliftAssignment: cold start and daily ping ----

        [Test]
        public void ColdStartSendsOneAssignmentWithDocumentedFields() {
            string user = FindUserId(ExperimentA, 5000, Group.Metica);
            SetUserId(user);
            AssignmentResult result = StartSessionCached(ConfigA());

            Assert.AreEqual(1, Sink.Sent.Count);
            SentEvent e = Assignments()[0];
            Assert.AreEqual(ExperimentA, e.Payload["experiment_id"]);
            Assert.AreEqual("metica", e.Payload["group"]);
            Assert.AreEqual(true, e.Payload["metica_route"]);
            Assert.IsInstanceOf<double>(e.Payload["metica_percentage"]);
            Assert.AreEqual(50.0, e.Payload["metica_percentage"]);
            Assert.AreEqual("cached_remote", e.Payload["config_source"]);
            Assert.AreEqual(result.SessionId, e.Payload["session_id"]);
            Assert.AreEqual("2026-10-16", e.Payload["activity_date_utc"]);
            Assert.AreEqual("unknown", e.Payload["consent_status"], "never assumed given");
            Assert.IsFalse(e.Payload.ContainsKey("custom_user_ids"));
            Assert.IsFalse(e.Payload.ContainsKey("error"));
            Assert.IsFalse(e.Payload.ContainsKey("user_id"), "the SDK adds the user id");
            Assert.IsFalse(e.Payload.ContainsKey("bucket"));
        }

        [Test]
        public void TheEventIsSentWhenTheGroupIsDecidedNotAtStartup() {
            NewProcess();
            Assert.IsNotNull(ABAssignment.UserId);
            Clock.Advance(TimeSpan.FromDays(1));
            ABAssignment.Tick();
            Assert.AreEqual(0, Assignments().Count, "no group needed yet, so no assignment and no day ping");

            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, ConfigA(), null);
            Assert.IsNotNull(ABAssignment.Current);
            Assert.AreEqual(1, Assignments().Count);
            Assert.AreEqual("remote_this_session", Assignments()[0].Payload["config_source"]);
        }

        [Test]
        public void EveryAssignmentRowSplitsBySource() {
            // Fresh install, fetch failed before the group was needed: unassigned, waiting.
            NewProcess();
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, "offline");
            Assert.IsNotNull(ABAssignment.Current);
            SentEvent waiting = Assignments()[0];
            Assert.AreEqual("unassigned", waiting.Payload["group"]);
            Assert.AreEqual("unassigned_waiting", waiting.Payload["config_source"]);
            Assert.IsFalse(string.IsNullOrEmpty((string)waiting.Payload["error"]));
            Assert.IsFalse(waiting.Payload.ContainsKey("metica_percentage"));
            // Next session the value arrives before the group is needed: assigned from it.
            StartSession(ConfigA());
            AssertAssignedFrom(Assignments()[1], "remote_this_session");
            // Next session nothing new arrives in time: assigned from the cache.
            StartSession();
            AssertAssignedFrom(Assignments()[2], "cached_remote");
            // A day-change ping keeps the session's source.
            Clock.Advance(TimeSpan.FromDays(1));
            ABAssignment.Tick();
            AssertAssignedFrom(Assignments()[3], "cached_remote");

            // Storage failure: unassigned with an error.
            Store = new FakeStore { ThrowOnKey = AssignmentEngine.UserIdKey };
            Sink.Sent.Clear();
            StartSessionCached(ConfigA());
            SentEvent failed = Assignments()[0];
            Assert.AreEqual("unassigned", failed.Payload["group"]);
            Assert.AreEqual("unassigned_error", failed.Payload["config_source"]);
            Assert.IsFalse(string.IsNullOrEmpty((string)failed.Payload["error"]));
        }

        private static void AssertAssignedFrom(SentEvent e, string configSource) {
            Assert.AreNotEqual("unassigned", e.Payload["group"]);
            Assert.AreEqual(configSource, e.Payload["config_source"]);
            Assert.IsFalse(e.Payload.ContainsKey("error"));
        }

        [Test]
        public void DecimalPercentageIsSentAsDouble() {
            StartSessionCached(Config("studio_mygame-20261016-12.5", "12.5", true));
            Assert.IsInstanceOf<double>(Assignments()[0].Payload["metica_percentage"]);
            Assert.AreEqual(12.5, Assignments()[0].Payload["metica_percentage"]);
        }

        [Test]
        public void EveryColdStartSendsOneEventEvenOnACoveredDay() {
            StartSessionCached(ConfigA());
            StartSessionCached(ConfigA());
            Assert.AreEqual(2, Assignments().Count);
            Assert.AreNotEqual(Assignments()[0].Payload["session_id"], Assignments()[1].Payload["session_id"]);
        }

        [Test]
        public void ResumeOnTheSameDaySendsNothing() {
            StartSessionCached(ConfigA());
            Clock.Advance(TimeSpan.FromHours(3));
            ABAssignment.OnApplicationResumed();
            ABAssignment.OnApplicationResumed();
            ABAssignment.Tick();
            Assert.AreEqual(1, Assignments().Count);
        }

        [Test]
        public void ForegroundAcrossMidnightSendsOneForTheNewDayWithoutResume() {
            Clock.UtcNow = new DateTime(2026, 10, 16, 23, 55, 0, DateTimeKind.Utc);
            StartSessionCached(ConfigA());
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, ConfigA(), null);
            for (int minute = 0; minute < 15; minute++) {
                Clock.Advance(TimeSpan.FromMinutes(1));
                ABAssignment.Tick();
            }
            Assert.AreEqual(2, Assignments().Count);
            Assert.AreEqual("2026-10-17", Assignments()[1].Payload["activity_date_utc"]);
            Assert.AreEqual(Assignments()[0].Payload["session_id"], Assignments()[1].Payload["session_id"]);
        }

        [Test]
        public void BackgroundPastMidnightThenResumeSendsOneAndFurtherResumesNone() {
            Clock.UtcNow = new DateTime(2026, 10, 16, 22, 0, 0, DateTimeKind.Utc);
            StartSessionCached(ConfigA());
            Clock.Advance(TimeSpan.FromHours(4));
            ABAssignment.OnApplicationResumed();
            ABAssignment.Tick();
            Clock.Advance(TimeSpan.FromHours(1));
            ABAssignment.OnApplicationResumed();
            ABAssignment.OnApplicationResumed();
            Assert.AreEqual(2, Assignments().Count);
            Assert.AreEqual("2026-10-17", Assignments()[1].Payload["activity_date_utc"]);
        }

        [Test]
        public void DayAlreadyCoveredOnTheDeviceIsNotSentAgainOnDayChange() {
            Clock.UtcNow = new DateTime(2026, 10, 16, 23, 0, 0, DateTimeKind.Utc);
            StartSessionCached(ConfigA());
            // Another event already covered day 17 for this experiment (as a cold start or a custom-id event would).
            Store.Values[AssignmentEngine.DailyKey] = ExperimentA + "|2026-10-17";
            Clock.Advance(TimeSpan.FromHours(2));
            ABAssignment.OnApplicationResumed();
            ABAssignment.Tick();
            Assert.AreEqual(1, Assignments().Count);
        }

        [Test]
        public void EventQueuedOnDayOneAndSentOnDayTwoKeepsDayOneAndItsTime() {
            Transport.AutoStatus = 503;
            StartSessionCached(ConfigA());
            Clock.Advance(TimeSpan.FromDays(1));
            Transport.AutoStatus = 202;
            ABAssignment.Tick();
            // The first event sent is the one logged on day one; the new day's ping follows it.
            JObject sent = Transport.AllEvents()[0];
            Assert.AreEqual("upliftAssignment", (string)sent["eventType"]);
            Assert.AreEqual("2026-10-16", (string)sent["customPayload"]["activity_date_utc"]);
            Assert.AreEqual(1792144800000L, (long)sent["eventTime"], "2026-10-16T10:00:00Z, the time it was logged");
        }

        // ---- custom ids ----

        [Test]
        public void CustomIdChangeSendsOneEventAndRepeatsAreSilent() {
            StartSessionCached(ConfigA());
            ABAssignment.AddCustomUserId("device_unique_identifier", "abc");
            ABAssignment.AddCustomUserId("device_unique_identifier", "abc");
            Assert.AreEqual(2, Assignments().Count);
            Assert.AreEqual("device_unique_identifier=abc", Assignments()[1].Payload["custom_user_ids"]);

            ABAssignment.AddCustomUserId("device_unique_identifier", "def");
            Assert.AreEqual(3, Assignments().Count);
            Assert.AreEqual("device_unique_identifier=def", Assignments()[2].Payload["custom_user_ids"]);
        }

        [Test]
        public void CustomIdsAreEncodedSortedAndOnEveryDailyPing() {
            StartSessionCached(ConfigA());
            ABAssignment.AddCustomUserId("player", "p,1 x");
            ABAssignment.AddCustomUserId("device_unique_identifier", "abc=");
            const string expected = "device_unique_identifier=abc%3D,player=p%2C1%20x";
            Assert.AreEqual(expected, Assignments()[2].Payload["custom_user_ids"]);

            StartSessionCached(ConfigA());
            Assert.AreEqual(expected, Assignments()[3].Payload["custom_user_ids"]);
            Clock.Advance(TimeSpan.FromDays(1));
            ABAssignment.Tick();
            Assert.AreEqual(expected, Assignments()[4].Payload["custom_user_ids"]);
        }

        [Test]
        public void CustomIdAddedBeforeTheGroupIsDecidedIsOnTheFirstEvent() {
            CacheConfig(ConfigA());
            NewProcess();
            ABAssignment.AddCustomUserId("device_unique_identifier", "abc");
            Assert.AreEqual(0, Assignments().Count);
            Assert.IsNotNull(ABAssignment.Current);
            Assert.AreEqual(1, Assignments().Count);
            Assert.AreEqual("device_unique_identifier=abc", Assignments()[0].Payload["custom_user_ids"]);

            ABAssignment.AddCustomUserId("player", "p-1");
            Assert.AreEqual(2, Assignments().Count);
        }

        [Test]
        public void LostLinkEventIsRecoveredByTheNextDailyPing() {
            StartSessionCached(ConfigA());
            ABAssignment.AddCustomUserId("device_unique_identifier", "abc");
            Assert.AreEqual(2, Assignments().Count);
            Sink.Sent.Clear(); // the change event was lost after sending (no cold start follows)
            Clock.Advance(TimeSpan.FromDays(1));
            ABAssignment.Tick();
            Assert.AreEqual(1, Assignments().Count);
            SentEvent nextDay = Assignments()[0];
            Assert.AreEqual("2026-10-17", nextDay.Payload["activity_date_utc"]);
            Assert.AreEqual("device_unique_identifier=abc", nextDay.Payload["custom_user_ids"]);
        }

        [Test]
        public void EmptyCustomIdIsRejected() {
            StartSessionCached(ConfigA());
            ABAssignment.AddCustomUserId("player", "");
            ABAssignment.AddCustomUserId(null, "x");
            Assert.AreEqual(1, Assignments().Count);
            Assert.AreEqual(2, Errors.Count);
        }

        // ---- upliftConfigFetch ----

        [Test]
        public void SuccessfulFetchSendsReceivedWithLatency() {
            AssignmentResult result = StartSessionCached(ConfigA());
            Clock.Advance(TimeSpan.FromMilliseconds(2140));
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, ConfigB(), null);
            SentEvent e = ConfigFetches()[0];
            Assert.AreEqual(true, e.Payload["rc_received"]);
            Assert.AreEqual("success", e.Payload["fetch_status"]);
            Assert.AreEqual(2140L, e.Payload["fetch_latency_ms"]);
            Assert.AreEqual(result.SessionId, e.Payload["session_id"]);
            Assert.AreEqual("2026-10-16", e.Payload["activity_date_utc"]);
            Assert.IsFalse(e.Payload.ContainsKey("error"));
        }

        [TestCase(RemoteConfigStatus.Success, null, "missing")]
        [TestCase(RemoteConfigStatus.Success, "", "missing")]
        [TestCase(RemoteConfigStatus.FetchFailed, null, "fetch_failed")]
        [TestCase(RemoteConfigStatus.ActivationFailed, null, "activation_failed")]
        [TestCase(RemoteConfigStatus.Success, "{\"experiment_id\":\"x\"}", "invalid")]
        public void FailedOutcomesAreDistinct(RemoteConfigStatus status, string value, string expected) {
            StartSession();
            ABAssignment.ReportRemoteConfig(status, value, "some error");
            SentEvent e = ConfigFetches()[0];
            Assert.AreEqual(false, e.Payload["rc_received"]);
            Assert.AreEqual(expected, e.Payload["fetch_status"]);
            Assert.IsTrue(e.Payload.ContainsKey("error"));
            Assert.IsFalse(Store.Values.ContainsKey(AssignmentEngine.ConfigKey));
        }

        [Test]
        public void TimeoutAfterTenSecondsThenAtMostOneLateResult() {
            StartSessionCached(ConfigA());
            Clock.Advance(TimeSpan.FromMilliseconds(9999));
            ABAssignment.Tick();
            Assert.AreEqual(0, ConfigFetches().Count);
            Clock.Advance(TimeSpan.FromMilliseconds(1));
            ABAssignment.Tick();
            ABAssignment.Tick();
            Assert.AreEqual(1, ConfigFetches().Count);
            Assert.AreEqual("timeout", ConfigFetches()[0].Payload["fetch_status"]);

            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, ConfigB(), null);
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, ConfigB(), null);
            Assert.AreEqual(2, ConfigFetches().Count);
            Assert.AreEqual("success", ConfigFetches()[1].Payload["fetch_status"]);
            Assert.AreEqual(ExperimentB, StartSession().ExperimentId, "a late valid value is still cached");
        }

        [Test]
        public void SecondFetchResultWithoutTimeoutSendsNoSecondEvent() {
            StartSessionCached(ConfigA());
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, "throttled");
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, ConfigB(), null);
            Clock.Advance(TimeSpan.FromSeconds(20));
            ABAssignment.Tick();
            Assert.AreEqual(1, ConfigFetches().Count);
        }

        // ---- consent status ----

        [Test]
        public void EveryEventCarriesTheConsentStatusAtSendTime() {
            Transport.AutoStatus = 503;
            StartSessionCached(ConfigA());
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, "offline");
            ABAssignment.ReportAdsInit(AdsInitStage.SkippedLowMemory);
            ABAssignment.SetConsentStatus(ConsentStatus.Granted);
            Transport.AutoStatus = 202;
            Clock.Advance(TimeSpan.FromMinutes(10));
            ABAssignment.FlushEvents();
            JArray events = Transport.Last.Events;
            Assert.AreEqual(3, events.Count);
            foreach (JToken e in events) {
                Assert.AreEqual("granted", (string)e["customPayload"]["consent_status"], (string)e["eventType"]);
            }
            // The listener got each copy when the event was logged, before the status changed.
            foreach (SentEvent e in Sink.Sent) {
                Assert.AreEqual("unknown", e.Payload["consent_status"], e.Name);
            }
        }

        [Test]
        public void EventsAreSentWhateverTheConsentStatus() {
            NewProcess();
            ABAssignment.SetConsentStatus(ConsentStatus.Denied);
            CacheConfig(ConfigA());
            Assert.IsNotNull(ABAssignment.Current);
            Assert.AreEqual(1, Assignments().Count);
            Assert.AreEqual("denied", Assignments()[0].Payload["consent_status"]);
            Assert.AreNotEqual(Group.Unassigned, ABAssignment.Current.Group, "consent never changes the group");
        }

        [Test]
        public void ConsentStatusIsPersistedAndUnknownClearsIt() {
            NewProcess();
            ABAssignment.SetConsentStatus(ConsentStatus.Granted);
            StartSessionCached(ConfigA());
            Assert.AreEqual("granted", Assignments()[0].Payload["consent_status"]);

            ABAssignment.SetConsentStatus(ConsentStatus.Unknown);
            Assert.IsFalse(Store.Values.ContainsKey(AssignmentEngine.ConsentKey));
            StartSession();
            Assert.AreEqual("unknown", Assignments()[1].Payload["consent_status"]);
        }

        [Test]
        public void OffThreadConsentStatusIsLoggedAndIgnored() {
            StartSessionCached(ConfigA());
            var worker = new Thread(() => ABAssignment.SetConsentStatus(ConsentStatus.Granted));
            worker.Start();
            worker.Join();
            Assert.AreEqual(1, Errors.Count);
            Assert.IsFalse(Store.AccessedFromOtherThread);
            Clock.Advance(TimeSpan.FromDays(1));
            ABAssignment.Tick();
            Assert.AreEqual("unknown", Assignments()[1].Payload["consent_status"]);
        }

        // ---- upliftAdsInit ----

        [Test]
        public void StartedThenSucceededSendsOneSuccess() {
            SetUserId(FindUserId(ExperimentA, 5000, Group.Metica));
            AssignmentResult result = StartSessionCached(ConfigA());
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            Clock.Advance(TimeSpan.FromMilliseconds(3120));
            ABAssignment.ReportAdsInit(AdsInitStage.Succeeded, null, "TRIAL", false);

            Assert.AreEqual(1, AdsInits().Count);
            SentEvent e = AdsInits()[0];
            Assert.AreEqual(result.SessionId, e.Payload["session_id"]);
            Assert.AreEqual(ExperimentA, e.Payload["experiment_id"]);
            Assert.AreEqual(result.Label, e.Payload["group"]);
            Assert.AreEqual("metica", e.Payload["network"]);
            Assert.AreEqual("success", e.Payload["outcome"]);
            Assert.AreEqual(3120L, e.Payload["elapsed_ms"]);
            Assert.AreEqual("TRIAL", e.Payload["metica_internal_group"]);
            Assert.AreEqual(false, e.Payload["forced_holdout"]);
        }

        [Test]
        public void ForcedHoldoutIsATypedBool() {
            StartSessionCached(ConfigA());
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            ABAssignment.ReportAdsInit(AdsInitStage.Succeeded, null, "HOLDOUT", true);
            Assert.AreEqual(true, AdsInits()[0].Payload["forced_holdout"]);
        }

        [Test]
        public void StartedThenFailedSendsFailedWithError() {
            StartSessionCached(ConfigA());
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            ABAssignment.ReportAdsInit(AdsInitStage.Failed, "init threw", "TRIAL", true);
            SentEvent e = AdsInits()[0];
            Assert.AreEqual("failed", e.Payload["outcome"]);
            Assert.AreEqual("init threw", e.Payload["error"]);
            Assert.IsFalse(e.Payload.ContainsKey("metica_internal_group"), "only recorded on success");
            Assert.IsFalse(e.Payload.ContainsKey("forced_holdout"));
        }

        [Test]
        public void PendingTimeoutAfterThirtySecondsThenExactlyOneLateOutcome() {
            SetUserId(FindUserId(ExperimentA, 5000, Group.Metica));
            StartSessionCached(ConfigA());
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, null, null);
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            Clock.Advance(TimeSpan.FromMilliseconds(29999));
            ABAssignment.Tick();
            Assert.AreEqual(0, AdsInits().Count);
            Clock.Advance(TimeSpan.FromMilliseconds(1));
            ABAssignment.Tick();
            ABAssignment.Tick();
            Assert.AreEqual(1, AdsInits().Count);
            Assert.AreEqual("pending_timeout", AdsInits()[0].Payload["outcome"]);
            Assert.AreEqual("metica", AdsInits()[0].Payload["network"]);

            ABAssignment.ReportAdsInit(AdsInitStage.Succeeded, null, "TRIAL", false);
            ABAssignment.ReportAdsInit(AdsInitStage.Failed, "late", null, null);
            Assert.AreEqual(2, AdsInits().Count);
            Assert.AreEqual("success", AdsInits()[1].Payload["outcome"]);
        }

        [Test]
        public void SessionEndingBeforeAnOutcomeSendsNoAdsInitEvent() {
            StartSessionCached(ConfigA());
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            Clock.Advance(TimeSpan.FromSeconds(5));
            ABAssignment.Tick();
            StartSessionCached(ConfigA()); // the app was killed; next cold start
            Clock.Advance(TimeSpan.FromSeconds(40));
            ABAssignment.Tick();
            Assert.AreEqual(0, AdsInits().Count);
        }

        [Test]
        public void LowMemorySkipWithoutStartedIsReported() {
            SetUserId(FindUserId(ExperimentA, 5000, Group.Baseline));
            StartSessionCached(ConfigA());
            ABAssignment.ReportAdsInit(AdsInitStage.SkippedLowMemory);
            SentEvent e = AdsInits()[0];
            Assert.AreEqual("skipped_low_memory", e.Payload["outcome"]);
            Assert.AreEqual("applovin", e.Payload["network"]);
            Assert.AreEqual(0L, e.Payload["elapsed_ms"]);
        }

        [Test]
        public void SkippedLowMemoryThenStartedThenSucceededSendsBothOutcomes() {
            StartSessionCached(ConfigA());
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            ABAssignment.ReportAdsInit(AdsInitStage.SkippedLowMemory);
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            Clock.Advance(TimeSpan.FromMilliseconds(1500));
            ABAssignment.ReportAdsInit(AdsInitStage.Succeeded, null, "TRIAL", false);

            Assert.AreEqual(2, AdsInits().Count, "skipped_low_memory must not block the real outcome");
            Assert.AreEqual("skipped_low_memory", AdsInits()[0].Payload["outcome"]);
            Assert.AreEqual("success", AdsInits()[1].Payload["outcome"]);
            Assert.AreEqual(1500L, AdsInits()[1].Payload["elapsed_ms"], "elapsed is measured from the second Started");
        }

        [Test]
        public void PendingTimeoutWorksForTheAttemptAfterSkippedLowMemory() {
            StartSessionCached(ConfigA());
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            ABAssignment.ReportAdsInit(AdsInitStage.SkippedLowMemory);
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            Clock.Advance(TimeSpan.FromMilliseconds(29999));
            ABAssignment.Tick();
            Assert.AreEqual(1, AdsInits().Count);
            Clock.Advance(TimeSpan.FromMilliseconds(1));
            ABAssignment.Tick();
            ABAssignment.Tick();
            Assert.AreEqual(2, AdsInits().Count, "the retry attempt can time out");
            Assert.AreEqual("pending_timeout", AdsInits()[1].Payload["outcome"]);
            Assert.AreEqual(30000L, AdsInits()[1].Payload["elapsed_ms"], "the timeout counts from the second Started");
        }

        [Test]
        public void RepeatedLowMemorySkipsAllowOnlyOneRetry() {
            StartSessionCached(ConfigA());
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            ABAssignment.ReportAdsInit(AdsInitStage.SkippedLowMemory);
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            ABAssignment.ReportAdsInit(AdsInitStage.SkippedLowMemory);
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            ABAssignment.ReportAdsInit(AdsInitStage.Succeeded, null, "TRIAL", false);

            Assert.AreEqual(2, AdsInits().Count, "one retry after the first skip, then nothing more");
            Assert.AreEqual("skipped_low_memory", AdsInits()[0].Payload["outcome"]);
            Assert.AreEqual("skipped_low_memory", AdsInits()[1].Payload["outcome"]);
        }

        [Test]
        public void StartedTwiceWithoutSkipIgnoresTheSecond() {
            StartSessionCached(ConfigA());
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            Clock.Advance(TimeSpan.FromMilliseconds(1000));
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            Clock.Advance(TimeSpan.FromMilliseconds(500));
            ABAssignment.ReportAdsInit(AdsInitStage.Succeeded, null, "TRIAL", false);

            Assert.AreEqual(1, AdsInits().Count);
            Assert.AreEqual(1500L, AdsInits()[0].Payload["elapsed_ms"], "elapsed counts from the first Started");
            Assert.IsTrue(Warnings.Exists(w => w.Contains("Started was reported twice")));
        }

        [Test]
        public void StartedAfterFailedIsIgnored() {
            StartSessionCached(ConfigA());
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            ABAssignment.ReportAdsInit(AdsInitStage.Failed, "init threw");
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            ABAssignment.ReportAdsInit(AdsInitStage.Succeeded, null, "TRIAL", false);

            Assert.AreEqual(1, AdsInits().Count, "only a skip allows a new attempt");
            Assert.AreEqual("failed", AdsInits()[0].Payload["outcome"]);
        }

        [Test]
        public void SecondOutcomeIsIgnored() {
            StartSessionCached(ConfigA());
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            ABAssignment.ReportAdsInit(AdsInitStage.Succeeded, null, "TRIAL", false);
            ABAssignment.ReportAdsInit(AdsInitStage.Succeeded, null, "TRIAL", false);

            Assert.AreEqual(1, AdsInits().Count);
            Assert.AreEqual("success", AdsInits()[0].Payload["outcome"]);
        }

        [Test]
        public void SkipWithoutStartedThenStartedAndSucceededSendsBothOutcomes() {
            StartSessionCached(ConfigA());
            ABAssignment.ReportAdsInit(AdsInitStage.SkippedLowMemory);
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            Clock.Advance(TimeSpan.FromMilliseconds(700));
            ABAssignment.ReportAdsInit(AdsInitStage.Succeeded, null, "TRIAL", false);

            Assert.AreEqual(2, AdsInits().Count);
            Assert.AreEqual("skipped_low_memory", AdsInits()[0].Payload["outcome"]);
            Assert.AreEqual("success", AdsInits()[1].Payload["outcome"]);
            Assert.AreEqual(700L, AdsInits()[1].Payload["elapsed_ms"]);
        }

        [Test]
        public void OffThreadCallIsLoggedAndIgnored() {
            StartSessionCached(ConfigA());
            var thread = new Thread(() => ABAssignment.ReportAdsInit(AdsInitStage.Succeeded));
            thread.Start();
            thread.Join();
            Assert.AreEqual(0, AdsInits().Count);
            Assert.AreEqual(1, Errors.Count);
            StringAssert.Contains("main thread", Errors[0]);
            Assert.IsFalse(Store.AccessedFromOtherThread);

            ABAssignment.ReportAdsInit(AdsInitStage.Succeeded);
            Assert.AreEqual(1, AdsInits().Count);
        }

        [Test]
        public void AssignedGroupIsOnAdsInitEventsUnderKillSwitch() {
            string user = FindUserId(ExperimentA, 5000, Group.Metica);
            SetUserId(user);
            StartSessionCached(ConfigA(false));
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            ABAssignment.ReportAdsInit(AdsInitStage.Succeeded);
            Assert.AreEqual("metica", AdsInits()[0].Payload["group"]);
            Assert.AreEqual("applovin", AdsInits()[0].Payload["network"]);
            Assert.AreEqual(false, Assignments()[0].Payload["metica_route"]);
        }
    }
}
