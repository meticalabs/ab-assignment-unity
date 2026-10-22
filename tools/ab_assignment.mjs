#!/usr/bin/env node
// Read and update the metica_assignment Firebase Remote Config parameter.
//
// Uses the Remote Config REST API (GET/PUT .../v1/projects/<id>/remoteConfig with ETag/If-Match).
// Only the one parameter is changed; every other parameter, condition and parameter group is sent back as read.
// Values are validated with the same rules as the game (com.abassignment.unity AssignmentConfig.TryParse).
// See README.md.

import { realpathSync } from "node:fs";
import { createInterface } from "node:readline";
import { fileURLToPath } from "node:url";
import { isDeepStrictEqual, parseArgs } from "node:util";

export const DEFAULT_PARAMETER = "metica_assignment";
const SCOPES = ["https://www.googleapis.com/auth/firebase.remoteconfig"];
const VALUE_TYPE = "JSON";
export const DESCRIPTION = "Metica on/off experiment. Edit only per the AB Assignment guide. Do not add conditions.";
const SCRIPT_NAME = "ab_assignment.mjs";

// Same limit as Json.cs MaxDepth: each object or array adds one level.
const MAX_JSON_DEPTH = 64;

// Same pattern as AssignmentConfig.ExperimentIdPattern. Without the m flag, $ is the end of the string,
// like \z in .NET, so a trailing newline is rejected.
const EXPERIMENT_ID_PATTERN = /^[a-z0-9._]{1,64}-([0-9]{8})-(100|[1-9]?[0-9](?:\.[0-9]?[1-9])?)$/;

export class ConfigError extends Error {
    // A value or argument that breaks the metica_assignment rules.
    name = "ConfigError";
}

export class TemplateError extends Error {
    // A Remote Config template that cannot be updated safely, or a rejected API request.
    name = "TemplateError";
}

export class ClientError extends Error {
    // Missing packages, unusable credentials or a failed connection.
    name = "ClientError";
}

class UsageError extends Error {
    // Bad command line, reported like Python's argparse.
    name = "UsageError";
}

// ---------------------------------------------------------------------------
// Decimal numbers (the parts of Python's decimal.Decimal that the tool needs)
// ---------------------------------------------------------------------------

// Finite numbers as Python's Decimal(text) reads them, after whitespace and underscores are removed.
const DECIMAL_PATTERN = /^([-+])?(?=\.?[0-9])([0-9]*)(?:\.([0-9]*))?(?:e([-+]?[0-9]+))?$/i;
const NON_FINITE_PATTERN = /^[-+]?(?:inf(?:inity)?|s?nan[0-9]*)$/i;

// A finite decimal number stored like Python's Decimal: sign, coefficient digits and exponent.
// JSON numbers are kept in this form, so they are compared exactly and written back as Python's json module writes them.
export class Decimal {
    constructor(negative, digits, exponent) {
        this.negative = negative;
        this.digits = digits;
        this.exponent = exponent;
    }

    // Same text as str(Decimal) in Python: 50, 12.5, 5E+1, 1E-7, -0.
    toString() {
        const length = BigInt(this.digits.length);
        const leftDigits = this.exponent + length;
        let dotPlace = 1n;
        if (this.exponent <= 0n && leftDigits > -6n) {
            dotPlace = leftDigits;
        }
        let intPart = this.digits;
        let fracPart = "";
        if (dotPlace <= 0n) {
            intPart = "0";
            fracPart = "." + "0".repeat(Number(-dotPlace)) + this.digits;
        } else if (dotPlace < length) {
            intPart = this.digits.slice(0, Number(dotPlace));
            fracPart = "." + this.digits.slice(Number(dotPlace));
        }
        let exponentPart = "";
        if (leftDigits !== dotPlace) {
            const adjusted = leftDigits - dotPlace;
            exponentPart = "E" + adjusted;
            if (adjusted >= 0n) {
                exponentPart = "E+" + adjusted;
            }
        }
        let sign = "";
        if (this.negative) {
            sign = "-";
        }
        return sign + intPart + fracPart + exponentPart;
    }
}

// Returns the Decimal for finite number text, or null if the text is not one.
function parseDecimal(text) {
    const match = DECIMAL_PATTERN.exec(text);
    if (match === null) {
        return null;
    }
    const intPart = match[2];
    const fracPart = match[3] ?? "";
    const digits = (intPart + fracPart).replace(/^0+/, "") || "0";
    const exponent = BigInt(match[4] ?? "0") - BigInt(fracPart.length);
    return new Decimal(match[1] === "-", digits, exponent);
}

// ---------------------------------------------------------------------------
// Validation (mirrors AssignmentConfig.TryParse and Json.cs)
// ---------------------------------------------------------------------------

const JSON_WHITESPACE = " \t\n\r";
const JSON_NUMBER = /-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][-+]?[0-9]+)?/y;
const JSON_ESCAPES = { '"': '"', "\\": "\\", "/": "/", b: "\b", f: "\f", n: "\n", r: "\r", t: "\t" };

function jsonError(message, text, position) {
    const line = text.slice(0, position).split("\n").length;
    let lastNewline = -1;
    if (position > 0) {
        lastNewline = text.lastIndexOf("\n", position - 1);
    }
    return new ConfigError("config value is not valid JSON: " + message + ": line " + line + " column " +
        (position - lastNewline) + " (char " + position + ")");
}

// Parses like Json.cs: objects as Map (key order kept), numbers as Decimal,
// duplicate keys, NaN/Infinity and deep nesting rejected. Error messages follow Python's json module.
class StrictJsonParser {
    constructor(text) {
        this.text = text;
        this.position = 0;
        this.depth = 0;
    }

