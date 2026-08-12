// WorkspaceChromeController.js — the always-loaded WebView shell chrome:
// per-column tab strips in the 40px workspace row, the global popover menus
// (create, theme, column) anchored to the activity rail, and the notice
// region. C# owns the requested layout truth; this controller
// renders it. A column with a single tab keeps the legacy nameplate look; a
// multi-tab column renders an overflowable tab bar (wheel -> horizontal
// scroll). Clicking a tab activates it (workspace_layout_intent activate);
// the close button closes it; right-click opens the column menu.

import { Bridge } from './Bridge.js';
import { createProviderIconSvg } from './ProviderIcons.js';

const ATTENTION_LABELS = Object.freeze({
    permission: '需确认',
    question: '待回复',
    error: '出错',
    completed: '已完成'
});

export class WorkspaceChromeController {
    constructor(root, portalRoot) {
        if (!root || !portalRoot) throw new Error('WorkspaceChromeController requires chrome and portal roots');
        this.root = root;
        this.portalRoot = portalRoot;
        // The three global buttons live in the left activity rail
        // (index.html), not inside the chrome row; query them document-wide
        // by data-role so the constructor signature stays (root, portalRoot).
        this.rail = document.getElementById('activity-rail');
        this.nameplates = root.querySelector('[data-role="pane-nameplates"]');
        this.historyButton = document.querySelector('[data-role="history-toggle"]');
        this.createButton = document.querySelector('[data-role="workspace-create-toggle"]');
        this.themeButton = document.querySelector('[data-role="theme-toggle"]');
        this.catalogRevision = -1;
        this.themeRevision = -1;
        this.catalog = { workspaces: [], providers: [], maxColumns: 3 };
        this.themeCatalog = { themes: [] };
        this.layout = null;
        this.rects = new Map();
        this.openMenu = null;
        this.openTrigger = null;
        this.paneMenuWorkspace = null;
        this.createPlacement = 'focused';
        this.capacityChecker = null;
        this.historyButton.disabled = false;
        this.historyButton.setAttribute('aria-expanded', 'false');

        this.historyButton.addEventListener('click', () => {
            document.dispatchEvent(new window.CustomEvent('psx-history-toggle'));
        });
        this.createButton.addEventListener('click', () => this.toggleMenu('create', this.createButton));
        this.themeButton.addEventListener('click', () => this.toggleMenu('theme', this.themeButton));
        this.onDocumentPointerDown = (event) => {
            if (!this.openMenu) return;
            const menu = this.portalRoot.firstElementChild;
            if (menu?.contains(event.target)) return;
            if (this.openTrigger?.contains(event.target)) {
                // Create/Theme toggle themselves on click. A pane menu opens
                // by contextmenu, so a normal click on its tab is an outside
                // action and should dismiss it before the tab activates.
                if (this.openMenu !== 'pane' || event.button !== 0) return;
            }
            // Pointer dismissal must not put focus back on the trigger: the
            // pressed target (for example the Agent textarea) owns the next
            // focus. Theme preview still rolls back on every non-confirming
            // close.
            this.closeMenu(this.openMenu === 'theme', false);
        };
        this.onDocumentKeyDown = (event) => this.handleDocumentKeyDown(event);
        this.onHistoryState = (event) => {
            const open = event.detail?.open === true;
            this.historyButton.setAttribute('aria-expanded', String(open));
        };
        document.addEventListener('pointerdown', this.onDocumentPointerDown, true);
        document.addEventListener('keydown', this.onDocumentKeyDown);
        document.addEventListener('psx-history-state', this.onHistoryState);
    }

    dispose() {
        document.removeEventListener('pointerdown', this.onDocumentPointerDown, true);
        document.removeEventListener('keydown', this.onDocumentKeyDown);
        document.removeEventListener('psx-history-state', this.onHistoryState);
        this.closeMenu(this.openMenu === 'theme', false);
    }

