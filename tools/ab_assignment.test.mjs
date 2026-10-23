// Offline tests for ab_assignment.mjs. Run: npm test

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

import * as ma from "./ab_assignment.mjs";

const GAME = "acme_blockpuzzle-20261016-";
const TESTDATA = new URL("./testdata/", import.meta.url);
const NEW = config("acme_blockpuzzle-20261101-90", "90");

function config(experimentId, percentage, enabled = "true") {
    return '{"experiment_id":"' + experimentId + '","metica_percentage":' + percentage + ',"enabled":' + enabled + "}";
}

function loadTemplate(fileName = "sample_template.json") {
    return JSON.parse(readFileSync(new URL(fileName, TESTDATA), "utf-8"));
}

// Validation
// Cases taken from Packages/com.abassignment.unity/Tests/Editor/ConfigTests.cs.

test("accepts valid experiment ids", () => {
    const cases = [
        [GAME + "50", "50", 5000],
        [GAME + "0", "0", 0],
        [GAME + "100", "100", 10000],
        [GAME + "12.5", "12.5", 1250],
        [GAME + "0.05", "0.05", 5],
        ["com.acme.blockpuzzle-20261016-50", "50", 5000],
    ];
    for (const [experimentId, percentage, basisPoints] of cases) {
        const fields = ma.validateConfig(config(experimentId, percentage));
        assert.equal(fields.get("experiment_id"), experimentId);
        assert.equal(ma.parseExperimentId(experimentId), basisPoints);
    }
});

test("rejects invalid experiment ids", () => {
    const cases = [
        ["Com.acme.blockpuzzle-20261016-50", "50"],
        ["com-acme-20261016-50", "50"],
        [GAME + "050", "50"],
        [GAME + "50.0", "50"],
        [GAME + "12.50", "12.5"],
        [GAME + "100.5", "100"],
        ["acme_blockpuzzle-20261332-50", "50"],
        ["acme_blockpuzzle-20260230-50", "50"],
        [GAME + "50", "90"],
        ["acme_blockpuzzle_2026_10", "50"],
        ["", "50"],
        [GAME + "50\\n", "50"],
    ];
    for (const [experimentId, percentage] of cases) {
        assert.throws(() => ma.validateConfig(config(experimentId, percentage)), ma.ConfigError,
            experimentId + " / " + percentage);
    }
});

test("game segment length limit", () => {
    assert.throws(() => ma.validateConfig(config("a".repeat(65) + "-20261016-50", "50")), ma.ConfigError);
    ma.validateConfig(config("a".repeat(64) + "-20261016-0.05", "0.05"));
});

test("rejects invalid values", () => {
    const cases = [
        '{"metica_percentage":50,"enabled":true}',
        '{"experiment_id":"' + GAME + '50","enabled":true}',
        '{"experiment_id":"' + GAME + '50","metica_percentage":50}',
        '{"experiment_id":50,"metica_percentage":50,"enabled":true}',
        '{"experiment_id":"' + GAME + '50","metica_percentage":"50","enabled":true}',
        '{"experiment_id":"' + GAME + '50","metica_percentage":true,"enabled":true}',
        '{"experiment_id":"' + GAME + '50","metica_percentage":50,"enabled":"true"}',
        '{"experiment_id":"' + GAME + '50","metica_percentage":50,"enabled":1}',
        '{"experiment_id":"' + GAME + '100","metica_percentage":100.5,"enabled":true}',
        '{"experiment_id":"' + GAME + '0","metica_percentage":-1,"enabled":true}',
        '{"experiment_id":"' + GAME + '12.5","metica_percentage":12.501,"enabled":true}',
        '{"experiment_id":"' + GAME + '50","experiment_id":"' + GAME + '50","metica_percentage":50,"enabled":true}',
        '{"experiment_id":"' + GAME + '50","metica_percentage":NaN,"enabled":true}',
        "[1,2]",
        '{"experiment_id":',
        config(GAME + "50", "50") + " x",
        "",
    ];
    for (const text of cases) {
        assert.throws(() => ma.validateConfig(text), ma.ConfigError, text);
    }
});

