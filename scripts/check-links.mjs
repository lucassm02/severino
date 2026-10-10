// Checks the relative links of every Markdown file in the repository: each one must point to a
// file or folder that exists. Web links and anchors inside the same page are left alone.
//   node scripts/check-links.mjs
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const files = execFileSync("git", ["ls-files", "--cached", "--others", "--exclude-standard", "*.md"], { cwd: root, encoding: "utf8" })
  .split("\n")
  .filter((f) => f && existsSync(join(root, f)));

// [text](target) and reference definitions like "[Unreleased]: target".
const inline = /\]\(([^)\s]+)(?:\s+"[^"]*")?\)/g;
const reference = /^\s*\[[^\]]+\]:\s*(\S+)/gm;
const broken = [];

for (const file of files) {
  // Code blocks show commands and paths, not links.
  const text = readFileSync(join(root, file), "utf8").replace(/```[\s\S]*?```/g, "");
  for (const match of [...text.matchAll(inline), ...text.matchAll(reference)]) {
    const target = match[1];
    if (/^[a-z][a-z0-9+.-]*:/i.test(target) || target.startsWith("#")) continue;
    const path = decodeURIComponent(target.split("#")[0]).replace(/:\d+$/, "");
    if (!existsSync(resolve(root, dirname(file), path))) broken.push(`${file}: ${target}`);
  }
}

if (broken.length) {
  console.error(`Broken links (${broken.length}):\n  ${broken.join("\n  ")}`);
  process.exit(1);
}
console.log(`${files.length} files, no broken links.`);
