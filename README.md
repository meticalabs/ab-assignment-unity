# AB Assignment for Unity

Splits your players into two groups and keeps the split fixed on each device:

- **Metica**: ads are shown through the Metica SDK.
- **Baseline**: ads are shown through your own ad code (for example AppLovin MAX).

You set the split in one Firebase Remote Config parameter. The package decides the group on the device, tags every ad placement with it, and sends small events to Metica so revenue can be compared by group. If your game already splits players itself, it can report its own groups instead ([client-managed mode](#7-client-managed-assignment-optional)).

The package has no dependency on Firebase, AppLovin MAX or the Metica SDK. Your game keeps calling those itself; the package only tells it which route to take.

AB Assignment is built and maintained by Metica. Until 2.0.0 it was called Metica Assignment (`com.metica.assignment`); see the [CHANGELOG](CHANGELOG.md) to migrate.

## Contents

1. [Install](#1-install)
2. [Set up the Firebase parameter](#2-set-up-the-firebase-parameter)
3. [Add the code](#3-add-the-code)
4. [Check that it works](#4-check-that-it-works)
5. [Kill switch and later changes](#5-kill-switch-and-later-changes)
6. [FAQ and troubleshooting](#6-faq-and-troubleshooting)
7. [Client-managed assignment (optional)](#7-client-managed-assignment-optional)
8. [What is sent](#what-is-sent)
9. [Repository layout](#repository-layout)
10. [License](#license)

Requirements: Unity 2021.3 or later, Android or iOS. Your game needs Firebase Remote Config, the Metica SDK (for the Metica group) and its own ad code (for the Baseline group).

---

## 1. Install

Add this line to the `dependencies` in your project's `Packages/manifest.json`:

```json
"com.abassignment.unity": "https://github.com/meticalabs/ab-assignment-unity.git?path=/Packages/com.abassignment.unity#v2.1.0"
```

Or in Unity: **Window › Package Manager › + › Add package from git URL…** and paste the URL (the part in quotes after the colon).

`#v2.1.0` pins the version. To update later, change the tag.

Notes:

- **Newtonsoft JSON.** The package depends on Unity's `com.unity.nuget.newtonsoft-json` (3.2.1 or later). Unity installs it for you. If your project has its own copy of `Newtonsoft.Json.dll` (for example from the Asset Store or inside another plugin folder), remove that copy, or Unity reports a duplicate assembly.
- **Assembly definitions.** If your game code is in an assembly definition (`.asmdef`), add a reference to `AB.Assignment`. Code in `Assembly-CSharp` needs nothing.

---

## 2. Set up the Firebase parameter

Create one Remote Config parameter called `metica_assignment` with a JSON value like this:

```json
{"experiment_id":"mystudio_mygame-20261021-50","metica_percentage":50,"enabled":true}
```

| Field | What to put |
|---|---|
| `experiment_id` | `<studio>_<game>-<YYYYMMDD>-<percentage>`. Lower case `a-z`, `0-9`, `_` and `.` only, no `-` inside the names. The date is the day you publish; it is only a label. |
| `metica_percentage` | Share of players in the Metica group, 0 to 100, at most two decimals. It must equal the last part of `experiment_id` (`...-50` goes with `50`). |
| `enabled` | `true`. Set it to `false` to stop Metica ads (see [kill switch](#5-kill-switch-and-later-changes)). |

A value that breaks a rule is ignored by every device, which keeps its last valid value. The tool below checks the value before it publishes.

### Option A: command-line tool

The tool is in [`tools/`](tools/) and needs Node 18.3 or later. Your Google account needs the **Firebase Remote Config Admin** role on the project.

```bash
git clone https://github.com/meticalabs/ab-assignment-unity.git
cd ab-assignment-unity/tools
npm ci
gcloud auth application-default login
node ab_assignment.mjs set --project <firebase-project-id> --studio mystudio --game mygame --percentage 50
```

It builds the value, checks it, shows what will change and asks before it publishes. Other commands: `get` (show and check the published value), `disable` (kill switch) and `enable` (turn it back on under the same experiment id). Credentials, service accounts and all options: [`tools/README.md`](tools/README.md).

### Option B: Firebase console

1. Open **Remote Config** and click **Add parameter**.
2. Fill in:

   | Setting | Value |
   |---|---|
   | Parameter name | `metica_assignment` |
   | Data type | JSON |
   | Default value | the JSON value above |
   | Use in-app default | unchecked |
   | Conditions | none |

3. Click **Save**, then **Publish changes**.

### Rules

- Do **not** add `metica_assignment` to your Firebase in-app defaults (`SetDefaultsAsync`). The package must only see values that came from the server.
- Stop any Firebase A/B test that decides ads or Metica on/off. This parameter must be the only thing that decides it.
- The JSON Schema of the value is in [`Packages/com.abassignment.unity/Tools~/firebase/metica_assignment.schema.json`](Packages/com.abassignment.unity/Tools~/firebase/metica_assignment.schema.json).

---

## 3. Add the code

There are five places to change. A complete, copy-ready version is in the package sample: **Package Manager › AB Assignment › Samples › Basic integration › Import**, or [`Samples~/BasicIntegration`](Packages/com.abassignment.unity/Samples~/BasicIntegration).

| When | Call | Section |
|---|---|---|
| App start | `Initialize` with your Metica app id and API key | [3.1](#31-app-start) |
| Firebase fetch finishes | `ReportRemoteConfig` | [3.2](#32-firebase-fetch-finishes) |
| Ads start | `Current.RunMetica` to choose the route, and `ReportAdsInit` | [3.3](#33-ads-start) |
| Every ad show, banner, MREC, revenue event | `TagPlacement` | [3.4](#34-tag-placements) |
| Player answers your GDPR prompt | `SetConsentStatus` | [3.5](#35-consent) |
| Optional: your own player id is known | `ReportCustomId` | [3.6](#36-link-your-own-player-id-optional) |

**One rule to remember:** `ABAssignment.Current` and `TagPlacement` decide the player's group the first time you call them. Do not call them at app start: Firebase has not answered yet, so the player would be unassigned for the whole session. `ABAssignment.UserId` is safe to read at any time.

In the snippets, `MeticaAppId`, `MeticaApiKey`, `MaxSdkKey` and the ad unit ids are your own values.

### 3.1 App start

```csharp
using AB.Assignment;
using UnityEngine;

public static class GameStartup {
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void OnBeforeSceneLoad() {
        ABAssignment.Initialize(new ABAssignmentOptions(MeticaAppId, MeticaApiKey));
    }
}
```

That is the only setup call. From then on the package sends its events to Metica by itself, for every group, without the Metica SDK. Events logged before `Initialize` wait in a queue on the device.

**Custom domain (dedicated clusters).** If your Metica setup runs on a dedicated cluster under your own domain, set the same domain you give the Metica SDK: `new ABAssignmentOptions(MeticaAppId, MeticaApiKey) { CustomDomain = "metica.example.com" }`. Leave it unset otherwise. An invalid value makes `Initialize` throw an `ArgumentException`. Accepted values are in the [package README](Packages/com.abassignment.unity/README.md#custom-domain-dedicated-clusters).

`ABAssignment.UserId` is a random id the package creates once per install. Use it as the user id in your other SDKs (Metica, MAX), so all data can be joined on the same id.

**Optional: copies for your own analytics.** Metica always gets the events. If you also want them in your own analytics:

```csharp
ABAssignment.EventLogged += (eventName, payload) => {
    // payload is read-only; copy it if your analytics needs a Dictionary.
    YourAnalytics.LogEvent(eventName, new Dictionary<string, object>(payload));
};
```

A listener cannot block or change what Metica receives. If it throws, the package logs the error and carries on.

### 3.2 Firebase fetch finishes

Tell the package how the Remote Config fetch ended. If your game already fetches Remote Config, add these three calls to that code; do not fetch a second time.

```csharp
using Firebase.Extensions;
using Firebase.RemoteConfig;
using AB.Assignment;

FirebaseRemoteConfig rc = FirebaseRemoteConfig.DefaultInstance;
rc.FetchAsync(System.TimeSpan.Zero).ContinueWithOnMainThread(fetchTask => {
    if (fetchTask.IsFaulted || fetchTask.IsCanceled || rc.Info.LastFetchStatus != LastFetchStatus.Success) {
        ABAssignment.ReportRemoteConfig(RemoteConfigStatus.FetchFailed, null, "fetch failed");
        return;
    }
    rc.ActivateAsync().ContinueWithOnMainThread(activateTask => {
        if (activateTask.IsFaulted || activateTask.IsCanceled) {
            ABAssignment.ReportRemoteConfig(RemoteConfigStatus.ActivationFailed, null, "activate failed");
            return;
        }
        // Pass the value only if it came from the server.
        ConfigValue value = rc.GetValue("metica_assignment");
        string remoteValue = null;
        if (value.Source == ValueSource.RemoteValue) {
            remoteValue = value.StringValue;
        }
        ABAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, remoteValue, null);

        // Your other Remote Config handlers go after this line.
    });
});
```

- Start the fetch as early as possible. The sooner it finishes, the more first sessions get a group.
- `TimeSpan.Zero` fetches a fresh value on every cold start. Firebase throttles an app that fetches more often than about 5 times an hour; a throttled fetch is reported as `FetchFailed` and the device keeps its saved value. If your game already uses a fetch interval, keep it.
- Report to the package before your other handlers, so an exception in them cannot stop it.
- Call it on the main thread (`ContinueWithOnMainThread` does this).

### 3.3 Ads start

This is where the group is decided. Read `ABAssignment.Current` once, then start **either** the Metica SDK **or** your own ad code, and report the start and the result with `ReportAdsInit`.

```csharp
using System.Threading.Tasks;
using Metica;
using Metica.Ads;
using AB.Assignment;

// Call on the main thread when the game is ready to start ads (after your consent flow), not at app start.
public static async Task InitializeAdsAsync() {
    AssignmentResult assignment = ABAssignment.Current;
    if (assignment.RunMetica) {
        await StartMetica(assignment);
    } else {
        // Baseline, unassigned (no Firebase value yet) and kill switch off.
        await StartMax(assignment);
    }
}
```

**Metica arm.** The Metica SDK sets the MAX user id and starts MAX itself, so do not call `MaxSdk.SetUserId` or `MaxSdk.InitializeSdk` on this route.

```csharp
private static async Task StartMetica(AssignmentResult assignment) {
    var config = new MeticaInitConfig(MeticaApiKey, MeticaAppId, assignment.UserId);
    var mediation = new MeticaMediationInfo(MeticaMediationInfo.MeticaMediationType.MAX, MaxSdkKey);

    ABAssignment.ReportAdsInit(AdsInitStage.Started);
    MeticaInitResponse response;
    try {
        response = await MeticaSdk.InitializeAsync(config, mediation);
    } catch (System.Exception e) {
        ABAssignment.ReportAdsInit(AdsInitStage.Failed, e.Message);
        return;
    }
    // Metica's own group (TRIAL/HOLDOUT) and whether init fell back to a forced holdout.
    ABAssignment.ReportAdsInit(AdsInitStage.Succeeded, null,
        response.SmartFloors?.UserGroup.ToString(), response.SmartFloors?.IsForcedHoldout);
}
```

`MeticaSdk.InitializeAsync` does not throw when init fails. It returns a forced holdout instead, which is reported as `Succeeded` with `forced_holdout = true`, so pass those two values. The `catch` is only for unexpected exceptions.

**Baseline arm (your MAX code).** Keep your existing MAX init; add the user id and the two `ReportAdsInit` calls.

```csharp
private static async Task StartMax(AssignmentResult assignment) {
    // MAX may call back off the main thread: complete a task and report after the await.
    var maxInit = new TaskCompletionSource<bool>();
    MaxSdkCallbacks.OnSdkInitializedEvent += configuration => {
        maxInit.TrySetResult(configuration.IsSuccessfullyInitialized);
    };
    MaxSdk.SetUserId(assignment.UserId);   // before InitializeSdk

    ABAssignment.ReportAdsInit(AdsInitStage.Started);
    MaxSdk.InitializeSdk();
    if (await maxInit.Task) {
        ABAssignment.ReportAdsInit(AdsInitStage.Succeeded);
    } else {
        ABAssignment.ReportAdsInit(AdsInitStage.Failed, "MAX failed to initialize");
    }
}
```

Rules for `ReportAdsInit`: `Started` just before you start the ad SDK, then exactly one of `Succeeded` or `Failed`. Main thread only. If no outcome is reported within 30 seconds, the package sends `pending_timeout` itself. If you report `SkippedLowMemory` and start the ad SDK later anyway, report `Started` plus one outcome then: that second attempt is sent and gets its own 30-second timeout. Only one such retry is sent per session.

### 3.4 Tag placements

`TagPlacement` puts the group in front of a placement name: `TagPlacement("LevelComplete")` returns `metica|LevelComplete`, `baseline|LevelComplete` or `unassigned|LevelComplete`. Use the tagged string everywhere a placement name goes to an ad SDK or a revenue event, so every report can be split by group. Read `Current` once on the main thread before your first ad call: if `TagPlacement` runs on another thread before the group is decided it cannot decide it there, logs an error and returns an `unassigned|` tag, and that tag stays on the placement for the whole session even after the group is decided.

Interstitial and rewarded: tag in your shared show function, after ads have started.

```csharp
string tagged = ABAssignment.TagPlacement(placement);
if (ABAssignment.Current.RunMetica) {
    MeticaSdk.Ads.ShowInterstitial(InterstitialAdUnitId, tagged);   // rewarded: MeticaSdk.Ads.ShowRewarded
} else {
    MaxSdk.ShowInterstitial(InterstitialAdUnitId, tagged);          // rewarded: MaxSdk.ShowRewardedAd
}
```

Banner and MREC: set the placement right after you create the ad view (on the Metica route, again after every re-create).

```csharp
MaxSdk.SetBannerPlacement(BannerAdUnitId, ABAssignment.TagPlacement("banner"));            // MAX route
MaxSdk.SetMRecPlacement(MrecAdUnitId, ABAssignment.TagPlacement("mrec"));                  // MAX route
MeticaSdk.Ads.SetBannerPlacement(BannerAdUnitId, ABAssignment.TagPlacement("banner"));     // Metica route
MeticaSdk.Ads.SetMrecPlacement(MrecAdUnitId, ABAssignment.TagPlacement("mrec"));           // Metica route
```

Revenue events you send yourself (Firebase, Adjust, Metica analytics): use the tagged placement there too. In a MAX revenue callback, `adInfo.Placement` is the tagged string you passed when showing the ad.

### 3.5 Consent

Only if your game has its own GDPR prompt. Call this when the player answers, and again if they change it in settings:

```csharp
ABAssignment.SetConsentStatus(ConsentStatus.Granted);   // or ConsentStatus.Denied
```

The package saves the answer on the device and sends it with every event as `consent_status` (`unknown` until you set it). It does not stop or delay anything; consent is applied on the Metica side. This is not the TCF/UMP ad consent string; keep passing that to your ad SDKs as you do now. If your game has no GDPR prompt, skip this step.

### 3.6 Link your own player id (optional)

If your game has its own player id (a login id, a backend id), call this when the id is known, at any time:

```csharp
ABAssignment.ReportCustomId(yourPlayerId, "login");   // the second argument is an optional label
```

It sends one `upliftCustomIdMapping` event that pairs your id with the package's `ABAssignment.UserId`. It works before or after `Initialize` (the event waits in the queue), before or after the group is decided, and after the Metica SDK has started. It never decides or changes the group, and it never replaces `ABAssignment.UserId`: that id stays the user id of every event.

- The id must not be null or blank and must be at most 200 characters. The label is optional; if you pass one, the same rules apply. An invalid call logs an error and sends nothing.
- The id is sent as you pass it. Do not pass personal data such as an email address or phone number; use an opaque or hashed id.
- Calling it again with the same id and label in the same session sends nothing. A different id or label sends another event, and each new session sends it again, so calling it on every launch is fine.

`AddCustomUserId(source, id)` from earlier versions still works and is unchanged. It is different: it saves the pair on the device and adds it to the `custom_user_ids` field of `upliftAssignment`, so nothing is sent until the group is decided. Use `ReportCustomId` for new code.

---

## 4. Check that it works

Run a **development build on a device or emulator**. The Unity Editor and desktop platforms log the events but do not send them. Every package log line starts with `[ABAssignment]`; on Android: `adb logcat -s Unity | grep ABAssignment`.

| Check | What you should see |
|---|---|
| `Initialize` was called | `Events go to Metica ingestion at https://api-global-gateway.prod-eu.metica.com for app <your app id>.` (your own domain if you set `CustomDomain`) |
| Firebase value arrived in time | `Remote config "<experiment_id>" received before the group was needed; it applies to this session.` |
| Group decided, fresh install online | `Group metica, route metica, ...` or `Group baseline, route own, ...` with your experiment id and `source "remote_this_session"` |
| Second launch | The same group; `source "cached_remote"`, or `"remote_this_session"` if Firebase answered before ads started |
| Firebase not answered yet when ads start | `Group unassigned, route own, ..., source "unassigned_waiting"`. The game uses its own ad code this session; the next session gets a group. |
| Events reach Metica | `Sent <n> event(s) to Metica (HTTP 202, route ga).` on both routes (`ga` is the main host, `relay` the fallback; `primary` with a custom domain) |
| Wrong app id or API key | `Metica ingestion rejected <n> event(s) with HTTP 401 ...` (or 403). Fix the values passed to `Initialize`. |
| `Initialize` missing | `ABAssignment.Initialize has not been called yet; events are queued until it is.` |
| Ads shown | The placement in your ad logs starts with `metica|` or `baseline|` |
| Client-managed group reported ([section 7](#7-client-managed-assignment-optional)) | `Client-managed group metica, experiment "", provider user <your id or "not known yet">, user <ABAssignment.UserId>` |

To test a fresh install on Android: `adb shell pm clear <your.package.name>`. This also creates a new user id, so the device may land in the other group.

To force a route while testing (development builds and the Editor only), call `ABAssignment.SetDebugOverride(Group.Metica)` or `Group.Baseline`. It applies from the next launch and changes the route only, not the group label. The kill switch wins over it: with `"enabled": false` the route stays off on that device even with the override set (a warning says the override was ignored), and the override applies again as soon as publishing is enabled. It has no effect while the device is unassigned (no Firebase value saved yet). `SetDebugOverride(null)` clears it.

### Run the package tests (optional)

The package ships EditMode tests. To run them in your project, add the package to `testables` in `Packages/manifest.json`:

```json
{
  "dependencies": { "...": "..." },
  "testables": ["com.abassignment.unity"]
}
```

Then **Window › General › Test Runner › EditMode** and run `AB.Assignment.Tests.Editor`. The tests use an in-memory store and a fake clock; they do not touch PlayerPrefs or the network.

---

## 5. Kill switch and later changes

| You want to | Do this |
|---|---|
| Stop Metica ads for everyone | Publish the same value with `"enabled": false`, or `node ab_assignment.mjs disable --project <id>` |
| Turn Metica ads back on | Publish the same value with `"enabled": true`, or `node ab_assignment.mjs enable --project <id>` |
| Change the percentage | Publish a new `experiment_id` with today's date and the new percentage (with the tool: `set ... --new-experiment`). All players are split again. |

- With `enabled: false` every device runs your own ad code. Groups and labels stay the same, so the data stays comparable.
- A new value applies on the device's next launch (or in the current session, if it arrives before ads start). Expect a few days until all active players have it.
- Do not delete the parameter to stop the test. Devices keep their saved value and carry on. Use `"enabled": false`.

---

## 6. FAQ and troubleshooting

**Do I need the Metica SDK for the events?**
No. The package sends its own events with `UnityWebRequest`. You need the Metica SDK only to show ads on the Metica route.

**Why is my first session unassigned?**
The group is decided the first time you read `Current` (or call `TagPlacement` / `ReportAdsInit`). If Firebase has not answered by then, the session is unassigned and runs your own ad code. Start the Remote Config fetch earlier, or start ads later. The value is saved, so the next session gets a group.

**Can a player change groups?**
Not during a session. Between sessions the group only changes when you publish a new `experiment_id`, or after a reinstall (the user id is stored in PlayerPrefs and lost on reinstall).

**Does `EventLogged` replace delivery to Metica?**
No. The event is already queued for Metica before listeners run.

**What happens offline?**
Events stay in a queue on the device while it is offline or Metica cannot be reached, and are sent later with retries. The queue holds at most 100 events; beyond that the oldest are dropped. A 4xx answer other than 408 or 429 (for example a wrong API key) drops the batch with an error log.

**Can I call the API from a background thread?**
`Initialize`, `ReportRemoteConfig`, `ReportAdsInit`, `SetConsentStatus`, `AddCustomUserId`, `ReportCustomId`, `SetAssignmentMode`, `ReportClientAssignment` and the first read of `Current` must be on the main thread; other threads get an error log and the call is ignored. Once the group is decided, `Current` and `TagPlacement` are safe from any thread, including ad revenue callbacks.

**Can I link my own player id?**
Yes: `ABAssignment.ReportCustomId(yourPlayerId, "login")` (see [3.6](#36-link-your-own-player-id-optional)). It never changes the group.

**Unity says `Newtonsoft.Json` is defined twice.**
Remove the extra `Newtonsoft.Json.dll` from your `Assets` folder and keep `com.unity.nuget.newtonsoft-json`.

**Unity cannot resolve the Git URL.**
Check that Git is installed and that your machine (and build server) can read this repository. The URL must include `?path=/Packages/com.abassignment.unity`.

**Can I check a value without publishing it?**
`node ab_assignment.mjs set ... --dry-run`, or in code `ABAssignment.IsValidConfig(json, out string error)`.

The full technical reference (events and fields, delivery and retries, persistence keys, bucketing test vectors) is in the [package README](Packages/com.abassignment.unity/README.md).

---

## 7. Client-managed assignment (optional)

Use this only if your game already decides itself which players get Metica and you want to keep that split. The package then does not use the Firebase parameter: it records the group your game reports, together with the user id your ad stack actually used, and sends the same `upliftAssignment` and `upliftAdsInit` events.

**Turn it on at app start, on every launch** (it is not saved), before anything reads `Current` or `TagPlacement` or calls `ReportAdsInit`:

```csharp
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
private static void OnBeforeSceneLoad() {
    ABAssignment.SetAssignmentMode(AssignmentMode.ClientManaged);
    ABAssignment.Initialize(new ABAssignmentOptions(MeticaAppId, MeticaApiKey));
}
```

Skip [section 2](#2-set-up-the-firebase-parameter) and [3.2](#32-firebase-fetch-finishes): in this mode `ReportRemoteConfig` is ignored with a warning.

**Report the group, then the user id the ad stack used.** `ReportClientAssignment(group, providerUserId)` takes `Group.Metica` or `Group.Baseline` and the user id that your ad SDK uses for this player. Report the group before you start the ad SDK, so every session is recorded even if the init never finishes. If you do not know the id yet, pass `null` and call again with the same group and the id once you have it. The package does not read any device or advertising id itself, so pass the id you actually gave to (or got from) the ad SDK:

- Metica route: the id in `MeticaInitResponse.UserId`. If you do not pass a user id to `MeticaInitConfig`, the Metica SDK generates one, and you only get it in the init response. If you pass your own user id, report that one before init. In the Unity Editor the response has no generated id.
- Your own MAX route: the id you passed to `MaxSdk.SetUserId`. The MAX Unity plugin has no getter for it, so keep your own copy.

These ids are not interchangeable with each other or with `ABAssignment.UserId`, and an IDFV is not the Metica SDK id. The package keeps them apart: the event's user id is always `ABAssignment.UserId`, and your id goes into `provider_user_id`.

```csharp
// useMetica comes from your own split.
public static async Task InitializeAdsAsync(bool useMetica) {
    if (useMetica) {
        await StartMetica();
    } else {
        await StartMax();
    }
}

private static async Task StartMetica() {
    var config = new MeticaInitConfig(MeticaApiKey, MeticaAppId, null);   // no user id: the Metica SDK generates one
    var mediation = new MeticaMediationInfo(MeticaMediationInfo.MeticaMediationType.MAX, MaxSdkKey);

    // The generated id is known only after init, so report the group now and the id later.
    ABAssignment.ReportClientAssignment(Group.Metica, null);
    ABAssignment.ReportAdsInit(AdsInitStage.Started);
    MeticaInitResponse response;
    try {
        response = await MeticaSdk.InitializeAsync(config, mediation);
    } catch (System.Exception e) {
        ABAssignment.ReportAdsInit(AdsInitStage.Failed, e.Message);
        return;
    }
    if (!string.IsNullOrEmpty(response.UserId)) {
        ABAssignment.ReportClientAssignment(Group.Metica, response.UserId);
    }
    ABAssignment.ReportAdsInit(AdsInitStage.Succeeded, null,
        response.SmartFloors?.UserGroup.ToString(), response.SmartFloors?.IsForcedHoldout);
}

private static async Task StartMax() {
    var maxInit = new TaskCompletionSource<bool>();
    MaxSdkCallbacks.OnSdkInitializedEvent += configuration => {
        maxInit.TrySetResult(configuration.IsSuccessfullyInitialized);
    };
    MaxSdk.SetUserId(MyMaxUserId);
    ABAssignment.ReportClientAssignment(Group.Baseline, MyMaxUserId);

    ABAssignment.ReportAdsInit(AdsInitStage.Started);
    MaxSdk.InitializeSdk();
    if (await maxInit.Task) {
        ABAssignment.ReportAdsInit(AdsInitStage.Succeeded);
    } else {
        ABAssignment.ReportAdsInit(AdsInitStage.Failed, "MAX failed to initialize");
    }
}
```

After the report, `Current` returns your group, and `TagPlacement` uses it (`metica|...` or `baseline|...`). Tag placements after the report.

| Situation | What happens |
|---|---|
| Before the report | Nothing decides the group and no `upliftAssignment` is sent. `Current` is unassigned (`ConfigSource` `client_managed_pending`), `TagPlacement` gives `unassigned|...`, and there is no Firebase fetch timeout event. |
| `ReportAdsInit(Started)` before the report | Accepted (it records the start time). |
| `ReportAdsInit` outcome before the report | Logged as an error and ignored. Report the group first. |
| No outcome 30 s after `Started` | `pending_timeout` is sent once the group is reported. |
| First valid report | Fixes the group for this session and sends one `upliftAssignment`. Works before `Initialize` (queued). With a `null` provider id the event has no `provider_user_id`. |
| Same report again (for example a retry), or the same group with `null` | Nothing is sent. `null` never clears an id already reported. |
| Same group, provider id now known or changed | One more `upliftAssignment` with the new `provider_user_id`. The group does not change. |
| Different group or experiment in the same session | Logged as an error and ignored. The group never changes during a session. |
| `Group.Unassigned`, an undefined `Group` value, a blank id, or an id longer than 200 characters | Logged as an error and ignored. |
| `ReportClientAssignment` without `SetAssignmentMode(ClientManaged)` | Logged as an error and ignored. |
| `SetAssignmentMode` after the group was decided | Logged as an error and ignored (setting the mode already in effect is not an error). |
| New UTC day while the app runs | One more `upliftAssignment` with the reported group and the latest provider id. |
| Next launch | Starts in automatic mode with nothing reported. Call `SetAssignmentMode` and `ReportClientAssignment` again. |

`ReportClientAssignment` also takes an optional third argument, your own experiment label, sent as `experiment_id` (empty when not given). `SetDebugOverride` has no effect in this mode. `ReportCustomId` works the same in both modes.

---

## What is sent

The package sends four events, `upliftAssignment`, `upliftConfigFetch`, `upliftAdsInit` and `upliftCustomIdMapping` (only when your game calls `ReportCustomId`), and only to Metica ingestion (`https://api-global-gateway.prod-eu.metica.com`, fallback `https://relay.prod-eu.metica.com`, or your custom domain if you set one), with your Metica API key in the `X-API-KEY` header. Each event has exactly these fields:

| Field | Value |
|---|---|
| `eventType` | The event name |
| `eventId` | A random UUID per event, the same on every retry |
| `appId` | Your Metica app id |
| `eventTime` | When the event was logged, Unix epoch milliseconds |
| `userId` | The random id the package generates on first launch (`ABAssignment.UserId`). It is not derived from any device or advertising id. |
| `platform` | `android` or `ios` |
| `customPayload` | The event's own fields (experiment id, group, route, config source, fetch and ads-init outcome, session id, UTC date; full list in the [package README](Packages/com.abassignment.unity/README.md#events)) and `consent_status`. `custom_user_ids`, `custom_id` and `provider_user_id` hold only ids your game passes to `AddCustomUserId`, `ReportCustomId` and `ReportClientAssignment`. `assignment_user_id` repeats `ABAssignment.UserId` in `upliftCustomIdMapping` and in client-managed `upliftAssignment`. |

The request URL repeats `appId`, `userId` and `platform` as query parameters.

Nothing else is sent: no app or package version, no device model, manufacturer, OS version, memory, locale, time zone or country, and no advertising or vendor id (GAID, IDFA, IDFV). The package never reads those ids; an id your game passes in is sent as given. As with any HTTP request, Metica's servers see the device's IP address.

## Repository layout

```
Packages/com.abassignment.unity/  Unity package (install with the Git URL above)
  Runtime/                        package code
  Tests/Editor/                   EditMode tests
  Samples~/BasicIntegration/      sample scripts (import from the Package Manager)
  Tools~/firebase/                JSON Schema of the Remote Config value
tools/                            command-line tool to set the Firebase parameter
```

## License

Proprietary, source available for reference. See [LICENSE.md](LICENSE.md).
