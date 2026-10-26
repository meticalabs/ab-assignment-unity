# AB Assignment

Decides on the device whether it is in the **Metica** group (Metica on) or the **Baseline** group (Metica off). By default (automatic mode) the split comes from one Firebase Remote Config parameter; a game that decides the group itself reports it instead ([client-managed mode](#client-managed-mode)). The package sends its own events straight to Metica ingestion. It has no dependency on Firebase, AppLovin MAX or the Metica SDK; the game wires those up. Its only package dependency is `com.unity.nuget.newtonsoft-json` (Unity's Json.NET package). A project that ships its own `Newtonsoft.Json.dll` (for example an Asset Store copy) must remove it, or Unity reports a duplicate assembly.

Built and maintained by Metica. License: proprietary, source available for reference. See [LICENSE.md](LICENSE.md).

## Quick start

```csharp
using AB.Assignment;

// Once, as early as possible (e.g. [RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]): your Metica app id and API key.
ABAssignment.Initialize(new ABAssignmentOptions("yourMeticaAppId", "yourMeticaApiKey"));
string userId = ABAssignment.UserId;   // available at once; give it to SDKs at startup (does not decide the group)

// Optional: also forward a copy of each event to your own analytics.
ABAssignment.EventLogged += (eventName, payload) => MyAnalytics.Log(eventName, payload);

// The player's answer to your own GDPR prompt (not the TCF ad consent); passed on with every event:
ABAssignment.SetConsentStatus(ConsentStatus.Granted);   // or Denied; Unknown until you set it

// When the Remote Config fetch finishes (no Firebase in-app default for metica_assignment):
ABAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, value, null);   // only for a value whose source is remote

// Later, when the game needs the group (e.g. at ads init). The first read decides it for the whole session:
AssignmentResult a = ABAssignment.Current;
if (a.RunMetica) { /* start Metica ads */ } else { /* start the game's own ad code */ }
```

This is automatic mode. In [client-managed mode](#client-managed-mode) the game calls `SetAssignmentMode` and `ReportClientAssignment` instead of `ReportRemoteConfig`.

`Initialize` is the only setup call, and the build ships no config file. The session (user id, event queue, fetch timer) starts before the first scene loads; the group is decided the first time the game needs it (reading `Current`, `TagPlacement` or `ReportAdsInit`). A hidden `ABAssignment` object then checks the deadlines and the UTC day every frame and on resume, and sends queued events to Metica.

Firebase Remote Config mapping (the game owns this; the package has no Firebase dependency):

| Firebase outcome | Call |
|---|---|
| fetch failed | `ReportRemoteConfig(FetchFailed, null, message)` |
| fetch succeeded, `ActivateAsync` faulted or cancelled | `ReportRemoteConfig(ActivationFailed, null, message)` (`ActivateAsync` returning `false` is not a failure) |
| fetch and activation fine | `ReportRemoteConfig(Success, remoteValue, null)`, where `remoteValue` is `GetValue("metica_assignment").StringValue` if its `Source == ValueSource.RemoteValue`, otherwise `null` (reported as `missing`) |

## Groups and route

- `Group`: `Metica`, `Baseline` or `Unassigned`. `Label` is `metica`, `baseline` or `unassigned`.
- `bucket = uint64_be(SHA256(UTF8(experiment_id + ":" + user_id))[0..8]) % 10000`; `group = bucket < metica_percentage × 100 ? Metica : Baseline`. The salt is the full `experiment_id`, so the group is a pure function of `(user_id, experiment_id)`.
- **When the group is decided:** on first need. It uses a valid Firebase value received earlier in this session (`remote_this_session`), otherwise the value cached from an earlier session (`cached_remote`). With neither, the session is `Unassigned` (`unassigned_waiting`): the game runs its own ad code, and a value that arrives later is cached and applies from the next session. `unassigned_error` means deciding failed.
- After the group is decided it never changes during the session; a later value only updates the cache.
- `RunMetica` (the route) = `Group == Metica && enabled`. The kill switch changes the route only, never the group or the label.
- The user id is a GUID (`N` format) generated on first run and persisted in PlayerPrefs. It is lost on reinstall.
- `ReportCustomId(customId, source = null)` sends an `upliftCustomIdMapping` event that maps a game id to the generated id, at any time; see [Custom id mapping](#custom-id-mapping). `AddCustomUserId(source, id)` (older, unchanged) stores game ids and adds them to `upliftAssignment`. Neither changes the group or the generated id.
- `SetAssignmentMode(AssignmentMode.ClientManaged)` lets the game decide the group itself; see [Client-managed mode](#client-managed-mode).
- `SetDebugOverride(Group?)` (editor and development builds only) forces the route from the next session. The group and the label stay as assigned. The kill switch wins: with `"enabled": false` the route stays off even with an override set, and a warning says the override was ignored.

## Placement tags

`ABAssignment.TagPlacement("MissionComplete")` returns `metica|MissionComplete` (the assigned label). Null or empty gives `<label>|none`; already tagged input is returned unchanged.

## Events

The library always sends its events to Metica ingestion itself (see [Delivery to Metica](#delivery-to-metica)). Payloads are flat; values are string, bool, long or double. The request adds the user id and a timestamp, so payloads do not repeat the user id, except `assignment_user_id` in `upliftCustomIdMapping` and client-managed `upliftAssignment` (see [Ids](#ids)). Every event also carries `consent_status` (`unknown`, `granted`, `denied`), the value at send time (see below).

| Event | When | Fields |
|---|---|---|
| `upliftAssignment` | once per session when the group is decided (client-managed: when the game reports it); after that on a new UTC day while in the foreground or on resume, once per experiment and day; when a custom id is added or changed with `AddCustomUserId`; client-managed: when the provider user id becomes known or changes | `experiment_id`, `group`, `metica_route`, `metica_percentage` (always a double, e.g. `50.0`; not when unassigned or client-managed), `config_source` (`remote_this_session`, `cached_remote`, `unassigned_waiting`, `unassigned_error`, `client_managed`), `session_id`, `activity_date_utc`, `custom_user_ids` (when any; `source=id` pairs, URL-encoded, sorted, comma separated), `error` (only when unassigned, never empty), `assignment_user_id` and `provider_user_id` (client-managed only; `provider_user_id` once known) |
| `upliftConfigFetch` | once per cold start when the game reports the fetch, or after 10 s with `timeout`; at most one more if a result arrives after the timeout | `rc_received`, `fetch_status` (`success`, `invalid`, `missing`, `fetch_failed`, `activation_failed`, `timeout`), `fetch_latency_ms`, `session_id`, `activity_date_utc`, `error` (when given or invalid) |
| `upliftAdsInit` | when the game reports a terminal stage; `pending_timeout` 30 s after `Started` without one, then at most one more; after `skipped_low_memory`, one later `Started` plus its outcome (or `pending_timeout`) is sent as a second row, once per session | `session_id`, `experiment_id`, `group`, `network`, `outcome` (`success`, `failed`, `pending_timeout`, `skipped_low_memory`), `elapsed_ms`, `activity_date_utc`, `metica_internal_group` and `forced_holdout` (success only, when given), `error` (failed only) |
| `upliftCustomIdMapping` | when the game calls `ReportCustomId`, once per distinct pair per session | `assignment_user_id`, `custom_id`, `custom_id_source` (when given), `session_id`, `activity_date_utc` |

Deadlines are checked every frame by the library, so they fire only while the app runs.

### Ids

| Id | Where | Lifetime |
|---|---|---|
| `ABAssignment.UserId` | event `userId` (and URL `userId`) of every event; `assignment_user_id` in `upliftCustomIdMapping` and client-managed `upliftAssignment` | generated on first launch, persisted, lost on reinstall; never replaced by any other id |
| `session_id` | payload | new per cold start |
| `eventId` | event | new per event, the same on every retry |
| `experiment_id` | payload | from the Firebase value; client-managed: the game's optional label, else empty |
| `custom_id` | `upliftCustomIdMapping` | as passed to `ReportCustomId`; not stored |
| `provider_user_id` | client-managed `upliftAssignment` | as passed to `ReportClientAssignment`; left out until the game passes one; kept for the session only |

`assignment_user_id` repeats the event `userId` inside the payload of `upliftCustomIdMapping` and client-managed `upliftAssignment`, next to the game-supplied id. None of these ids are derived from one another. Game-supplied ids are sent as given whatever `consent_status` is, so games must not pass personal data such as email addresses; an opaque or hashed id is fine.

### Custom id mapping

`ReportCustomId(customId, source = null)` queues one `upliftCustomIdMapping` event:

```json
{"eventType":"upliftCustomIdMapping","eventId":"<uuid>","appId":"<appId>","eventTime":1792144800000,
 "userId":"<ABAssignment.UserId>","platform":"android",
 "customPayload":{"assignment_user_id":"<ABAssignment.UserId>","custom_id":"player-42","custom_id_source":"login",
  "session_id":"<session>","activity_date_utc":"2026-10-16","consent_status":"unknown"}}
```

- Main thread; any time: before or after `Initialize` (queued), before or after the group is decided, in both modes. It never decides the group, never stores the id and never changes the generated id.
- `customId`: not null or blank (whitespace only), at most 200 characters; sent as given, never trimmed or cut. `source`: null means none (the field is left out); otherwise the same rules. An invalid call, an off-thread call, or a session whose generated id could not be loaded is logged as an error and sends nothing.
- The same `(customId, source)` pair is sent once per session; a different pair sends another event. A new session sends it again.
- `AddCustomUserId` is unchanged: it persists the pair (`metica_assignment_custom_ids_v1`) and adds all pairs to every `upliftAssignment` in `custom_user_ids`, and sends nothing before the group is decided. The two do not share state.

### Client-managed mode

`SetAssignmentMode(AssignmentMode.ClientManaged)` makes the game the owner of the group for the session. Call it on every launch (not persisted), before `Current`, `TagPlacement` or `ReportAdsInit` are used; once the group is decided, a change of mode is logged as an error and ignored. A value outside the enum is rejected the same way.

Until the game calls `ReportClientAssignment(group, providerUserId, experimentId = null)`:

- `Current` returns a new unassigned result with `ConfigSource` `client_managed_pending` each time; it is not stored, so `TryGetCurrent` is false and nothing is sent. `TagPlacement` gives `unassigned|...`.
- No automatic decision, no cached or Firebase config, no enrolment record, no `upliftConfigFetch` (`ReportRemoteConfig` is ignored with a warning; no 10 s timeout), no daily `upliftAssignment`.
- `ReportAdsInit(Started)` records the start time; an outcome is logged as an error and ignored. The 30 s `pending_timeout` waits until the group is reported.

`ReportClientAssignment`:

- `group` must be `Group.Metica` or `Group.Baseline`. `providerUserId` is the user id the game's ad stack uses (`MeticaInitResponse.UserId` on the Metica route, the id given to `MaxSdk.SetUserId` on the game's own MAX route), or null while it is not known yet. `experimentId` is optional. A non-null id follows the `ReportCustomId` id rules (not blank, at most 200 characters). Otherwise: error, ignored.
- Report the group before starting the ad SDK, with a null provider id if it is not known yet, so that sessions whose init never finishes are still recorded and get `pending_timeout`.
- The package never reads device or advertising ids (GAID, IDFA, IDFV); pass the id the ad SDK actually uses. On the Metica route, without a user id in `MeticaInitConfig` the Metica SDK generates one and returns it only in `MeticaInitResponse.UserId` (in the Unity Editor there is none); with your own user id, report that one before init. The MAX Unity plugin has no user id getter, so keep a copy of what you pass to `MaxSdk.SetUserId`. These ids are not interchangeable with each other or with `ABAssignment.UserId`.
- The first valid report (it may come before `Initialize`; the event is queued) creates the session's `AssignmentResult` (`Group` as reported, `RunMetica = Group == Metica`, `Enabled = true`, `ExperimentId` = label or empty, `Bucket = -1`, `MeticaPercentageBasisPoints = 0`, `UserId` = the generated id, `ConfigSource = "client_managed"`, `IsNewAssignment = false`) and queues one `upliftAssignment`:

```json
{"eventType":"upliftAssignment","eventId":"<uuid>","appId":"<appId>","eventTime":1792144800000,
 "userId":"<ABAssignment.UserId>","platform":"android",
 "customPayload":{"experiment_id":"","group":"metica","metica_route":true,"provider_user_id":"<id the ad stack used>",
  "assignment_user_id":"<ABAssignment.UserId>","config_source":"client_managed","session_id":"<session>",
  "activity_date_utc":"2026-10-16","consent_status":"unknown"}}
```

  With a null provider id the same event is sent without `provider_user_id`. A session can therefore have several client-managed rows (id reported later, id changed, day change); use the latest non-empty `provider_user_id` per `session_id`.

- Same group, experiment and provider id again, or the same group and experiment with a null provider id: nothing sent (null never clears a known id). Same group and experiment with a new provider id: one more `upliftAssignment` with it. Another group or experiment: error, ignored; the group stays fixed for the session.
- After the report, the day change, `AddCustomUserId`, `ReportAdsInit` and `TagPlacement` work as in automatic mode, with the reported group; `upliftAdsInit.network` is `metica` for `Group.Metica`, otherwise `applovin`. `SetDebugOverride` has no effect.
- Without the call, `ReportClientAssignment` is logged as an error and ignored. The library never chooses a group in this mode.
- The next launch starts in automatic mode with nothing reported; call `SetAssignmentMode` and `ReportClientAssignment` again.

Example with both routes (`useMetica` comes from the game's own split; `MeticaAppId`, `MeticaApiKey`, `MaxSdkKey` and `MyMaxUserId` are the game's own values):

```csharp
using System.Threading.Tasks;
using AB.Assignment;
using Metica;
using Metica.Ads;

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

`MeticaSdk.InitializeAsync` does not throw when init fails; it returns a forced holdout, which is reported as `Succeeded` with `forced_holdout = true`. The `catch` is for unexpected exceptions, such as an invalid config.

### Delivery to Metica

The request has the format of a Metica SDK custom event, so the events are stored like the SDK's custom events, with no backend change. It carries only the fields the uplift analysis needs:

- `POST https://api-global-gateway.prod-eu.metica.com/ingest/v1/events?appId=<appId>&userId=<userId>&platform=android|ios`, headers `Content-Type: application/json` and `X-API-KEY: <apiKey>`.
- Body `{"events":[...]}`, at most 10 events per request.
- Ingestion answers `202` before it validates the body, so the library applies the SDK's rules before queueing: event name camelCase letters only, flat payload of at most 100 keys, no nested objects or arrays, finite numbers. Strings longer than 200 characters are cut to 200, ending in `...[truncated]`. An event that breaks a rule is logged as an error and not sent.
- One request at a time, oldest events first. An event leaves the persisted queue (`metica_assignment_queue_v1`, at most 100 events, oldest dropped first) only when ingestion answers 2xx, or a 4xx other than 408 and 429; a 401/403 (wrong key) or other 4xx is logged as an error and the batch is dropped, as the SDK does. Network errors, timeouts (20 s), 408, 429 and 5xx keep the batch (logged at Info) and retry after `2^n` seconds with 50–100 % jitter, at most 5 minutes. The queue is also sent when the app goes to the background or loses focus.
- If the app is killed while a request is in flight, the batch is sent again on the next start with the same `eventId`s.
- Metica production only: after two sends in a row without an HTTP answer, or one timeout, requests move to `https://relay.prod-eu.metica.com`, as in the SDK. A 5xx from the relay counts as no answer. A custom domain has no relay (see below).
- Events logged before `Initialize` wait in the queue. In the Unity Editor and on platforms other than Android and iOS, events are logged and discarded instead of sent. Development builds log each request body (never the key).

#### Custom domain (dedicated clusters)

If your Metica setup runs on a dedicated cluster under your own domain, give the package the same domain you give the Metica SDK:

```csharp
ABAssignment.Initialize(new ABAssignmentOptions(MeticaAppId, MeticaApiKey) {
    CustomDomain = "metica.example.com"
});
```

- The value is a host, optionally with a scheme and a port: `metica.example.com`, `https://metica.example.com`, `metica.example.com:8443`. Without a scheme, `https` is used. Requests then go to `https://metica.example.com/ingest/v1/events?...`.
- The rules are the Metica SDK's: surrounding whitespace and a trailing `/` or `.` are removed and the value is lowercased. A path, query, fragment, credentials, whitespace inside, non-ASCII characters, `_` in the host, a scheme other than `https`/`http`, or a port outside 1–65535 make it invalid.
- Null, empty or whitespace only means Metica production.
- An invalid value makes `Initialize` throw an `ArgumentException` whose message names the broken rule and the value (never the API key), for example `customDomain must not contain a path, was '/metica': 'https://metica.example.com/metica'`. Nothing is sent, and the package stays uninitialised, so you can fix the value and call `Initialize` again; events logged in the meantime stay queued. Code after `Initialize` in the same method does not run unless you catch the exception. This is deliberately unlike the Metica SDK, which logs and falls back to Metica production: a cluster on its own domain must not have its events go to Metica production because of a typo.
- With a custom domain there is no relay host; failed sends retry against the same domain.
- `http` works only if the platform allows cleartext traffic to that host, and the package logs a warning.

#### What is sent

Each event has exactly these fields, and nothing else:

| Field | Value |
|---|---|
| `eventType` | The event name |
| `eventId` | Lowercase UUID v4, the same on every retry |
| `appId` | The Metica app id from `ABAssignmentOptions` |
| `eventTime` | Unix epoch milliseconds when the event was logged |
| `userId` | `ABAssignment.UserId`, a random id generated on first launch |
| `platform` | `android` or `ios` |
| `customPayload` | The event's fields from the table above, plus `consent_status` |

No app, package or SDK version, no device fields (model, manufacturer, OS version or build, phone/tablet, memory, locale, time zone, country) and no advertising or vendor id (GAID, IDFA, IDFV). Their columns stay null. Events queued on the device by 1.0.0 are also sent without their device fields.

### Forwarding to your own analytics

`ABAssignment.EventLogged` is an optional C# event. Each subscriber receives the event name and a read-only copy of the payload (with `consent_status`) when the event is logged, on the main thread. It is additive: the event is already queued for Metica before subscribers run, and a subscriber that throws is logged and skipped without affecting Metica delivery or other subscribers.

`SetEventSink` and `IAssignmentEventSink` are obsolete and kept only so 0.1.0 code compiles: `SetEventSink` logs an error and ignores the sink, so a 0.1.0 integration sends nothing until it calls `Initialize`. A 0.1.0 sink was the transport to Metica (for example through the Metica SDK's `LogCustomEvent`), so passing it copies would send every event twice. Use `EventLogged` instead.

Every `upliftAssignment` row on its own falls into exactly one of these classes:

| Class | Condition |
|---|---|
| assigned from a value received this session | `config_source = 'remote_this_session'` |
| assigned from a value cached in an earlier session | `config_source = 'cached_remote'` |
| unassigned: no Firebase config yet when the group was needed | `config_source = 'unassigned_waiting'` (`group = 'unassigned'`, `error` set) |
| unassigned: deciding failed | `config_source = 'unassigned_error'` (`group = 'unassigned'`, `error` set) |
| reported by the game (client-managed mode) | `config_source = 'client_managed'` (`assignment_user_id` set; `provider_user_id` once known) |

## Consent status

`SetConsentStatus(ConsentStatus)` records the player's answer to the game's own GDPR prompt (not the TCF/UMP ad consent string). The library does not gate anything on it: events are always sent and the group is decided as usual. It only adds `consent_status` to every event, with the value when the event is sent, so the backend can apply consent.

- Default `unknown` until the game sets a status; it is never assumed given.
- Persisted (`metica_assignment_consent_v1`), so it carries over to later sessions. Setting `Unknown` clears it.
- An event queued before the status changed carries the new status when it is sent to Metica. `EventLogged` subscribers get the status at the time the event is logged.

## Threading

`Initialize`, subscribing to `EventLogged`, `SetConsentStatus`, `AddCustomUserId`, `ReportCustomId`, `SetAssignmentMode`, `ReportClientAssignment`, `ReportRemoteConfig`, `ReportAdsInit`, `SetDebugOverride` and the first read of `Current` / `UserId` are main-thread only. The library records the main thread at startup; a call from another thread is logged as an error and ignored. Once the group is decided, `Current` and `TagPlacement` only read the immutable snapshot and are safe from any thread, including ad revenue callbacks. `TryGetCurrent` returns the assignment only if it is already decided, and never decides it.

## Persistence (PlayerPrefs)

`metica_assignment_user_id_v1`, `metica_assignment_config_v1` (last valid remote value), `metica_assignment_v1` (first session per experiment, last 8), `metica_assignment_daily_v1`, `metica_assignment_queue_v1`, `metica_assignment_custom_ids_v1`, `metica_assignment_debug_v1`, `metica_assignment_consent_v1` (`granted` / `denied`; absent = unknown).

## Test vectors

| experiment_id | user_id | bucket |
|---|---|---|
| `studio_mygame-20261016-50` | `user-0` | 465 |
| `studio_mygame-20261016-50` | `user-1` | 253 |
| `studio_mygame-20261016-50` | `user-2` | 2694 |
| `studio_mygame-20261016-50` | `6c0f2b0e9d6a4f7f8a0c1d2e3f405162` | 4221 |
| `studio_mygame-20261016-12.5` | `user-0` | 5302 |
| `studio_mygame-20261016-12.5` | `user-1` | 4056 |
| `studio_mygame-20261016-12.5` | `6c0f2b0e9d6a4f7f8a0c1d2e3f405162` | 7512 |
| `studio_mygame-20261016-90` | `user-0` | 6572 |
| `studio_mygame-20261016-90` | `user-2` | 9362 |
| `studio_mygame-20261016-90` | `6c0f2b0e9d6a4f7f8a0c1d2e3f405162` | 4386 |

The percentage is the last `-` segment of `experiment_id`.

## Tests

EditMode tests are in `Tests/Editor` (Unity Test Runner → EditMode → `AB.Assignment.Tests.Editor`). They use an in-memory store and a fake clock, so they are deterministic and do not touch PlayerPrefs.

## Remote Config parameter `metica_assignment`

One parameter, value type **JSON**, in the Remote Config parameter group "Metica". JSON Schema: [`Tools~/firebase/metica_assignment.schema.json`](Tools~/firebase/metica_assignment.schema.json).

```json
{
  "experiment_id": "studio_mygame-20261016-50",
  "metica_percentage": 50,
  "enabled": true
}
```

| Field | Type | Rules |
|---|---|---|
| `experiment_id` | string, required | Format `<studio>_<game>-<datestamp>-<metica_percentage>`, e.g. `studio_mygame-20261016-50` at 50 %. Pattern `^[a-z0-9._]{1,64}-[0-9]{8}-(100\|[1-9]?[0-9](\.[0-9]?[1-9])?)$` (at most 79 characters). The game segment is `<studio>_<game>`, lower case, with no `-` (the pattern also accepts `.`, so package-id style segments stay valid). The datestamp is a real UTC calendar date `YYYYMMDD`. The percentage segment is in canonical form: no leading zeros, no trailing decimal zeros, at most two decimals (`50`, `12.5`, `0.05`, `100`; not `050`, `50.0`, `12.50`). **The percentage segment must equal `metica_percentage`** (compared in basis points). The id is also the hash salt. |
| `metica_percentage` | number, required | Share of devices assigned to the Metica group under this `experiment_id`, 0–100, at most two decimals. Because it is part of the id, changing it means publishing a new `experiment_id`. |
| `enabled` | bool, required | Kill switch. `false`: no device routes ads to Metica, from the session in which the value arrives (if it arrives before the group is needed) or else from the next session. The assigned group and the labels do not change. It may change under the same `experiment_id`. |

There is no schema version and no config version.

**What the client does with a value**
- A value that breaks any rule (bad JSON, a required field missing or of the wrong type, an id that does not match the pattern, a percentage segment that does not equal `metica_percentage`) is ignored. The device keeps its cached config (or stays unassigned if it has none).
- Unknown extra fields are allowed and ignored.
- A valid value is cached. It decides this session's group if the group has not been needed yet; otherwise it applies from the next session. Never mid-session.

**Compatibility rules**
- Only additive changes are allowed in `metica_assignment`: new optional fields, each with a default that keeps the current behaviour. No field is removed, renamed, or given a new meaning or type.
- A breaking change is published under a new parameter name (`metica_assignment_v2`) next to the old one. Both stay published while older builds matter.
- If the parameter is missing, the client uses its cached remote config or, if it has none, stays unassigned. This is not an error.
- Do **not** register `metica_assignment` as a Firebase in-app default. If it were registered, Firebase would return it after a fetch in which the parameter is missing, and it would look like a remote value.

**No baked default.** The build ships no config. A device is assigned only from a Firebase value: in the session it arrives (if before the group is needed) or from the next session. Until then it is unassigned and runs the game's own ad code.