    parse() {
        this.skipWhitespace();
        const value = this.parseValue();
        this.skipWhitespace();
        if (this.position !== this.text.length) {
            throw jsonError("Extra data", this.text, this.position);
        }
        return value;
    }

    skipWhitespace() {
        while (this.position < this.text.length && JSON_WHITESPACE.includes(this.text[this.position])) {
            this.position++;
        }
    }

    startsWith(literal) {
        return this.text.startsWith(literal, this.position);
    }

    parseValue() {
        const c = this.text[this.position];
        if (c === '"') {
            return this.parseString();
        }
        if (c === "{") {
            return this.parseObject();
        }
        if (c === "[") {
            return this.parseArray();
        }
        for (const [literal, value] of [["null", null], ["true", true], ["false", false]]) {
            if (this.startsWith(literal)) {
                this.position += literal.length;
                return value;
            }
        }
        JSON_NUMBER.lastIndex = this.position;
        const number = JSON_NUMBER.exec(this.text);
        if (number !== null) {
            this.position += number[0].length;
            return parseDecimal(number[0]);
        }
        for (const constant of ["NaN", "Infinity", "-Infinity"]) {
            if (this.startsWith(constant)) {
                throw new ConfigError("config value is not valid JSON: " + constant + " is not allowed");
            }
        }
        throw jsonError("Expecting value", this.text, this.position);
    }

    enter() {
        this.depth++;
        if (this.depth > MAX_JSON_DEPTH) {
            throw new ConfigError("config value is not valid JSON: nesting too deep");
        }
        this.position++;
        this.skipWhitespace();
    }

    parseObject() {
        this.enter();
        const pairs = [];
        if (this.text[this.position] === "}") {
            this.position++;
        } else {
            while (true) {
                if (this.text[this.position] !== '"') {
                    throw jsonError("Expecting property name enclosed in double quotes", this.text, this.position);
                }
                const key = this.parseString();
                this.skipWhitespace();
                if (this.text[this.position] !== ":") {
                    throw jsonError("Expecting ':' delimiter", this.text, this.position);
                }
                this.position++;
                this.skipWhitespace();
                pairs.push([key, this.parseValue()]);
                this.skipWhitespace();
                const next = this.text[this.position];
                this.position++;
                if (next === "}") {
                    break;
                }
                if (next !== ",") {
                    throw jsonError("Expecting ',' delimiter", this.text, this.position - 1);
                }
                this.skipWhitespace();
            }
        }
        this.depth--;
        // Checked when the object is complete, like Python's object_pairs_hook.
        const result = new Map();
        for (const [key, value] of pairs) {
            if (result.has(key)) {
                throw new ConfigError('config value is not valid JSON: duplicate key "' + key + '"');
            }
            result.set(key, value);
        }
        return result;
    }

    parseArray() {
        this.enter();
        const values = [];
        if (this.text[this.position] === "]") {
            this.position++;
        } else {
            while (true) {
                values.push(this.parseValue());
                this.skipWhitespace();
                const next = this.text[this.position];
                this.position++;
                if (next === "]") {
                    break;
                }
                if (next !== ",") {
                    throw jsonError("Expecting ',' delimiter", this.text, this.position - 1);
                }
                this.skipWhitespace();
            }
        }
        this.depth--;
        return values;
    }

    parseString() {
        const begin = this.position;
        this.position++;
        let result = "";
        while (true) {
            if (this.position >= this.text.length) {
                throw jsonError("Unterminated string starting at", this.text, begin);
            }
            const c = this.text[this.position];
            if (c === '"') {
                this.position++;
                return result;
            }
            if (c < " ") {
                throw jsonError("Invalid control character at", this.text, this.position);
            }
            if (c !== "\\") {
                result += c;
                this.position++;
                continue;
            }
            const escape = this.text[this.position + 1];
            if (escape === undefined) {
                throw jsonError("Unterminated string starting at", this.text, begin);
            }
            if (escape === "u") {
                const hex = this.text.slice(this.position + 2, this.position + 6);
                if (!/^[0-9a-fA-F]{4}$/.test(hex)) {
                    throw jsonError("Invalid \\uXXXX escape", this.text, this.position + 1);
                }
                // Surrogate pairs come out right, because each \uXXXX is one UTF-16 code unit.
                result += String.fromCharCode(parseInt(hex, 16));
                this.position += 6;
                continue;
            }
            if (!Object.hasOwn(JSON_ESCAPES, escape)) {
                throw jsonError("Invalid \\escape", this.text, this.position);
            }
            result += JSON_ESCAPES[escape];
            this.position += 2;
        }
    }
}

function parseStrictJson(text) {
    return new StrictJsonParser(text).parse();
}

const PYTHON_STRING_ESCAPES = { '"': '\\"', "\\": "\\\\", "\b": "\\b", "\f": "\\f", "\n": "\\n", "\r": "\\r", "\t": "\\t" };

// Same text as Python's json.dumps(string): everything outside printable ASCII is written as \uXXXX.
function pythonJsonString(text) {
    let result = '"';
    for (const c of text.split("")) {
        const code = c.charCodeAt(0);
        if (Object.hasOwn(PYTHON_STRING_ESCAPES, c)) {
            result += PYTHON_STRING_ESCAPES[c];
        } else if (code < 0x20 || code > 0x7e) {
            result += "\\u" + code.toString(16).padStart(4, "0");
        } else {
            result += c;
        }
    }
    return result + '"';
}

// Compact JSON that writes Decimal numbers as Python's json module does, so extra fields are not changed.
function writeJson(value) {
    if (value instanceof Map) {
        const items = [];
        for (const [key, item] of value) {
            items.push(pythonJsonString(key) + ":" + writeJson(item));
        }
        return "{" + items.join(",") + "}";
    }
    if (Array.isArray(value)) {
        return "[" + value.map(writeJson).join(",") + "]";
    }
    if (value instanceof Decimal) {
        return value.toString();
    }
    if (typeof value === "string") {
        return pythonJsonString(value);
    }
    return JSON.stringify(value);
}

