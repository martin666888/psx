// zstd-smoke-fixture.mjs — generates the two synthetic session.jsonl.zstd
// fixtures used by the build-release zstd smoke (kept as a file because
// inline node -e quoting does not survive PowerShell argument passing).
//
// Usage: node zstd-smoke-fixture.mjs <good.jsonl.zstd> <truncated.jsonl.zstd>
//
// good:      head frame + usage frame (valid, two concatenated frames).
// truncated: the same bytes plus a tail frame with its last 4 bytes cut off.

import { writeFileSync } from 'node:fs';
import { zstdCompressSync } from 'node:zlib';

const [, , goodPath, truncatedPath] = process.argv;
if (!goodPath || !truncatedPath) {
  process.stderr.write('usage: node zstd-smoke-fixture.mjs <good> <truncated>\n');
  process.exit(1);
}

const head = Buffer.from(`${JSON.stringify({ type: 'session', createdAt: 1 })}\n`);
const usage = Buffer.from(`${JSON.stringify({
  type: 'assistant/message',
  time: 2,
  data: {
    turn: 1,
    step: 1,
    message: { id: 'm1' },
    usage: { inputTokens: 3, outputTokens: 4, cacheReadTokens: 5, cacheWriteTokens: 6 },
  },
})}\n`);

const good = Buffer.concat([zstdCompressSync(head), zstdCompressSync(usage)]);
writeFileSync(goodPath, good);
const bad = zstdCompressSync(Buffer.from('{"type":"session"'));
writeFileSync(truncatedPath, Buffer.concat([good, bad.subarray(0, bad.length - 4)]));
