// dsh-session-extract.mjs — minimal-event extractor for DeepSeek Harness
// session logs (session.jsonl.zstd). Shipped inside the PSX package and run
// with the bundled portable Node; never bundled with third-party deps.
//
// Protocol v3 (line-delimited JSON):
//   stdin : one absolute session.jsonl.zstd path per line; EOF ends the batch.
//   stdout: {"k":"hello","v":3} first, then per input file (index = order):
//     {"k":"begin","file":i}
//     {"k":"head","file":i,"createdAt":ms,"delegationDepth":n,"hasParent":bool}
//     {"k":"usage","file":i,"kind":"assistant/message","t":ms,"mid":"...",
//      "turn":n,"step":n,"attempt":n|null,"seq":n|null,
//      "in":x,"out":y,"cr":z,"cw":w}
//     {"k":"retry","file":i,"t":ms,"turn":n,"step":n,"attempt":n|null,"seq":n|null}
//     {"k":"eof","file":i,"frames":n,"lines":n,"malformed":n,"stray":n,
//      "truncated":bool}
//     {"k":"err","file":i,"code":"...","detail":"..."}  (per-file, scan continues)
//   Only the fields above are ever emitted — never prompts, replies, titles,
//   cwd, paths or any conversation content.
//
// Accounting rules:
//   - Any event carrying a data.usage object is forwarded (assistant/message
//     and streaming chunks alike); C# owns the billing fold (per-attempt
//     samples accumulate, the final message of one attempt wins).
//   - A retry marker's attempt number is stamped onto subsequent usage events
//     of the same (turn, step) that lack an explicit attempt, so retry
//     attempts stay distinguishable even when only the marker carries it.
//   - Usage records with a missing input/output token count, or any
//     non-integer / negative / absurd token count, are not forwarded — they
//     count into eof.malformed so the file is marked partial.
//   - eof.stray counts bytes outside any valid frame (garbage between/after
//     frames). A non-empty file with zero valid frames is err bad_format; a
//     truly empty file (0 bytes) is a complete empty session.
//
// Parsing is streaming and bounded: the compressed file is read in chunks;
// frames are walked per RFC 8878 with the declared decompressed size checked
// before decompression (node:zlib streaming silently drops concatenated
// frames, and zstdDecompressSync on a truncated frame decodes to empty output
// instead of throwing, so frame boundaries are parsed, never guessed); each
// frame is decompressed on its own; JSONL lines are handled incrementally
// with the unterminated tail carried across frames and oversized lines
// dropped to the next newline (counted as malformed). stdout writes respect
// backpressure. Neither the whole compressed file nor the whole decompressed
// text is ever held at once.

import { createReadStream } from 'node:fs';
import { createInterface } from 'node:readline';
import { once } from 'node:events';
import { zstdDecompressSync } from 'node:zlib';

const PROTOCOL_VERSION = 3;
const READ_CHUNK_BYTES = 1024 * 1024;
const MAX_FRAME_BYTES = 64 * 1024 * 1024;
const MAX_DECOMPRESSED_FRAME_BYTES = 64 * 1024 * 1024;
const MAX_LINE_CHARS = 1024 * 1024;
const MAX_TOKEN_VALUE = 2 ** 48;
const ZSTD_MAGIC = 0xfd2fb528;
const SKIPPABLE_MAGIC_MIN = 0x184d2a50;
const SKIPPABLE_MAGIC_MAX = 0x184d2a5f;

async function emit(event) {
  if (!process.stdout.write(`${JSON.stringify(event)}\n`)) {
    await once(process.stdout, 'drain');
  }
}

function isSkippableMagic(value) {
  return value >= SKIPPABLE_MAGIC_MIN && value <= SKIPPABLE_MAGIC_MAX;
}

// Incremental reader over a file stream: keeps only the unconsumed window.
function createWindow(stream) {
  const iterator = stream[Symbol.asyncIterator]();
  let buffer = Buffer.alloc(0);
  let offset = 0;
  let ended = false;
  return {
    // Ensures at least n bytes are available; returns false on EOF. Stream
    // errors propagate as rejections from iterator.next().
    async need(n) {
      while (buffer.length - offset < n) {
        if (ended) return false;
        const { value, done } = await iterator.next();
        if (done) {
          ended = true;
          return false;
        }
        if (offset > 0) {
          buffer = buffer.subarray(offset);
          offset = 0;
        }
        buffer = Buffer.concat([buffer, value]);
      }
      return true;
    },
    readUInt32LE() {
      const value = buffer.readUInt32LE(offset);
      offset += 4;
      return value;
    },
    slice(n) {
      const out = buffer.subarray(offset, offset + n);
      offset += n;
      return out;
    },
    skip(n) {
      offset += n;
    },
    remaining() {
      return buffer.length - offset;
    },
  };
}