test("accepts unknown extra fields", () => {
    const text = '{"experiment_id":"' + GAME + '50","metica_percentage":50,"enabled":false,' +
        '"future_field":{"nested":[1,2,{"x":null}]},"other":"x"}';
    assert.equal(ma.validateConfig(text).get("enabled"), false);
});

test("nesting limit matches the game", () => {
    function nested(levels) {
        // The root object is one level; each extra list adds one.
        return '{"experiment_id":"' + GAME + '50","metica_percentage":50,"enabled":true,"x":' +
            "[".repeat(levels - 1) + "]".repeat(levels - 1) + "}";
    }
    ma.validateConfig(nested(64));
    for (const levels of [65, 5000]) {
        assert.throws(() => ma.validateConfig(nested(levels)), { name: "ConfigError", message: /nesting too deep/ });
    }
});

test("accepts equivalent number forms", () => {
    ma.validateConfig(config(GAME + "50", "50.00"));
    ma.validateConfig(config(GAME + "50", "5e1"));
});

test("mismatch error names both values", () => {
    assert.throws(() => ma.validateConfig(config(GAME + "50", "90")), { name: "ConfigError", message: /does not equal/ });
});

// Build value
test("format percentage is canonical", () => {
    const cases = { 0: "0", 5: "0.05", 10: "0.1", 1250: "12.5", 1255: "12.55", 5000: "50", 10000: "100" };
    for (const [basisPoints, text] of Object.entries(cases)) {
        assert.equal(ma.formatPercentage(Number(basisPoints)), text);
    }
});

test("parse percentage argument", () => {
    assert.equal(ma.parsePercentageArgument("50"), 5000);
    assert.equal(ma.parsePercentageArgument("50.00"), 5000);
    assert.equal(ma.parsePercentageArgument("12.5"), 1250);
    for (const bad of ["abc", "-1", "100.01", "12.345", "nan", "inf"]) {
        assert.throws(() => ma.parsePercentageArgument(bad), ma.ConfigError, bad);
    }
});

test("build value", () => {
    const experimentId = ma.buildExperimentId("acme", "blockpuzzle", "20261016", 1250);
    assert.equal(experimentId, "acme_blockpuzzle-20261016-12.5");
    assert.equal(ma.buildValue(experimentId, 1250, true),
        '{"experiment_id":"acme_blockpuzzle-20261016-12.5","metica_percentage":12.5,"enabled":true}');
    assert.equal(ma.buildValue(GAME + "50", 5000, false),
        '{"experiment_id":"acme_blockpuzzle-20261016-50","metica_percentage":50,"enabled":false}');
});

test("build value rejects bad segments", () => {
    for (const [studio, game] of [["Acme", "blockpuzzle"], ["acme", "block-puzzle"], ["acme", ""], ["", "blockpuzzle"]]) {
        assert.throws(() => ma.buildValue(ma.buildExperimentId(studio, game, "20261016", 5000), 5000, true),
            ma.ConfigError, studio + " / " + game);
    }
    assert.throws(() => ma.buildValue(ma.buildExperimentId("acme", "blockpuzzle", "20260230", 5000), 5000, true),
        ma.ConfigError);
});

test("disabled value keeps id, percentage and extra fields", () => {
    const old = '{"experiment_id":"' + GAME + '12.5","metica_percentage":12.5,"enabled":true,"note":"x"}';
    assert.deepEqual(JSON.parse(ma.disabledValue(old)),
        { experiment_id: GAME + "12.5", metica_percentage: 12.5, enabled: false, note: "x" });
});

test("disabled value keeps extra numbers exact", () => {
    const old = '{"experiment_id":"' + GAME + '50","metica_percentage":5e1,"enabled":true,' +
        '"a":1e2,"b":12345678901234567890.123456789,"c":[0.1,{"d":-0}]}';
    assert.equal(ma.disabledValue(old), '{"experiment_id":"' + GAME + '50","metica_percentage":5E+1,"enabled":false,' +
        '"a":1E+2,"b":12345678901234567890.123456789,"c":[0.1,{"d":-0}]}');
});

