// TerminalManager.js — Manages multiple xterm.js Terminal instances

// xterm and its addons are still loaded as classic scripts from
// public/vendor/xterm (CP0-verified byte-identical to the npm packages);
// they publish these globals. The npm import switch lands as its own commit.
/* global Terminal, FitAddon, Unicode11Addon */

import { Bridge } from './Bridge.js';

// 默认 xterm 主题 — 运行时由 psx.ini 覆盖
const defaultXtermTheme = {
    background: '#1d1d1a',
    foreground: '#cdd6f4',
    cursor: '#f5e0dc',
    cursorAccent: '#1d1d1a',
    selectionBackground: '#585b7066',
    selectionForeground: '#cdd6f4',
    black: '#45475a',
    red: '#f38ba8',
    green: '#a6e3a1',
    yellow: '#f9e2af',
    blue: '#89b4fa',
    magenta: '#f5c2e7',
    cyan: '#94e2d5',
    white: '#bac2de',
    brightBlack: '#585b70',
    brightRed: '#f38ba8',
    brightGreen: '#a6e3a1',
    brightYellow: '#f9e2af',
    brightBlue: '#89b4fa',
    brightMagenta: '#f5c2e7',
    brightCyan: '#94e2d5',
    brightWhite: '#a6adc8'
};

export class TerminalManager {
    constructor(container) {
        this.container = container;
        this.terminals = new Map();
        this.activeSessionId = null;
        this._pendingPasteRequests = new Map();
        this.viewVisible = !container.hidden;
        this._encoder = new TextEncoder();
        this._xtermTheme = { ...defaultXtermTheme };
        this._fontFitGeneration = 0;
        this.options = {
            fontSize: 14,
            fontFamily: "Cascadia Code, Consolas, monospace",
            theme: 'dark',
            scrollback: 10000,
            windowsBuildNumber: null
        };

        this._resizeObserver = typeof ResizeObserver === 'function'
            ? new ResizeObserver(() => this.fitActiveTerminal('container-resize'))
            : null;
        this._resizeObserver?.observe(this.container);

        if (document.fonts?.ready) {
            document.fonts.ready.then(() => this.fitActiveTerminal('initial-fonts-ready'));
        }
    }

    setSettings(settings) {
        if (!settings || typeof settings !== 'object') return;

        const next = { ...this.options };

        if (Number.isInteger(settings.fontSize) && settings.fontSize >= 6 && settings.fontSize <= 72) {
            next.fontSize = settings.fontSize;
        }

        if (typeof settings.fontFamily === 'string' && settings.fontFamily.trim()) {
            next.fontFamily = settings.fontFamily.trim();
        }

        if (Number.isInteger(settings.scrollback) && settings.scrollback >= 0 && settings.scrollback <= 1000000) {
            next.scrollback = settings.scrollback;
        }

        if (Number.isInteger(settings.windowsBuildNumber) && settings.windowsBuildNumber > 0) {
            next.windowsBuildNumber = settings.windowsBuildNumber;
        }

        if (typeof settings.theme === 'string' && settings.theme.toLowerCase() === 'dark') {
            next.theme = 'dark';
        }

        const fontChanged = next.fontSize !== this.options.fontSize || next.fontFamily !== this.options.fontFamily;
        this.options = next;

        // 从 psx.ini 构造 xterm 主题
        if (settings.terminalColors && typeof settings.terminalColors === 'object') {
            const tc = settings.terminalColors;
            this._xtermTheme = {
                background: this._resolveXtermBg(settings.themeColors),
                foreground: tc.foreground || defaultXtermTheme.foreground,
                cursor: tc.cursor || defaultXtermTheme.cursor,
                cursorAccent: tc.cursorAccent || defaultXtermTheme.cursorAccent,
                selectionBackground: tc.selectionBackground || defaultXtermTheme.selectionBackground,
                selectionForeground: tc.selectionForeground || defaultXtermTheme.selectionForeground,
                black: tc.black || defaultXtermTheme.black,
                red: tc.red || defaultXtermTheme.red,
                green: tc.green || defaultXtermTheme.green,
                yellow: tc.yellow || defaultXtermTheme.yellow,
                blue: tc.blue || defaultXtermTheme.blue,
                magenta: tc.magenta || defaultXtermTheme.magenta,
                cyan: tc.cyan || defaultXtermTheme.cyan,
                white: tc.white || defaultXtermTheme.white,
                brightBlack: tc.brightBlack || defaultXtermTheme.brightBlack,
                brightRed: tc.brightRed || defaultXtermTheme.brightRed,
                brightGreen: tc.brightGreen || defaultXtermTheme.brightGreen,
                brightYellow: tc.brightYellow || defaultXtermTheme.brightYellow,
                brightBlue: tc.brightBlue || defaultXtermTheme.brightBlue,
                brightMagenta: tc.brightMagenta || defaultXtermTheme.brightMagenta,
                brightCyan: tc.brightCyan || defaultXtermTheme.brightCyan,
                brightWhite: tc.brightWhite || defaultXtermTheme.brightWhite
            };
        }

        // 写入终端页面 CSS 变量
        if (settings.themeColors && typeof settings.themeColors === 'object') {
            const root = document.documentElement;
            const tc = settings.themeColors;
            this._setCssVar(root, '--term-bg', tc.background);
            this._setCssVar(root, '--term-scrollbar', tc.scrollbar);
            this._setCssVar(root, '--term-scrollbar-hover', tc.scrollbarHover);
        }

        // Theme previews must update terminals that are already open, not only
        // terminals created after the settings message.
        for (const entry of this.terminals.values()) {
            entry.terminal.options.theme = this._xtermTheme;
            entry.terminal.options.fontSize = this.options.fontSize;
            entry.terminal.options.fontFamily = this.options.fontFamily;
            if (fontChanged) {
                entry.needsFit = true;
            }
        }

        if (fontChanged) {
            this._scheduleFontFit();
        }
    }

