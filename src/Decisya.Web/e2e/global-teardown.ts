import { existsSync, readdirSync, readFileSync, rmSync } from 'node:fs';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

// G3 M4. Scans test-results/ for the dev password in raw, URL-encoded, form-encoded and
// JSON-escaped form. A file with a hit is deleted (a file left on disk can be read by any
// agent), then the run fails. The message names the file path, never the value.

const resultsDirectory = fileURLToPath(new URL('../test-results', import.meta.url));

function listFiles(directory: string): string[] {
  const files: string[] = [];
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const full = join(directory, entry.name);
    if (entry.isDirectory()) {
      files.push(...listFiles(full));
    } else {
      files.push(full);
    }
  }
  return files;
}

function forms(password: string): Buffer[] {
  const variants = new Set<string>([
    password,
    encodeURIComponent(password),
    new URLSearchParams({ p: password }).toString().slice(2),
    JSON.stringify(password).slice(1, -1),
  ]);
  return [...variants].filter((variant) => variant !== '').map((variant) => Buffer.from(variant));
}

export default function globalTeardown(): void {
  const password = process.env.E2E_DEV_PASSWORD;
  if (password === undefined || password === '' || !existsSync(resultsDirectory)) {
    return;
  }
  const needles = forms(password);
  const offenders: string[] = [];
  for (const file of listFiles(resultsDirectory)) {
    const content = readFileSync(file);
    if (needles.some((needle) => content.includes(needle))) {
      offenders.push(relative(resultsDirectory, file));
      rmSync(file, { force: true });
    }
  }
  if (offenders.length > 0) {
    throw new Error(
      'The dev password was found in test-results/ and the files below were deleted: ' +
        offenders.join(', '),
    );
  }
}