test("disabled value rejects invalid current value", () => {
    assert.throws(() => ma.disabledValue(config(GAME + "50", "90")), ma.ConfigError);
});

function setArgs(overrides) {
    return { studio: null, game: null, percentage: null, date: null, experimentId: null, enabled: true, ...overrides };
}

// Set arguments
test("from studio, game, percentage and date", () => {
    const value = ma.newValueForSet(setArgs({ studio: "acme", game: "blockpuzzle", percentage: "50", date: "20261016" }));
    assert.equal(value, config(GAME + "50", "50"));
});

test("default date is today", () => {
    const value = JSON.parse(ma.newValueForSet(setArgs({ studio: "acme", game: "blockpuzzle", percentage: "10" })));
    assert.match(value.experiment_id, /^acme_blockpuzzle-\d{8}-10$/);
});

test("from experiment id", () => {
    const value = ma.newValueForSet(setArgs({ experimentId: GAME + "12.5", enabled: false }));
    assert.equal(value, config(GAME + "12.5", "12.5", "false"));
});

test("experiment id with matching percentage", () => {
    ma.newValueForSet(setArgs({ experimentId: GAME + "50", percentage: "50.0" }));
});

test("rejects conflicting arguments", () => {
    const cases = [
        setArgs({ experimentId: GAME + "50", percentage: "90" }),
        setArgs({ experimentId: GAME + "50", studio: "acme" }),
        setArgs({ studio: "acme", game: "blockpuzzle" }),
        setArgs({ studio: "acme", game: "blockpuzzle", percentage: "50", date: "2026-10-16" }),
    ];
    for (const args of cases) {
        assert.throws(() => ma.newValueForSet(args), ma.ConfigError, JSON.stringify(args));
    }
});

// Template merge

test("updates parameter inside its group", () => {
    const template = loadTemplate();
    const updated = ma.applyValue(template, "metica_assignment", NEW, null);
    const parameter = updated.parameterGroups.Metica.parameters.metica_assignment;
    assert.deepEqual(parameter.defaultValue, { value: NEW });
    assert.equal(parameter.valueType, "JSON");
    assert.equal(parameter.description, "Studio note");
    assert.ok(!Object.hasOwn(updated.parameters, "metica_assignment"));
    ma.checkUntouched(template, updated, "metica_assignment");
});

test("keeps everything else", () => {
    const template = loadTemplate();
    const original = structuredClone(template);
    const updated = ma.applyValue(template, "metica_assignment", NEW, null);
    assert.deepEqual(template, original, "input template must not be modified");
    assert.deepEqual(updated.conditions, original.conditions);
    assert.deepEqual(updated.parameters, original.parameters);
    assert.deepEqual(updated.parameterGroups.Metica.parameters.metica_other,
        original.parameterGroups.Metica.parameters.metica_other);
    assert.equal(updated.parameterGroups.Metica.description, "Metica experiment");
    assert.ok(!Object.hasOwn(updated, "version"));
});

test("updates top-level parameter", () => {
    const template = loadTemplate();
    delete template.parameterGroups.Metica.parameters.metica_assignment;
    template.parameters.metica_assignment = { defaultValue: { useInAppDefault: true } };
    const updated = ma.applyValue(template, "metica_assignment", NEW, null);
    const parameter = updated.parameters.metica_assignment;
    assert.deepEqual(parameter.defaultValue, { value: NEW });
    assert.equal(parameter.valueType, "JSON");
    assert.equal(parameter.description, ma.DESCRIPTION);
    ma.checkUntouched(template, updated, "metica_assignment");
});