// Python's default Decimal context: 28 significant digits, and no exponent below Emin - precision + 1.
const DECIMAL_PRECISION = 28n;
const DECIMAL_ETINY = -999999n - DECIMAL_PRECISION + 1n;

// Drops the last `drop` digits of a coefficient with `length` digits, rounding half to even like Decimal.
function roundHalfEven(coefficient, length, drop) {
    if (drop > BigInt(length)) {
        return 0n;
    }
    const divisor = 10n ** drop;
    let quotient = coefficient / divisor;
    const remainder = coefficient % divisor;
    if (2n * remainder > divisor || (2n * remainder === divisor && quotient % 2n === 1n)) {
        quotient++;
    }
    return quotient;
}

// Converts a 0..100 percentage with at most two decimals to basis points.
export function percentageToBasisPoints(percentage, raw) {
    if (percentage.digits === "0") {
        return 0;
    }
    if (percentage.negative) {
        throw new ConfigError("metica_percentage " + raw + " is not between 0 and 100");
    }
    // The value is at least 10^adjusted and below 10^(adjusted + 1). Of the values from 100 up, only 100 is allowed.
    const adjusted = percentage.exponent + BigInt(percentage.digits.length) - 1n;
    if (adjusted > 2n || (adjusted === 2n && !/^10*$/.test(percentage.digits))) {
        throw new ConfigError("metica_percentage " + raw + " is not between 0 and 100");
    }

    // Multiplies by 100 as Python does in the default Decimal context, so a value with more than
    // 28 significant digits is rounded before the whole-number check (C# decimal also keeps only 28-29 digits).
    let coefficient = BigInt(percentage.digits);
    let exponent = percentage.exponent + 2n;
    let smallestExponent = BigInt(percentage.digits.length) + exponent - DECIMAL_PRECISION;
    if (smallestExponent < DECIMAL_ETINY) {
        smallestExponent = DECIMAL_ETINY;
    }
    if (exponent < smallestExponent) {
        coefficient = roundHalfEven(coefficient, percentage.digits.length, smallestExponent - exponent);
        exponent = smallestExponent;
    }
    if (coefficient === 0n) {
        return 0;
    }
    while (coefficient % 10n === 0n) {
        coefficient /= 10n;
        exponent++;
    }
    if (exponent < 0n) {
        throw new ConfigError("metica_percentage " + raw + " has more than two decimals");
    }
    return Number(coefficient * 10n ** exponent);
}

// Canonical percentage text: no leading zeros, no trailing decimal zeros (50, 12.5, 0.05, 100).
export function formatPercentage(basisPoints) {
    const whole = Math.floor(basisPoints / 100);
    const fraction = basisPoints % 100;
    if (fraction === 0) {
        return String(whole);
    }
    return (whole + "." + String(fraction).padStart(2, "0")).replace(/0+$/, "");
}

// Same check as Python's datetime.strptime(datestamp, "%Y%m%d") on 8 digits.
function isCalendarDate(datestamp) {
    const year = Number(datestamp.slice(0, 4));
    const month = Number(datestamp.slice(4, 6));
    const day = Number(datestamp.slice(6, 8));
    if (year < 1 || month < 1 || month > 12 || day < 1) {
        return false;
    }
    const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
    const daysInMonth = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
    if (leap) {
        daysInMonth[1] = 29;
    }
    return day <= daysInMonth[month - 1];
}

// Checks the experiment id format and returns its percentage segment in basis points.
export function parseExperimentId(experimentId) {
    const match = EXPERIMENT_ID_PATTERN.exec(experimentId);
    if (match === null) {
        throw new ConfigError('experiment_id "' + experimentId +
            '" does not match <studio>_<game>-<YYYYMMDD>-<metica_percentage>');
    }
    const datestamp = match[1];
    if (!isCalendarDate(datestamp)) {
        throw new ConfigError('experiment_id datestamp "' + datestamp + '" is not a real calendar date');
    }
    return percentageToBasisPoints(parseDecimal(match[2]), match[2]);
}

// Validates a metica_assignment value like the game does. Returns the parsed object (a Map) or throws ConfigError.
export function validateConfig(text) {
    if (!text) {
        throw new ConfigError("config value is empty");
    }
    const root = parseStrictJson(text);
    if (!(root instanceof Map)) {
        throw new ConfigError("config value is not a JSON object");
    }

    if (!root.has("experiment_id")) {
        throw new ConfigError("experiment_id is missing");
    }
    const experimentId = root.get("experiment_id");
    if (typeof experimentId !== "string") {
        throw new ConfigError("experiment_id is not a string");
    }

    if (!root.has("metica_percentage")) {
        throw new ConfigError("metica_percentage is missing");
    }
    const percentage = root.get("metica_percentage");
    if (!(percentage instanceof Decimal)) {
        throw new ConfigError("metica_percentage is not a number");
    }
    const percentageBasisPoints = percentageToBasisPoints(percentage, percentage.toString());

    if (!root.has("enabled")) {
        throw new ConfigError("enabled is missing");
    }
    if (typeof root.get("enabled") !== "boolean") {
        throw new ConfigError("enabled is not a bool");
    }

    const idBasisPoints = parseExperimentId(experimentId);
    if (idBasisPoints !== percentageBasisPoints) {
        throw new ConfigError('the percentage segment of experiment_id "' + experimentId +
            '" does not equal metica_percentage ' + percentage.toString());
    }
    return root;
}

