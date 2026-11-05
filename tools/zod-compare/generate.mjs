// Runs every catalogue schema over every shared document with real Zod, and writes
// tests/Zorg.Json.ComplianceTests/Fixtures/zod-comparison.json. Also records z.toJSONSchema output for
// every target, io and reused option.
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { z } from "zod";
import { cases } from "./catalogue.mjs";
import { documents } from "./documents.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const outPath = resolve(here, "../../tests/Zorg.Json.ComplianceTests/Fixtures/zod-comparison.json");

const zodVersion = JSON.parse(readFileSync(resolve(here, "node_modules/zod/package.json"), "utf8")).version;

export const targets = ["draft-2020-12", "draft-07", "draft-04", "openapi-3.0"];
export const ios = ["input", "output"];
export const reuseds = ["inline", "ref"];

export function canonical(value) {
  if (Array.isArray(value)) return "[" + value.map(canonical).join(",") + "]";
  if (value !== null && typeof value === "object") {
    return "{" + Object.keys(value).sort().map((k) => JSON.stringify(k) + ":" + canonical(value[k])).join(",") + "}";
  }
  return JSON.stringify(value);
}

function pointer(path) {
  return path.map((p) => "/" + String(p).replaceAll("~", "~0").replaceAll("/", "~1")).join("");
}

function encodeIssue(issue) {
  const code = issue.code === "invalid_type" && issue.input === undefined && "input" in issue ? "required" : issue.code;
  return `${code}@${pointer(issue.path)}`;
}

function run(schema, text) {
  let value;
  try {
    value = JSON.parse(text);
  } catch (error) {
    return { x: `unparseable: ${error.message}` };
  }
  let result;
  try {
    result = schema.safeParse(value, { reportInput: true });
  } catch (error) {
    return { x: String(error.message ?? error) };
  }
  if (!result.success) return result.error.issues.map(encodeIssue);
  const out = result.data;
  if (out === undefined) return { o: "undefined" };
  return canonical(out) === canonical(value) ? "=" : { o: JSON.stringify(out) };
}

function schemasFor(schema) {
  const all = {};
  for (const target of targets) {
    all[target] = {};
    for (const io of ios) {
      const inline = emit(schema, target, io, "inline");
      const ref = emit(schema, target, io, "ref");
      all[target][io] = {
        inline,
        ref: JSON.stringify(ref) === JSON.stringify(inline) ? "=inline" : ref,
      };
    }
  }
  return all;
}

function emit(schema, target, io, reused) {
  try {
    return { schema: z.toJSONSchema(schema, { target, io, reused }) };
  } catch (error) {
    return { error: String(error.message ?? error).slice(0, 300) };
  }
}

const docs = documents();
const lines = [];
for (const c of cases) {
  const results = docs.map((d) => run(c.schema, d.text));
  lines.push(
    "    " + JSON.stringify({ name: c.name, group: c.group, results, schemas: schemasFor(c.schema) }),
  );
}

const names = new Set(cases.map((c) => c.name));
if (names.size !== cases.length) throw new Error("duplicate case names");

const text =
  "{\n" +
  `  "zod": ${JSON.stringify(zodVersion)},\n` +
  `  "documents": [\n${docs.map((d) => "    " + JSON.stringify(d)).join(",\n")}\n  ],\n` +
  `  "cases": [\n${lines.join(",\n")}\n  ]\n}\n`;

mkdirSync(dirname(outPath), { recursive: true });
writeFileSync(outPath, text);
console.log(`wrote ${cases.length} cases x ${docs.length} documents to ${outPath} (${text.length} bytes)`);
