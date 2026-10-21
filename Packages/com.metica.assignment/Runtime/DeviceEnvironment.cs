using System;
using System.Collections.Generic;
using UnityEngine;

namespace Metica.Assignment {
    /// <summary>
    /// The device and app fields the Metica SDK adds to every event, read with Unity and (on Android) JNI calls so the
    /// values match the SDK's. Fields the SDK only fills from its own state (sessionCount, lastSessionLength, gaid,
    /// idfa, idfv, isHoldout, meticaNativeSdk, meticaUnitySdk) are left out; the columns accept null. Main thread.
    /// </summary>
    internal static class DeviceEnvironment {
        private const string Unknown = "unknown";

        /// <summary>"platform" is missing on platforms other than Android and iOS.</summary>
        public static Dictionary<string, object> Collect() {
            var fields = new Dictionary<string, object> { { "appVersion", Application.version } };
#if UNITY_ANDROID && !UNITY_EDITOR
            CollectAndroid(fields);
#elif UNITY_IOS && !UNITY_EDITOR
            CollectIos(fields);
#endif
            return fields;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        // Same sources as the Android SDK's DeviceInfoProvider; a failed read gets the SDK's default.
        private static void CollectAndroid(Dictionary<string, object> fields) {
            fields["platform"] = "android";
            fields["osVersion"] = Read("osVersion", Unknown, () => {
                using (var version = new AndroidJavaClass("android.os.Build$VERSION")) {
                    return version.GetStatic<string>("RELEASE");
                }
            });
            // Build.ID, the OS build, as the Android SDK sends it.
            fields["buildNumber"] = Read("buildNumber", Unknown, () => BuildField("ID"));
            fields["deviceModel"] = Read("deviceModel", Unknown, () => BuildField("MODEL"));
            fields["deviceManufacturer"] = Read("deviceManufacturer", Unknown, () => BuildField("MANUFACTURER"));
            fields["deviceType"] = Read("deviceType", "phone", () => {
                using (AndroidJavaObject activity = CurrentActivity())
                using (AndroidJavaObject resources = activity.Call<AndroidJavaObject>("getResources"))
                using (AndroidJavaObject configuration = resources.Call<AndroidJavaObject>("getConfiguration")) {
                    // SCREENLAYOUT_SIZE_MASK = 0x0F, SCREENLAYOUT_SIZE_LARGE = 3.
                    if ((configuration.Get<int>("screenLayout") & 0x0F) >= 3) {
                        return "tablet";
                    }
                    return "phone";
                }
            });
            fields["deviceMemory"] = Read("deviceMemory", 0L, () => {
                using (AndroidJavaObject activity = CurrentActivity())
                using (AndroidJavaObject manager = activity.Call<AndroidJavaObject>("getSystemService", "activity"))
                using (var memoryInfo = new AndroidJavaObject("android.app.ActivityManager$MemoryInfo")) {
                    manager.Call("getMemoryInfo", memoryInfo);
                    return memoryInfo.Get<long>("totalMem") / (1024L * 1024L);
                }
            });
            fields["locale"] = Read("locale", Unknown, () => {
                using (AndroidJavaObject locale = DefaultLocale()) {
                    return locale.Call<string>("toLanguageTag");
                }
            });
            fields["localTimezone"] = Read("localTimezone", Unknown, () => {
                using (var timeZoneClass = new AndroidJavaClass("java.util.TimeZone"))
                using (AndroidJavaObject timeZone = timeZoneClass.CallStatic<AndroidJavaObject>("getDefault")) {
                    return timeZone.Call<string>("getID");
                }
            });
            // SIM country, else the locale's country, lower case.
            string country = Read<string>("country", null, () => {
                string iso = null;
                using (AndroidJavaObject activity = CurrentActivity())
                using (AndroidJavaObject telephony = activity.Call<AndroidJavaObject>("getSystemService", "phone")) {
                    if (telephony != null) {
                        iso = telephony.Call<string>("getSimCountryIso");
                    }
                }
                if (string.IsNullOrWhiteSpace(iso)) {
                    using (AndroidJavaObject locale = DefaultLocale()) {
                        iso = locale.Call<string>("getCountry");
                    }
                }
                return iso.ToLowerInvariant();
            });
            if (country != null) {
                fields["country"] = country;
            }
        }

        private static AndroidJavaObject CurrentActivity() {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer")) {
                return player.GetStatic<AndroidJavaObject>("currentActivity");
            }
        }

        private static string BuildField(string name) {
            using (var build = new AndroidJavaClass("android.os.Build")) {
                return build.GetStatic<string>(name);
            }
        }

        private static AndroidJavaObject DefaultLocale() {
            using (var localeClass = new AndroidJavaClass("java.util.Locale")) {
                return localeClass.CallStatic<AndroidJavaObject>("getDefault");
            }
        }
#endif

#if UNITY_IOS && !UNITY_EDITOR
        // Approximations of the iOS SDK's values. Left out: buildNumber (CFBundleVersion has no Unity runtime API), and
        // locale, localTimezone and country, because .NET under IL2CPP may return the invariant culture, "us" or
        // "Local" instead of the device setting; an empty column is better than a wrong one.
        private static void CollectIos(Dictionary<string, object> fields) {
            fields["platform"] = "ios";
            fields["osVersion"] = Read("osVersion", Unknown, () => UnityEngine.iOS.Device.systemVersion);
            string model = SystemInfo.deviceModel;
            fields["deviceModel"] = model;
            string deviceType = Unknown;
            if (model.StartsWith("iPad", StringComparison.Ordinal)) {
                deviceType = "tablet";
            } else if (model.StartsWith("iPhone", StringComparison.Ordinal) ||
                       model.StartsWith("iPod", StringComparison.Ordinal)) {
                deviceType = "phone";
            }
            fields["deviceType"] = deviceType;
            fields["deviceMemory"] = (long)SystemInfo.systemMemorySize;
        }
#endif

        private static T Read<T>(string field, T fallback, Func<T> read) {
            try {
                T value = read();
                if (value == null) {
                    return fallback;
                }
                return value;
            } catch (Exception e) {
                AssignmentLog.Warning("Could not read " + field + "; sending " + fallback + ": " + e.Message);
                return fallback;
            }
        }
    }
}