// ---------------------------------------------------------------------------
// Building values
// ---------------------------------------------------------------------------

export function buildExperimentId(studio, game, date, basisPoints) {
    if (!studio || !game) {
        throw new ConfigError("--studio and --game must not be empty");
    }
    return studio + "_" + game + "-" + date + "-" + formatPercentage(basisPoints);
}

export function parsePercentageArgument(text) {
    const cleaned = text.trim().replaceAll("_", "");
    if (NON_FINITE_PATTERN.test(cleaned)) {
        throw new ConfigError("metica_percentage " + text + " is out of range");
    }
    const percentage = parseDecimal(cleaned);
    if (percentage === null) {
        throw new ConfigError("--percentage " + text + " is not a number");
    }
    return percentageToBasisPoints(percentage, text);
}

// Returns the compact JSON value, validated.
export function buildValue(experimentId, basisPoints, enabled) {
    const text = JSON.stringify({
        experiment_id: experimentId,
        metica_percentage: Number(formatPercentage(basisPoints)),
        enabled: enabled,
    });
    validateConfig(text);
    return text;
}

// Returns the current value with another enabled flag. experiment_id, metica_percentage and extra fields stay.
function valueWithEnabled(current, enabled) {
    const fields = validateConfig(current);
    fields.set("enabled", enabled);
    const text = writeJson(fields);
    validateConfig(text);
    return text;
}

// Returns the current value with enabled set to false. experiment_id, metica_percentage and extra fields stay.
export function disabledValue(current) {
    return valueWithEnabled(current, false);
}

// ---------------------------------------------------------------------------
// Template handling
// ---------------------------------------------------------------------------

// Returns [parameter, group name or null]. Throws if the name exists in more than one place.
export function findParameter(template, name) {
    const found = [];
    const parameters = template.parameters ?? {};
    if (Object.hasOwn(parameters, name)) {
        found.push([parameters[name], null]);
    }
    for (const [groupName, group] of Object.entries(template.parameterGroups ?? {})) {
        const groupParameters = group.parameters ?? {};
        if (Object.hasOwn(groupParameters, name)) {
            found.push([groupParameters[name], groupName]);
        }
    }
    if (found.length > 1) {
        throw new TemplateError('parameter "' + name + '" exists in more than one place in the template');
    }
    if (found.length === 0) {
        return [null, null];
    }
    return found[0];
}

export function describeLocation(group) {
    if (group === null) {
        return "top-level parameters";
    }
    return 'parameter group "' + group + '"';
}

// Returns the published value for the default or the given condition, or null if there is none.
export function currentValue(parameter, condition) {
    let entry = parameter.defaultValue ?? {};
    if (condition !== null) {
        const conditionalValues = parameter.conditionalValues ?? {};
        entry = {};
        if (Object.hasOwn(conditionalValues, condition)) {
            entry = conditionalValues[condition];
        }
    }
    return entry.value ?? null;
}

// Returns a copy of the template where only the named parameter carries the new value.
//
// The parameter stays where it is (top level or its group). A new parameter is added at the top level.
// Without a condition the default value is set; with one, only that conditional value is set.
// Other conditional values of the parameter are kept.
export function applyValue(template, name, value, condition) {
    const updated = structuredClone(template);
    delete updated.version;
    let [parameter] = findParameter(updated, name);

    if (condition !== null) {
        const known = (updated.conditions ?? []).map((c) => c.name);
        if (!known.includes(condition)) {
            throw new TemplateError('condition "' + condition + '" does not exist in the template; create it first');
        }
        if (parameter === null) {
            throw new TemplateError('parameter "' + name + '" does not exist yet; set its default value first ' +
                "(run without --condition)");
        }
    }

    if (parameter === null) {
        parameter = {};
        updated.parameters ??= {};
        updated.parameters[name] = parameter;
    }

    if (condition === null) {
        parameter.defaultValue = { value: value };
    } else {
        parameter.conditionalValues ??= {};
        parameter.conditionalValues[condition] = { value: value };
    }
    parameter.valueType = VALUE_TYPE;
    if (!parameter.description) {
        parameter.description = DESCRIPTION;
    }
    return updated;
}

// Same text as Python's json.dumps(value, indent=2, sort_keys=True).
function pythonJsonDumps(value, level) {
    const inner = " ".repeat(2 * (level + 1));
    const outer = " ".repeat(2 * level);
    if (Array.isArray(value)) {
        if (value.length === 0) {
            return "[]";
        }
        return "[\n" + value.map((item) => inner + pythonJsonDumps(item, level + 1)).join(",\n") + "\n" + outer + "]";
    }
    if (value !== null && typeof value === "object") {
        const keys = Object.keys(value).sort();
        if (keys.length === 0) {
            return "{}";
        }
        const items = keys.map((key) => inner + pythonJsonString(key) + ": " + pythonJsonDumps(value[key], level + 1));
        return "{\n" + items.join(",\n") + "\n" + outer + "}";
    }
    if (typeof value === "string") {
        return pythonJsonString(value);
    }
    return JSON.stringify(value);
}

