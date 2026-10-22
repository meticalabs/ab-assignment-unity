using System;
using System.Threading.Tasks;
using Metica;
using Metica.Ads;
using AB.Assignment;

namespace ABAssignmentSample {
    /// <summary>
    /// Ads start: reads the group once, then starts either the Metica SDK or your own AppLovin MAX code.
    /// </summary>
    public static class ABAssignmentAds {
        // Your own values.
        private const string MaxSdkKey = "YOUR_MAX_SDK_KEY";
        private const string InterstitialAdUnitId = "YOUR_INTERSTITIAL_AD_UNIT_ID";
        private const string RewardedAdUnitId = "YOUR_REWARDED_AD_UNIT_ID";
        private const string BannerAdUnitId = "YOUR_BANNER_AD_UNIT_ID";

        public static bool AdsInitialized { get; private set; }

        /// <summary>
        /// Call when the game is ready to start ads (after your consent flow), not at app start: the first read of
        /// ABAssignment.Current decides the group for the session.
        /// </summary>
        public static async Task InitializeAsync() {
            AssignmentResult assignment = ABAssignment.Current;
            if (assignment.RunMetica) {
                await StartMetica(assignment);
            } else {
                // Baseline, unassigned (no Firebase value yet) and kill switch off: your own MAX code.
                await StartMax(assignment);
            }
        }

        // Metica arm. The Metica SDK sets the MAX user id and starts MAX itself.
        private static async Task StartMetica(AssignmentResult assignment) {
            var config = new MeticaInitConfig(ABAssignmentStartup.MeticaApiKey, ABAssignmentStartup.MeticaAppId,
                assignment.UserId);
            var mediation = new MeticaMediationInfo(MeticaMediationInfo.MeticaMediationType.MAX, MaxSdkKey);

            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            // MeticaSdk.InitializeAsync does not throw when init fails: it returns a forced holdout, which is
            // reported as Succeeded with forcedHoldout = true. The catch is for unexpected exceptions.
            MeticaInitResponse response;
            try {
                response = await MeticaSdk.InitializeAsync(config, mediation);
            } catch (Exception e) {
                ABAssignment.ReportAdsInit(AdsInitStage.Failed, e.Message);
                return;
            }
            string meticaGroup = null;
            bool? forcedHoldout = null;
            if (response.SmartFloors != null) {
                meticaGroup = response.SmartFloors.UserGroup.ToString();
                forcedHoldout = response.SmartFloors.IsForcedHoldout;
            }
            ABAssignment.ReportAdsInit(AdsInitStage.Succeeded, null, meticaGroup, forcedHoldout);
            AdsInitialized = true;
        }

        // Baseline arm: your existing MAX init, with the package's user id and two ReportAdsInit calls added.
        private static async Task StartMax(AssignmentResult assignment) {
            // MAX may call back off the main thread: complete a task and report after the await.
            var maxInit = new TaskCompletionSource<bool>();
            MaxSdkCallbacks.OnSdkInitializedEvent += configuration => {
                maxInit.TrySetResult(configuration.IsSuccessfullyInitialized);
            };
            MaxSdk.SetUserId(assignment.UserId);

            ABAssignment.ReportAdsInit(AdsInitStage.Started);
            MaxSdk.InitializeSdk();
            bool initialized = await maxInit.Task;
            if (initialized) {
                ABAssignment.ReportAdsInit(AdsInitStage.Succeeded);
                AdsInitialized = true;
            } else {
                ABAssignment.ReportAdsInit(AdsInitStage.Failed, "MAX failed to initialize");
            }
        }

        /// <summary>Shows an interstitial on the route of this session, with the group in the placement name.</summary>
        public static void ShowInterstitial(string placement) {
            if (!AdsInitialized) {
                return;
            }
            string tagged = ABAssignment.TagPlacement(placement);
            if (ABAssignment.Current.RunMetica) {
                MeticaSdk.Ads.ShowInterstitial(InterstitialAdUnitId, tagged);
            } else {
                MaxSdk.ShowInterstitial(InterstitialAdUnitId, tagged);
            }
        }

        /// <summary>Shows a rewarded ad on the route of this session, with the group in the placement name.</summary>
        public static void ShowRewarded(string placement) {
            if (!AdsInitialized) {
                return;
            }
            string tagged = ABAssignment.TagPlacement(placement);
            if (ABAssignment.Current.RunMetica) {
                MeticaSdk.Ads.ShowRewarded(RewardedAdUnitId, tagged);
            } else {
                MaxSdk.ShowRewardedAd(RewardedAdUnitId, tagged);
            }
        }

        /// <summary>Call right after you create the banner (and again after every re-create on the Metica route).</summary>
        public static void TagBanner() {
            string tagged = ABAssignment.TagPlacement("banner");
            if (ABAssignment.Current.RunMetica) {
                MeticaSdk.Ads.SetBannerPlacement(BannerAdUnitId, tagged);
            } else {
                MaxSdk.SetBannerPlacement(BannerAdUnitId, tagged);
            }
        }
    }
}