test("adds missing parameter at top level", () => {
    const template = loadTemplate();
    delete template.parameterGroups.Metica.parameters.metica_assignment;
    const updated = ma.applyValue(template, "metica_assignment", NEW, null);
    assert.deepEqual(updated.parameters.metica_assignment,
        { defaultValue: { value: NEW }, valueType: "JSON", description: ma.DESCRIPTION });
    ma.checkUntouched(template, updated, "metica_assignment");
});

test("adds to empty template", () => {
    const updated = ma.applyValue({}, "metica_assignment", NEW, null);
    assert.equal(updated.parameters.metica_assignment.defaultValue.value, NEW);
});

test("sets conditional value only", () => {
    const template = loadTemplate();
    const updated = ma.applyValue(template, "metica_assignment", NEW, "qa_devices");
    const parameter = updated.parameterGroups.Metica.parameters.metica_assignment;
    assert.deepEqual(parameter.conditionalValues.qa_devices, { value: NEW });
    assert.deepEqual(parameter.defaultValue, template.parameterGroups.Metica.parameters.metica_assignment.defaultValue);
    ma.checkUntouched(template, updated, "metica_assignment");
});

test("rejects unknown condition", () => {
    assert.throws(() => ma.applyValue(loadTemplate(), "metica_assignment", NEW, "no_such_condition"),
        { name: "TemplateError", message: /does not exist/ });
});

test("rejects condition on missing parameter", () => {
    const template = loadTemplate();
    delete template.parameterGroups.Metica.parameters.metica_assignment;
    assert.throws(() => ma.applyValue(template, "metica_assignment", NEW, "qa_devices"),
        { name: "TemplateError", message: /default value first/ });
});

test("rejects parameter in two places", () => {
    const template = loadTemplate();
    template.parameters.metica_assignment = { defaultValue: { value: "x" } };
    assert.throws(() => ma.applyValue(template, "metica_assignment", NEW, null), ma.TemplateError);
});

test("check untouched detects other changes", () => {
    const template = loadTemplate();
    const updated = ma.applyValue(template, "metica_assignment", NEW, null);
    updated.parameters.AdsSettings.defaultValue.value = "{}";
    assert.throws(() => ma.checkUntouched(template, updated, "metica_assignment"), ma.TemplateError);
});

test("diff shows only the parameter", () => {
    const template = loadTemplate();
    const updated = ma.applyValue(template, "metica_assignment", NEW, null);
    const diff = ma.parameterDiff(template, updated, "metica_assignment");
    assert.ok(diff.includes("20261101-90"));
    assert.ok(diff.includes('+  "valueType": "JSON"'));
    assert.ok(!diff.includes("AdsSettings"));
});

class FakeClient {
    // history: published templates, newest first, as Remote Config lists its versions.
    constructor(template, etag = "etag-1", history = []) {
        this.template = template;
        this.etag = etag;
        this.history = history;
        this.puts = [];
    }

    async listVersions(pageToken) {
        const start = pageToken === null ? 0 : Number(pageToken);
        const versions = this.history.slice(start, start + 2).map((_, i) => ({ versionNumber: String(start + i) }));
        const next = start + 2 < this.history.length ? String(start + 2) : undefined;
        return { versions: versions, nextPageToken: next };
    }

    async getVersion(versionNumber) {
        return structuredClone(this.history[Number(versionNumber)]);
    }

    async get() {
        return [structuredClone(this.template), this.etag];
    }

    async put(template, etag, validateOnly) {
        this.puts.push([structuredClone(template), etag, validateOnly]);
        const result = structuredClone(template);
        result.version = { versionNumber: "43" };
        return [result, "etag-2"];
    }
}

async function runQuietly(fn, ...args) {
    const lines = [];
    const log = console.log;
    console.log = (...parts) => {
        lines.push(parts.join(" "));
    };
    try {
        const code = await fn(...args);
        return [code, lines.join("\n")];
    } finally {
        console.log = log;
    }
}

// Publish
const NAME = "metica_assignment";

