// Produces the golden outputs for the local commitlint mirror (.claude/scripts/commitlint.py).
// Run once with the exact versions CI uses (wagoid/commitlint-github-action v6.2.1 lockfile):
//   npm install @commitlint/cli@19.2.1 @commitlint/config-conventional@19.1.0   (in a temp folder)
//   node make_golden.mjs <temp folder> <this fixtures folder>
// The "comment-and-scissors" fixture is linted after git's default strip cleanup (what git passes
// to commit-msg hooks with `git commit`), applied here the same way the Python mirror applies it.
import { spawnSync } from "node:child_process";
import { readdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const [, , toolDir, fixturesDir] = process.argv;
const bin = join(toolDir, "node_modules", ".bin", process.platform === "win32" ? "commitlint.cmd" : "commitlint");
const cfg = join(toolDir, "commitlint.config.mjs");

function gitStrip(text) {
  const out = [];
  for (const line of text.split("\n")) {
    if (line === "# ------------------------ >8 ------------------------") break;
    if (line.startsWith("#")) continue;
    const trimmed = line.replace(/\s+$/, "");
    // git stripspace: collapse blank-line runs and drop leading blank lines
    if (trimmed || (out.length && out[out.length - 1])) out.push(trimmed);
  }
  return out.join("\n").replace(/\s+$/, "") + "\n";
}

const expected = {};
for (const file of readdirSync(fixturesDir).filter((f) => f.endsWith(".msg")).sort()) {
  const name = file.slice(0, -4);
  const message = gitStrip(readFileSync(join(fixturesDir, file), "utf8"));
  const r = spawnSync(bin, ["--config", cfg], { input: message, encoding: "utf8", shell: process.platform === "win32" });
  writeFileSync(join(fixturesDir, `${name}.expected`), r.stdout, "utf8");
  expected[name] = r.status;
}
writeFileSync(join(fixturesDir, "expected.json"), JSON.stringify(expected, null, 2) + "\n", "utf8");
console.log(`golden outputs for ${Object.keys(expected).length} fixtures`);
