using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AB.Assignment {
    /// <summary>
    /// A validated metica_assignment config value.
    /// </summary>
    internal sealed class AssignmentConfig {
        public const string ExperimentIdField = "experiment_id";
        public const string MeticaPercentageField = "metica_percentage";
        public const string EnabledField = "enabled";

        // <game-applovin-id>-<YYYYMMDD>-<canonical percentage>. \z, not $, so a trailing newline is rejected.
        private static readonly Regex ExperimentIdPattern = new Regex(
            @"^[a-z0-9._]{1,64}-([0-9]{8})-(100|[1-9]?[0-9](?:\.[0-9]?[1-9])?)\z",
            RegexOptions.CultureInvariant);

        public string ExperimentId { get; }
        public int MeticaPercentageBasisPoints { get; }
        public bool Enabled { get; }

        private AssignmentConfig(string experimentId, int meticaPercentageBasisPoints, bool enabled) {
            ExperimentId = experimentId;
            MeticaPercentageBasisPoints = meticaPercentageBasisPoints;
            Enabled = enabled;
        }

        /// <summary>
        /// Parses and validates a config JSON value. Unknown extra fields are ignored.
        /// </summary>
        public static bool TryParse(string json, out AssignmentConfig config, out string error) {
            config = null;
            if (string.IsNullOrEmpty(json)) {
                error = "config value is empty";
                return false;
            }
            if (!Json.TryParse(json, out object root, out string jsonError)) {
                error = "config value is not valid JSON: " + jsonError;
                return false;
            }
            if (!(root is Dictionary<string, object> fields)) {
                error = "config value is not a JSON object";
                return false;
            }

            if (!fields.TryGetValue(ExperimentIdField, out object idValue)) {
                error = ExperimentIdField + " is missing";
                return false;
            }
            if (!(idValue is string experimentId)) {
                error = ExperimentIdField + " is not a string";
                return false;
            }

            if (!fields.TryGetValue(MeticaPercentageField, out object percentageValue)) {
                error = MeticaPercentageField + " is missing";
                return false;
            }
            if (!(percentageValue is JsonNumber percentageNumber)) {
                error = MeticaPercentageField + " is not a number";
                return false;
            }
            if (!TryPercentageToBasisPoints(percentageNumber, out int percentageBasisPoints, out error)) {
                return false;
            }

            if (!fields.TryGetValue(EnabledField, out object enabledValue)) {
                error = EnabledField + " is missing";
                return false;
            }
            if (!(enabledValue is bool enabled)) {
                error = EnabledField + " is not a bool";
                return false;
            }

            if (!TryParseExperimentId(experimentId, out int idPercentageBasisPoints, out error)) {
                return false;
            }
            if (idPercentageBasisPoints != percentageBasisPoints) {
                error = "the percentage segment of " + ExperimentIdField + " \"" + experimentId +
                        "\" does not equal " + MeticaPercentageField + " " + percentageNumber.Raw;
                return false;
            }

            config = new AssignmentConfig(experimentId, percentageBasisPoints, enabled);
            error = null;
            return true;
        }

        /// <summary>
        /// Checks the experiment id rules and returns the percentage segment in basis points.
        /// </summary>
        public static bool TryParseExperimentId(string experimentId, out int percentageBasisPoints, out string error) {
            percentageBasisPoints = 0;
            Match match = ExperimentIdPattern.Match(experimentId ?? "");
            if (!match.Success) {
                error = ExperimentIdField + " \"" + experimentId +
                        "\" does not match <game-applovin-id>-<YYYYMMDD>-<metica_percentage>";
                return false;
            }
            string datestamp = match.Groups[1].Value;
            if (!DateTime.TryParseExact(datestamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) {
                error = ExperimentIdField + " datestamp \"" + datestamp + "\" is not a real calendar date";
                return false;
            }
            // The pattern only lets through 0..100 with at most two decimals, so this is exact.
            decimal percentage = decimal.Parse(match.Groups[2].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            percentageBasisPoints = (int)(percentage * 100m);
            error = null;
            return true;
        }

        private static bool TryPercentageToBasisPoints(JsonNumber number, out int basisPoints, out string error) {
            basisPoints = 0;
            if (!number.TryGetDecimal(out decimal percentage)) {
                error = MeticaPercentageField + " " + number.Raw + " is out of range";
                return false;
            }
            if (percentage < 0m || percentage > 100m) {
                error = MeticaPercentageField + " " + number.Raw + " is not between 0 and 100";
                return false;
            }
            decimal scaled = percentage * 100m;
            if (scaled != decimal.Truncate(scaled)) {
                error = MeticaPercentageField + " " + number.Raw + " has more than two decimals";
                return false;
            }
            basisPoints = (int)scaled;
            error = null;
            return true;
        }
    }
}