    applyCatalog(message) {
        const revision = Number(message?.revision);
        if (!Number.isFinite(revision) || revision <= this.catalogRevision) return false;
        this.catalogRevision = revision;
        this.catalog = {
            workspaces: Array.isArray(message.workspaces) ? message.workspaces : [],
            providers: Array.isArray(message.providers) ? message.providers : [],
            maxColumns: Number(message.maxColumns) || 3
        };
        // The open column menu holds a workspace object from the previous
        // catalog: rebind it to the fresh entry so capabilities (canSplitRight,
        // canCollapse…) never render stale; the menu closes when its workspace
        // is gone.
        if (this.openMenu === 'pane' && this.paneMenuWorkspace) {
            const refreshed = this.catalog.workspaces.find(
                (item) => item.workspaceId === this.paneMenuWorkspace.workspaceId
            );
            if (refreshed) this.paneMenuWorkspace = refreshed;
            else this.closeMenu(false, false);
        }
        this.renderTabStrips();
        if (this.openMenu === 'create' || this.openMenu === 'pane') {
            this.renderOpenMenu();
        }
        return true;
    }

    applyThemeCatalog(message) {
        const revision = Number(message?.revision);
        if (!Number.isFinite(revision) || revision <= this.themeRevision) return false;
        this.themeRevision = revision;
        this.themeCatalog = message;
        if (this.openMenu === 'theme') this.renderOpenMenu();
        return true;
    }

    applyLayout(snapshot, rects) {
        this.layout = snapshot;
        this.rects = rects;
        this.renderTabStrips();
        if (this.openMenu === 'pane') this.renderOpenMenu();
    }

    showNotice(message) {
        if (typeof message !== 'string' || !message.trim()) return;
        const region = document.getElementById('workspace-notices');
        if (!region) return;
        const notice = document.createElement('div');
        notice.className = 'workspace-notice';
        notice.textContent = message.trim();
        region.appendChild(notice);
        window.setTimeout(() => notice.remove(), 5000);
    }

    // Injected by main.js: returns { fitsAgent, fitsTerminal,
    // requestedColumnCount } describing whether the requested layout plus one
    // more column of each kind fits the current width. Frontend pixel gating
    // is the first defensive line; the C# 3-column count in the catalog
    // remains the second.
    setCapacityChecker(checker) {
        this.capacityChecker = typeof checker === 'function' ? checker : null;
    }

    // Per-column tab strips: each strip sits exactly on its column rect
    // (left/width from the geometry engine) and renders that column's tabs.
    // The effective layout drives this — while zoomed only the focused column
    // is present, so only its strip renders (decision F).
    renderTabStrips() {
        if (!this.layout || !this.nameplates) return;
        const desired = new Set();
        this.layout.columns.forEach((column, index) => {
            desired.add(column.columnId);
            let strip = [...this.nameplates.children].find((item) => item.dataset.columnId === column.columnId);
            if (!strip) {
                strip = document.createElement('div');
                strip.className = 'workspace-tab-strip';
                strip.dataset.columnId = column.columnId;
                strip.setAttribute('role', 'group');
                strip.setAttribute('aria-label', 'Workspaces in column');
                // Wheel over an overflowed strip scrolls horizontally; a strip
                // that fits its column leaves the wheel untouched.
                strip.addEventListener('wheel', (event) => {
                    if (event.deltaY === 0) return;
                    const maxScroll = strip.scrollWidth - strip.clientWidth;
                    if (maxScroll <= 0) return;
                    strip.scrollLeft = Math.min(maxScroll, Math.max(0, strip.scrollLeft + event.deltaY));
                    event.preventDefault();
                });
                this.nameplates.appendChild(strip);
            }
            const rect = this.rects.get(column.columnId);
            if (rect) {
                strip.style.left = `${rect.left}px`;
                strip.style.width = `${rect.width}px`;
            }
            strip.dataset.focused = column.columnId === this.layout.focusedColumnId ? 'true' : 'false';
            strip.dataset.last = index === this.layout.columns.length - 1 ? 'true' : 'false';
            this.fillTabStrip(strip, column);
        });
        for (const strip of [...this.nameplates.children]) {
            if (!desired.has(strip.dataset.columnId)) strip.remove();
        }
    }

