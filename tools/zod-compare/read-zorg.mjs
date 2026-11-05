// Loads the JSON Schema that Zorg emits for each catalogue schema (zorg-emitted.json, written by the .NET
// test) with z.fromJSONSchema, runs the shared documents through it and writes zod-reads-zorg.json.
// z.fromJSONSchema is documented as experimental: a refusal is recorded, not hidden.
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { z } from "zod";

const here = dirname(fileURLToPath(import.meta.url));
const fixtures = resolve(here, "../../tests/Zorg.Json.ComplianceTests/Fixtures");
const comparison = JSON.parse(readFileSync(resolve(fixtures, "zod-comparison.json"), "utf8"));
const emitted = JSON.parse(readFileSync(resolve(fixtures, "zorg-emitted.json"), "utf8"));
const targets = ["draft-2020-12", "draft-07"];

const lines = [];
let refused = 0;
let read = 0;

for (const c of comparison.cases) {
  const perTarget = {};
  for (const target of targets) {
    const entry = emitted.cases[c.name]?.[target];
    if (!entry || entry.error) {
      perTarget[target] = { refused: `no Zorg schema: ${entry?.error ?? "missing"}` };
      refused++;
      continue;
    }
    let schema;
    try {
      schema = z.fromJSONSchema(entry.schema);
    } catch (error) {
      perTarget[target] = { refused: String(error.message ?? error).split("\n")[0].slice(0, 200) };
      refused++;
      continue;
    }
    let verdicts = "";
    for (const doc of comparison.documents) {
      try {
        verdicts += schema.safeParse(JSON.parse(doc.text)).success ? "A" : "R";
      } catch {
        verdicts += "E";
      }
    }
    perTarget[target] = { results: verdicts };
    read++;
  }
  lines.push("    " + JSON.stringify({ name: c.name, targets: perTarget }));
}

const text = `{\n  "zod": ${JSON.stringify(comparison.zod)},\n  "cases": [\n${lines.join(",\n")}\n  ]\n}\n`;
writeFileSync(resolve(fixtures, "zod-reads-zorg.json"), text);
console.log(`read ${read} schemas, refused ${refused}`);