// Walks one zstd frame from the window. Returns { frame, contentSize } or
// { truncated: true } when the frame extends past EOF, { skipped: true } for
// a skippable frame, { stray: true } for a non-frame byte. Throws on a frame
// larger than MAX_FRAME_BYTES or with a declared decompressed size larger
// than MAX_DECOMPRESSED_FRAME_BYTES.
async function readFrame(window) {
  const magic = window.readUInt32LE();
  if (isSkippableMagic(magic)) {
    if (!(await window.need(4))) return { truncated: true };
    const size = window.readUInt32LE();
    if (size > MAX_FRAME_BYTES) {
      // A corrupt size would make need() buffer gigabytes: reject instead of
      // trying to skip payload that cannot be sane.
      throw Object.assign(new Error('skippable frame too large'), { code: 'too_large' });
    }
    if (!(await window.need(size))) return { truncated: true };
    window.skip(size);
    return { skipped: true };
  }
  if (magic !== ZSTD_MAGIC) return { stray: true };

  const magicBuf = Buffer.alloc(4);
  magicBuf.writeUInt32LE(magic, 0);
  const parts = [magicBuf];
  let frameBytes = 4;
  const take = async (n) => {
    if (n === 0) return true;
    if (!(await window.need(n))) return null;
    parts.push(window.slice(n));
    frameBytes += n;
    if (frameBytes > MAX_FRAME_BYTES) {
      throw Object.assign(new Error('frame too large'), { code: 'too_large' });
    }
    return true;
  };

  if (!(await take(1))) return { truncated: true };
  const descriptor = parts[parts.length - 1][0];
  const fcsFlag = descriptor >> 6;
  const singleSegment = (descriptor >> 5) & 1;
  const checksumFlag = (descriptor >> 2) & 1;
  const didFlag = descriptor & 3;
  if (!singleSegment && !(await take(1))) return { truncated: true };
  const didSizes = [0, 1, 2, 4];
  const fcsSizes = fcsFlag === 0 ? (singleSegment ? 1 : 0) : fcsFlag === 1 ? 2 : fcsFlag === 2 ? 4 : 8;
  if (!(await take(didSizes[didFlag] + fcsSizes))) return { truncated: true };

  // Frame_Content_Size field: its raw value plus the flag-specific base.
  // When present it declares the decompressed size, so pathological
  // expansion bombs are rejected before any decompression happens.
  let contentSize = null;
  if (fcsSizes > 0) {
    const fcsPart = parts[parts.length - 1].subarray(parts[parts.length - 1].length - fcsSizes);
    contentSize = fcsSizes === 8
      ? Number(fcsPart.readBigUInt64LE(0))
      : fcsPart.readUIntLE(0, fcsSizes);
    if (fcsFlag === 1) contentSize += 256;
    if (contentSize > MAX_DECOMPRESSED_FRAME_BYTES) {
      throw Object.assign(new Error('frame content size too large'), { code: 'too_large' });
    }
  }

  for (;;) {
    if (!(await take(3))) return { truncated: true };
    const headerBytes = parts[parts.length - 1];
    const blockHeader = headerBytes[0] | (headerBytes[1] << 8) | (headerBytes[2] << 16);
    const lastBlock = blockHeader & 1;
    const blockType = (blockHeader >> 1) & 3;
    const blockSize = blockHeader >> 3;
    if (blockType === 3) {
      throw Object.assign(new Error('reserved block type'), { code: 'decode_failed' });
    }
    if (!(await take(blockType === 1 ? 1 : blockSize))) return { truncated: true };
    if (lastBlock) break;
  }
  if (checksumFlag && !(await take(4))) return { truncated: true };

  return { frame: Buffer.concat(parts), contentSize };
}