    fillTabStrip(strip, column) {
        strip.replaceChildren();
        for (const tab of column.tabs || []) {
            const workspace = this.catalog.workspaces.find((item) => item.workspaceId === tab.workspaceId);
            strip.appendChild(this.buildTab(tab, workspace, column));
        }
    }

    // tab = icon + title + attention badge + close. Clicking the tab activates
    // the workspace (its column focuses and the tab becomes active); the close
    // button closes it; right-click opens the column menu.
    buildTab(tab, workspace, column) {
        const tabEl = document.createElement('div');
        tabEl.className = 'workspace-tab';
        tabEl.setAttribute('role', 'presentation');
        tabEl.dataset.workspaceId = tab.workspaceId || '';
        tabEl.dataset.active = tab.workspaceId === column.activeTabId ? 'true' : 'false';
        tabEl.title = workspace?.title || 'Workspace';

        const target = document.createElement('button');
        target.type = 'button';
        target.className = 'workspace-tab-target';
        target.setAttribute('aria-pressed', tab.workspaceId === column.activeTabId ? 'true' : 'false');
        target.title = workspace?.title || 'Workspace';

        const icon = document.createElement('span');
        icon.className = 'workspace-tab-icon';
        icon.setAttribute('aria-hidden', 'true');
        // Terminal tabs keep the classic text glyph; agent tabs carry the
        // provider brand mark (createProviderIconSvg falls back to the generic
        // sparkle for unknown catalog keys).
        if ((workspace?.kind ?? tab.kind) === 'terminal') {
            icon.textContent = '>_';
        } else {
            icon.appendChild(createProviderIconSvg(workspace?.iconKey));
        }

        const title = document.createElement('span');
        title.className = 'workspace-tab-title';
        title.textContent = workspace?.title || 'Workspace';

        const attention = document.createElement('span');
        attention.className = 'workspace-tab-attention';
        attention.dataset.kind = workspace?.attentionKind || '';
        attention.textContent = ATTENTION_LABELS[workspace?.attentionKind] || '';

        const close = document.createElement('button');
        close.type = 'button';
        close.className = 'workspace-chrome-icon-button workspace-tab-close';
        close.setAttribute('aria-label', `关闭 ${workspace?.title || '工作区'}`);
        close.textContent = '×';
        close.disabled = !workspace;
        close.addEventListener('click', (event) => {
            event.stopPropagation();
            if (workspace) Bridge.sendWorkspaceLayoutIntent('close', workspace.workspaceId);
        });

        const activate = () => {
            if (workspace) Bridge.sendWorkspaceLayoutIntent('activate', workspace.workspaceId);
        };
        target.addEventListener('click', activate);
        tabEl.addEventListener('contextmenu', (event) => {
            event.preventDefault();
            if (workspace) this.openPaneMenu(workspace, target);
        });

        target.append(icon, title, attention);
        tabEl.append(target, close);
        return tabEl;
    }

    toggleMenu(kind, trigger) {
        if (this.openMenu === kind) {
            this.closeMenu(kind === 'theme');
            return;
        }
        if (this.openMenu) this.closeMenu(this.openMenu === 'theme', false);
        this.openMenu = kind;
        this.openTrigger = trigger;
        trigger.setAttribute('aria-expanded', 'true');
        if (kind === 'theme') Bridge.sendThemeAction('refresh');
        this.renderOpenMenu();
    }

    openPaneMenu(workspace, trigger) {
        if (this.openMenu) this.closeMenu(this.openMenu === 'theme', false);
        this.openMenu = 'pane';
        this.openTrigger = trigger;
        this.paneMenuWorkspace = workspace;
        trigger.setAttribute('aria-expanded', 'true');
        this.renderOpenMenu();
    }