    createTerminal(sessionId) {
        // Create a wrapper div — keep visible during open() so xterm 5.x
        // can correctly measure the container and initialise its renderer
        const wrapper = document.createElement('div');
        wrapper.id = 'term-' + sessionId;
        this.container.appendChild(wrapper);

        // Create terminal instance
        const terminal = new Terminal({
            fontSize: this.options.fontSize,
            fontFamily: this.options.fontFamily,
            cursorBlink: true,
            theme: this._resolveTheme(this.options.theme),
            scrollback: this.options.scrollback,
            allowProposedApi: true,
            scrollOnEraseInDisplay: true,
            drawBoldTextInBrightColors: true,
            tabStopWidth: 8,
            lineHeight: 1,
            letterSpacing: 0,
            bracketedPasteMode: true,
            smoothScrollDuration: 150,
            windowsPty: Number.isInteger(this.options.windowsBuildNumber)
                ? { backend: 'conpty', buildNumber: this.options.windowsBuildNumber }
                : undefined,
            windowOptions: {
                getWinSizeChars: true,
                getCellSizePixels: true,
                getWinSizePixels: true
            }
        });

        // Create and load fit addon (must be before open())
        // addon-fit.js uses UMD format: globalThis.FitAddon = { FitAddon: class, __esModule: true }
        const FitAddonClass = FitAddon.FitAddon ?? FitAddon;
        const fitAddon = new FitAddonClass();
        terminal.loadAddon(fitAddon);
        const decoder = new TextDecoder('utf-8');

        // Open terminal in the wrapper div
        terminal.open(wrapper);
        wrapper.addEventListener('mousedown', () => terminal.focus());

        // Load optional addons (must be after open())
        this._loadAddons(terminal);

        // Event: user input
        terminal.onData((data) => {
            const bytes = this._encoder.encode(data);
            const base64 = this._uint8ArrayToBase64(bytes);
            Bridge.sendInput(sessionId, base64);
        });

        terminal.onKey(({ key, domEvent }) => {
            this._logKeyEvent(key, domEvent);
        });

        // Forward modifier-key combos via Kitty Keyboard Protocol escape sequences
        // (xterm.js 5.3.0 doesn't have native Kitty support; 5.4.0-beta+ will)
        terminal.attachCustomKeyEventHandler((e) => {
            if (e.type !== 'keydown') return true;

            const key = e.key.toLowerCase();
            if (e.ctrlKey && key === 'v') {
                this._pasteFromClipboard(sessionId, terminal);
                e.preventDefault();
                return false;
            }

            if (e.ctrlKey && e.shiftKey && key === 'c') {
                this._copySelectionToClipboard(terminal);
                e.preventDefault();
                return false;
            }

            const mod = (e.shiftKey ? 1 : 0) | (e.altKey ? 2 : 0) | (e.ctrlKey ? 4 : 0);
            if (mod === 0) return true;

            if (e.key === 'Tab' && e.shiftKey && !e.altKey && !e.ctrlKey) {
                this._sendInput(sessionId, '\x1b[Z');
                e.preventDefault();
                return false;
            }

            const keyMap = { Enter: 13, Tab: 9, Escape: 27, Backspace: 127,
                ArrowUp: 64, ArrowDown: 65, ArrowRight: 67, ArrowLeft: 68 };
            const code = keyMap[e.key];
            if (code === undefined) return true;

            const seq = '\x1b[' + code.toString() + ';' + (mod + 1).toString() + 'u';
            this._sendInput(sessionId, seq);
            return false;
        });

        // Event: resize
        terminal.onResize(({ cols, rows }) => {
            Bridge.sendResize(sessionId, cols, rows);
        });

        // Event: title change
        terminal.onTitleChange((title) => {
            Bridge.sendTitle(sessionId, title);
        });

        const entry = {
            terminal,
            fitAddon,
            decoder,
            element: wrapper,
            pendingFitFrame: null,
            fitGeneration: 0,
            needsFit: true
        };
        this.terminals.set(sessionId, entry);

        if (this.terminals.size > 1) {
            wrapper.style.display = 'none';
        }

        // If this is the first terminal, activate it
        if (this.terminals.size === 1) {
            this.switchTerminal(sessionId);
        }
    }