test("validates then publishes with etag", async () => {
    const client = new FakeClient(loadTemplate());
    const [code, out] = await runQuietly(ma.publish, client, NAME, null, () => NEW, false, true);
    assert.equal(code, 0);
    assert.deepEqual(client.puts.map((p) => p[2]), [true, false]);
    assert.deepEqual(client.puts.map((p) => p[1]), ["etag-1", "etag-1"]);
    const sent = client.puts[1][0];
    assert.equal(sent.parameterGroups.Metica.parameters[NAME].defaultValue.value, NEW);
    assert.deepEqual(sent.version, { description: "Set metica_assignment with ab_assignment.mjs" });
    assert.ok(out.includes("experiment_id changes"));
    assert.ok(out.includes("version 43"));
});

test("publishes to template without parameters", async () => {
    // A new project's template has no "parameters" key at all.
    const client = new FakeClient(loadTemplate("new_project_template.json"));
    const [code] = await runQuietly(ma.publish, client, NAME, null, () => NEW, false, true);
    assert.equal(code, 0);
    const sent = client.puts[1][0];
    assert.deepEqual(Object.keys(sent.parameters), [NAME]);
    assert.equal(sent.parameters[NAME].defaultValue.value, NEW);
});

test("dry run does not publish", async () => {
    const client = new FakeClient(loadTemplate());
    const [code, out] = await runQuietly(ma.publish, client, NAME, null, () => NEW, true, false);
    assert.equal(code, 0);
    assert.deepEqual(client.puts, []);
    assert.ok(out.includes("Dry run"));
});

test("no change does not publish", async () => {
    const template = loadTemplate();
    const parameter = template.parameterGroups.Metica.parameters[NAME];
    parameter.valueType = "JSON";
    const current = parameter.defaultValue.value;
    const client = new FakeClient(template);
    const [code, out] = await runQuietly(ma.publish, client, NAME, null, () => current, false, true);
    assert.equal(code, 0);
    assert.deepEqual(client.puts, []);
    assert.ok(out.includes("No change"));
});

test("disable keeps experiment", async () => {
    const client = new FakeClient(loadTemplate());
    const args = { parameter: NAME, condition: null, dryRun: false, yes: true, experimentId: null };
    const [code] = await runQuietly(ma.commandDisable, client, args);
    assert.equal(code, 0);
    const sent = JSON.parse(client.puts[1][0].parameterGroups.Metica.parameters[NAME].defaultValue.value);
    assert.deepEqual(sent, { experiment_id: GAME + "50", metica_percentage: 50, enabled: false });
});

test("disable fails without value", async () => {
    const template = loadTemplate();
    delete template.parameterGroups.Metica.parameters[NAME];
    const args = { parameter: NAME, condition: null, dryRun: false, yes: true, experimentId: null };
    await assert.rejects(runQuietly(ma.commandDisable, new FakeClient(template), args), ma.TemplateError);
});

test("get reports validity", async () => {
    let [code, out] = await runQuietly(ma.commandGet, new FakeClient(loadTemplate()), NAME);
    assert.equal(code, 0);
    assert.ok(out.includes('parameter group "Metica"'));
    assert.ok(out.includes("[valid]"));

    const template = loadTemplate();
    template.parameterGroups.Metica.parameters[NAME].defaultValue.value = config(GAME + "50", "90");
    [code, out] = await runQuietly(ma.commandGet, new FakeClient(template), NAME);
    assert.equal(code, 1);
    assert.ok(out.includes("INVALID"));
});

// Enable and experiment id protection
const TOGGLE_ARGS = { command: "disable", parameter: NAME, condition: null, dryRun: false, yes: true, experimentId: null };

test("enable keeps the experiment id and turns enabled on", async () => {
    const template = loadTemplate();
    template.parameterGroups.Metica.parameters[NAME].defaultValue.value = config(GAME + "50", "50", "false");
    const client = new FakeClient(template);
    const [code, out] = await runQuietly(ma.commandEnable, client, { ...TOGGLE_ARGS, command: "enable" });
    assert.equal(code, 0);
    const sent = JSON.parse(client.puts[1][0].parameterGroups.Metica.parameters[NAME].defaultValue.value);
    assert.deepEqual(sent, { experiment_id: GAME + "50", metica_percentage: 50, enabled: true });
    assert.ok(!out.includes("experiment_id changes"));
});