    closeMenu(cancelTheme = false, restoreFocus = true) {
        if (!this.openMenu) return;
        if (cancelTheme) Bridge.sendThemeAction('cancel');
        this.openTrigger?.setAttribute('aria-expanded', 'false');
        const focusTarget = this.openTrigger;
        this.openMenu = null;
        this.openTrigger = null;
        this.paneMenuWorkspace = null;
        this.portalRoot.replaceChildren();
        if (restoreFocus) focusTarget?.focus();
    }

    renderOpenMenu() {
        if (!this.openMenu) return;
        // In-place refresh (segment toggle, catalog/layout refresh while the
        // menu stays open) vs. a fresh open: the popover node is swapped
        // either way, so only a fresh open may replay the entry animation and
        // steal focus — a refresh would otherwise flash and yank focus off
        // the control the user just clicked.
        const isRefresh = this.portalRoot.firstElementChild?.dataset?.menu === this.openMenu;
        const menu = document.createElement('section');
        menu.className = `workspace-popover workspace-popover-${this.openMenu}`;
        menu.dataset.menu = this.openMenu;
        menu.setAttribute('role', 'dialog');
        menu.setAttribute('aria-label', this.menuLabel());
        if (this.openMenu === 'pane' && this.openTrigger) {
            // The column menu's trigger lives on a tab anywhere across the
            // chrome row: anchor directly under it. The exact left is set after
            // mount (measured width), left-aligned to the trigger and clamped
            // only when needed to keep it inside the viewport. A transform
            // would fight the popover-in animation's translateY.
            const rect = this.openTrigger.getBoundingClientRect();
            menu.dataset.anchorLeft = String(rect.left);
            menu.style.top = `${rect.bottom + 4}px`;
        } else if (this.openTrigger) {
            // Global menus hug the activity rail's right edge (left comes from
            // the .workspace-popover CSS) and align their top with the trigger
            // button. Re-renders (catalog/layout refresh) recompute this; an
            // open menu does not re-anchor on window resize.
            const triggerTop = this.openTrigger.getBoundingClientRect().top;
            menu.style.top = `${triggerTop}px`;
            menu.style.maxHeight = `calc(100vh - ${triggerTop}px - 8px)`;
        }
        if (this.openMenu === 'create') this.renderCreateMenu(menu);
        if (this.openMenu === 'theme') this.renderThemeMenu(menu);
        if (this.openMenu === 'pane') this.renderPaneMenu(menu, this.paneMenuWorkspace);
        if (isRefresh) menu.style.animation = 'none';
        this.portalRoot.replaceChildren(menu);
        if (menu.dataset.anchorLeft) {
            const anchorLeft = Number(menu.dataset.anchorLeft);
            const rightmostLeft = Math.max(44, window.innerWidth - menu.offsetWidth - 8);
            const left = Math.min(Math.max(44, anchorLeft), rightmostLeft);
            menu.style.left = `${left}px`;
            delete menu.dataset.anchorLeft;
        }
        if (!isRefresh) window.queueMicrotask(() => menu.querySelector('button:not(:disabled)')?.focus());
    }

    menuLabel() {
        return { create: '新建工作区', theme: '主题', pane: '工作区操作' }[this.openMenu] || '菜单';
    }