    switchTerminal(sessionId) {
        const target = this.terminals.get(sessionId);
        if (!target) return;

        for (const [id, entry] of this.terminals) {
            entry.element.style.display = id === sessionId ? 'block' : 'none';
        }

        target.element.style.display = 'block';
        this.activeSessionId = sessionId;
        this._scheduleFit(target, { focusAfterFit: true, reason: 'tab-switch' });
    }

    setViewVisible(visible) {
        this.viewVisible = visible;

        if (!visible) {
            for (const entry of this.terminals.values()) {
                this._cancelPendingFit(entry);
                entry.needsFit = true;
            }
            this.container.hidden = true;
            return;
        }

        this.container.hidden = false;
        const entry = this.terminals.get(this.activeSessionId);
        if (entry) {
            entry.needsFit = true;
            this._scheduleFit(entry, { focusAfterFit: true, reason: 'view-restored' });
        }
    }

    fitActiveTerminal(reason = 'explicit-fit') {
        if (!this.activeSessionId) return;

        const entry = this.terminals.get(this.activeSessionId);
        if (!entry) return;

        this._scheduleFit(entry, { reason });
    }

    writeOutput(sessionId, base64Data) {
        const entry = this.terminals.get(sessionId);
        if (!entry) return;

        const bytes = this._base64ToUint8Array(base64Data);
        const text = entry.decoder.decode(bytes, { stream: true });
        entry.terminal.write(text);
    }

    resizeTerminal(sessionId, cols, rows) {
        const entry = this.terminals.get(sessionId);
        if (!entry) return;
        try { entry.terminal.resize(cols, rows); } catch { /* ignore */ }
    }

    closeTerminal(sessionId) {
        const entry = this.terminals.get(sessionId);
        if (!entry) return;

        const flushed = entry.decoder.decode(new Uint8Array(0));
        if (flushed) {
            entry.terminal.write(flushed);
        }
        this._cancelPendingFit(entry);
        entry.terminal.dispose();
        entry.element.remove();
        this.terminals.delete(sessionId);
        for (const [requestId, pending] of this._pendingPasteRequests) {
            if (pending.sessionId === sessionId) {
                this._pendingPasteRequests.delete(requestId);
            }
        }

        if (this.activeSessionId === sessionId) {
            this.activeSessionId = null;
            // Show another terminal if available
            const firstKey = this.terminals.keys().next().value;
            if (firstKey) {
                this.switchTerminal(firstKey);
            }
        }
    }