function numberOrNull(value) {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function integerOrNull(value) {
  return typeof value === 'number' && Number.isFinite(value) && Number.isInteger(value)
    ? value
    : null;
}

// Token counts: input/output are required; cache read/write default to 0 when
// absent. Every present field must be a non-negative integer within sanity
// bounds — anything else marks the record malformed instead of guessing.
function readTokenCount(usage, name, required) {
  if (!(name in usage)) return required ? null : 0;
  const value = usage[name];
  if (typeof value !== 'number' || !Number.isFinite(value) || !Number.isInteger(value)
    || value < 0 || value > MAX_TOKEN_VALUE) {
    return null;
  }
  return value;
}

// Streaming per-file state machine. The unterminated line tail is capped at
// MAX_LINE_CHARS; an oversized line is dropped to the next newline and
// counted as malformed. The most recent retry marker stamps its attempt onto
// same-(turn, step) usage events that lack an explicit attempt.
function createFileState(file) {
  return {
    file,
    frames: 0,
    lines: 0,
    malformed: 0,
    stray: 0,
    truncated: false,
    leftover: '',
    dropping: false,
    lastRetry: null,
    syntheticAttempt: 0,
    sawAnyByte: false,

    async frameText(text) {
      let start = 0;
      for (;;) {
        const newline = text.indexOf('\n', start);
        if (newline === -1) {
          this.appendTail(text.slice(start));
          return;
        }
        if (this.dropping) {
          // The overlong line was already counted when detected; skip to its
          // newline and resume normal handling.
          this.dropping = false;
        } else if (this.leftover.length + (newline - start) > MAX_LINE_CHARS) {
          this.malformed += 1;
          this.leftover = '';
        } else {
          this.lines += 1;
          await this.handleLine(this.leftover + text.slice(start, newline));
          this.leftover = '';
        }
        start = newline + 1;
      }
    },

    appendTail(tail) {
      if (this.dropping) return;
      if (this.leftover.length + tail.length > MAX_LINE_CHARS) {
        this.malformed += 1;
        this.dropping = true;
        this.leftover = '';
      } else {
        this.leftover += tail;
      }
    },

    async finishTail() {
      if (this.dropping) {
        this.dropping = false;
        return;
      }
      const line = this.leftover.trim();
      this.leftover = '';
      if (!line) return;
      this.lines += 1;
      try {
        await this.forward(JSON.parse(line));
      } catch {
        this.malformed += 1;
      }
    },

    async handleLine(raw) {
      const line = raw.trim();
      if (!line) return;
      try {
        await this.forward(JSON.parse(line));
      } catch {
        this.malformed += 1;
      }
    },

    async forward(parsed) {
      const data = parsed.data && typeof parsed.data === 'object' ? parsed.data : {};
      if (parsed.type === 'session') {
        this.lastRetry = null;
        await emit({
          k: 'head',
          file: this.file,
          createdAt: numberOrNull(parsed.createdAt),
          delegationDepth: numberOrNull(parsed.delegationDepth) ?? 0,
          hasParent: parsed.parentSession !== undefined && parsed.parentSession !== null,
        });
        return;
      }
      if (data.usage && typeof data.usage === 'object') {
        const input = readTokenCount(data.usage, 'inputTokens', true);
        const output = readTokenCount(data.usage, 'outputTokens', true);
        const cacheRead = readTokenCount(data.usage, 'cacheReadTokens', false);
        const cacheWrite = readTokenCount(data.usage, 'cacheWriteTokens', false);
        if (input === null || output === null || cacheRead === null || cacheWrite === null) {
          this.malformed += 1;
          return;
        }
        let attempt = integerOrNull(data.attempt);
        let attemptSynthetic = false;
        const turn = integerOrNull(data.turn);
        const step = integerOrNull(data.step);
        if (attempt === null
          && this.lastRetry
          && turn !== null && step !== null
          && this.lastRetry.turn === turn && this.lastRetry.step === step) {
          attempt = this.lastRetry.attempt;
          attemptSynthetic = this.lastRetry.synthetic;
        }
        const message = data.message && typeof data.message === 'object' ? data.message : {};
        await emit({
          k: 'usage',
          file: this.file,
          kind: typeof parsed.type === 'string' ? parsed.type : null,
          t: numberOrNull(parsed.time),
          mid: typeof message.id === 'string' ? message.id : null,
          turn,
          step,
          attempt,
          attemptSynthetic,
          seq: integerOrNull(parsed.seq),
          in: input,
          out: output,
          cr: cacheRead,
          cw: cacheWrite,
        });
        return;
      }
      if (typeof parsed.type === 'string' && parsed.type.includes('retry')) {
        // A retry marker without an explicit attempt gets a monotonically
        // increasing synthetic number (initial attempt stays 0), so retries
        // never collapse back onto the first attempt's fold key.
        const explicitAttempt = integerOrNull(data.attempt);
        if (explicitAttempt === null) {
          this.syntheticAttempt += 1;
        }
        this.lastRetry = {
          turn: integerOrNull(data.turn),
          step: integerOrNull(data.step),
          attempt: explicitAttempt ?? this.syntheticAttempt,
          synthetic: explicitAttempt === null,
        };
        await emit({
          k: 'retry',
          file: this.file,
          t: numberOrNull(parsed.time),
          turn: this.lastRetry.turn,
          step: this.lastRetry.step,
          attempt: explicitAttempt,
          seq: integerOrNull(parsed.seq),
        });
      }
    },
  };
}

async function processFile(index, path) {
  await emit({ k: 'begin', file: index });
  const state = createFileState(index);
  try {
    const stream = createReadStream(path, { highWaterMark: READ_CHUNK_BYTES });
    const window = createWindow(stream);
    try {
      for (;;) {
        if (!(await window.need(4))) break; // clean EOF at a frame boundary
        const result = await readFrame(window);
        if (result.truncated) {
          state.truncated = true;
          state.sawAnyByte = true;
          break;
        }
        if (result.skipped) {
          state.frames += 1;
          continue;
        }
        if (result.stray) {
          state.sawAnyByte = true;
          state.stray += 1;
          window.skip(-3);
          continue;
        }
        state.sawAnyByte = true;
        state.frames += 1;
        let text;
        try {
          // maxOutputLength bounds the allocation itself, so an undeclared
          // decompression bomb fails here instead of filling memory first.
          text = zstdDecompressSync(result.frame, {
            maxOutputLength: MAX_DECOMPRESSED_FRAME_BYTES,
          }).toString('utf8');
        } catch (error) {
          if (error && error.code === 'ERR_BUFFER_TOO_LARGE') {
            throw Object.assign(new Error('frame decompressed too large'), { code: 'too_large' });
          }
          await emit({ k: 'err', file: index, code: 'decode_failed', detail: 'frame decompress failed' });
          return;
        }
        await state.frameText(text);
      }
      const trailing = window.remaining();
      if (trailing > 0 && trailing < 4) {
        // A partial frame header at EOF: an interrupted append, not garbage.
        state.sawAnyByte = true;
        state.truncated = true;
        state.stray += trailing;
      }
    } finally {
      stream.destroy();
    }
    if (state.frames === 0 && state.sawAnyByte) {
      // Bytes but not one valid frame: not a DSH session log.
      await emit({
        k: 'err',
        file: index,
        code: 'bad_format',
        detail: 'no zstd frame found',
      });
      return;
    }
    await state.finishTail();
    await emit({
      k: 'eof',
      file: index,
      frames: state.frames,
      lines: state.lines,
      malformed: state.malformed,
      stray: state.stray,
      truncated: state.truncated,
    });
  } catch (error) {
    const known = ['too_large', 'decode_failed', 'bad_format'];
    let code = error && error.code;
    if (!known.includes(code)) {
      code = (error && (error.code === 'ENOENT' || error.code === 'EACCES' || error.code === 'EPERM'))
        ? 'read_failed'
        : 'extractor_error';
    }
    await emit({
      k: 'err',
      file: index,
      code,
      detail: String((error && error.message) || code).slice(0, 80),
    });
  }
}

async function main() {
  await emit({ k: 'hello', v: PROTOCOL_VERSION });
  const rl = createInterface({ input: process.stdin, crlfDelay: Infinity });
  let index = 0;
  for await (const line of rl) {
    const path = line.trim();
    if (!path) continue;
    try {
      await processFile(index, path);
    } catch (error) {
      await emit({
        k: 'err',
        file: index,
        code: 'extractor_error',
        detail: String((error && error.message) || 'extractor_error').slice(0, 80),
      });
    }
    index += 1;
  }
}

main().catch((error) => {
  process.stderr.write(`dsh-session-extract fatal: ${(error && error.stack) || error}\n`);
  process.exit(1);
});