    renderCreateMenu(menu) {
        menu.appendChild(this.heading('新建工作区'));
        const capacity = this.capacityChecker?.() ?? null;
        // The column-cap gate uses the REQUESTED column count (zoomed
        // effective layouts under-count) and the catalog's maxColumns.
        const columnCount = capacity?.requestedColumnCount ?? this.layout?.requested?.columns?.length ?? this.layout?.columns?.length ?? 1;
        const atColumnCap = columnCount >= this.catalog.maxColumns;
        const segments = document.createElement('div');
        segments.className = 'workspace-segments';
        for (const [value, label] of [['focused', '当前列'], ['new_right', '右侧新列']]) {
            const button = document.createElement('button');
            button.type = 'button';
            button.textContent = label;
            button.dataset.selected = this.createPlacement === value ? 'true' : 'false';
            // Layered gating: the C# 3-column count (maxColumns) and the
            // frontend pixel capacity both disable new-right; each carries its
            // own explanatory title. The frontend gate is the first defensive
            // line, the C# count the second.
            const capBlocked = value === 'new_right' && atColumnCap;
            const capacityBlocked = value === 'new_right' && capacity !== null && !capacity.fitsAgent;
            button.disabled = capBlocked || capacityBlocked;
            if (capBlocked) button.title = '最多支持 3 列';
            else if (capacityBlocked) button.title = '窗口宽度不足以容纳新列';
            button.addEventListener('click', () => {
                this.createPlacement = value;
                this.renderOpenMenu();
            });
            segments.appendChild(button);
        }
        menu.appendChild(segments);
        const terminalBlocked = this.createPlacement === 'new_right' && capacity !== null && !capacity.fitsTerminal;
        menu.appendChild(this.menuRow(
            'Terminal',
            '',
            () => this.createWorkspace('terminal'),
            terminalBlocked,
            terminalBlocked ? '窗口宽度不足以容纳新列' : ''
        ));
        if (this.catalog.providers.length) menu.appendChild(this.subheading('AGENT'));
        for (const provider of this.catalog.providers) {
            menu.appendChild(this.menuRow(provider.displayName, '', () => this.createWorkspace('agent', provider.key)));
        }
    }

    createWorkspace(kind, providerKey) {
        Bridge.sendWorkspaceCreate(kind, providerKey, this.createPlacement);
        this.createPlacement = 'focused';
        this.closeMenu(false);
    }

    renderThemeMenu(menu) {
        const top = document.createElement('div');
        top.className = 'workspace-popover-heading-row';
        top.append(this.heading('Theme'));
        const current = document.createElement('span');
        current.textContent = this.themeCatalog.currentLabel || '';
        top.append(current);
        menu.appendChild(top);
        const themes = Array.isArray(this.themeCatalog.themes) ? this.themeCatalog.themes : [];
        for (const source of ['builtin', 'user']) {
            const group = themes.filter((theme) => theme.source === source);
            if (!group.length) continue;
            menu.appendChild(this.subheading(source === 'builtin' ? 'BUILT-IN' : 'CUSTOM'));
            for (const theme of group) {
                const row = document.createElement('div');
                row.className = 'workspace-theme-row';
                row.appendChild(this.menuRow(
                    theme.name,
                    theme.isUpdated ? 'Updated' : theme.isCurrent ? 'Current' : '',
                    () => Bridge.sendThemeAction('preview', theme.key),
                    !theme.isAvailable,
                    theme.diagnostic
                ));
                if (theme.isPreview) {
                    const confirm = document.createElement('button');
                    confirm.type = 'button';
                    confirm.className = 'workspace-theme-confirm';
                    confirm.textContent = 'Confirm';
                    confirm.addEventListener('click', () => {
                        Bridge.sendThemeAction('confirm', theme.key);
                        this.closeMenu(false);
                    });
                    row.appendChild(confirm);
                }
                menu.appendChild(row);
            }
        }
        if (this.themeCatalog.message) {
            const message = document.createElement('p');
            message.className = 'workspace-popover-message';
            message.dataset.error = this.themeCatalog.isMessageError ? 'true' : 'false';
            message.textContent = this.themeCatalog.message;
            menu.appendChild(message);
        }
        const footer = document.createElement('div');
        footer.className = 'workspace-popover-footer';
        footer.append(
            this.actionButton('Refresh', () => Bridge.sendThemeAction('refresh')),
            this.actionButton('Open theme folder', () => Bridge.sendThemeAction('open_folder'))
        );
        menu.appendChild(footer);
    }