// Port of Python's difflib.SequenceMatcher(None, a, b).get_matching_blocks(), used to build the diff.
function matchingBlocks(a, b) {
    const b2j = new Map();
    b.forEach((line, j) => {
        if (!b2j.has(line)) {
            b2j.set(line, []);
        }
        b2j.get(line).push(j);
    });
    // difflib's autojunk: in sequences of 200 or more lines, very common lines are not used to start a match.
    if (b.length >= 200) {
        const limit = Math.floor(b.length / 100) + 1;
        for (const [line, indexes] of b2j) {
            if (indexes.length > limit) {
                b2j.delete(line);
            }
        }
    }

    function longestMatch(alo, ahi, blo, bhi) {
        let bestI = alo;
        let bestJ = blo;
        let bestSize = 0;
        let j2len = new Map();
        for (let i = alo; i < ahi; i++) {
            const newJ2len = new Map();
            for (const j of b2j.get(a[i]) ?? []) {
                if (j < blo) {
                    continue;
                }
                if (j >= bhi) {
                    break;
                }
                const k = (j2len.get(j - 1) ?? 0) + 1;
                newJ2len.set(j, k);
                if (k > bestSize) {
                    bestI = i - k + 1;
                    bestJ = j - k + 1;
                    bestSize = k;
                }
            }
            j2len = newJ2len;
        }
        while (bestI > alo && bestJ > blo && a[bestI - 1] === b[bestJ - 1]) {
            bestI--;
            bestJ--;
            bestSize++;
        }
        while (bestI + bestSize < ahi && bestJ + bestSize < bhi && a[bestI + bestSize] === b[bestJ + bestSize]) {
            bestSize++;
        }
        return [bestI, bestJ, bestSize];
    }

    const queue = [[0, a.length, 0, b.length]];
    const blocks = [];
    while (queue.length > 0) {
        const [alo, ahi, blo, bhi] = queue.pop();
        const [i, j, k] = longestMatch(alo, ahi, blo, bhi);
        if (k > 0) {
            blocks.push([i, j, k]);
            if (alo < i && blo < j) {
                queue.push([alo, i, blo, j]);
            }
            if (i + k < ahi && j + k < bhi) {
                queue.push([i + k, ahi, j + k, bhi]);
            }
        }
    }
    blocks.sort((x, y) => x[0] - y[0] || x[1] - y[1]);

    const merged = [];
    let [i1, j1, k1] = [0, 0, 0];
    for (const [i2, j2, k2] of blocks) {
        if (i1 + k1 === i2 && j1 + k1 === j2) {
            k1 += k2;
        } else {
            if (k1 > 0) {
                merged.push([i1, j1, k1]);
            }
            [i1, j1, k1] = [i2, j2, k2];
        }
    }
    if (k1 > 0) {
        merged.push([i1, j1, k1]);
    }
    merged.push([a.length, b.length, 0]);
    return merged;
}

// Port of difflib's get_opcodes() and get_grouped_opcodes(n).
function groupedOpcodes(a, b, n) {
    let codes = [];
    let i = 0;
    let j = 0;
    for (const [ai, bj, size] of matchingBlocks(a, b)) {
        let tag = "";
        if (i < ai && j < bj) {
            tag = "replace";
        } else if (i < ai) {
            tag = "delete";
        } else if (j < bj) {
            tag = "insert";
        }
        if (tag) {
            codes.push([tag, i, ai, j, bj]);
        }
        i = ai + size;
        j = bj + size;
        if (size > 0) {
            codes.push(["equal", ai, i, bj, j]);
        }
    }

    if (codes.length === 0) {
        codes = [["equal", 0, 1, 0, 1]];
    }
    if (codes[0][0] === "equal") {
        const [tag, i1, i2, j1, j2] = codes[0];
        codes[0] = [tag, Math.max(i1, i2 - n), i2, Math.max(j1, j2 - n), j2];
    }
    const last = codes.length - 1;
    if (codes[last][0] === "equal") {
        const [tag, i1, i2, j1, j2] = codes[last];
        codes[last] = [tag, i1, Math.min(i2, i1 + n), j1, Math.min(j2, j1 + n)];
    }

    const groups = [];
    let group = [];
    for (let [tag, i1, i2, j1, j2] of codes) {
        if (tag === "equal" && i2 - i1 > 2 * n) {
            group.push([tag, i1, Math.min(i2, i1 + n), j1, Math.min(j2, j1 + n)]);
            groups.push(group);
            group = [];
            i1 = Math.max(i1, i2 - n);
            j1 = Math.max(j1, j2 - n);
        }
        group.push([tag, i1, i2, j1, j2]);
    }
    if (group.length > 0 && !(group.length === 1 && group[0][0] === "equal")) {
        groups.push(group);
    }
    return groups;
}

function formatRangeUnified(start, stop) {
    let beginning = start + 1;
    const length = stop - start;
    if (length === 1) {
        return String(beginning);
    }
    if (length === 0) {
        beginning--;
    }
    return beginning + "," + length;
}

// Port of difflib.unified_diff(a, b, fromFile, toFile) with 3 lines of context. Lines keep their "\n".
function unifiedDiff(a, b, fromFile, toFile) {
    const lines = [];
    for (const group of groupedOpcodes(a, b, 3)) {
        if (lines.length === 0) {
            lines.push("--- " + fromFile + "\n", "+++ " + toFile + "\n");
        }
        const first = group[0];
        const last = group[group.length - 1];
        lines.push("@@ -" + formatRangeUnified(first[1], last[2]) + " +" + formatRangeUnified(first[3], last[4]) + " @@\n");
        for (const [tag, i1, i2, j1, j2] of group) {
            if (tag === "equal") {
                lines.push(...a.slice(i1, i2).map((line) => " " + line));
                continue;
            }
            if (tag === "replace" || tag === "delete") {
                lines.push(...a.slice(i1, i2).map((line) => "-" + line));
            }
            if (tag === "replace" || tag === "insert") {
                lines.push(...b.slice(j1, j2).map((line) => "+" + line));
            }
        }
    }
    return lines.join("");
}

// Unified diff of the named parameter between two templates.
export function parameterDiff(before, after, name) {
    function render(template) {
        const [parameter, group] = findParameter(template, name);
        if (parameter === null) {
            return [];
        }
        const text = "# " + describeLocation(group) + "\n" + pythonJsonDumps(parameter, 0) + "\n";
        return text.split(/(?<=\n)/);
    }

    return unifiedDiff(render(before), render(after), "published", "new");
}

