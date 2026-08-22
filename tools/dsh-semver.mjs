// SemVer 2 precedence shared by tools/generate-dsh-lock.ps1 and
// tools/build-release.ps1 (invoked as `node dsh-semver.mjs ...`).
// Semantics intentionally mirror PSX.Services.DshSemanticVersion:
//   - build metadata is ignored for precedence;
//   - a version without a prerelease sorts above one with it;
//   - numeric identifiers compare numerically, numeric < alphanumeric,
//     and remaining identifiers are compared as ASCII strings;
//   - a shorter identifier list sorts below a longer one when it is a prefix.
// Shared conformance vectors live in tools/dsh-semver-vectors.json and are
// executed by both the C# and web test suites.

import { pathToFileURL } from "node:url";

const Pattern =
  /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;

export function parseSemVer(value) {
  if (typeof value !== "string" || value.length === 0) return null;
  const match = Pattern.exec(value);
  if (!match) return null;
  // SemVer 2: purely numeric prerelease identifiers must not carry leading
  // zeroes ("01" is invalid; "0", "0a" and "alpha" are not numeric).
  if (match[4] && match[4].split(".").some(
    (identifier) => identifier.length > 1 && identifier[0] === "0" && /^[0-9]+$/.test(identifier)
  )) {
    return null;
  }
  return {
    major: Number(match[1]),
    minor: Number(match[2]),
    patch: Number(match[3]),
    prerelease: match[4] ? match[4].split(".") : null,
  };
}

function isNumeric(identifier) {
  return identifier.length > 0 && /^[0-9]+$/.test(identifier);
}

function compareIdentifier(left, right) {
  const leftNumeric = isNumeric(left);
  const rightNumeric = isNumeric(right);
  if (leftNumeric && rightNumeric) {
    if (left.length !== right.length) {
      return left.length < right.length ? -1 : 1;
    }
    return left < right ? -1 : left > right ? 1 : 0;
  }
  if (leftNumeric !== rightNumeric) {
    return leftNumeric ? -1 : 1;
  }
  return left < right ? -1 : left > right ? 1 : 0;
}

export function compareSemVer(left, right) {
  const a = parseSemVer(left);
  const b = parseSemVer(right);
  if (!a || !b) {
    throw new Error(`invalid semver: ${!a ? left : right}`);
  }
  if (a.major !== b.major) return a.major < b.major ? -1 : 1;
  if (a.minor !== b.minor) return a.minor < b.minor ? -1 : 1;
  if (a.patch !== b.patch) return a.patch < b.patch ? -1 : 1;
  if (a.prerelease === null && b.prerelease === null) return 0;
  if (a.prerelease === null) return 1;
  if (b.prerelease === null) return -1;
  const shared = Math.min(a.prerelease.length, b.prerelease.length);
  for (let index = 0; index < shared; index += 1) {
    const comparison = compareIdentifier(a.prerelease[index], b.prerelease[index]);
    if (comparison !== 0) return comparison;
  }
  if (a.prerelease.length === b.prerelease.length) return 0;
  return a.prerelease.length < b.prerelease.length ? -1 : 1;
}

// `compare a b` prints -1 | 0 | 1; exit code 2 when either value is not
// valid semver. `latest <seed> v...` prints the newest version strictly
// greater than seed, or nothing when no candidate qualifies.
function main(argv) {
  if (argv.length >= 1 && argv[0] === "latest") {
    const seed = argv[1];
    if (!parseSemVer(seed)) {
      process.stderr.write(`invalid semver: ${seed}\n`);
      process.exit(2);
    }
    let best = null;
    for (const candidate of argv.slice(2)) {
      if (!parseSemVer(candidate)) {
        process.stderr.write(`invalid semver: ${candidate}\n`);
        process.exit(2);
      }
      if (compareSemVer(seed, candidate) < 0) {
        if (best === null || compareSemVer(best, candidate) < 0) best = candidate;
      }
    }
    if (best !== null) process.stdout.write(`${best}\n`);
    return;
  }

  if (argv.length !== 2) {
    process.stderr.write("usage: node dsh-semver.mjs <a> <b> | latest <seed> <v...>\n");
    process.exit(2);
  }
  try {
    process.stdout.write(`${compareSemVer(argv[0], argv[1])}\n`);
  } catch (error) {
    process.stderr.write(`${error.message}\n`);
    process.exit(2);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv.slice(2));
}
