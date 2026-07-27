// bridgeContract.test.js — cross-language drift gate for the bridge message
// type contract.
//
// bridgeMessages.test.js already pins every BridgeSendType/BridgeEventType
// literal so a rename cannot slip in silently. This gate closes the other half:
// it proves the JS constant tables and the code that actually parses, emits and
// dispatches those types stay in lockstep, so dropping or renaming a top-level
// `type` on EITHER side is caught mechanically. No expected set is hand-written
// here — every side is extracted from the real sources.
//
// The two bridge directions are validated SEPARATELY on purpose. `resize` (and
// `settings`, `appearance_settings`) exist in both directions, so folding send
// and event types into one set would let a one-directional drop hide behind the
// other direction. Each group below asserts one direction against its own real
// producer/consumer so a gap on either wire is surfaced on its own.
//
//   Group 1  BridgeSendType (JS -> C#)  <->  the two C# inbound parsers.
//   Group 2  BridgeEventType (C# -> JS) <->  the C# services that actually emit
//            events to the WebView.
//   Group 3  BridgeEventType (C# -> JS) <->  the frontend that actually handles
//            them (the main.js switch + the Agent decoder scope sets).
//
// This only inspects source text and the declared constants; it changes no wire
// type, field, value, or runtime bridge behavior.

import assert from 'node:assert/strict';
import { describe, it } from 'vitest';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import fs from 'node:fs';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '../../..');

function read(relative) {
  return fs.readFileSync(path.join(repoRoot, relative.split('/').join(path.sep)), 'utf8');
}

function matchAll(text, regex, group = 1) {
  return [...text.matchAll(regex)].map((match) => match[group]);
}

function unique(values) {
  return [...new Set(values)];
}

function difference(a, b) {
  return [...a].filter((value) => !b.has(value)).sort();
}

// --- Declared constant tables ---------------------------------------------
// Extract the `Key: 'value'` pairs from each Object.freeze({...}) body so both
// the union of values and the name -> value map (used to resolve main.js case
// labels) come straight from BridgeMessages.js.
const bridgeMessagesSource = read('frontend/webview/src/BridgeMessages.js');

function readFrozenTable(name) {
  const body = new RegExp(`const ${name} = Object\\.freeze\\(\\{([\\s\\S]*?)\\}\\)`).exec(bridgeMessagesSource);
  assert.ok(body, `Could not locate ${name} in BridgeMessages.js`);
  const pairs = [...body[1].matchAll(/(\w+)\s*:\s*'([a-z0-9_]+)'/g)];
  return new Map(pairs.map(([, key, value]) => [key, value]));
}

const sendTable = readFrozenTable('BridgeSendType');
const eventTable = readFrozenTable('BridgeEventType');
const declaredSend = new Set(sendTable.values());
const declaredEvent = new Set(eventTable.values());

// --- Documented, self-validated exclusions --------------------------------
// Two small allowlists keep the event-direction assertions honest without
// letting them mask real drift. Each allowlist is verified against reality
// below, so a stale or typo'd entry fails the gate instead of hiding a gap.

// Emitted by a C# session but intercepted inside the Agent Workspace; it never
// crosses to the browser as a public event and is intentionally NOT declared.
const INTERNAL_HOST_EVENTS = new Set(['agent_workspace_close_requested']);

// Declared and handled by the frontend, but synthesized in the frontend
// (reducer / DecisionController) rather than sent by the host — the host emits
// `agent_modes` (carrying currentModeId) and `permission_request` instead.
const FRONTEND_DERIVED_EVENTS = new Set(['question_request', 'agent_mode_current']);

// --- C# inbound reality: the two parsers ----------------------------------
// Both BridgeMessageParsers parsers switch on the message `type`, so every
// `case "..."` label is a send type the host really accepts.
const parserSource = read('Services/BridgeMessageParsers.cs');
const csharpParsed = new Set(matchAll(parserSource, /case\s+"([a-z0-9_]+)"/g));