// Fails if anything other than the named parameter differs between the two templates.
// A missing "parameters" key counts as empty, because adding the parameter to a template without one creates it.
export function checkUntouched(before, after, name) {
    function strip(template) {
        const result = structuredClone(template);
        delete result.version;
        result.parameters ??= {};
        delete result.parameters[name];
        for (const group of Object.values(result.parameterGroups ?? {})) {
            delete (group.parameters ?? {})[name];
        }
        return result;
    }

    if (!isDeepStrictEqual(strip(before), strip(after))) {
        throw new TemplateError("internal error: the update would change more than " + name);
    }
}

// ---------------------------------------------------------------------------
// Remote Config REST API
// ---------------------------------------------------------------------------

function describeError(error) {
    if (error.cause instanceof Error) {
        return error.message + ": " + error.cause.message;
    }
    return error.message;
}

export class RemoteConfigClient {
    static async create(project, credentialsPath) {
        // Imported here so validation and tests work without google-auth-library installed.
        let GoogleAuth;
        try {
            ({ GoogleAuth } = await import("google-auth-library"));
        } catch (error) {
            if (error.code !== "ERR_MODULE_NOT_FOUND") {
                throw error;
            }
            throw new ClientError(error.message + ". Run: npm install");
        }

        // Without --credentials, GoogleAuth uses GOOGLE_APPLICATION_CREDENTIALS, else gcloud application-default credentials.
        const options = { scopes: SCOPES };
        if (credentialsPath) {
            options.keyFilename = credentialsPath;
        }
        const auth = new GoogleAuth(options);
        try {
            await auth.getClient();
        } catch (error) {
            throw new ClientError("cannot load credentials: " + describeError(error));
        }
        return new RemoteConfigClient(auth, project);
    }

    constructor(auth, project) {
        this.auth = auth;
        this.url = "https://firebaseremoteconfig.googleapis.com/v1/projects/" + project + "/remoteConfig";
    }

    async get() {
        const response = await this.send("GET", this.url, {}, undefined);
        const etag = response.headers.get("ETag");
        if (!etag) {
            throw new TemplateError("GET response has no ETag header");
        }
        return [responseJson(response.text, "GET"), etag];
    }

    async put(template, etag, validateOnly) {
        let url = this.url;
        if (validateOnly) {
            url += "?validateOnly=true";
        }
        const response = await this.send("PUT", url, {
            "Content-Type": "application/json; UTF-8",
            "If-Match": etag,
        }, JSON.stringify(template));
        return [responseJson(response.text, "PUT"), response.headers.get("ETag") ?? ""];
    }

    async send(method, url, extraHeaders, body) {
        let response;
        let text;
        try {
            // Includes the quota project (x-goog-user-project) when the credentials have one.
            const headers = new Headers(await this.auth.getRequestHeaders(url));
            for (const [key, value] of Object.entries(extraHeaders)) {
                headers.set(key, value);
            }
            response = await fetch(url, { method: method, headers: headers, body: body });
            text = await response.text();
        } catch (error) {
            throw new ClientError(method + " request failed: " + describeError(error));
        }
        raiseForStatus(response.status, text, method);
        return { headers: response.headers, text: text };
    }
}

function responseJson(text, method) {
    try {
        return JSON.parse(text);
    } catch (error) {
        if (!(error instanceof SyntaxError)) {
            throw error;
        }
        throw new TemplateError(method + " response is not JSON: " + text.slice(0, 200));
    }
}

function raiseForStatus(status, text, method) {
    if (status === 200) {
        return;
    }
    if (status === 409 || status === 412) {
        throw new TemplateError("the template was changed by someone else since it was read (HTTP " + status +
            "). Run the command again.");
    }
    throw new TemplateError(method + " failed with HTTP " + status + ": " + text);
}

// ---------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------

export async function commandGet(client, name) {
    const [template, etag] = await client.get();
    const version = template.version?.versionNumber ?? "?";
    console.log("Template version " + version + ", ETag " + etag);
    const [parameter, group] = findParameter(template, name);
    if (parameter === null) {
        console.log('Parameter "' + name + '" is not in the template. Devices report "missing".');
        return 1;
    }

    console.log('Parameter "' + name + '" in ' + describeLocation(group) + ", valueType " +
        (parameter.valueType ?? "(not set)"));
    const entries = [["default", currentValue(parameter, null)]];
    for (const condition of Object.keys(parameter.conditionalValues ?? {})) {
        entries.push(['condition "' + condition + '"', currentValue(parameter, condition)]);
    }

    let allValid = true;
    for (const [label, value] of entries) {
        if (value === null) {
            console.log("  " + label + ': no value (in-app default). Devices report "missing".');
            allValid = false;
            continue;
        }
        try {
            validateConfig(value);
            console.log("  " + label + ": " + value + "  [valid]");
        } catch (error) {
            if (!(error instanceof ConfigError)) {
                throw error;
            }
            console.log("  " + label + ": " + value + "  [INVALID: " + error.message + "]");
            allValid = false;
        }
    }
    if (allValid) {
        return 0;
    }
    return 1;
}

export function newValueForSet(args) {
    if (args.experimentId !== null) {
        if (args.studio !== null || args.game !== null || args.date !== null) {
            throw new ConfigError("--experiment-id cannot be combined with --studio, --game or --date");
        }
        const basisPoints = parseExperimentId(args.experimentId);
        if (args.percentage !== null && parsePercentageArgument(args.percentage) !== basisPoints) {
            throw new ConfigError("--percentage " + args.percentage + " does not equal the percentage segment of " +
                args.experimentId);
        }
        return buildValue(args.experimentId, basisPoints, args.enabled);
    }

    if (args.studio === null || args.game === null || args.percentage === null) {
        throw new ConfigError("set needs --studio, --game and --percentage, or --experiment-id");
    }
    const basisPoints = parsePercentageArgument(args.percentage);
    let date = args.date;
    if (date === null) {
        date = new Date().toISOString().slice(0, 10).replaceAll("-", "");
    }
    const experimentId = buildExperimentId(args.studio, args.game, date, basisPoints);
    return buildValue(experimentId, basisPoints, args.enabled);
}

