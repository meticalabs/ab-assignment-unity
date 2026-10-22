# Changelog

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