    // Column menu (opened from a tab's context menu): move the tab to a fresh
    // right-hand column (double-gated by the C# canSplitRight decision and the
    // frontend pixel capacity) or merge every column into one ("合并为单列",
    // decision G). Cross-column tab drags are a later phase.
    renderPaneMenu(menu, workspace) {
        if (!workspace) return;
        menu.appendChild(this.heading(workspace.title));
        const capacity = this.capacityChecker?.() ?? null;
        const newPaneKind = workspace.kind === 'terminal' ? 'terminal' : 'agent';
        // Splitting the sole tab of its column collapses that column — no
        // column count grows, so the pixel gate must not add the phantom
        // new-column minimum width (the removed column's minimum equals the
        // new one's: the same workspace).
        const sourceColumnTabs = this.sourceColumnTabCount(workspace);
        const capacityFits = capacity === null || sourceColumnTabs === 1
            ? true
            : (newPaneKind === 'terminal' ? capacity.fitsTerminal : capacity.fitsAgent);
        const splitBlocked = !workspace.canSplitRight;
        const capacityBlocked = capacity !== null && !capacityFits;
        // Layered gating: the C# canSplitRight decision and the frontend
        // pixel capacity both block the split entry; the C# reason text wins
        // the secondary line when present, otherwise the capacity text.
        menu.appendChild(this.menuRow(
            '移到右侧新列',
            (splitBlocked ? workspace.splitBlockedReason || '' : '') || (capacityBlocked ? '窗口宽度不足以容纳新列' : ''),
            () => {
                Bridge.sendWorkspaceLayoutIntent('split_right', workspace.workspaceId);
                this.closeMenu(false);
            },
            splitBlocked || capacityBlocked
        ));
        if (workspace.canCollapse) menu.appendChild(this.menuRow('合并为单列', '', () => {
            Bridge.sendWorkspaceLayoutIntent('collapse_single');
            this.closeMenu(false);
        }));
    }

    // The number of tabs in the workspace's requested column, or null when
    // the layout has no such column yet. A count of one means the split
    // collapses its source column (the total column count does not grow).
    sourceColumnTabCount(workspace) {
        const columns = this.layout?.requested?.columns ?? this.layout?.columns ?? [];
        const column = columns.find((item) =>
            (item.tabs || []).some((tab) => tab.workspaceId === workspace.workspaceId)
        );
        return column ? column.tabs.length : null;
    }

    heading(text) {
        const heading = document.createElement('h2');
        heading.className = 'workspace-popover-heading';
        heading.textContent = text;
        return heading;
    }

    subheading(text) {
        const heading = document.createElement('div');
        heading.className = 'workspace-popover-subheading';
        heading.textContent = text;
        return heading;
    }

    menuRow(primaryText, secondaryText, action, disabled = false, title = '') {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'workspace-menu-row';
        button.disabled = disabled;
        if (title) button.title = title;
        const primary = document.createElement('span');
        primary.className = 'workspace-menu-primary';
        primary.textContent = primaryText;
        const secondary = document.createElement('span');
        secondary.className = 'workspace-menu-secondary';
        secondary.textContent = secondaryText;
        button.append(primary, secondary);
        button.addEventListener('click', action);
        return button;
    }

    actionButton(text, action) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'workspace-popover-action';
        button.textContent = text;
        button.addEventListener('click', action);
        return button;
    }

    handleDocumentKeyDown(event) {
        if (!this.openMenu) return;
        if (event.key === 'Escape') {
            event.preventDefault();
            this.closeMenu(this.openMenu === 'theme');
            return;
        }
        if (!['ArrowDown', 'ArrowUp'].includes(event.key)) return;
        const buttons = [...this.portalRoot.querySelectorAll('button:not(:disabled)')];
        if (!buttons.length) return;
        const current = buttons.indexOf(document.activeElement);
        const delta = event.key === 'ArrowDown' ? 1 : -1;
        buttons[(current + delta + buttons.length) % buttons.length].focus();
        event.preventDefault();
    }
}
