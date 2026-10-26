# Changelog

## [Unreleased]

Custom id mapping and client-managed assignment. Automatic assignment, its events and the existing API are unchanged.

- `ABAssignment.ReportCustomId(customId, source = null)` sends a new `upliftCustomIdMapping` event that maps a game id to `ABAssignment.UserId` (carried in `assignment_user_id`). It works at any time, before or after `Initialize` and the group decision, and never decides the group. `AddCustomUserId` is unchanged.
- `ABAssignment.SetAssignmentMode(AssignmentMode.ClientManaged)` and `ABAssignment.ReportClientAssignment(group, providerUserId, experimentId = null)` let a game that splits players itself report its group and the user id its ad stack used (null until known; reporting it later sends one more event). `upliftAssignment` then has `config_source = "client_managed"`, `assignment_user_id` and, once known, `provider_user_id`. The mode is per session and must be set before the group is needed.

## [2.1.0] - 2026-10-23

Runtime: the kill switch wins over the debug override and one ads-init retry after a low-memory skip. Firebase tool: `enable`, and protection against re-assigning every device by accident.

### Runtime

- With `"enabled": false`, `SetDebugOverride(Group.Metica)` no longer routes to Metica in development builds. A warning, `Debug override ignored: the kill switch is on`, says why. Release builds were never affected.
- After `ReportAdsInit(SkippedLowMemory)`, one later `Started` plus one outcome is sent as a second `upliftAdsInit` row, with `elapsed_ms` and the 30-second `pending_timeout` counted from that `Started`. Before, the skip blocked the real outcome. A session can now have two `upliftAdsInit` rows this way (as it already could with `pending_timeout` and a late outcome), so analysis should take the last row per `session_id`.
- Only one retry is allowed per session. `Started` twice, `Started` after `Failed` or `Succeeded`, and a second outcome are still ignored with a warning.
- The off-thread error when the group is first needed off the main thread now says what to do: read `Current` once on the main thread before any ad call.

### Firebase tool

- New `enable` command, the mirror of `disable`: keeps `experiment_id` and `metica_percentage` and sets `enabled` to true.
- `set` refuses to publish an `experiment_id` other than the one devices use, and exits with code 2. Re-running the original `set` command on a later day used to publish a new id (the date defaults to today) and re-split every device. Pass `--new-experiment` to start a new experiment on purpose. Scripts that run `set ... --yes` to change the experiment need the new flag.
- When the published value is missing or invalid, devices keep their last valid cached value, so the tool looks up the last valid `experiment_id` in the Remote Config version history and protects that one.
- `enable` and `disable` now work when the published value is missing or invalid: pass `--experiment-id` with the last valid id (the error names it). Another id needs `--new-experiment`. Before, `disable` refused, and the only way out was `set`.
- A malformed `--experiment-id` for `enable` and `disable` is rejected before credentials are loaded.

## [2.0.0] - 2026-10-22

Renamed from Metica Assignment to AB Assignment. The package id, assembly, namespace and the two public types changed; behaviour, events and stored data did not.

### Migration

In `Packages/manifest.json`, replace

```json
"com.metica.assignment": "https://github.com/meticalabs/metica-assignment-unity.git?path=/Packages/com.metica.assignment#v1.3.0"
```

with

```json
"com.abassignment.unity": "https://github.com/meticalabs/ab-assignment-unity.git?path=/Packages/com.abassignment.unity#v2.0.0"
```

An embedded copy moves from `Packages/com.metica.assignment` to `Packages/com.abassignment.unity`. A `testables` entry changes the same way.

A sample imported from 1.x (`Assets/Samples/Metica Assignment/...`) is not updated by the package upgrade and stops compiling. Delete it and import the sample again, or apply the table below to it and to any code copied from it.