test("command line accepts enable", () => {
    assert.equal(ma.parseCommandLine(["enable", "--project", "p"]).command, "enable");
});

test("set refuses a new experiment id", async () => {
    const client = new FakeClient(loadTemplate());
    const args = { parameter: NAME, condition: null, dryRun: false, yes: true, newExperiment: false };
    await assert.rejects(runQuietly(ma.commandSet, client, args, config("acme_blockpuzzle-20261102-50", "50")),
        { name: "ConfigError", message: /--new-experiment/ });
    assert.deepEqual(client.puts, []);
});

test("set publishes a new experiment id with --new-experiment", async () => {
    const client = new FakeClient(loadTemplate());
    const args = ma.parseCommandLine(["set", "--project", "p", "--experiment-id", "acme_blockpuzzle-20261102-50",
        "--new-experiment", "--yes"]);
    const [code] = await runQuietly(ma.commandSet, client, args, config("acme_blockpuzzle-20261102-50", "50"));
    assert.equal(code, 0);
    const sent = JSON.parse(client.puts[1][0].parameterGroups.Metica.parameters[NAME].defaultValue.value);
    assert.equal(sent.experiment_id, "acme_blockpuzzle-20261102-50");
});

test("set keeps the published experiment id", async () => {
    const client = new FakeClient(loadTemplate());
    const args = ma.parseCommandLine(["set", "--project", "p", "--experiment-id", GAME + "50", "--disabled", "--yes"]);
    const [code] = await runQuietly(ma.commandSet, client, args, config(GAME + "50", "50", "false"));
    assert.equal(code, 0);
    const sent = JSON.parse(client.puts[1][0].parameterGroups.Metica.parameters[NAME].defaultValue.value);
    assert.deepEqual(sent, { experiment_id: GAME + "50", metica_percentage: 50, enabled: false });
});

// Disable with a broken value

test("disable with an experiment id repairs an invalid value", async () => {
    const template = loadTemplate();
    template.parameterGroups.Metica.parameters[NAME].defaultValue.value = config(GAME + "50", "90");
    const client = new FakeClient(template);
    const args = ma.parseCommandLine(["disable", "--project", "p", "--experiment-id", GAME + "12.5", "--yes"]);
    const [code] = await runQuietly(ma.commandDisable, client, args);
    assert.equal(code, 0);
    const sent = JSON.parse(client.puts[1][0].parameterGroups.Metica.parameters[NAME].defaultValue.value);
    assert.deepEqual(sent, { experiment_id: GAME + "12.5", metica_percentage: 12.5, enabled: false });
});

test("disable with an experiment id repairs a missing value", async () => {
    const template = loadTemplate();
    delete template.parameterGroups.Metica.parameters[NAME];
    const client = new FakeClient(template);
    const args = ma.parseCommandLine(["disable", "--project", "p", "--experiment-id", GAME + "50", "--yes"]);
    const [code] = await runQuietly(ma.commandDisable, client, args);
    assert.equal(code, 0);
    const sent = JSON.parse(client.puts[1][0].parameters[NAME].defaultValue.value);
    assert.deepEqual(sent, { experiment_id: GAME + "50", metica_percentage: 50, enabled: false });
});

test("disable without an experiment id explains an invalid value", async () => {
    const template = loadTemplate();
    template.parameterGroups.Metica.parameters[NAME].defaultValue.value = config(GAME + "50", "90");
    await assert.rejects(runQuietly(ma.commandDisable, new FakeClient(template), TOGGLE_ARGS),
        { name: "TemplateError", message: /--experiment-id/ });
});