    _loadAddons(terminal) {
        // Fit addon — already loaded inline, this handles the rest

        // Unicode11 addon (xterm 5.x global: Unicode11Addon)
        if (window.Unicode11Addon) {
            try {
                const Unicode11AddonClass = Unicode11Addon.Unicode11Addon ?? Unicode11Addon;
                terminal.loadAddon(new Unicode11AddonClass());
                terminal.unicode.activeVersion = '11';
            } catch (e) {
                console.warn('Failed to load Unicode11 addon', e);
            }
        }

        // WebGL addon (xterm 5.x global: WebglAddon) — load after open()
        // TODO: enable after testing basic functionality; WebGL requires open() first
        // if (window.WebglAddon) {
        //     try {
        //         terminal.loadAddon(new WebglAddon());
        //     } catch (e) {
        //         console.warn('WebGL unavailable, falling back to DOM renderer', e);
        //     }
        // }
    }

    _scheduleFit(entry, { focusAfterFit = false, reason = 'unspecified' } = {}) {
        entry.needsFit = true;
        this._cancelPendingFit(entry);

        if (!this._isMeasurable(entry)) {
            this._debugFit(reason, entry, null, 'deferred-hidden');
            return;
        }

        const generation = ++entry.fitGeneration;
        const measure = (previous, attempt) => {
            entry.pendingFitFrame = requestAnimationFrame(() => {
                if (generation !== entry.fitGeneration || !this._isMeasurable(entry)) {
                    entry.pendingFitFrame = null;
                    entry.needsFit = true;
                    this._debugFit(reason, entry, null, 'cancelled-stale');
                    return;
                }

                const proposed = this._proposeDimensions(entry);
                if (!proposed) {
                    entry.pendingFitFrame = null;
                    entry.needsFit = true;
                    this._debugFit(reason, entry, null, 'invalid-measurement');
                    return;
                }

                const stable = previous && previous.cols === proposed.cols && previous.rows === proposed.rows;
                if (!stable && attempt < 3) {
                    measure(proposed, attempt + 1);
                    return;
                }

                entry.pendingFitFrame = null;
                entry.needsFit = false;
                this._applyDimensions(entry, proposed, reason);
                if (focusAfterFit && this._isMeasurable(entry)) {
                    entry.terminal.focus();
                }
            });
        };

        measure(null, 0);
    }

    _cancelPendingFit(entry) {
        entry.fitGeneration++;
        if (entry.pendingFitFrame !== null) {
            cancelAnimationFrame(entry.pendingFitFrame);
            entry.pendingFitFrame = null;
        }
    }

    _isMeasurable(entry) {
        return this.viewVisible
            && !this.container.hidden
            && entry.element.isConnected
            && entry.element.style.display !== 'none'
            && entry.element.clientWidth > 0
            && entry.element.clientHeight > 0
            && entry.element.getClientRects().length > 0;
    }

    _proposeDimensions(entry) {
        try {
            const dimensions = entry.fitAddon.proposeDimensions();
            if (!dimensions || !Number.isInteger(dimensions.cols) || !Number.isInteger(dimensions.rows)) return null;
            if (dimensions.cols < 2 || dimensions.rows < 1) return null;
            return dimensions;
        } catch {
            return null;
        }
    }

    _applyDimensions(entry, dimensions, reason) {
        const changed = entry.terminal.cols !== dimensions.cols || entry.terminal.rows !== dimensions.rows;
        this._debugFit(reason, entry, dimensions, changed ? 'resize' : 'unchanged');
        if (changed) {
            entry.terminal.resize(dimensions.cols, dimensions.rows);
        }
    }

    _scheduleFontFit() {
        const generation = ++this._fontFitGeneration;
        const ready = document.fonts?.ready || Promise.resolve();
        ready.then(() => {
            if (generation !== this._fontFitGeneration) return;
            const active = this.terminals.get(this.activeSessionId);
            if (!active) return;
            this._scheduleFit(active, { reason: 'font-changed' });
        });
    }

    _debugFit(reason, entry, proposed, outcome) {
        if (localStorage.getItem('psxDebugResize') !== '1') return;
        console.debug('[PSX terminal fit]', {
            reason,
            outcome,
            containerHidden: this.container.hidden,
            width: entry.element.clientWidth,
            height: entry.element.clientHeight,
            oldCols: entry.terminal.cols,
            oldRows: entry.terminal.rows,
            proposedCols: proposed?.cols ?? null,
            proposedRows: proposed?.rows ?? null,
            generation: entry.fitGeneration
        });
    }

