using System;
using System.Diagnostics;
using UnityEngine;

namespace Metica.Assignment {
    /// <summary>
    /// Persistent key-value storage. PlayerPrefs in production, a dictionary in tests. Main thread only.
    /// </summary>
    internal interface IKeyValueStore {
        /// <summary>Null when the key does not exist.</summary>
        string GetString(string key);
        void SetString(string key, string value);
        void DeleteKey(string key);
        void Save();
    }

    internal sealed class PlayerPrefsStore : IKeyValueStore {
        public string GetString(string key) {
            if (!PlayerPrefs.HasKey(key)) {
                return null;
            }
            return PlayerPrefs.GetString(key);
        }

        public void SetString(string key, string value) {
            PlayerPrefs.SetString(key, value);
        }

        public void DeleteKey(string key) {
            PlayerPrefs.DeleteKey(key);
        }

        public void Save() {
            PlayerPrefs.Save();
        }
    }

    internal interface IClock {
        DateTime UtcNow { get; }

        /// <summary>Monotonic milliseconds; only differences are meaningful.</summary>
        long ElapsedMilliseconds { get; }
    }

    internal sealed class SystemClock : IClock {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public DateTime UtcNow => DateTime.UtcNow;

        public long ElapsedMilliseconds => _stopwatch.ElapsedMilliseconds;
    }

    internal static class AssignmentLog {
        private const string Prefix = "[MeticaAssignment] ";

        /// <summary>Replaced in tests to capture log lines.</summary>
        internal static Action<LogType, string> Handler = DefaultHandler;

        public static void Info(string message) {
            Handler(LogType.Log, Prefix + message);
        }

        public static void Warning(string message) {
            Handler(LogType.Warning, Prefix + message);
        }

        public static void Error(string message) {
            Handler(LogType.Error, Prefix + message);
        }

        private static void DefaultHandler(LogType type, string message) {
            if (type == LogType.Error) {
                UnityEngine.Debug.LogError(message);
            } else if (type == LogType.Warning) {
                UnityEngine.Debug.LogWarning(message);
            } else {
                UnityEngine.Debug.Log(message);
            }
        }
    }
}