// True if the new value has another experiment_id. An old value that is not a JSON object counts as a change.
export function experimentIdChanges(oldValue, newValue) {
    let old;
    try {
        old = JSON.parse(oldValue);
    } catch (error) {
        if (!(error instanceof SyntaxError)) {
            throw error;
        }
        return true;
    }
    if (old === null || typeof old !== "object" || Array.isArray(old)) {
        return true;
    }
    return old.experiment_id !== JSON.parse(newValue).experiment_id;
}

// True if the published value is a valid config with another experiment_id. Devices ignore an invalid value, so
// replacing it does not assign any device again.
function changesPublishedExperiment(oldValue, newValue) {
    try {
        validateConfig(oldValue);
    } catch (error) {
        if (!(error instanceof ConfigError)) {
            throw error;
        }
        return false;
    }
    return experimentIdChanges(oldValue, newValue);
}

async function confirm(prompt) {
    if (!process.stdin.isTTY) {
        throw new ConfigError("no terminal to confirm on; pass --yes to publish without asking");
    }
    const readline = createInterface({ input: process.stdin, output: process.stdout });
    return new Promise((resolve) => {
        // Ctrl-D closes the input without an answer, which counts as no.
        readline.once("close", () => resolve(false));
        readline.question(prompt + " [y/N] ", (answer) => {
            resolve(["y", "yes"].includes(answer.trim().toLowerCase()));
            readline.close();
        });
    });
}

export async function publish(client, name, condition, makeValue, dryRun, yes) {
    const [template, etag] = await client.get();
    const [parameter] = findParameter(template, name);
    let oldValue = null;
    if (parameter !== null) {
        oldValue = currentValue(parameter, condition);
    }
    const value = makeValue(oldValue);

    const updated = applyValue(template, name, value, condition);
    checkUntouched(template, updated, name);
    updated.version = { description: "Set " + name + " with " + SCRIPT_NAME };

    const diff = parameterDiff(template, updated, name);
    if (!diff) {
        console.log("No change: " + name + " already has this value.");
        return 0;
    }
    console.log(diff);
    if (oldValue !== null && experimentIdChanges(oldValue, value)) {
        console.log("Note: experiment_id changes. Every device is assigned again under the new id.");
    }
    if (condition === null && parameter !== null && parameter.conditionalValues &&
        Object.keys(parameter.conditionalValues).length > 0) {
        console.log("Warning: " + name + " has conditional values. They are kept and override the default for " +
            "matching devices. The integration guide recommends no conditions.");
    }

    if (dryRun) {
        console.log("Dry run: nothing published.");
        return 0;
    }
    if (!yes && !(await confirm("Publish this change?"))) {
        console.log("Cancelled: nothing published.");
        return 1;
    }

    await client.put(updated, etag, true);
    console.log("Firebase validation passed.");
    const [published, newEtag] = await client.put(updated, etag, false);
    const version = published.version?.versionNumber ?? "?";
    console.log("Published template version " + version + ", ETag " + newEtag);
    return 0;
}

export async function commandSet(client, args, value) {
    function makeValue(old) {
        if (!args.newExperiment && old !== null && changesPublishedExperiment(old, value)) {
            throw new ConfigError("this would publish a new experiment_id, which assigns every device again. " +
                "Use enable to turn the experiment back on under the same id, or pass --new-experiment to " +
                "start a new experiment.");
        }
        return value;
    }

    return publish(client, args.parameter, args.condition, makeValue, args.dryRun, args.yes);
}

// Returns the value for enable and disable: the published value with enabled switched, or a valid value built
// from --experiment-id when the published value is missing or invalid.
function toggleValue(args, oldValue, enabled) {
    let problem = null;
    let fields = null;
    if (oldValue === null) {
        problem = '"' + args.parameter + '" has no published value to ' + (enabled ? "enable" : "disable");
    } else {
        try {
            fields = validateConfig(oldValue);
        } catch (error) {
            if (!(error instanceof ConfigError)) {
                throw error;
            }
            problem = 'the published value of "' + args.parameter + '" is invalid: ' + error.message +
                ". Devices ignore it.";
        }
    }
    if (problem !== null) {
        if (args.experimentId === null) {
            throw new TemplateError(problem + " Pass --experiment-id <id> to publish a valid value with enabled " +
                enabled + ".");
        }
        return buildValue(args.experimentId, parseExperimentId(args.experimentId), enabled);
    }
    if (args.experimentId !== null && args.experimentId !== fields.get("experiment_id")) {
        throw new ConfigError("--experiment-id " + args.experimentId + " is not the published experiment_id " +
            fields.get("experiment_id") + "; --experiment-id is only needed when the published value is missing " +
            "or invalid");
    }
    return valueWithEnabled(oldValue, enabled);
}

async function publishToggle(client, args, enabled) {
    const makeValue = (old) => toggleValue(args, old, enabled);
    return publish(client, args.parameter, args.condition, makeValue, args.dryRun, args.yes);
}

export async function commandDisable(client, args) {
    return publishToggle(client, args, false);
}

export async function commandEnable(client, args) {
    return publishToggle(client, args, true);
}

// ---------------------------------------------------------------------------
// Command line
// ---------------------------------------------------------------------------

