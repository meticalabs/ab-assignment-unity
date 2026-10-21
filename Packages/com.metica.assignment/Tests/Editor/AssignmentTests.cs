using System;
using System.Threading;
using NUnit.Framework;

namespace Metica.Assignment.Tests {
    public class AssignmentTests : AssignmentTestBase {
        // ---- user id ----

        [Test]
        public void FirstRunGeneratesAndPersistsUserId() {
            AssignmentResult first = StartSession(ConfigA());
            Assert.AreEqual(32, first.UserId.Length);
            Assert.AreEqual(first.UserId, Store.Values[AssignmentEngine.UserIdKey]);

            AssignmentResult second = StartSession();
            Assert.AreEqual(first.UserId, second.UserId);
            Assert.AreNotEqual(first.SessionId, second.SessionId);
        }

        [Test]
        public void UserIdIsAvailableWithoutDecidingTheGroup() {
            NewProcess();
            string userId = MeticaAssignment.UserId;
            Assert.AreEqual(32, userId.Length);
            Assert.IsFalse(MeticaAssignment.TryGetCurrent(out _));
            Assert.AreEqual(0, Assignments().Count);

            MeticaAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, ConfigA(), null);
            AssignmentResult result = MeticaAssignment.Current;
            Assert.AreEqual(userId, result.UserId);
            Assert.AreEqual(AssignmentResult.SourceRemoteThisSession, result.ConfigSource);
        }

        [Test]
        public void NewInstallGetsNewUserIdAndIndependentDraw() {
            AssignmentResult first = StartSession(ConfigA());
            Store = new FakeStore(); // reinstall clears PlayerPrefs
            AssignmentResult reinstalled = StartSession(ConfigA());
            Assert.AreNotEqual(first.UserId, reinstalled.UserId);
            Assert.AreEqual(Bucketing.Bucket(ExperimentA, reinstalled.UserId), reinstalled.Bucket);
        }

        // ---- when the group is decided ----

        [Test]
        public void FirstSessionIsAssignedFromAValueThatArrivesBeforeTheGroupIsNeeded() {
            AssignmentResult result = StartSession(ConfigA());
            Assert.AreEqual(ExperimentA, result.ExperimentId);
            Assert.AreEqual(AssignmentResult.SourceRemoteThisSession, result.ConfigSource);
            Assert.AreEqual(5000, result.MeticaPercentageBasisPoints);
            Assert.IsTrue(result.Enabled);
            Assert.IsTrue(result.IsNewAssignment);

            AssignmentResult next = StartSession();
            Assert.AreEqual(AssignmentResult.SourceCachedRemote, next.ConfigSource);
            Assert.AreEqual(result.Group, next.Group);
            Assert.IsFalse(next.IsNewAssignment);
        }

        [Test]
        public void FirstSessionIsUnassignedWhenTheGroupIsNeededBeforeAnyValue() {
            AssignmentResult result = StartSession();
            Assert.AreEqual(Group.Unassigned, result.Group);
            Assert.AreEqual("unassigned", result.Label);
            Assert.AreEqual(AssignmentResult.SourceUnassignedWaiting, result.ConfigSource);
            Assert.IsFalse(result.RunMetica);
            Assert.AreEqual("", result.ExperimentId);
            Assert.AreEqual(-1, result.Bucket);
            Assert.AreEqual(32, result.UserId.Length);
            Assert.AreEqual("unassigned|AdBreak", MeticaAssignment.TagPlacement("AdBreak"));

            // The value arrives later in the same session: the group stays fixed; it applies next session.
            MeticaAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, ConfigA(), null);
            Assert.AreSame(result, MeticaAssignment.Current);
            Assert.AreEqual(Group.Unassigned, MeticaAssignment.Current.Group);

