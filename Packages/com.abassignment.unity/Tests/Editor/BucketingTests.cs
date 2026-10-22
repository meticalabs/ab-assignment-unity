using NUnit.Framework;

namespace AB.Assignment.Tests {
    public class BucketingTests {
        // Generated with Python: int.from_bytes(hashlib.sha256((experiment_id + ":" + user_id).encode()).digest()[:8], "big") % 10000
        [TestCase("studio_mygame-20261016-50", "user-0", 465)]
        [TestCase("studio_mygame-20261016-50", "user-1", 253)]
        [TestCase("studio_mygame-20261016-50", "user-2", 2694)]
        [TestCase("studio_mygame-20261016-50", "6c0f2b0e9d6a4f7f8a0c1d2e3f405162", 4221)]
        [TestCase("studio_mygame-20261016-12.5", "user-0", 5302)]
        [TestCase("studio_mygame-20261016-12.5", "user-1", 4056)]
        [TestCase("studio_mygame-20261016-12.5", "6c0f2b0e9d6a4f7f8a0c1d2e3f405162", 7512)]
        [TestCase("studio_mygame-20261016-90", "user-0", 6572)]
        [TestCase("studio_mygame-20261016-90", "user-2", 9362)]
        [TestCase("studio_mygame-20261016-90", "6c0f2b0e9d6a4f7f8a0c1d2e3f405162", 4386)]
        public void MatchesPublishedTestVectors(string experimentId, string userId, int expectedBucket) {
            Assert.AreEqual(expectedBucket, Bucketing.Bucket(experimentId, userId));
        }

        [TestCase("studio_mygame-20261016-50", 5000)]
        [TestCase("studio_mygame-20261016-10", 1000)]
        [TestCase("studio_mygame-20261016-90", 9000)]
        public void SplitIsWithinOnePointAndUniform(string experimentId, int percentageBasisPoints) {
            const int users = 100000;
            int metica = 0;
            int[] ranges = new int[100];
            for (int i = 0; i < users; i++) {
                int bucket = Bucketing.Bucket(experimentId, "user-" + i);
                ranges[bucket / 100]++;
                if (Bucketing.GroupFor(bucket, percentageBasisPoints) == Group.Metica) {
                    metica++;
                }
            }
            double share = 100.0 * metica / users;
            Assert.AreEqual(percentageBasisPoints / 100.0, share, 1.0);

            // Chi-square over 100 equal bucket ranges, 99 degrees of freedom; 148.23 is the p = 0.001 critical value.
            double expected = users / 100.0;
            double chiSquare = 0;
            foreach (int count in ranges) {
                chiSquare += (count - expected) * (count - expected) / expected;
            }
            Assert.Less(chiSquare, 148.23);
        }

        [Test]
        public void EdgesOfTheSplit() {
            Assert.AreEqual(Group.Baseline, Bucketing.GroupFor(0, 0), "0% puts bucket 0 in Baseline");
            Assert.AreEqual(Group.Metica, Bucketing.GroupFor(9999, 10000), "100% puts bucket 9999 in Metica");
            Assert.AreEqual(Group.Metica, Bucketing.GroupFor(1249, 1250), "12.5%: bucket 1249 is Metica");
            Assert.AreEqual(Group.Baseline, Bucketing.GroupFor(1250, 1250), "12.5%: bucket 1250 is Baseline");
            Assert.AreEqual(Group.Metica, Bucketing.GroupFor(4999, 5000), "50%: bucket 4999 is Metica");
            Assert.AreEqual(Group.Baseline, Bucketing.GroupFor(5000, 5000), "50%: bucket 5000 is Baseline");
        }

        [Test]
        public void ZeroAndHundredPercentAssignEveryone() {
            for (int i = 0; i < 1000; i++) {
                string user = "user-" + i;
                Assert.AreEqual(Group.Baseline, Bucketing.GroupFor(Bucketing.Bucket("g-20261016-0", user), 0));
                Assert.AreEqual(Group.Metica, Bucketing.GroupFor(Bucketing.Bucket("g-20261016-100", user), 10000));
            }
        }

        /// <summary>
        /// What a percentage change 50 -> 90 does to already assigned devices. The shipped rule salts with the full
        /// experiment id, so the new id is an independent draw. A salt without the percentage is computed here only to
        /// show the difference.
        /// </summary>
        [Test]
        public void PercentageChange_FullIdSaltRedrawsEveryone_SaltWithoutPercentageMovesOnlyThresholdBuckets() {
            const string oldId = "studio_mygame-20261016-50";
            const string newId = "studio_mygame-20261016-90";
            const string saltWithoutPercentage = "studio_mygame-20261016";
            int o3MetaToBaseline = 0;
            int o2MetaToBaseline = 0;
            int o2Moved = 0;
            for (int i = 0; i < 10000; i++) {
                string user = "user-" + i;
                Group o3Old = Bucketing.GroupFor(Bucketing.Bucket(oldId, user), 5000);
                Group o3New = Bucketing.GroupFor(Bucketing.Bucket(newId, user), 9000);
                if (o3Old == Group.Metica && o3New == Group.Baseline) {
                    o3MetaToBaseline++;
                }
                int o2Bucket = Bucketing.Bucket(saltWithoutPercentage, user);
                Group o2Old = Bucketing.GroupFor(o2Bucket, 5000);
                Group o2New = Bucketing.GroupFor(o2Bucket, 9000);
                if (o2Old == Group.Metica && o2New == Group.Baseline) {
                    o2MetaToBaseline++;
                }
                if (o2Old != o2New) {
                    o2Moved++;
                    Assert.That(o2Bucket >= 5000 && o2Bucket < 9000, "A salt without the percentage moves only buckets between the old and new threshold");
                }
            }
            Assert.AreEqual(0, o2MetaToBaseline, "A salt without the percentage never moves a Metica device to Baseline when the share grows");
            Assert.Greater(o2Moved, 0);
            // The full-id salt is an independent draw: about 10% of the old Metica half lands in Baseline.
            Assert.Greater(o3MetaToBaseline, 300);
        }
    }
}
