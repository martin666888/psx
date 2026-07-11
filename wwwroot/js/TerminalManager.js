// TerminalManager.js — Manages multiple xterm.js Terminal instances

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

class TerminalManager {
    constructor(container) {
        this.container = container;
        this.terminals = new Map();
        this.activeSessionId = null;
        this._encoder = new TextEncoder();
        this._xtermTheme = { ...defaultXtermTheme };
        this.options = {
            fontSize: 14,
            fontFamily: "Cascadia Code, Consolas, monospace",
            theme: 'dark',
            scrollback: 10000
        };
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

        if (typeof settings.theme === 'string' && settings.theme.toLowerCase() === 'dark') {
            next.theme = 'dark';
        }

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
            if (entry.element.style.display !== 'none') {
                this._fitVisibleTerminal(entry, true);
            }
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

        const entry = { terminal, fitAddon, decoder, element: wrapper };
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
        this._fitVisibleTerminal(target, true);
    }

    fitActiveTerminal() {
        if (!this.activeSessionId) return;

        const entry = this.terminals.get(this.activeSessionId);
        if (!entry) return;

        if (entry.element.style.display === 'none') return;

        this._fitVisibleTerminal(entry);
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
        try { entry.terminal.resize(cols, rows); } catch (e) { /* ignore */ }
    }

    closeTerminal(sessionId) {
        const entry = this.terminals.get(sessionId);
        if (!entry) return;

        const flushed = entry.decoder.decode(new Uint8Array(0));
        if (flushed) {
            entry.terminal.write(flushed);
        }
        entry.terminal.dispose();
        entry.element.remove();
        this.terminals.delete(sessionId);

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

    _fitVisibleTerminal(entry, focusAfterFit = false) {
        requestAnimationFrame(() => {
            entry.element.offsetHeight;
            this._fitTerminal(entry);

            requestAnimationFrame(() => {
                this._fitTerminal(entry);
                if (focusAfterFit) {
                    entry.terminal.focus();
                }
            });
        });

        if (document.fonts?.ready) {
            document.fonts.ready.then(() => {
                if (entry.element.style.display !== 'none') {
                    this._fitTerminal(entry);
                }
            });
        }
    }

    _fitTerminal(entry) {
        try { entry.fitAddon.fit(); } catch (e) { /* ignore */ }
    }

    _resolveTheme(theme) {
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

    async _pasteFromClipboard(sessionId, terminal) {
        try {
            const text = await navigator.clipboard.readText();
            if (text) {
                this._pasteText(sessionId, terminal, text);
            }
        } catch (e) {
            console.warn('Failed to paste from clipboard', e);
        }
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