test("disable keeps a valid value with its experiment id", async () => {
    const client = new FakeClient(loadTemplate());
    const args = ma.parseCommandLine(["disable", "--project", "p", "--experiment-id", GAME + "50", "--yes"]);
    const [code] = await runQuietly(ma.commandDisable, client, args);
    assert.equal(code, 0);
    const sent = JSON.parse(client.puts[1][0].parameterGroups.Metica.parameters[NAME].defaultValue.value);
    assert.deepEqual(sent, { experiment_id: GAME + "50", metica_percentage: 50, enabled: false });
});

test("disable rejects an experiment id that is not published", async () => {
    const args = { ...TOGGLE_ARGS, experimentId: GAME + "12.5" };
    await assert.rejects(runQuietly(ma.commandDisable, new FakeClient(loadTemplate()), args), ma.ConfigError);
});

// A missing or invalid published value: devices keep their last valid cached config

function templateWith(value) {
    const template = loadTemplate();
    if (value === null) {
        delete template.parameterGroups.Metica.parameters[NAME];
    } else {
        template.parameterGroups.Metica.parameters[NAME].defaultValue.value = value;
    }
    return template;
}

const BROKEN = config(GAME + "50", "90");

function brokenClient(lastValidId = GAME + "50") {
    const current = templateWith(BROKEN);
    const history = [current, templateWith("not json"), templateWith(config(lastValidId, lastValidId.split("-").pop()))];
    return new FakeClient(current, "etag-1", history);
}

test("set refuses a new experiment id when the published value is invalid", async () => {
    const client = brokenClient();
    const args = ma.parseCommandLine(["set", "--project", "p", "--experiment-id", "acme_blockpuzzle-20261102-50",
        "--yes"]);
    await assert.rejects(runQuietly(ma.commandSet, client, args, config("acme_blockpuzzle-20261102-50", "50")),
        { name: "ConfigError", message: new RegExp(GAME + "50[\\s\\S]*--new-experiment") });
    assert.deepEqual(client.puts, []);
});

test("set repairs an invalid value under the last valid experiment id", async () => {
    const client = brokenClient();
    const args = ma.parseCommandLine(["set", "--project", "p", "--experiment-id", GAME + "50", "--yes"]);
    const [code] = await runQuietly(ma.commandSet, client, args, config(GAME + "50", "50"));
    assert.equal(code, 0);
    assert.equal(client.puts.length, 2);
});

test("set on an invalid value publishes a new experiment id with --new-experiment", async () => {
    const client = brokenClient();
    const args = ma.parseCommandLine(["set", "--project", "p", "--experiment-id", "acme_blockpuzzle-20261102-50",
        "--new-experiment", "--yes"]);
    const [code] = await runQuietly(ma.commandSet, client, args, config("acme_blockpuzzle-20261102-50", "50"));
    assert.equal(code, 0);
});

test("set on an invalid value with no valid history publishes", async () => {
    const current = templateWith(BROKEN);
    const client = new FakeClient(current, "etag-1", [current, templateWith(null)]);
    const args = ma.parseCommandLine(["set", "--project", "p", "--experiment-id", "acme_blockpuzzle-20261102-50",
        "--yes"]);
    const [code] = await runQuietly(ma.commandSet, client, args, config("acme_blockpuzzle-20261102-50", "50"));
    assert.equal(code, 0);
});

test("set with a condition refuses a conditional value with another experiment id", async () => {
    const template = loadTemplate();
    template.parameterGroups.Metica.parameters[NAME].conditionalValues = { qa_devices: { value: config(GAME + "50", "50") } };
    const client = new FakeClient(template);
    const args = ma.parseCommandLine(["set", "--project", "p", "--experiment-id", "acme_blockpuzzle-20261102-50",
        "--condition", "qa_devices", "--yes"]);
    await assert.rejects(runQuietly(ma.commandSet, client, args, config("acme_blockpuzzle-20261102-50", "50")),
        { name: "ConfigError", message: /--new-experiment/ });
    assert.deepEqual(client.puts, []);
});