| 1.x | 2.0.0 |
|---|---|
| `com.metica.assignment` | `com.abassignment.unity` |
| `using Metica.Assignment;` | `using AB.Assignment;` |
| asmdef reference `Metica.Assignment` | `AB.Assignment` |
| `MeticaAssignment` | `ABAssignment` |
| `MeticaAssignmentOptions` | `ABAssignmentOptions` |
| Log prefix `[MeticaAssignment]` | `[ABAssignment]` |
| Firebase tool `tools/metica_assignment.mjs` | `tools/ab_assignment.mjs` |
| Sample namespace `MeticaAssignmentSample` | `ABAssignmentSample` |
| Sample classes `MeticaAssignmentStartup`, `MeticaAssignmentRemoteConfig`, `MeticaAssignmentAds` | `ABAssignmentStartup`, `ABAssignmentRemoteConfig`, `ABAssignmentAds` |

Unchanged: the Firebase parameter `metica_assignment` and its fields, `Group.Metica` and `RunMetica`, the event names and payloads, and the PlayerPrefs keys (`metica_assignment_*_v1`), so a device keeps its user id, group and queued events when it updates from 1.x. The `asmdef` and script GUIDs are unchanged.

## [1.3.0] - 2026-10-22

An invalid custom domain is an error.

- `MeticaAssignment.Initialize` throws `ArgumentException` when `MeticaAssignmentOptions.CustomDomain` is set but invalid, with the same validation message as the Metica SDK. Before, the package logged an error and sent to Metica production, as the SDK does. Now nothing is sent and the package stays uninitialised, so a later `Initialize` with a valid value works. Events logged in the meantime stay queued.
- Null or blank `CustomDomain` still means Metica production. The validation rules are unchanged.

## [1.2.0] - 2026-10-22

Custom domain support; development-only options removed.

- Added `MeticaAssignmentOptions.CustomDomain` for setups that run Metica on a dedicated cluster under their own domain. It takes the same value and follows the same rules as the Metica SDK's custom domain: a host with an optional scheme and port, normalised to `scheme://host[:port]`. Null or blank means Metica production. An invalid value is logged as an error and Metica production is used. A custom domain has no relay host.
- Removed `MeticaEnvironment` and `MeticaAssignmentOptions.Environment`. Events go to Metica production unless `CustomDomain` is set.
- Removed `MeticaAssignmentOptions.BaseUrl`. Use `CustomDomain`; unlike `BaseUrl`, it rejects a path.
- The `Initialize` log line now names the ingestion host: `Events go to Metica ingestion at <base URL> for app <app id>.`

## [1.1.0] - 2026-10-22

Events carry only the fields the uplift analysis needs.

- Removed from every event: `appVersion`, `osVersion`, `buildNumber`, `deviceModel`, `deviceManufacturer`, `deviceType`, `deviceMemory`, `locale`, `localTimezone`, `country`. Each event now has `eventType`, `eventId`, `appId`, `eventTime`, `userId`, `platform` and `customPayload`. The event payloads are unchanged.
- Removed the `sdkVersion` and `route` query parameters.
- Events queued on the device by 1.0.0 are sent without their stored device fields.
- Removed the `com.unity.modules.androidjni` dependency (it was only used to read device fields).
- READMEs list exactly what is sent.

## [1.0.0] - 2026-10-21

First public release.

- Deterministic on-device assignment of each device to the Metica group or the Baseline group, from one Firebase Remote Config parameter (`metica_assignment`). SHA-256 bucket, salted with the full experiment id.
- `MeticaAssignment.Initialize(new MeticaAssignmentOptions(appId, apiKey))`: the package sends its three events (`upliftAssignment`, `upliftConfigFetch`, `upliftAdsInit`) to Metica ingestion itself, in the same request format as the Metica SDK's custom events. No Metica SDK needed for this.
- Persisted event queue with retries, backoff and a relay host fallback in production; events are validated before they are queued.
- Optional `MeticaAssignment.EventLogged` event with read-only copies of each event for your own analytics.
- Kill switch (`enabled`), placement tags (`TagPlacement`), consent status, custom user ids, debug override for development builds.
- Basic integration sample and EditMode tests.
