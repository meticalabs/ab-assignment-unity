using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AB.Assignment.Tests {
    public class ClientIdTests : AssignmentTestBase {
        private const string ProviderId = "metica-sdk-user-1";

        private List<SentEvent> Mappings() {
            return Sink.Named(AssignmentEngine.CustomIdMappingEvent);
        }

        private List<JObject> SentNamed(string eventName) {
            return Transport.AllEvents().FindAll(e => (string)e["eventType"] == eventName);
        }

        /// <summary>A new initialized process in client-managed mode; nothing decided yet.</summary>
        private void NewClientManagedProcess() {
            NewProcess();
            ABAssignment.SetAssignmentMode(AssignmentMode.ClientManaged);
        }

        // ---- ReportCustomId: upliftCustomIdMapping ----

        [Test]
        public void CustomIdSendsOneMappingEventWithDocumentedFieldsAndEnvelope() {
            NewProcess();
            string userId = ABAssignment.UserId;
            ABAssignment.ReportCustomId("player-42", "login");

            Assert.AreEqual(1, Sink.Sent.Count);
            SentEvent e = Mappings()[0];
            Assert.AreEqual(userId, e.Payload["assignment_user_id"]);
            Assert.AreEqual("player-42", e.Payload["custom_id"]);
            Assert.AreEqual("login", e.Payload["custom_id_source"]);
            Assert.AreEqual(32, ((string)e.Payload["session_id"]).Length);
            Assert.AreNotEqual(userId, e.Payload["session_id"]);
            Assert.AreEqual("2026-10-16", e.Payload["activity_date_utc"]);
            Assert.AreEqual("unknown", e.Payload["consent_status"]);
            Assert.AreEqual(6, e.Payload.Count, "no group, experiment or other fields");

            JObject sent = SentNamed("upliftCustomIdMapping")[0];
            Assert.AreEqual(userId, (string)sent["userId"], "the event user is the generated id, not the custom id");
            Assert.AreEqual(36, ((string)sent["eventId"]).Length);
            Assert.AreEqual(1792144800000L, (long)sent["eventTime"]);
            Assert.AreEqual("player-42", (string)sent["customPayload"]["custom_id"]);
            Assert.AreEqual(userId, (string)sent["customPayload"]["assignment_user_id"]);
            StringAssert.Contains("userId=" + userId, Transport.Last.Url);
        }

        [Test]
        public void CustomIdDoesNotDecideTheGroupAndALaterFirebaseValueStillApplies() {
            NewProcess();
            ABAssignment.ReportCustomId("player-42");
            Assert.IsFalse(ABAssignment.TryGetCurrent(out _));
            Assert.AreEqual(0, Assignments().Count);
            Assert.IsFalse(Mappings()[0].Payload.ContainsKey("custom_id_source"));

            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, ConfigA(), null);
            Assert.AreEqual(AssignmentResult.SourceRemoteThisSession, ABAssignment.Current.ConfigSource);
            Assert.AreEqual(1, Assignments().Count);
            Assert.IsFalse(Assignments()[0].Payload.ContainsKey("custom_user_ids"), "not stored like AddCustomUserId");
        }

        [Test]
        public void CustomIdBeforeInitializeIsQueuedAndSentOnInitialize() {
            NewBareProcess();
            ABAssignment.EventLogged += Sink.OnEvent;
            ABAssignment.ReportCustomId("player-42", "login");
            Assert.AreEqual(1, Mappings().Count);
            Assert.AreEqual(0, Transport.Requests.Count);

            ABAssignment.Initialize(new ABAssignmentOptions(AppId, ApiKey));
            JObject sent = SentNamed("upliftCustomIdMapping")[0];
            Assert.AreEqual(ABAssignment.UserId, (string)sent["userId"]);
            Assert.AreEqual(ABAssignment.UserId, (string)sent["customPayload"]["assignment_user_id"]);
        }

        [Test]
        public void LateCustomIdCarriesTheSameStableUserIdInEverySession() {
            AssignmentResult first = StartSessionCached(ConfigA());
            Clock.Advance(TimeSpan.FromMinutes(20));
            ABAssignment.ReportCustomId("player-42", "login");
            Assert.AreEqual(first.UserId, Mappings()[0].Payload["assignment_user_id"]);
            Assert.AreEqual(first.SessionId, Mappings()[0].Payload["session_id"]);
            Assert.AreEqual(1, Assignments().Count, "no extra upliftAssignment");

            AssignmentResult second = StartSession();
            ABAssignment.ReportCustomId("player-42", "login");
            Assert.AreEqual(2, Mappings().Count, "the same pair is sent again in a new session");
            Assert.AreEqual(first.UserId, Mappings()[1].Payload["assignment_user_id"]);
            Assert.AreEqual(second.SessionId, Mappings()[1].Payload["session_id"]);
        }

        [Test]
        public void SamePairIsSentOncePerSessionAndADifferentPairAgain() {
            NewProcess();
            ABAssignment.ReportCustomId("player-42", "login");
            ABAssignment.ReportCustomId("player-42", "login");
            Assert.AreEqual(1, Mappings().Count);
            ABAssignment.ReportCustomId("player-43", "login");
            ABAssignment.ReportCustomId("player-43", "game_center");
            ABAssignment.ReportCustomId("player-43");
            Assert.AreEqual(4, Mappings().Count);
            Assert.AreEqual(0, Errors.Count);
        }

        [TestCase(null, null)]
        [TestCase("", null)]
        [TestCase("   ", null)]
        [TestCase("player-42", "")]
        [TestCase("player-42", " ")]
        public void BlankCustomIdOrSourceIsRejected(string customId, string source) {
            NewProcess();
            ABAssignment.ReportCustomId(customId, source);
            Assert.AreEqual(0, Sink.Sent.Count);
            Assert.AreEqual(1, Errors.Count);
            Assert.IsFalse(ABAssignment.TryGetCurrent(out _));
        }

        [Test]
        public void CustomIdLongerThanTheStringCapIsRejectedNotCut() {
            NewProcess();
            ABAssignment.ReportCustomId(new string('a', 201));
            ABAssignment.ReportCustomId("player-42", new string('s', 201));
            Assert.AreEqual(0, Sink.Sent.Count);
            Assert.AreEqual(2, Errors.Count);
            ABAssignment.ReportCustomId(new string('a', 200));
            Assert.AreEqual(new string('a', 200), Mappings()[0].Payload["custom_id"]);
        }

        [Test]
        public void OffThreadCustomIdIsLoggedAndIgnored() {
            NewProcess();
            Assert.IsNotNull(ABAssignment.UserId);
            var worker = new Thread(() => ABAssignment.ReportCustomId("player-42"));
            worker.Start();
            worker.Join();
            Assert.AreEqual(0, Sink.Sent.Count);
            Assert.AreEqual(1, Errors.Count);
            Assert.IsFalse(Store.AccessedFromOtherThread);
        }

        [Test]
        public void CustomIdIsRejectedWhenTheGeneratedIdIsMissing() {
            Store.ThrowOnKey = AssignmentEngine.UserIdKey;
            NewProcess();
            Errors.Clear();
            ABAssignment.ReportCustomId("player-42");
            Assert.AreEqual(0, Mappings().Count);
            Assert.AreEqual(1, Errors.Count);
        }

        // ---- client-managed mode ----

        [Test]
        public void ClientReportSendsOneAssignmentWithBothIdsInSeparateFields() {
            NewClientManagedProcess();
            string userId = ABAssignment.UserId;
            ABAssignment.ReportClientAssignment(Group.Metica, ProviderId, "studio_test_1");

            Assert.AreEqual(1, Sink.Sent.Count);
            SentEvent e = Assignments()[0];
            Assert.AreEqual("studio_test_1", e.Payload["experiment_id"]);
            Assert.AreEqual("metica", e.Payload["group"]);
            Assert.AreEqual(true, e.Payload["metica_route"]);
            Assert.AreEqual("client_managed", e.Payload["config_source"]);
            Assert.AreEqual(ProviderId, e.Payload["provider_user_id"]);
            Assert.AreEqual(userId, e.Payload["assignment_user_id"]);
            Assert.AreEqual("2026-10-16", e.Payload["activity_date_utc"]);
            Assert.IsFalse(e.Payload.ContainsKey("metica_percentage"));
            Assert.IsFalse(e.Payload.ContainsKey("error"));

            JObject sent = SentNamed("upliftAssignment")[0];
            Assert.AreEqual(userId, (string)sent["userId"], "the provider id never replaces the event user");
            Assert.AreEqual(ProviderId, (string)sent["customPayload"]["provider_user_id"]);

            AssignmentResult current = ABAssignment.Current;
            Assert.AreEqual(Group.Metica, current.Group);
            Assert.IsTrue(current.RunMetica);
            Assert.AreEqual(AssignmentResult.SourceClientManaged, current.ConfigSource);
            Assert.AreEqual(userId, current.UserId);
            Assert.AreEqual(e.Payload["session_id"], current.SessionId);
            Assert.AreEqual("metica|LevelEnd", ABAssignment.TagPlacement("LevelEnd"));
        }

        [Test]
        public void ClientReportWithoutExperimentSendsAnEmptyExperimentId() {
            NewClientManagedProcess();
            ABAssignment.ReportClientAssignment(Group.Baseline, ProviderId);
            Assert.AreEqual("", Assignments()[0].Payload["experiment_id"]);
            Assert.AreEqual("baseline", Assignments()[0].Payload["group"]);
            Assert.AreEqual(false, Assignments()[0].Payload["metica_route"]);
            Assert.IsFalse(ABAssignment.Current.RunMetica);
        }

        [Test]
        public void BeforeTheReportNothingDecidesOrSendsTheGroup() {
            string user = FindUserId(ExperimentA, 5000, Group.Metica);
            SetUserId(user);
            CacheConfig(ConfigA());
            string cached = Store.Values[AssignmentEngine.ConfigKey];
            NewClientManagedProcess();

            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, ConfigB(), null);
            AssignmentResult pending = ABAssignment.Current;
            Assert.AreEqual(Group.Unassigned, pending.Group);
            Assert.AreEqual(AssignmentResult.SourceClientManagedPending, pending.ConfigSource);
            Assert.AreEqual(user, pending.UserId);
            Assert.AreEqual("unassigned|LevelEnd", ABAssignment.TagPlacement("LevelEnd"));
            Assert.IsFalse(ABAssignment.TryGetCurrent(out _));
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            Clock.Advance(TimeSpan.FromSeconds(40));
            ABAssignment.Tick();
            Clock.Advance(TimeSpan.FromDays(1));
            ABAssignment.Tick();
            ABAssignment.OnApplicationResumed();

            Assert.AreEqual(0, Sink.Sent.Count, "no assignment, fetch timeout or ads-init event");
            Assert.AreEqual(cached, Store.Values[AssignmentEngine.ConfigKey], "the Firebase value is not cached");
            Assert.IsFalse(Store.Values.ContainsKey(AssignmentEngine.EnrollmentKey));
            Assert.AreEqual(0, Errors.Count);

            // The cache would put this user in Metica; the game's choice is reported as given.
            ABAssignment.ReportClientAssignment(Group.Baseline, ProviderId);
            Assert.AreEqual(1, Assignments().Count);
            Assert.AreEqual("baseline", Assignments()[0].Payload["group"]);
            Assert.AreEqual(Group.Baseline, ABAssignment.Current.Group);
        }

        [Test]
        public void OutcomeBeforeAnyReportIsRejectedAndAcceptedAfterIt() {
            NewClientManagedProcess();
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            Clock.Advance(TimeSpan.FromMilliseconds(2500));
            ABAssignment.ReportAdsInit(AdsInitStage.Succeeded, null, "TRIAL", false);
            Assert.AreEqual(0, Sink.Sent.Count, "an outcome cannot be sent without a group");
            Assert.AreEqual(1, Errors.Count);

            ABAssignment.ReportClientAssignment(Group.Metica, ProviderId);
            ABAssignment.ReportAdsInit(AdsInitStage.Succeeded, null, "TRIAL", false);
            Assert.AreEqual(1, AdsInits().Count);
            Assert.AreEqual(2500L, AdsInits()[0].Payload["elapsed_ms"]);
        }

        [Test]
        public void MeticaRouteReportsTheGroupFirstAndTheIdWhenInitReturnsIt() {
            NewClientManagedProcess();
            // The Metica SDK user id is known only in the init response: report the group without it.
            ABAssignment.ReportClientAssignment(Group.Metica, null);
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            Clock.Advance(TimeSpan.FromMilliseconds(2500));
            ABAssignment.ReportClientAssignment(Group.Metica, ProviderId);
            ABAssignment.ReportAdsInit(AdsInitStage.Succeeded, null, "TRIAL", false);

            Assert.AreEqual(2, Assignments().Count);
            Assert.IsFalse(Assignments()[0].Payload.ContainsKey("provider_user_id"), "never a placeholder id");
            Assert.AreEqual(ABAssignment.UserId, Assignments()[0].Payload["assignment_user_id"]);
            Assert.AreEqual(ProviderId, Assignments()[1].Payload["provider_user_id"]);
            Assert.AreEqual(Assignments()[0].Payload["session_id"], Assignments()[1].Payload["session_id"]);
            Assert.AreEqual(1, AdsInits().Count);
            SentEvent e = AdsInits()[0];
            Assert.AreEqual("metica", e.Payload["group"]);
            Assert.AreEqual("metica", e.Payload["network"]);
            Assert.AreEqual("success", e.Payload["outcome"]);
            Assert.AreEqual(2500L, e.Payload["elapsed_ms"]);
            Assert.AreEqual("TRIAL", e.Payload["metica_internal_group"]);
            Assert.AreEqual(0, Errors.Count);
        }

        [Test]
        public void MeticaInitThatNeverAnswersStillSendsTheGroupAndTheTimeout() {
            NewClientManagedProcess();
            ABAssignment.ReportClientAssignment(Group.Metica, null);
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            Clock.Advance(TimeSpan.FromSeconds(31));
            ABAssignment.Tick();
            Assert.AreEqual(1, Assignments().Count);
            Assert.IsFalse(Assignments()[0].Payload.ContainsKey("provider_user_id"));
            Assert.AreEqual("pending_timeout", AdsInits()[0].Payload["outcome"]);
            Assert.AreEqual("metica", AdsInits()[0].Payload["group"]);
        }

        [Test]
        public void BaselineRouteReportsBeforeInitAndTheAutomaticFlowIsNotUsed() {
            NewClientManagedProcess();
            ABAssignment.ReportClientAssignment(Group.Baseline, "max-user-1");
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            ABAssignment.ReportAdsInit(AdsInitStage.Failed, "MAX failed");
            Assert.AreEqual(1, Assignments().Count);
            Assert.AreEqual("applovin", AdsInits()[0].Payload["network"]);
            Assert.AreEqual("failed", AdsInits()[0].Payload["outcome"]);
            Assert.AreEqual(0, ConfigFetches().Count);
        }

        [Test]
        public void AdsInitTimeoutWaitsForTheReport() {
            NewClientManagedProcess();
            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            Clock.Advance(TimeSpan.FromSeconds(31));
            ABAssignment.Tick();
            Assert.AreEqual(0, Sink.Sent.Count);

            ABAssignment.ReportClientAssignment(Group.Metica, ProviderId);
            ABAssignment.Tick();
            Assert.AreEqual(1, AdsInits().Count);
            Assert.AreEqual("pending_timeout", AdsInits()[0].Payload["outcome"]);
            Assert.AreEqual("metica", AdsInits()[0].Payload["group"]);
        }

        [Test]
        public void RepeatedReportsAndIdentityChanges() {
            NewClientManagedProcess();
            ABAssignment.ReportClientAssignment(Group.Metica, ProviderId, "studio_test_1");
            ABAssignment.ReportClientAssignment(Group.Metica, ProviderId, "studio_test_1");
            Assert.AreEqual(1, Assignments().Count, "a retried report sends nothing");

            ABAssignment.ReportClientAssignment(Group.Metica, "metica-sdk-user-2", "studio_test_1");
            Assert.AreEqual(2, Assignments().Count, "a new provider id sends one more");
            Assert.AreEqual("metica-sdk-user-2", Assignments()[1].Payload["provider_user_id"]);
            Assert.AreEqual(Assignments()[0].Payload["assignment_user_id"], Assignments()[1].Payload["assignment_user_id"]);
            ABAssignment.ReportClientAssignment(Group.Metica, null, "studio_test_1");
            Assert.AreEqual(2, Assignments().Count, "null never clears a known id");
            Assert.AreEqual("metica-sdk-user-2", Assignments()[1].Payload["provider_user_id"]);
            Assert.AreEqual(0, Errors.Count);

            ABAssignment.ReportClientAssignment(Group.Baseline, "metica-sdk-user-2", "studio_test_1");
            ABAssignment.ReportClientAssignment(Group.Metica, "metica-sdk-user-2", "studio_test_2");
            ABAssignment.ReportClientAssignment(Group.Metica, "metica-sdk-user-2");
            Assert.AreEqual(2, Assignments().Count);
            Assert.AreEqual(3, Errors.Count);
            Assert.AreEqual(Group.Metica, ABAssignment.Current.Group, "never overwritten");
            Assert.AreEqual("studio_test_1", ABAssignment.Current.ExperimentId);
        }

        [Test]
        public void DayChangeResendsTheReportedGroupWithTheLatestProviderId() {
            Clock.UtcNow = new DateTime(2026, 10, 16, 23, 50, 0, DateTimeKind.Utc);
            NewClientManagedProcess();
            ABAssignment.ReportClientAssignment(Group.Baseline, ProviderId);
            ABAssignment.ReportClientAssignment(Group.Baseline, "max-user-2");
            Clock.Advance(TimeSpan.FromMinutes(20));
            ABAssignment.Tick();
            ABAssignment.Tick();
            Assert.AreEqual(3, Assignments().Count);
            SentEvent nextDay = Assignments()[2];
            Assert.AreEqual("2026-10-17", nextDay.Payload["activity_date_utc"]);
            Assert.AreEqual("client_managed", nextDay.Payload["config_source"]);
            Assert.AreEqual("max-user-2", nextDay.Payload["provider_user_id"]);
            Assert.AreEqual(Assignments()[0].Payload["session_id"], nextDay.Payload["session_id"]);
        }

        [Test]
        public void DayChangeBeforeTheProviderIdIsKnownSendsNoProviderId() {
            Clock.UtcNow = new DateTime(2026, 10, 16, 23, 50, 0, DateTimeKind.Utc);
            NewClientManagedProcess();
            ABAssignment.ReportClientAssignment(Group.Metica, null);
            Clock.Advance(TimeSpan.FromMinutes(20));
            ABAssignment.Tick();
            Assert.AreEqual(2, Assignments().Count);
            Assert.AreEqual("2026-10-17", Assignments()[1].Payload["activity_date_utc"]);
            Assert.IsFalse(Assignments()[1].Payload.ContainsKey("provider_user_id"));
            Assert.AreEqual(ABAssignment.UserId, Assignments()[1].Payload["assignment_user_id"]);
        }

        [Test]
        public void ModeIsPerSessionAndANewSessionNeedsANewReport() {
            NewClientManagedProcess();
            ABAssignment.ReportClientAssignment(Group.Metica, ProviderId);

            CacheConfig(ConfigA());
            AssignmentResult automatic = StartSession();
            Assert.AreEqual(AssignmentResult.SourceCachedRemote, automatic.ConfigSource, "not persisted");

            NewClientManagedProcess();
            Assert.AreEqual(AssignmentResult.SourceClientManagedPending, ABAssignment.Current.ConfigSource);
            ABAssignment.ReportClientAssignment(Group.Metica, ProviderId);
            Assert.AreEqual(3, Assignments().Count);
            Assert.AreEqual(automatic.UserId, Assignments()[2].Payload["assignment_user_id"]);
        }

        [Test]
        public void ClientReportBeforeInitializeIsQueuedAndSentOnInitialize() {
            NewBareProcess();
            ABAssignment.SetAssignmentMode(AssignmentMode.ClientManaged);
            ABAssignment.ReportClientAssignment(Group.Metica, ProviderId);
            Assert.AreEqual(0, Transport.Requests.Count);
            Assert.AreEqual(1, Warnings.Count, "Initialize not called yet");

            ABAssignment.Initialize(new ABAssignmentOptions(AppId, ApiKey));
            JObject sent = SentNamed("upliftAssignment")[0];
            Assert.AreEqual(ABAssignment.UserId, (string)sent["userId"]);
            Assert.AreEqual(ProviderId, (string)sent["customPayload"]["provider_user_id"]);
        }

        [Test]
        public void ModeChangeAfterTheGroupIsDecidedIsRejected() {
            CacheConfig(ConfigA());
            AssignmentResult automatic = StartSession();
            ABAssignment.SetAssignmentMode(AssignmentMode.ClientManaged);
            ABAssignment.ReportClientAssignment(Group.Metica, ProviderId);
            Assert.AreEqual(2, Errors.Count);
            Assert.AreSame(automatic, ABAssignment.Current);
            Assert.AreEqual(1, Assignments().Count);

            ABAssignment.SetAssignmentMode(AssignmentMode.Automatic);
            Assert.AreEqual(2, Errors.Count, "setting the mode in effect is not an error");
        }

        [Test]
        public void ClientModeCanBeSwitchedBackBeforeAnyReport() {
            NewClientManagedProcess();
            ABAssignment.SetAssignmentMode(AssignmentMode.Automatic);
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, ConfigA(), null);
            Assert.AreEqual(AssignmentResult.SourceRemoteThisSession, ABAssignment.Current.ConfigSource);
            Assert.AreEqual(1, ConfigFetches().Count);
        }

        [Test]
        public void ReportWithoutClientManagedModeIsRejected() {
            NewProcess();
            ABAssignment.ReportClientAssignment(Group.Metica, ProviderId);
            Assert.AreEqual(1, Errors.Count);
            Assert.AreEqual(0, Sink.Sent.Count);
            Assert.IsFalse(ABAssignment.TryGetCurrent(out _));
        }

        [Test]
        public void UnknownModeIsRejected() {
            NewProcess();
            ABAssignment.SetAssignmentMode((AssignmentMode)5);
            Assert.AreEqual(1, Errors.Count);
            CacheConfig(ConfigA());
            Assert.AreEqual(AssignmentResult.SourceCachedRemote, ABAssignment.Current.ConfigSource, "still automatic");
        }

        [TestCase(Group.Unassigned, ProviderId, null)]
        [TestCase((Group)7, ProviderId, null)]
        [TestCase(Group.Metica, "", null)]
        [TestCase(Group.Metica, "  ", null)]
        [TestCase(Group.Metica, ProviderId, "")]
        [TestCase(Group.Metica, ProviderId, " ")]
        public void InvalidReportIsRejectedAndALaterValidOneWorks(Group group, string providerUserId, string experimentId) {
            NewClientManagedProcess();
            ABAssignment.ReportClientAssignment(group, providerUserId, experimentId);
            Assert.AreEqual(0, Sink.Sent.Count);
            Assert.AreEqual(1, Errors.Count);
            Assert.IsFalse(ABAssignment.TryGetCurrent(out _));

            ABAssignment.ReportClientAssignment(Group.Metica, ProviderId);
            Assert.AreEqual(1, Assignments().Count);
        }

        [Test]
        public void ProviderIdLongerThanTheStringCapIsRejected() {
            NewClientManagedProcess();
            ABAssignment.ReportClientAssignment(Group.Metica, new string('p', 201));
            ABAssignment.ReportClientAssignment(Group.Metica, ProviderId, new string('e', 201));
            Assert.AreEqual(0, Sink.Sent.Count);
            Assert.AreEqual(2, Errors.Count);
        }

        [Test]
        public void OffThreadClientCallsAreLoggedAndIgnored() {
            NewProcess();
            Assert.IsNotNull(ABAssignment.UserId);
            var worker = new Thread(() => {
                ABAssignment.SetAssignmentMode(AssignmentMode.ClientManaged);
                ABAssignment.ReportClientAssignment(Group.Metica, ProviderId);
            });
            worker.Start();
            worker.Join();
            Assert.AreEqual(2, Errors.Count);
            Assert.AreEqual(0, Sink.Sent.Count);
            Assert.IsFalse(Store.AccessedFromOtherThread);
        }

        [Test]
        public void DebugOverrideDoesNotChangeTheClientRoute() {
            ABAssignment.SetDebugOverride(Group.Metica);
            NewClientManagedProcess();
            ABAssignment.ReportClientAssignment(Group.Baseline, ProviderId);
            Assert.IsFalse(ABAssignment.Current.RunMetica);
        }

        [Test]
        public void CustomIdsWorkTheSameInClientManagedMode() {
            NewClientManagedProcess();
            ABAssignment.ReportCustomId("player-42", "login");
            ABAssignment.AddCustomUserId("login", "player-42");
            Assert.AreEqual(1, Sink.Sent.Count, "AddCustomUserId waits for the group");
            ABAssignment.ReportClientAssignment(Group.Metica, ProviderId);
            Assert.AreEqual("login=player-42", Assignments()[0].Payload["custom_user_ids"]);
            Assert.AreEqual(Mappings()[0].Payload["assignment_user_id"], Assignments()[0].Payload["assignment_user_id"]);
        }

        // ---- automatic mode is unchanged ----

        [Test]
        public void AutomaticAssignmentHasNoClientFields() {
            StartSessionCached(ConfigA());
            ABAssignment.ReportCustomId("player-42");
            Assert.AreEqual(1, Assignments().Count);
            Assert.IsFalse(Assignments()[0].Payload.ContainsKey("provider_user_id"));
            Assert.IsFalse(Assignments()[0].Payload.ContainsKey("assignment_user_id"));
            Assert.IsTrue(Assignments()[0].Payload.ContainsKey("metica_percentage"));
        }
    }
}
