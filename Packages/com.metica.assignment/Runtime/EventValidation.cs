using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Metica.Assignment {
    /// <summary>
    /// The checks the Metica SDK applies to a custom event before queueing it. Metica ingestion answers 202 before it
    /// validates, so an event that breaks these rules would be accepted and then lost; the library checks them itself.
    /// </summary>
    internal static class EventValidation {
        public const int MaxPayloadKeys = 100;
        public const int MaxStringLength = 200;
        public const string TruncationSuffix = "...[truncated]";

        // Same as the SDK: camelCase letters only, no digits or underscores.
        private static readonly Regex EventTypePattern = new Regex(@"^[a-z]+(?:[A-Z][a-z]*)*\z",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// True if the event name and payload are valid. <paramref name="reservedKeys"/> counts keys added at send
        /// time (consent_status) towards the key limit. Values must be null, string, bool, int, long or a finite double; nested
        /// objects and arrays are not allowed.
        /// </summary>
        public static bool TryValidate(string eventType, Dictionary<string, object> payload, int reservedKeys,
            out string error) {
            error = null;
            if (eventType == null || !EventTypePattern.IsMatch(eventType)) {
                error = "event name \"" + eventType + "\" is not camelCase letters only";
                return false;
            }
            if (payload == null) {
                error = "payload is null";
                return false;
            }
            if (payload.Count + reservedKeys > MaxPayloadKeys) {
                error = "payload has more than " + MaxPayloadKeys + " keys";
                return false;
            }
            foreach (KeyValuePair<string, object> pair in payload) {
                if (string.IsNullOrEmpty(pair.Key)) {
                    error = "payload has an empty key";
                    return false;
                }
                if (!IsAllowedValue(pair.Value)) {
                    error = "payload value " + pair.Key + " has unsupported type " + pair.Value.GetType().Name;
                    return false;
                }
            }
            return true;
        }

        /// <summary>The SDK's cap: at most 200 characters, the suffix included.</summary>
        public static string Cap(string value) {
            if (value == null || value.Length <= MaxStringLength) {
                return value;
            }
            return value.Substring(0, MaxStringLength - TruncationSuffix.Length) + TruncationSuffix;
        }

        /// <summary>Caps every string value in place. Returns true if any value was cut.</summary>
        public static bool CapStrings(Dictionary<string, object> values) {
            List<string> longKeys = null;
            foreach (KeyValuePair<string, object> pair in values) {
                if (pair.Value is string text && text.Length > MaxStringLength) {
                    if (longKeys == null) {
                        longKeys = new List<string>();
                    }
                    longKeys.Add(pair.Key);
                }
            }
            if (longKeys == null) {
                return false;
            }
            foreach (string key in longKeys) {
                values[key] = Cap((string)values[key]);
            }
            return true;
        }

        private static bool IsAllowedValue(object value) {
            switch (value) {
                case null:
                case string _:
                case bool _:
                case int _:
                case long _:
                    return true;
                case double d:
                    return !double.IsNaN(d) && !double.IsInfinity(d);
                default:
                    return false;
            }
        }
    }
}
