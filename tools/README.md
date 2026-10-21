# metica_assignment tool

Reads and updates the Firebase Remote Config parameter `metica_assignment` from the command line. It is an alternative to the Firebase console steps in the [repository README](../README.md#2-set-up-the-firebase-parameter).

The tool is one Node script, [`metica_assignment.mjs`](metica_assignment.mjs). The tests use the templates in [`testdata/`](testdata/): `sample_template.json` (a template with groups and conditions) and `new_project_template.json` (a template with no parameters yet).

What it does:

- Uses the official Remote Config REST API: `GET` and `PUT https://firebaseremoteconfig.googleapis.com/v1/projects/<project-id>/remoteConfig`, with the template's ETag sent as `If-Match`. If someone publishes in between, the `PUT` is rejected and nothing changes. Run the command again.
- Changes only this one parameter. All other parameters, conditions and parameter groups are sent back exactly as they were read. If the parameter is in a group (for example `Metica`), it stays in that group. A new parameter is added at the top level; you can move it to a group in the console later. This also works on a new project whose template has no parameters yet.
- Checks the value with the same rules the game uses (`AssignmentConfig.TryParse` in `com.metica.assignment`) before anything is sent.
- Sets the data type to **JSON** and adds a description if the parameter has none.
- Shows a diff and asks before publishing (`--yes` skips the question). Then it asks Firebase to check the template (`?validateOnly=true`), and only then publishes. The new template version gets the description `Set metica_assignment with metica_assignment.mjs`.

## Setup

Node 18.3 or later (for `util.parseArgs`). The only package is `google-auth-library`; requests use Node's built-in `fetch`. No build step. `node_modules/` is git-ignored.

`<path-to-tools>` below is this `tools/` folder.

```bash
cd <path-to-tools>
npm install
node metica_assignment.mjs --help
```

## Getting credentials

The tool needs a Google identity that can read (for `get`) or publish (for `set` and `disable`) the project's Remote Config. Use option A or option B.

### Who needs which permission

| Command | Needed access | Role |
|---|---|---|
| `get` | read Remote Config | **Firebase Remote Config Viewer** (`roles/firebase.remoteConfigViewer`), or the Admin role below |
| `set`, `disable` | read and publish Remote Config | **Firebase Remote Config Admin** (`roles/firebase.remoteConfigAdmin`) |

Project-wide roles such as Owner, Editor or Firebase Admin also work, but give much more access than the tool needs.

### Option A: service account key

**Quick way (Firebase Admin SDK key).** **Firebase console › Project settings › Service accounts › Generate new private key**. This downloads a JSON key for the project's `firebase-adminsdk` service account. That account has wide admin rights on the project, much more than this tool needs.

**Recommended: a dedicated least-privilege service account.**

1. Google Cloud console › select the Firebase project › **IAM & Admin › Service Accounts** › **Create service account** (for example `remote-config-tool`).
2. Grant it **Firebase Remote Config Admin** (`roles/firebase.remoteConfigAdmin`). If it only needs `get`, grant **Firebase Remote Config Viewer** (`roles/firebase.remoteConfigViewer`) instead.
3. Open the service account › **Keys** › **Add key › Create new key › JSON**.

Or with gcloud:

```bash
gcloud iam service-accounts create remote-config-tool --project <project-id>
gcloud projects add-iam-policy-binding <project-id> \
    --member "serviceAccount:remote-config-tool@<project-id>.iam.gserviceaccount.com" \
    --role roles/firebase.remoteConfigAdmin
gcloud iam service-accounts keys create <credentials.json> \
    --iam-account remote-config-tool@<project-id>.iam.gserviceaccount.com
```

**Store the key outside the repository** and make it readable only by you. Never commit it, paste it into chat, or put it in a shared folder:

```bash
chmod 600 <credentials.json>
```

Then pass it with `--credentials <credentials.json>`, or set it once per shell:

```bash
export GOOGLE_APPLICATION_CREDENTIALS=<credentials.json>
```

If a key leaks, delete it in **IAM & Admin › Service Accounts › Keys** and create a new one.

### Option B: no key, your own Google account

Use gcloud application-default credentials (ADC) with a Google account that has one of the roles above on the project:

```bash
gcloud auth application-default login
```

If the API answers HTTP 403 and mentions a quota project, or says the API is not enabled for another project, set the quota project to the Firebase project:

```bash
gcloud auth application-default set-quota-project <project-id>
```

This needs `serviceusage.services.use` on that project (Owner, Editor and Service Usage Consumer have it).

### Which credentials are used

The tool takes the first one that is set:

1. `--credentials <credentials.json>` (a service account JSON key file)
2. `GOOGLE_APPLICATION_CREDENTIALS`
3. gcloud application-default credentials (option B)

If `--credentials` is given, `GOOGLE_APPLICATION_CREDENTIALS` is ignored.

## The value

```json
{"experiment_id":"<studio>_<game>-<YYYYMMDD>-<pct>","metica_percentage":<pct>,"enabled":true}
```

| Field | Meaning |
|---|---|
| `experiment_id` | `<studio>_<game>-<YYYYMMDD>-<pct>`. Also the hash salt that puts each device in a group, so a new id assigns every device again. The date is a label (use the day you publish), not a schedule. |
| `metica_percentage` | Share of devices in the Metica group, 0 to 100, at most two decimals. Must equal the `<pct>` segment of `experiment_id`. A new percentage therefore needs a new id. |
| `enabled` | Kill switch. `false`: no device routes ads to Metica. Groups and labels do not change. Can change under the same id. |

Rules the tool checks, the same as the game:

- `experiment_id` matches `^[a-z0-9._]{1,64}-[0-9]{8}-(100|[1-9]?[0-9](\.[0-9]?[1-9])?)$` (where `$` is the end of the string; a trailing newline is rejected), and the date is a real calendar date.
- The `<pct>` segment is in canonical form: `50`, `12.5`, `0.05`, `100`; not `050`, `50.0`, `12.50`.
- The `<pct>` segment equals `metica_percentage` (compared in basis points).
- All three fields are present with the right types (string, number, boolean). Extra fields are allowed.
- The JSON is strict: no duplicate keys, no `NaN`, no trailing text, at most 64 levels of nesting.

A value that breaks a rule is ignored by every device, which then reports `fetch_status = invalid`. The tool refuses to publish such a value.

How the game reads it: the game reads the parameter as a string (`ConfigValue.StringValue`) and parses the JSON itself. Firebase's data type does not change what the device receives; the tool sets it to JSON so the console checks the JSON on edits.

## Commands and options

| Option | Commands | Meaning |
|---|---|---|
| `--project` | all | Firebase project id (required) |
| `--credentials` | all | Service account JSON key file |
| `--parameter` | all | Parameter name. Default `metica_assignment`. Only change it if your game reads another name. |
| `--studio`, `--game` | set | Lower case `a-z`, `0-9`, `_`, `.`; no `-`. Joined as `<studio>_<game>`, at most 64 characters together. |
| `--percentage` | set | `metica_percentage`, 0 to 100, at most two decimals |
| `--date` | set | `YYYYMMDD` datestamp. Default: today in UTC. |
| `--experiment-id` | set | Full id instead of `--studio`/`--game`/`--date` |
| `--enabled` / `--disabled` | set | Value of `enabled`. Default `--enabled`. |
| `--condition` | set, disable | Change the value for this condition instead of the default value. The condition must already exist in the template, and the parameter must already have a default value. The repository README recommends no conditions on this parameter; use this only for something like a QA-device condition. |
| `--dry-run` | set, disable | Print the diff and stop |
| `--yes` | set, disable | Publish without asking. Required when there is no terminal (CI). |

Exit codes: `0` success (or nothing to change, or a dry run); `1` for `get` when the value is missing or invalid, or when you answer no to the publish question; `2` for bad arguments, an invalid value, credential or connection problems, or a rejected API request.

The examples below assume you ran the setup and are in `<path-to-tools>`.

Show the published value and check it (exit code 1 if it is missing or invalid). This only reads:

```bash
node metica_assignment.mjs get --project <project-id> \
    --credentials <credentials.json>
```

The other examples leave out `--credentials` (so `GOOGLE_APPLICATION_CREDENTIALS` or ADC is used).

Start an experiment with 50% of devices in the Metica group. The date defaults to today (UTC):

```bash
node metica_assignment.mjs set --project <project-id> \
    --studio <studio> --game <game> --percentage 50

# Sets the value: {"experiment_id":"<studio>_<game>-<today>-50","metica_percentage":50,"enabled":true}
```

Same, with a fixed date, and only print the diff:

```bash
node metica_assignment.mjs set --project <project-id> \
    --studio <studio> --game <game> --percentage 12.5 --date <YYYYMMDD> --dry-run
```

Give the experiment id directly (the percentage is taken from its last segment):

```bash
node metica_assignment.mjs set --project <project-id> \
    --experiment-id <experiment-id>
```

Kill switch: keep `experiment_id` and `metica_percentage`, set `enabled` to `false`:

```bash
node metica_assignment.mjs disable --project <project-id>
```

Turn routing back on with the same experiment id:

```bash
node metica_assignment.mjs set --project <project-id> \
    --experiment-id <experiment-id> --enabled
```

## Tests

Offline: no Firebase calls and no credentials. Also runs without `npm install`.

```bash
cd <path-to-tools>
npm test
```
