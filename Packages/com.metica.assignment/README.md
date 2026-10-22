# Metica Assignment

Decides on the device whether it is in the **Metica** group (Metica on) or the **Baseline** group (Metica off). The split comes from one Firebase Remote Config parameter. The package sends its own events straight to Metica ingestion. It has no dependency on Firebase, AppLovin MAX or the Metica SDK; the game wires those up. Its only package dependency is `com.unity.nuget.newtonsoft-json` (Unity's Json.NET package). A project that ships its own `Newtonsoft.Json.dll` (for example an Asset Store copy) must remove it, or Unity reports a duplicate assembly.

License: proprietary, source available for reference. See [LICENSE.md](LICENSE.md).

## Quick start

```csharp
using Metica.Assignment;

// Once, as early as possible (e.g. [RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]): your Metica app id and API key.
MeticaAssignment.Initialize(new MeticaAssignmentOptions("yourMeticaAppId", "yourMeticaApiKey"));
string userId = MeticaAssignment.UserId;   // available at once; give it to SDKs at startup (does not decide the group)

// Optional: also forward a copy of each event to your own analytics.
MeticaAssignment.EventLogged += (eventName, payload) => MyAnalytics.Log(eventName, payload);

// The player's answer to your own GDPR prompt (not the TCF ad consent); passed on with every event:
MeticaAssignment.SetConsentStatus(ConsentStatus.Granted);   // or Denied; Unknown until you set it

// When the Remote Config fetch finishes (no Firebase in-app default for metica_assignment):
MeticaAssignment.ReportRemoteConfig(RemoteConfigStatus.Success, value, null);   // only for a value whose source is remote

// Later, when the game needs the group (e.g. at ads init). The first read decides it for the whole session:
AssignmentResult a = MeticaAssignment.Current;
if (a.RunMetica) { /* start Metica ads */ } else { /* start the game's own ad code */ }
```

`Initialize` is the only setup call, and the build ships no config file. The session (user id, event queue, fetch timer) starts before the first scene loads; the group is decided the first time the game needs it (reading `Current`, `TagPlacement` or `ReportAdsInit`). A hidden `MeticaAssignment` object then checks the deadlines and the UTC day every frame and on resume, and sends queued events to Metica.

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
- `AddCustomUserId(source, id)` links game ids to the generated id. It never changes the group.
- `SetDebugOverride(Group?)` (editor and development builds only) forces the route from the next session. The group and the label stay as assigned.

## Placement tags

`MeticaAssignment.TagPlacement("MissionComplete")` returns `metica|MissionComplete` (the assigned label). Null or empty gives `<label>|none`; already tagged input is returned unchanged.

## Events

The library always sends its events to Metica ingestion itself (see [Delivery to Metica](#delivery-to-metica)). Payloads are flat; values are string, bool, long or double. The request adds the user id and a timestamp, so payloads do not repeat the user id. Every event also carries `consent_status` (`unknown`, `granted`, `denied`), the value at send time (see below).

| Event | When | Fields |
|---|---|---|
| `upliftAssignment` | once per session when the group is decided; after that on a new UTC day while in the foreground or on resume, once per experiment and day; when a custom id is added or changed | `experiment_id`, `group`, `metica_route`, `metica_percentage` (always a double, e.g. `50.0`; not when unassigned), `config_source` (`remote_this_session`, `cached_remote`, `unassigned_waiting`, `unassigned_error`), `session_id`, `activity_date_utc`, `custom_user_ids` (when any; `source=id` pairs, URL-encoded, sorted, comma separated), `error` (only when unassigned, never empty) |
| `upliftConfigFetch` | once per cold start when the game reports the fetch, or after 10 s with `timeout`; at most one more if a result arrives after the timeout | `rc_received`, `fetch_status` (`success`, `invalid`, `missing`, `fetch_failed`, `activation_failed`, `timeout`), `fetch_latency_ms`, `session_id`, `activity_date_utc`, `error` (when given or invalid) |
| `upliftAdsInit` | when the game reports a terminal stage; `pending_timeout` 30 s after `Started` without one, then at most one more | `session_id`, `experiment_id`, `group`, `network`, `outcome` (`success`, `failed`, `pending_timeout`, `skipped_low_memory`), `elapsed_ms`, `activity_date_utc`, `metica_internal_group` and `forced_holdout` (success only, when given), `error` (failed only) |

Deadlines are checked every frame by the library, so they fire only while the app runs.

### Delivery to Metica

The request has the format of a Metica SDK custom event, so the events are stored like the SDK's custom events, with no backend change. It carries only the fields the uplift analysis needs:

- `POST https://api-global-gateway.prod-eu.metica.com/ingest/v1/events?appId=<appId>&userId=<userId>&platform=android|ios`, headers `Content-Type: application/json` and `X-API-KEY: <apiKey>`.
- Body `{"events":[...]}`, at most 10 events per request.

#### What is sent

Each event has exactly these fields, and nothing else:

| Field | Value |
|---|---|
| `eventType` | The event name |
| `eventId` | Lowercase UUID v4, the same on every retry |
| `appId` | The Metica app id from `MeticaAssignmentOptions` |
| `eventTime` | Unix epoch milliseconds when the event was logged |
| `userId` | `MeticaAssignment.UserId`, a random id generated on first launch |
| `platform` | `android` or `ios` |
| `customPayload` | The event's fields from the table above, plus `consent_status` |

No app, package or SDK version, no device fields (model, manufacturer, OS version or build, phone/tablet, memory, locale, time zone, country) and no advertising or vendor id (GAID, IDFA, IDFV). Their columns stay null. Events queued on the device by 1.0.0 are also sent without their device fields.
- Ingestion answers `202` before it validates the body, so the library applies the SDK's rules before queueing: event name camelCase letters only, flat payload of at most 100 keys, no nested objects or arrays, finite numbers. Strings longer than 200 characters are cut to 200, ending in `...[truncated]`. An event that breaks a rule is logged as an error and not sent.
- One request at a time, oldest events first. An event leaves the persisted queue (`metica_assignment_queue_v1`, at most 100 events, oldest dropped first) only when ingestion answers 2xx, or a 4xx other than 408 and 429; a 401/403 (wrong key) or other 4xx is logged as an error and the batch is dropped, as the SDK does. Network errors, timeouts (20 s), 408, 429 and 5xx keep the batch (logged at Info) and retry after `2^n` seconds with 50–100 % jitter, at most 5 minutes. The queue is also sent when the app goes to the background or loses focus.
- If the app is killed while a request is in flight, the batch is sent again on the next start with the same `eventId`s.
- Production only: after two sends in a row without an HTTP answer, or one timeout, requests move to `https://relay.prod-eu.metica.com`, as in the SDK. A 5xx from the relay counts as no answer.
- `MeticaAssignmentOptions.Environment = MeticaEnvironment.Development` sends to `https://dev-host.invalid`; use a DEV app id and key. `BaseUrl` replaces the host (no fallback).
- Events logged before `Initialize` wait in the queue. In the Unity Editor and on platforms other than Android and iOS, events are logged and discarded instead of sent. Development builds log each request body (never the key).

### Forwarding to your own analytics

`MeticaAssignment.EventLogged` is an optional C# event. Each subscriber receives the event name and a read-only copy of the payload (with `consent_status`) when the event is logged, on the main thread. It is additive: the event is already queued for Metica before subscribers run, and a subscriber that throws is logged and skipped without affecting Metica delivery or other subscribers.

`SetEventSink` and `IAssignmentEventSink` are obsolete and kept only so 0.1.0 code compiles: `SetEventSink` logs an error and ignores the sink, so a 0.1.0 integration sends nothing until it calls `Initialize`. A 0.1.0 sink was the transport to Metica (for example through the Metica SDK's `LogCustomEvent`), so passing it copies would send every event twice. Use `EventLogged` instead.

Every `upliftAssignment` row on its own falls into exactly one of three classes:

| Class | Condition |
|---|---|
| assigned from a value received this session | `config_source = 'remote_this_session'` |
| assigned from a value cached in an earlier session | `config_source = 'cached_remote'` |
| unassigned: no Firebase config yet when the group was needed | `config_source = 'unassigned_waiting'` (`group = 'unassigned'`, `error` set) |
| unassigned: deciding failed | `config_source = 'unassigned_error'` (`group = 'unassigned'`, `error` set) |

## Consent status

`SetConsentStatus(ConsentStatus)` records the player's answer to the game's own GDPR prompt (not the TCF/UMP ad consent string). The library does not gate anything on it: events are always sent and the group is decided as usual. It only adds `consent_status` to every event, with the value when the event is sent, so the backend can apply consent.

- Default `unknown` until the game sets a status; it is never assumed given.
- Persisted (`metica_assignment_consent_v1`), so it carries over to later sessions. Setting `Unknown` clears it.
- An event queued before the status changed carries the new status when it is sent to Metica. `EventLogged` subscribers get the status at the time the event is logged.

## Threading

`Initialize`, subscribing to `EventLogged`, `SetConsentStatus`, `AddCustomUserId`, `ReportRemoteConfig`, `ReportAdsInit`, `SetDebugOverride` and the first read of `Current` / `UserId` are main-thread only. The library records the main thread at startup; a call from another thread is logged as an error and ignored. Once the group is decided, `Current` and `TagPlacement` only read the immutable snapshot and are safe from any thread, including ad revenue callbacks. `TryGetCurrent` returns the assignment only if it is already decided, and never decides it.

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

EditMode tests are in `Tests/Editor` (Unity Test Runner → EditMode → `Metica.Assignment.Tests.Editor`). They use an in-memory store and a fake clock, so they are deterministic and do not touch PlayerPrefs.

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
