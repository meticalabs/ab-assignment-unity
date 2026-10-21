using NUnit.Framework;

namespace Metica.Assignment.Tests {
    public class ConfigTests {
        private const string Game = "studio_mygame-20261016-";

        private static string Config(string experimentId, string percentage) {
            return "{\"experiment_id\":\"" + experimentId + "\",\"metica_percentage\":" + percentage + ",\"enabled\":true}";
        }

        [TestCase(Game + "50", "50", 5000)]
        [TestCase(Game + "0", "0", 0)]
        [TestCase(Game + "100", "100", 10000)]
        [TestCase(Game + "12.5", "12.5", 1250)]
        [TestCase(Game + "0.05", "0.05", 5)]
        [TestCase("com.studio.mygame-20261016-50", "50", 5000)]
        public void AcceptsValidExperimentIds(string experimentId, string percentage, int expectedBasisPoints) {
            Assert.IsTrue(AssignmentConfig.TryParse(Config(experimentId, percentage), out AssignmentConfig config, out string error), error);
            Assert.AreEqual(experimentId, config.ExperimentId);
            Assert.AreEqual(expectedBasisPoints, config.MeticaPercentageBasisPoints);
            Assert.IsTrue(config.Enabled);
        }

        [TestCase("Com.studio.mygame-20261016-50", "50", TestName = "Rejects upper-case game segment")]
        [TestCase("com-studio-20261016-50", "50", TestName = "Rejects '-' inside the game segment")]
        [TestCase(Game + "050", "50", TestName = "Rejects leading zero")]
        [TestCase(Game + "50.0", "50", TestName = "Rejects trailing decimal zero")]
        [TestCase(Game + "12.50", "12.5", TestName = "Rejects 12.50")]
        [TestCase(Game + "100.5", "100", TestName = "Rejects 100.5")]
        [TestCase("studio_mygame-20261332-50", "50", TestName = "Rejects date 20261332")]
        [TestCase("studio_mygame-20260230-50", "50", TestName = "Rejects date 20260230")]
        [TestCase(Game + "50", "90", TestName = "Rejects percentage segment != metica_percentage")]
        [TestCase("studio_mygame_2026_10", "50", TestName = "Rejects studio_game_yyyy_mm format")]
        [TestCase("studio_game_2026_10", "50", TestName = "Rejects an id without date and percentage segments")]
        [TestCase("", "50", TestName = "Rejects empty id")]
        public void RejectsInvalidExperimentIds(string experimentId, string percentage) {
            Assert.IsFalse(AssignmentConfig.TryParse(Config(experimentId, percentage), out _, out string error));
            Assert.IsNotNull(error);
        }

        [Test]
        public void RejectsGameSegmentLongerThan64Characters() {
            string id = new string('a', 65) + "-20261016-50";
            Assert.AreEqual(77, id.Length);
            Assert.IsFalse(AssignmentConfig.TryParse(Config(id, "50"), out _, out _));
            string longest = new string('a', 64) + "-20261016-0.05";
            Assert.AreEqual(78, longest.Length);
            Assert.IsTrue(AssignmentConfig.TryParse(Config(longest, "0.05"), out _, out string error), error);
        }

        [Test]
        public void RejectsTrailingNewlineInId() {
            Assert.IsFalse(AssignmentConfig.TryParse(Config(Game + "50\\n", "50"), out _, out _));
        }

        [Test]
        public void AcceptsUnknownExtraFields() {
            string json = "{\"experiment_id\":\"" + Game + "50\",\"metica_percentage\":50,\"enabled\":false," +
                          "\"future_field\":{\"nested\":[1,2,{\"x\":null}]},\"other\":\"x\"}";
            Assert.IsTrue(AssignmentConfig.TryParse(json, out AssignmentConfig config, out string error), error);
            Assert.IsFalse(config.Enabled);
        }