            AssignmentResult next = StartSession();
            Assert.AreEqual(ExperimentA, next.ExperimentId);
            Assert.AreEqual(AssignmentResult.SourceCachedRemote, next.ConfigSource);
        }

        [Test]
        public void OfflineInstallStaysUnassignedUntilAValueArrivesInTime() {
            NewProcess();
            MeticaAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, "offline");
            Assert.AreEqual(AssignmentResult.SourceUnassignedWaiting, MeticaAssignment.Current.ConfigSource);

            NewProcess();
            MeticaAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, "offline");
            Assert.AreEqual(Group.Unassigned, MeticaAssignment.Current.Group);

            AssignmentResult online = StartSession(ConfigA());
            Assert.AreEqual(AssignmentResult.SourceRemoteThisSession, online.ConfigSource);
            Assert.AreEqual(ExperimentA, online.ExperimentId);
        }

        [Test]
        public void AFreshValueBeforeTheGroupIsNeededWinsOverTheCache() {
            StartSession(ConfigA());
            AssignmentResult result = StartSession(ConfigB());
            Assert.AreEqual(ExperimentB, result.ExperimentId);
            Assert.AreEqual(AssignmentResult.SourceRemoteThisSession, result.ConfigSource);
        }

        [Test]
        public void ReportingTheFetchDoesNotDecideTheGroup() {
            NewProcess();
            MeticaAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, "offline");
            Assert.AreEqual(0, Assignments().Count);
            Assert.AreEqual(1, ConfigFetches().Count);
            Assert.IsFalse(MeticaAssignment.TryGetCurrent(out _));

            MeticaAssignment.ReportAdsInit(AdsInitStage.Started);
            Assert.IsTrue(MeticaAssignment.TryGetCurrent(out AssignmentResult decided));
            Assert.AreEqual(AssignmentResult.SourceUnassignedWaiting, decided.ConfigSource);
            Assert.AreEqual(1, Assignments().Count);
        }

        [Test]
        public void TagPlacementDecidesTheGroupOnFirstUse() {
            SetUserId(FindUserId(ExperimentA, 5000, Group.Metica));
            NewProcess();
            MeticaAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, ConfigA(), null);
            Assert.AreEqual("metica|MissionComplete", MeticaAssignment.TagPlacement("MissionComplete"));
            Assert.IsTrue(MeticaAssignment.TryGetCurrent(out _));
        }

        [Test]
        public void EveryReadReturnsTheSameSession() {
            AssignmentResult first = StartSession(ConfigA());
            Assert.AreSame(first, MeticaAssignment.Current);
            Assert.AreEqual(1, Assignments().Count);
        }

        [Test]
        public void StorageFailureIsUnassignedWithAnError() {
            Store.ThrowOnKey = AssignmentEngine.UserIdKey;
            AssignmentResult result = StartSession(ConfigA());
            Assert.AreEqual(Group.Unassigned, result.Group);
            Assert.AreEqual(AssignmentResult.SourceUnassignedError, result.ConfigSource);
            Assert.IsNotEmpty(Errors);
        }

        // ---- determinism, cache and experiments ----

        [Test]
        public void GroupIsAPureFunctionOfUserAndExperiment() {
            SetUserId("user-1");
            AssignmentResult first = StartSession(ConfigA());
            Assert.AreEqual(Bucketing.Bucket(ExperimentA, "user-1"), first.Bucket);
            Assert.AreEqual(Bucketing.GroupFor(first.Bucket, 5000), first.Group);
            Assert.AreEqual(first.Group == Group.Metica, first.RunMetica);

            Store.Values.Remove(AssignmentEngine.EnrollmentKey);
            AssignmentResult again = StartSession(ConfigA());
            Assert.AreEqual(first.Group, again.Group);
            Assert.AreEqual(first.Bucket, again.Bucket);
            Assert.IsTrue(again.IsNewAssignment);
        }

        [Test]
        public void ValueReceivedAfterTheGroupIsDecidedAppliesFromTheNextSession() {
            StartSession(ConfigA());
            MeticaAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, ConfigB(), null);
            Assert.AreEqual(ExperimentA, MeticaAssignment.Current.ExperimentId, "no change mid-session");

            AssignmentResult next = StartSession();
            Assert.AreEqual(ExperimentB, next.ExperimentId);
            Assert.AreEqual(AssignmentResult.SourceCachedRemote, next.ConfigSource);
            Assert.AreEqual(9000, next.MeticaPercentageBasisPoints);
            Assert.IsTrue(next.IsNewAssignment);
        }

        [TestCase(RemoteConfigStatus.Success, null)]
        [TestCase(RemoteConfigStatus.FetchFailed, null)]
        [TestCase(RemoteConfigStatus.ActivationFailed, null)]
        [TestCase(RemoteConfigStatus.Success, "{\"experiment_id\":\"studio_mygame-20261021-10\",\"metica_percentage\":20,\"enabled\":true}")]
        [TestCase(RemoteConfigStatus.Success, "not json")]
        public void MissingFailedOrInvalidBeforeTheGroupIsNeededUsesTheCache(RemoteConfigStatus status, string value) {
            CacheConfig(ConfigB());
            NewProcess();
            MeticaAssignment.ReportRemoteConfig(status, value, "error");
            AssignmentResult result = MeticaAssignment.Current;
            Assert.AreEqual(ExperimentB, result.ExperimentId);
            Assert.AreEqual(AssignmentResult.SourceCachedRemote, result.ConfigSource);
            Assert.AreEqual(ExperimentB, StartSession().ExperimentId, "the cache is unchanged");
        }

        [Test]
        public void InvalidValueWithoutCacheLeavesTheSessionUnassigned() {
            AssignmentResult result = StartSession("{\"experiment_id\":\"bad\"}");
            Assert.AreEqual(Group.Unassigned, result.Group);
            Assert.AreEqual(AssignmentResult.SourceUnassignedWaiting, result.ConfigSource);
            Assert.AreEqual("invalid", ConfigFetches()[0].Payload["fetch_status"]);
            Assert.IsNotEmpty(Warnings);
        }

        [Test]
        public void UnreadableCacheIsIgnored() {
            Store.Values[AssignmentEngine.ConfigKey] = "{garbage";
            Assert.AreEqual(AssignmentResult.SourceUnassignedWaiting, StartSession().ConfigSource);
            Assert.AreEqual(AssignmentResult.SourceRemoteThisSession, StartSession(ConfigA()).ConfigSource);
        }

        [Test]
        public void CachedValueAppliesNextSessionEvenOffline() {
            string user = FindUserId(ExperimentA, 5000, Group.Metica);
            SetUserId(user);
            AssignmentResult one = StartSession(ConfigA());
            MeticaAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, ConfigB(false), null);

            NewProcess();
            MeticaAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, "offline");
            AssignmentResult two = MeticaAssignment.Current;
            Assert.AreEqual(ExperimentB, two.ExperimentId);
            Assert.AreEqual(Bucketing.GroupFor(Bucketing.Bucket(ExperimentB, user), 9000), two.Group);
            Assert.IsTrue(one.RunMetica);
            Assert.IsFalse(two.RunMetica, "kill switch in B");
            Assert.AreEqual(false, Assignments()[1].Payload["metica_route"]);
        }

        [Test]
        public void AToBToARestoresTheOriginalGroup() {
            string user = FindUserId(ExperimentA, 5000, Group.Baseline);
            SetUserId(user);
            AssignmentResult a1 = StartSession(ConfigA());
            MeticaAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, ConfigB(), null);
            AssignmentResult b = StartSession();
            MeticaAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, ConfigA(), null);
            AssignmentResult a2 = StartSession();

            Assert.AreEqual(Group.Baseline, a1.Group);
            Assert.AreEqual(Bucketing.GroupFor(Bucketing.Bucket(ExperimentB, user), 9000), b.Group);
            Assert.AreEqual(a1.Group, a2.Group);
            Assert.AreEqual(a1.Bucket, a2.Bucket);
            Assert.IsFalse(a2.IsNewAssignment);
        }

        [Test]
        public void TwoDevicesWithDifferentCachesReportTheirOwnExperiment() {
            StartSession(ConfigA());
            MeticaAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, ConfigB(), null);
            AssignmentResult one = StartSession();

            Store = new FakeStore();
            AssignmentResult two = StartSession(ConfigA());

            Assert.AreEqual(ExperimentB, one.ExperimentId);
            Assert.AreEqual(Bucketing.GroupFor(Bucketing.Bucket(ExperimentB, one.UserId), 9000), one.Group);
            Assert.AreEqual(ExperimentA, two.ExperimentId);
            Assert.AreEqual(Bucketing.GroupFor(Bucketing.Bucket(ExperimentA, two.UserId), 5000), two.Group);
        }

        // ---- kill switch ----

        [Test]
        public void KillSwitchEnableDisableEnableKeepsGroupAndLabel() {
            string user = FindUserId(ExperimentA, 5000, Group.Metica);
            SetUserId(user);

            AssignmentResult enabled = StartSession(ConfigA());
            Assert.IsTrue(enabled.RunMetica);
            MeticaAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, ConfigA(false), null);
            Assert.IsTrue(MeticaAssignment.Current.RunMetica, "not mid-session");

            AssignmentResult disabled = StartSession();
            Assert.AreEqual(Group.Metica, disabled.Group);
            Assert.AreEqual("metica", disabled.Label);
            Assert.IsFalse(disabled.Enabled);
            Assert.IsFalse(disabled.RunMetica);
            Assert.AreEqual("metica|MissionComplete", MeticaAssignment.TagPlacement("MissionComplete"));
            SentEvent ping = Assignments()[Assignments().Count - 1];
            Assert.AreEqual("metica", ping.Payload["group"]);
            Assert.AreEqual(false, ping.Payload["metica_route"]);
            MeticaAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, ConfigA(true), null);

            AssignmentResult reenabled = StartSession();
            Assert.AreEqual(Group.Metica, reenabled.Group);
            Assert.IsTrue(reenabled.RunMetica);
            Assert.AreEqual(enabled.Bucket, reenabled.Bucket);
            Assert.AreEqual(true, Assignments()[Assignments().Count - 1].Payload["metica_route"]);
        }

        [Test]
        public void KillSwitchReceivedBeforeTheGroupIsNeededAppliesInTheSameSession() {
            SetUserId(FindUserId(ExperimentA, 5000, Group.Metica));
            StartSession(ConfigA());
            AssignmentResult result = StartSession(ConfigA(false));
            Assert.AreEqual(Group.Metica, result.Group);
            Assert.IsFalse(result.RunMetica);
        }

        // ---- debug override, placement tags, threading, custom ids ----

        [Test]
        public void DebugOverrideChangesRouteFromNextSessionButNotGroupOrLabel() {
            string user = FindUserId(ExperimentA, 5000, Group.Baseline);
            SetUserId(user);
            StartSession(ConfigA());
            MeticaAssignment.SetDebugOverride(Group.Metica);
            Assert.IsFalse(MeticaAssignment.Current.RunMetica, "not mid-session");

            AssignmentResult overridden = StartSession();
            Assert.IsTrue(overridden.RunMetica);
            Assert.AreEqual(Group.Baseline, overridden.Group);
            Assert.AreEqual("baseline|AdBreak", MeticaAssignment.TagPlacement("AdBreak"));

            MeticaAssignment.SetDebugOverride(null);
            Assert.IsFalse(StartSession().RunMetica);
        }

        [Test]
        public void TagPlacementFormats() {
            string user = FindUserId(ExperimentA, 5000, Group.Metica);
            SetUserId(user);
            StartSession(ConfigA());
            Assert.AreEqual("metica|none", MeticaAssignment.TagPlacement(null));
            Assert.AreEqual("metica|none", MeticaAssignment.TagPlacement(""));
            Assert.AreEqual("metica|UnlockTruckOnMissionComplete", MeticaAssignment.TagPlacement("UnlockTruckOnMissionComplete"));
            Assert.AreEqual("metica|banner", MeticaAssignment.TagPlacement("metica|banner"));
            Assert.AreEqual("baseline|banner", MeticaAssignment.TagPlacement("baseline|banner"));
            Assert.AreEqual("unassigned|x", MeticaAssignment.TagPlacement("unassigned|x"));
        }

        [Test]
        public void TagPlacementAndCurrentAreSafeOffTheMainThreadOnceDecided() {
            string user = FindUserId(ExperimentA, 5000, Group.Metica);
            SetUserId(user);
            StartSession(ConfigA());
            string tag = null;
            string userId = null;
            var thread = new Thread(() => {
                tag = MeticaAssignment.TagPlacement("2XCoins");
                userId = MeticaAssignment.Current.UserId;
            });
            thread.Start();
            thread.Join();
            Assert.AreEqual("metica|2XCoins", tag);
            Assert.AreEqual(user, userId);
            Assert.IsFalse(Store.AccessedFromOtherThread);
        }

        [Test]
        public void OffThreadUseBeforeTheGroupIsDecidedDoesNotDecideIt() {
            NewProcess();
            Assert.IsNotNull(MeticaAssignment.UserId); // records the main thread
            string tag = null;
            var thread = new Thread(() => { tag = MeticaAssignment.TagPlacement("2XCoins"); });
            thread.Start();
            thread.Join();
            Assert.AreEqual("unassigned|2XCoins", tag);
            Assert.IsFalse(MeticaAssignment.TryGetCurrent(out _));
            Assert.IsFalse(Store.AccessedFromOtherThread);
        }

        [Test]
        public void CustomIdsNeverChangeGroupBucketOrUserId() {
            NewProcess();
            MeticaAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, ConfigA(), null);
            MeticaAssignment.AddCustomUserId("device_unique_identifier", "before-decision");
            AssignmentResult before = MeticaAssignment.Current;
            MeticaAssignment.AddCustomUserId("player", "p-1");
            AssignmentResult next = StartSession();
            Assert.AreEqual(before.UserId, next.UserId);
            Assert.AreEqual(before.Group, next.Group);
            Assert.AreEqual(before.Bucket, next.Bucket);
        }

        [Test]
        public void DatesUseUtc() {
            Clock.UtcNow = new DateTime(2026, 10, 16, 23, 59, 0, DateTimeKind.Utc);
            StartSession(ConfigA());
            Assert.AreEqual("2026-10-16", Assignments()[0].Payload["activity_date_utc"]);
        }
    }
}
