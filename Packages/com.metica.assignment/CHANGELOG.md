# Changelog

## [1.0.0] - 2026-10-21

First public release.

- Deterministic on-device assignment of each device to the Metica group or the Baseline group, from one Firebase Remote Config parameter (`metica_assignment`). SHA-256 bucket, salted with the full experiment id.
- `MeticaAssignment.Initialize(new MeticaAssignmentOptions(appId, apiKey))`: the package sends its three events (`upliftAssignment`, `upliftConfigFetch`, `upliftAdsInit`) to Metica ingestion itself, in the same request format as the Metica SDK's custom events. No Metica SDK needed for this.
- Persisted event queue with retries, backoff and a relay host fallback in production; events are validated before they are queued.
- Optional `MeticaAssignment.EventLogged` event with read-only copies of each event for your own analytics.
- Kill switch (`enabled`), placement tags (`TagPlacement`), consent status, custom user ids, debug override for development builds.
- Basic integration sample and EditMode tests.