test("enable with another experiment id than the last valid one is refused", async () => {
    const client = brokenClient();
    const args = ma.parseCommandLine(["enable", "--project", "p", "--experiment-id", GAME + "12.5", "--yes"]);
    await assert.rejects(runQuietly(ma.commandEnable, client, args),
        { name: "ConfigError", message: new RegExp(GAME + "50") });
    assert.deepEqual(client.puts, []);
});

test("disable with another experiment id than the last valid one needs --new-experiment", async () => {
    let client = brokenClient();
    let args = ma.parseCommandLine(["disable", "--project", "p", "--experiment-id", GAME + "12.5", "--yes"]);
    await assert.rejects(runQuietly(ma.commandDisable, client, args), { name: "ConfigError" });
    assert.deepEqual(client.puts, []);

    client = brokenClient();
    args = ma.parseCommandLine(["disable", "--project", "p", "--experiment-id", GAME + "12.5", "--new-experiment",
        "--yes"]);
    const [code] = await runQuietly(ma.commandDisable, client, args);
    assert.equal(code, 0);
    const sent = JSON.parse(client.puts[1][0].parameterGroups.Metica.parameters[NAME].defaultValue.value);
    assert.deepEqual(sent, { experiment_id: GAME + "12.5", metica_percentage: 12.5, enabled: false });
});

test("enable with the last valid experiment id repairs an invalid value", async () => {
    const client = brokenClient();
    const args = ma.parseCommandLine(["enable", "--project", "p", "--experiment-id", GAME + "50", "--yes"]);
    const [code] = await runQuietly(ma.commandEnable, client, args);
    assert.equal(code, 0);
    const sent = JSON.parse(client.puts[1][0].parameterGroups.Metica.parameters[NAME].defaultValue.value);
    assert.deepEqual(sent, { experiment_id: GAME + "50", metica_percentage: 50, enabled: true });
});

test("enable with an experiment id repairs a missing value", async () => {
    const current = templateWith(null);
    const client = new FakeClient(current, "etag-1", [current]);
    const args = ma.parseCommandLine(["enable", "--project", "p", "--experiment-id", GAME + "50", "--yes"]);
    const [code] = await runQuietly(ma.commandEnable, client, args);
    assert.equal(code, 0);
    const sent = JSON.parse(client.puts[1][0].parameters[NAME].defaultValue.value);
    assert.deepEqual(sent, { experiment_id: GAME + "50", metica_percentage: 50, enabled: true });
});

test("enable without an experiment id names the last valid one", async () => {
    const args = { ...TOGGLE_ARGS, command: "enable" };
    await assert.rejects(runQuietly(ma.commandEnable, brokenClient(), args),
        { name: "TemplateError", message: new RegExp("--experiment-id " + GAME + "50") });
});

test("enable without an experiment id explains a missing value", async () => {
    const current = templateWith(null);
    const args = { ...TOGGLE_ARGS, command: "enable" };
    await assert.rejects(runQuietly(ma.commandEnable, new FakeClient(current, "etag-1", [current]), args),
        { name: "TemplateError", message: /no published value[\s\S]*--experiment-id/ });
});

test("enable on an enabled value changes nothing", async () => {
    const template = loadTemplate();
    template.parameterGroups.Metica.parameters[NAME].valueType = "JSON";
    const client = new FakeClient(template);
    const [code, out] = await runQuietly(ma.commandEnable, client, { ...TOGGLE_ARGS, command: "enable" });
    assert.equal(code, 0);
    assert.ok(out.includes("No change"));
    assert.deepEqual(client.puts, []);
});

test("enable checks --experiment-id before loading credentials", async () => {
    const errors = [];
    const error = console.error;
    console.error = (...parts) => {
        errors.push(parts.join(" "));
    };
    try {
        const code = await ma.main(["enable", "--project", "p", "--experiment-id", "not-an-id",
            "--credentials", "/nonexistent/key.json"]);
        assert.equal(code, 2);
    } finally {
        console.error = error;
    }
    assert.match(errors.join("\n"), /does not match/);
});