const USAGE = `usage: node ${SCRIPT_NAME} {get,set,enable,disable} --project PROJECT [options]

Read or update the metica_assignment Firebase Remote Config parameter.

commands:
  get                  print the published value and check it
  set                  set the experiment
  enable               set enabled to true, keep experiment_id and metica_percentage
  disable              set enabled to false, keep experiment_id and metica_percentage

options for all commands:
  --project PROJECT    Firebase project id (required)
  --credentials FILE   service account JSON key. Default: GOOGLE_APPLICATION_CREDENTIALS,
                       else gcloud application-default credentials
  --parameter NAME     parameter name (default ${DEFAULT_PARAMETER})

options for set, enable and disable:
  --condition NAME     set the value for this existing condition instead of the default value
  --dry-run            print the diff and stop
  --yes                publish without asking

options for enable and disable:
  --experiment-id ID   publish a valid value with enabled on/off for this id. Only needed when the
                       published value is missing or invalid

options for set:
  --studio STUDIO      studio segment, lower case, e.g. acme
  --game GAME          game segment, lower case, e.g. blockpuzzle
  --percentage PCT     metica_percentage, 0-100, at most two decimals
  --date YYYYMMDD      datestamp (default: today, UTC)
  --experiment-id ID   full experiment id instead of --studio/--game/--date
  --enabled            route the Metica group to Metica (default)
  --disabled           kill switch on
  --new-experiment     allow a new experiment_id (assigns every device again)

See README.md.`;

const COMMON_OPTIONS = {
    project: { type: "string" },
    credentials: { type: "string" },
    parameter: { type: "string" },
};
const WRITE_OPTIONS = {
    condition: { type: "string" },
    "dry-run": { type: "boolean" },
    yes: { type: "boolean" },
};
const SET_OPTIONS = {
    studio: { type: "string" },
    game: { type: "string" },
    percentage: { type: "string" },
    date: { type: "string" },
    "experiment-id": { type: "string" },
    enabled: { type: "boolean" },
    disabled: { type: "boolean" },
    "new-experiment": { type: "boolean" },
};
const COMMAND_OPTIONS = {
    get: COMMON_OPTIONS,
    set: { ...COMMON_OPTIONS, ...WRITE_OPTIONS, ...SET_OPTIONS },
    enable: { ...COMMON_OPTIONS, ...WRITE_OPTIONS, "experiment-id": { type: "string" } },
    disable: { ...COMMON_OPTIONS, ...WRITE_OPTIONS, "experiment-id": { type: "string" } },
};

// Returns the parsed arguments. Missing values are null, like None in Python.
export function parseCommandLine(argv) {
    const command = argv[0];
    if (!Object.hasOwn(COMMAND_OPTIONS, command)) {
        throw new UsageError("choose a command: get, set, enable or disable");
    }
    const options = COMMAND_OPTIONS[command];
    // argparse takes a negative number as an option value (--percentage -1); parseArgs only takes --percentage=-1.
    const tokens = [];
    for (const token of argv.slice(1)) {
        const previous = tokens[tokens.length - 1];
        const name = String(previous).slice(2);
        if (/^-\d+$|^-\d*\.\d+$/.test(token) && String(previous).startsWith("--") && Object.hasOwn(options, name) &&
            options[name].type === "string") {
            tokens[tokens.length - 1] = previous + "=" + token;
        } else {
            tokens.push(token);
        }
    }
    let values;
    try {
        ({ values } = parseArgs({ args: tokens, options: options, strict: true }));
    } catch (error) {
        if (!String(error.code).startsWith("ERR_PARSE_ARGS")) {
            throw error;
        }
        throw new UsageError(error.message);
    }
    if (values.project === undefined) {
        throw new UsageError("the following arguments are required: --project");
    }
    if (values.enabled && values.disabled) {
        throw new UsageError("argument --disabled: not allowed with argument --enabled");
    }
    return {
        command: command,
        project: values.project,
        credentials: values.credentials ?? null,
        parameter: values.parameter ?? DEFAULT_PARAMETER,
        condition: values.condition ?? null,
        dryRun: values["dry-run"] ?? false,
        yes: values.yes ?? false,
        studio: values.studio ?? null,
        game: values.game ?? null,
        percentage: values.percentage ?? null,
        date: values.date ?? null,
        experimentId: values["experiment-id"] ?? null,
        newExperiment: values["new-experiment"] ?? false,
        enabled: !values.disabled,
    };
}

export async function main(argv) {
    if (argv.includes("-h") || argv.includes("--help")) {
        console.log(USAGE);
        return 0;
    }
    let args;
    try {
        args = parseCommandLine(argv);
    } catch (error) {
        if (!(error instanceof UsageError)) {
            throw error;
        }
        console.error(USAGE + "\n\n" + SCRIPT_NAME + ": error: " + error.message);
        return 2;
    }

    try {
        let value = null;
        if (args.command === "set") {
            // Built before loading credentials, so bad arguments fail first.
            value = newValueForSet(args);
        }
        const client = await RemoteConfigClient.create(args.project, args.credentials);
        if (args.command === "get") {
            return await commandGet(client, args.parameter);
        }
        if (args.command === "set") {
            return await commandSet(client, args, value);
        }
        if (args.command === "enable") {
            return await commandEnable(client, args);
        }
        return await commandDisable(client, args);
    } catch (error) {
        if (!(error instanceof ConfigError || error instanceof TemplateError || error instanceof ClientError)) {
            throw error;
        }
        console.error("Error: " + error.message);
        return 2;
    }
}

if (process.argv[1] !== undefined && realpathSync(process.argv[1]) === fileURLToPath(import.meta.url)) {
    process.exitCode = await main(process.argv.slice(2));
}