// --- C# outbound reality: the services that emit to the WebView -----------
// Every Services/*.cs file except the inbound parser is scanned for the shapes
// the host uses to send browser-bound payloads. Extraction is anchored to the
// send/build context on purpose: matching a bare `type = "..."` would also pick
// up nested ACP content blocks (e.g. `blocks.Add(new { type = "text" })`) that
// are sent TO the agent, not to the browser. The three real emit shapes are:
//   1. `SendEventAsync(new { type = "..." })` / `SendMessageToJs(new { type })`
//      — the top-level bridge event objects (type is always the first member).
//   2. `return new { type = "..." }` — the AgentThreadBridgePayload helpers
//      whose objects are handed to SendEventAsync elsewhere.
//   3. `Type = "..."` — TerminalMessage, camelCased to `type` on the wire
//      (PascalCase `Type` only occurs on bridge messages).
// Comparisons use `type ==` / `type is`, so the single-`=` pattern excludes
// them regardless of anchor.
const servicesDir = path.join(repoRoot, 'Services');
const emitterFiles = fs
  .readdirSync(servicesDir)
  .filter((name) => name.endsWith('.cs') && name !== 'BridgeMessageParsers.cs');

const csharpEmittedRaw = unique(
  emitterFiles.flatMap((name) => {
    const source = fs.readFileSync(path.join(servicesDir, name), 'utf8');
    return [
      ...matchAll(source, /(?:SendEventAsync|SendMessageToJs)\(new\s*\{\s*type = "([a-z0-9_]+)"/g),
      ...matchAll(source, /return new\s*\{\s*type = "([a-z0-9_]+)"/g),
      ...matchAll(source, /\bType = "([a-z0-9_]+)"/g)
    ];
  })
);
const csharpEmitted = new Set(csharpEmittedRaw.filter((type) => !INTERNAL_HOST_EVENTS.has(type)));

// --- Frontend reality: the code that handles host events ------------------
// Dispatcher 1 (terminal + shared): main.js names the types it handles inline
// via `case BridgeEventType.X:`; resolve each constant name back to its value
// through the declared table (an unresolved name is itself a contract break).
const mainSource = read('frontend/webview/src/main.js');
const mainDispatchNames = matchAll(mainSource, /case\s+BridgeEventType\.(\w+)\s*:/g);
const unresolvedMainNames = mainDispatchNames.filter((name) => !eventTable.has(name));
const mainDispatchTypes = mainDispatchNames
  .filter((name) => eventTable.has(name))
  .map((name) => eventTable.get(name));

// Dispatcher 2 (Agent decoder): AgentWorkspaceRegistry.handle routes through
// HostEventDecoder, whose authoritative recognized-type sets live in
// host-events.ts. Pull the literals from inside each `new Set([...])` body
// (the TS source annotates the element type, e.g. `new Set<AppHostEventType>`)
// so the scopeOfType return strings ('app', 'lifecycle', ...) are not counted.
const hostEventsSource = read('frontend/agent/src/contracts/host-events.ts');
const agentDispatchTypes = matchAll(hostEventsSource, /new Set(?:<[^>]*>)?\(\[([\s\S]*?)\]\)/g)
  .flatMap((setBody) => matchAll(setBody, /'([a-z0-9_]+)'/g));

const frontendHandled = new Set([...mainDispatchTypes, ...agentDispatchTypes]);

describe('Bridge message type contract (C# <-> JS drift gate)', () => {
  it('extracts a non-trivial set from each real source', () => {
    // Guards against a regex/path regression silently comparing empty sets.
    assert.ok(declaredSend.size >= 10, `BridgeSendType set unexpectedly small (${declaredSend.size})`);
    assert.ok(declaredEvent.size >= 50, `BridgeEventType set unexpectedly small (${declaredEvent.size})`);
    assert.ok(csharpParsed.size >= 10, `C# parser set unexpectedly small (${csharpParsed.size})`);
    assert.ok(csharpEmitted.size >= 40, `C# emitted event set unexpectedly small (${csharpEmitted.size})`);
    assert.ok(frontendHandled.size >= 50, `frontend-handled set unexpectedly small (${frontendHandled.size})`);
  });

  it('keeps the documented exclusions honest', () => {
    // Internal events must really be emitted by C# yet never declared — else the
    // exclusion is stale and could hide a genuine outbound type.
    for (const type of INTERNAL_HOST_EVENTS) {
      assert.ok(csharpEmittedRaw.includes(type), `INTERNAL_HOST_EVENTS lists '${type}' but no C# service emits it`);
      assert.ok(!declaredEvent.has(type), `INTERNAL_HOST_EVENTS lists '${type}' but it IS declared in BridgeEventType`);
    }
    // Frontend-derived events must really be declared + frontend-handled yet
    // never emitted by C# — else the allowlist masks a missing producer.
    for (const type of FRONTEND_DERIVED_EVENTS) {
      assert.ok(declaredEvent.has(type), `FRONTEND_DERIVED_EVENTS lists '${type}' but it is not declared`);
      assert.ok(frontendHandled.has(type), `FRONTEND_DERIVED_EVENTS lists '${type}' but the frontend does not handle it`);
      assert.ok(!csharpEmitted.has(type), `FRONTEND_DERIVED_EVENTS lists '${type}' but C# actually emits it`);
    }
  });

  // --- Group 1: BridgeSendType (JS -> C#) <-> the two C# inbound parsers ---
  it('BridgeSendType matches the C# inbound parser cases exactly', () => {
    const declaredButNeverParsed = difference(declaredSend, csharpParsed);
    const parsedButNotDeclared = difference(csharpParsed, declaredSend);
    assert.deepEqual(
      declaredButNeverParsed,
      [],
      `BridgeSendType declares send types no C# parser accepts: ${declaredButNeverParsed.join(', ')}`
    );
    assert.deepEqual(
      parsedButNotDeclared,
      [],
      `A C# parser accepts send types missing from BridgeSendType: ${parsedButNotDeclared.join(', ')}`
    );
  });

  // --- Group 2: BridgeEventType (C# -> JS) <-> the C# emitters -------------
  it('BridgeEventType matches the events C# actually emits to the WebView', () => {
    const emittedButNotDeclared = difference(csharpEmitted, declaredEvent);
    const declaredButNeverEmitted = difference(
      new Set(difference(declaredEvent, FRONTEND_DERIVED_EVENTS)),
      csharpEmitted
    );
    assert.deepEqual(
      emittedButNotDeclared,
      [],
      `C# emits event types missing from BridgeEventType (rename/new-type drift): ${emittedButNotDeclared.join(', ')}`
    );
    assert.deepEqual(
      declaredButNeverEmitted,
      [],
      `BridgeEventType declares host events no C# service emits: ${declaredButNeverEmitted.join(', ')}`
    );
  });

  // --- Group 3: BridgeEventType (C# -> JS) <-> the frontend handlers ------
  it('resolves every main.js BridgeEventType case against the declared table', () => {
    assert.deepEqual(
      unresolvedMainNames,
      [],
      `main.js references BridgeEventType constants that no longer exist: ${unresolvedMainNames.join(', ')}`
    );
  });

  it('BridgeEventType matches the events the frontend actually handles', () => {
    const declaredButNeverHandled = difference(declaredEvent, frontendHandled);
    const handledButNotDeclared = difference(frontendHandled, declaredEvent);
    assert.deepEqual(
      declaredButNeverHandled,
      [],
      `BridgeEventType declares events no frontend dispatcher handles: ${declaredButNeverHandled.join(', ')}`
    );
    assert.deepEqual(
      handledButNotDeclared,
      [],
      `A frontend dispatcher handles events missing from BridgeEventType: ${handledButNotDeclared.join(', ')}`
    );
  });
});