    _resolveTheme(_theme) {
        return this._xtermTheme;
    }

    _resolveXtermBg(themeColors) {
        if (themeColors && typeof themeColors.background === 'string' && themeColors.background.startsWith('#')) {
            return themeColors.background;
        }
        return defaultXtermTheme.background;
    }

    _setCssVar(el, name, value) {
        if (typeof value === 'string' && value.trim()) {
            el.style.setProperty(name, value.trim());
        }
    }

    _logKeyEvent(key, domEvent) {
        if (localStorage.getItem('psxDebugKeys') !== '1') return;

        const bytes = Array.from(new TextEncoder().encode(key));
        console.debug('[PSX key]', {
            key,
            bytes,
            domKey: domEvent.key,
            code: domEvent.code,
            ctrl: domEvent.ctrlKey,
            alt: domEvent.altKey,
            shift: domEvent.shiftKey,
            meta: domEvent.metaKey
        });
    }

    _pasteFromClipboard(sessionId, terminal) {
        const requestId = this._newPasteRequestId();
        if (!requestId) return;

        this._pendingPasteRequests.set(requestId, { sessionId, terminal });
        Bridge.sendPasteRequest(sessionId, requestId);
    }

    handlePasteResponse(message) {
        if (!message || typeof message.requestId !== 'string' || typeof message.sessionId !== 'string') return;

        const pending = this._pendingPasteRequests.get(message.requestId);
        if (!pending || pending.sessionId !== message.sessionId) return;

        this._pendingPasteRequests.delete(message.requestId);
        const entry = this.terminals.get(message.sessionId);
        if (!message.ok || !entry || entry.terminal !== pending.terminal || typeof message.text !== 'string') return;

        if (message.text) {
            this._pasteText(message.sessionId, pending.terminal, message.text);
        }
    }

    _newPasteRequestId() {
        if (globalThis.crypto?.randomUUID) {
            return globalThis.crypto.randomUUID();
        }

        if (!globalThis.crypto?.getRandomValues) return '';
        const bytes = globalThis.crypto.getRandomValues(new Uint8Array(16));
        bytes[6] = (bytes[6] & 0x0f) | 0x40;
        bytes[8] = (bytes[8] & 0x3f) | 0x80;
        const hex = Array.from(bytes, (value) => value.toString(16).padStart(2, '0')).join('');
        return hex.slice(0, 8) + '-' + hex.slice(8, 12) + '-' + hex.slice(12, 16)
            + '-' + hex.slice(16, 20) + '-' + hex.slice(20);
    }

    _pasteText(sessionId, terminal, text) {
        const prepared = text.replace(/\r?\n/g, '\r');
        const isMultiline = /[\r\n]/.test(text);
        const forceMultilineBracket = localStorage.getItem('psxForceBracketedPaste') !== '0';
        const shouldBracket = terminal.modes?.bracketedPasteMode || (forceMultilineBracket && isMultiline);
        const data = shouldBracket
            ? '\x1b[200~' + prepared + '\x1b[201~'
            : prepared;

        this._sendInput(sessionId, data);
    }

    async _copySelectionToClipboard(terminal) {
        const selection = terminal.getSelection();
        if (!selection) return;

        try {
            await navigator.clipboard.writeText(selection);
        } catch (e) {
            console.warn('Failed to copy terminal selection', e);
        }
    }

    _sendInput(sessionId, text) {
        Bridge.sendInput(sessionId, this._uint8ArrayToBase64(
            this._encoder.encode(text)
        ));
    }

    // Base64 helpers
    _uint8ArrayToBase64(bytes) {
        const chunkSize = 0x8000;
        let result = '';
        for (let i = 0; i < bytes.length; i += chunkSize) {
            const chunk = bytes.subarray(i, i + chunkSize);
            result += String.fromCharCode.apply(null, chunk);
        }
        return btoa(result);
    }

    _base64ToUint8Array(base64) {
        const binary = atob(base64);
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i++) {
            bytes[i] = binary.charCodeAt(i);
        }
        return bytes;
    }
}