        [TestCase("{\"metica_percentage\":50,\"enabled\":true}", TestName = "Missing experiment_id")]
        [TestCase("{\"experiment_id\":\"" + Game + "50\",\"enabled\":true}", TestName = "Missing metica_percentage")]
        [TestCase("{\"experiment_id\":\"" + Game + "50\",\"metica_percentage\":50}", TestName = "Missing enabled")]
        [TestCase("{\"experiment_id\":50,\"metica_percentage\":50,\"enabled\":true}", TestName = "experiment_id wrong type")]
        [TestCase("{\"experiment_id\":\"" + Game + "50\",\"metica_percentage\":\"50\",\"enabled\":true}", TestName = "metica_percentage wrong type")]
        [TestCase("{\"experiment_id\":\"" + Game + "50\",\"metica_percentage\":50,\"enabled\":\"true\"}", TestName = "enabled wrong type")]
        [TestCase("{\"experiment_id\":\"" + Game + "50\",\"metica_percentage\":50,\"enabled\":1}", TestName = "enabled as number")]
        [TestCase("{\"experiment_id\":\"" + Game + "100\",\"metica_percentage\":100.5,\"enabled\":true}", TestName = "percentage above 100")]
        [TestCase("{\"experiment_id\":\"" + Game + "0\",\"metica_percentage\":-1,\"enabled\":true}", TestName = "percentage below 0")]
        [TestCase("{\"experiment_id\":\"" + Game + "12.5\",\"metica_percentage\":12.501,\"enabled\":true}", TestName = "percentage with three decimals")]
        [TestCase("{\"experiment_id\":\"" + Game + "50\",\"experiment_id\":\"" + Game + "50\",\"metica_percentage\":50,\"enabled\":true}", TestName = "Duplicate key")]
        [TestCase("[1,2]", TestName = "Not an object")]
        [TestCase("{\"experiment_id\":", TestName = "Truncated JSON")]
        [TestCase("{\"experiment_id\":\"" + Game + "50\",\"metica_percentage\":50,\"enabled\":true} x", TestName = "Trailing garbage")]
        [TestCase("", TestName = "Empty value")]
        public void RejectsInvalidValues(string json) {
            Assert.IsFalse(AssignmentConfig.TryParse(json, out AssignmentConfig config, out string error));
            Assert.IsNull(config);
            Assert.IsNotNull(error);
        }

        [Test]
        public void AcceptsEquivalentNumberForms() {
            Assert.IsTrue(AssignmentConfig.TryParse(Config(Game + "50", "50.00"), out AssignmentConfig a, out string errorA), errorA);
            Assert.AreEqual(5000, a.MeticaPercentageBasisPoints);
            Assert.IsTrue(AssignmentConfig.TryParse(Config(Game + "50", "5e1"), out AssignmentConfig b, out string errorB), errorB);
            Assert.AreEqual(5000, b.MeticaPercentageBasisPoints);
        }

        [Test]
        public void PublicValidatorMatchesInternalRules() {
            Assert.IsTrue(MeticaAssignment.IsValidConfig(Config(Game + "50", "50"), out string error), error);
            Assert.IsFalse(MeticaAssignment.IsValidConfig(Config(Game + "50", "90"), out error));
            StringAssert.Contains("does not equal", error);
        }

        [Test]
        public void WholeDoublesKeepTheirTypeThroughJson() {
            Assert.AreEqual("50.0", Json.Write(50.0));
            Assert.AreEqual("12.5", Json.Write(12.5));
            Assert.IsTrue(Json.TryParse(Json.Write(50.0), out object parsed, out string error), error);
            object value = ((JsonNumber)parsed).ToLongOrDouble();
            Assert.IsInstanceOf<double>(value);
            Assert.AreEqual(50.0, value);
        }

        [Test]
        public void JsonRoundTripKeepsEscapes() {
            string text = Json.Write(new System.Collections.Generic.Dictionary<string, object> {
                { "s", "a\"b\\c\n\u0001é" },
                { "b", true },
                { "l", 42L },
                { "d", 12.5 }
            });
            Assert.IsTrue(Json.TryParse(text, out object parsed, out string error), error);
            var fields = (System.Collections.Generic.Dictionary<string, object>)parsed;
            Assert.AreEqual("a\"b\\c\n\u0001é", fields["s"]);
            Assert.AreEqual(true, fields["b"]);
            Assert.AreEqual(42L, ((JsonNumber)fields["l"]).ToLongOrDouble());
            Assert.AreEqual(12.5, ((JsonNumber)fields["d"]).ToLongOrDouble());
        }
    }
}
