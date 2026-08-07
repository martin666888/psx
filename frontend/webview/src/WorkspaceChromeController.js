import { Bridge } from './Bridge.js';

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
        // The four global buttons live in the left activity rail
        // (index.html), not inside the chrome row; query them document-wide
        // by data-role so the constructor signature stays (root, portalRoot).
        this.rail = document.getElementById('activity-rail');
        this.nameplates = root.querySelector('[data-role="pane-nameplates"]');
        this.historyButton = document.querySelector('[data-role="history-toggle"]');
        this.workspaceButton = document.querySelector('[data-role="workspace-menu-toggle"]');
        this.createButton = document.querySelector('[data-role="workspace-create-toggle"]');
        this.themeButton = document.querySelector('[data-role="theme-toggle"]');
        this.catalogRevision = -1;
        this.themeRevision = -1;
        this.catalog = { workspaces: [], providers: [], maxPanes: 4 };
        this.themeCatalog = { themes: [] };
        this.layout = null;
        this.rects = new Map();
        this.openMenu = null;
        this.openTrigger = null;
        this.createPlacement = 'focused';
        this.capacityChecker = null;
        this.historyButton.disabled = true;
        this.historyButton.setAttribute('aria-expanded', 'false');

        this.historyButton.addEventListener('click', () => {
            document.dispatchEvent(new window.CustomEvent('psx-history-toggle'));
        });
        this.workspaceButton.addEventListener('click', () => this.toggleMenu('workspaces', this.workspaceButton));
        this.createButton.addEventListener('click', () => this.toggleMenu('create', this.createButton));
        this.themeButton.addEventListener('click', () => this.toggleMenu('theme', this.themeButton));
        this.onDocumentPointerDown = (event) => {
            if (!this.openMenu) return;
            if (this.portalRoot.contains(event.target)) return;
            if (this.root.contains(event.target) || this.rail?.contains(event.target)) return;
            this.closeMenu(this.openMenu === 'theme');
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
            maxPanes: Number(message.maxPanes) || 4
        };
        this.historyButton.disabled = !this.catalog.workspaces.some((workspace) => workspace.kind === 'agent');
        // The open pane menu holds a workspace object from the previous
        // catalog: rebind it to the fresh entry so capabilities (canSplitRight,
        // availableTargetPanes…) never render stale; the menu closes when its
        // workspace is gone.
        if (this.openMenu === 'pane' && this.paneMenuWorkspace) {
            const refreshed = this.catalog.workspaces.find(
                (item) => item.workspaceId === this.paneMenuWorkspace.workspaceId
            );
            if (refreshed) this.paneMenuWorkspace = refreshed;
            else this.closeMenu(false, false);
        }
        this.renderNameplates();
        if (this.openMenu === 'workspaces' || this.openMenu === 'create' || this.openMenu === 'pane') {
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
        this.renderNameplates();
        if (this.openMenu === 'workspaces' || this.openMenu === 'pane') this.renderOpenMenu();
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

    // Injected by main.js: returns { fitsAgent, fitsTerminal } describing
    // whether the requested layout plus one more pane of each kind fits the
    // current width. Frontend pixel gating is the first defensive line; the
    // C# 4-column count in the catalog remains the second.
    setCapacityChecker(checker) {
        this.capacityChecker = typeof checker === 'function' ? checker : null;
    }

    renderNameplates() {
        if (!this.layout || !this.nameplates) return;
        const desired = new Set();
        this.layout.panes.forEach((pane, index) => {
            desired.add(pane.paneId);
            let plate = [...this.nameplates.children].find((item) => item.dataset.paneId === pane.paneId);
            if (!plate) {
                plate = document.createElement('div');
                plate.className = 'workspace-nameplate';
                plate.dataset.paneId = pane.paneId;
                plate.addEventListener('click', () => Bridge.sendPaneFocus(plate.dataset.paneId));
                this.nameplates.appendChild(plate);
            }
            const rect = this.rects.get(pane.paneId);
            if (rect) {
                plate.style.left = `${rect.left}px`;
                plate.style.width = `${rect.width}px`;
            }
            plate.dataset.focused = pane.paneId === this.layout.focusedPaneId ? 'true' : 'false';
            plate.dataset.last = index === this.layout.panes.length - 1 ? 'true' : 'false';
            this.fillNameplate(plate, pane);
        });
        for (const plate of [...this.nameplates.children]) {
            if (!desired.has(plate.dataset.paneId)) plate.remove();
        }
    }

    fillNameplate(plate, pane) {
        const workspace = this.catalog.workspaces.find((item) => item.workspaceId === pane.workspaceId);
        plate.replaceChildren();
        const icon = document.createElement('span');
        icon.className = 'workspace-nameplate-icon';
        icon.setAttribute('aria-hidden', 'true');
        icon.textContent = this.iconText(workspace?.iconKey, workspace?.kind ?? pane.kind);
        const title = document.createElement('span');
        title.className = 'workspace-nameplate-title';
        title.textContent = workspace?.title || 'Workspace';
        const attention = document.createElement('span');
        attention.className = 'workspace-nameplate-attention';
        attention.dataset.kind = workspace?.attentionKind || '';
        attention.textContent = ATTENTION_LABELS[workspace?.attentionKind] || '';
        const close = document.createElement('button');
        close.type = 'button';
        close.className = 'workspace-chrome-icon-button workspace-nameplate-close';
        close.setAttribute('aria-label', `关闭 ${workspace?.title || '工作区'}`);
        close.textContent = '×';
        close.disabled = !workspace;
        close.addEventListener('click', (event) => {
            event.stopPropagation();
            if (workspace) Bridge.sendWorkspaceLayoutIntent('close', workspace.workspaceId);
        });
        const more = document.createElement('button');
        more.type = 'button';
        more.className = 'workspace-chrome-icon-button workspace-nameplate-more';
        more.setAttribute('aria-label', `${workspace?.title || '工作区'} 操作`);
        more.textContent = '⋯';
        more.disabled = !workspace;
        more.addEventListener('click', (event) => {
            event.stopPropagation();
            if (workspace) this.openPaneMenu(workspace, more);
        });
        plate.oncontextmenu = (event) => {
            event.preventDefault();
            if (workspace) this.openPaneMenu(workspace, more);
        };
        plate.append(icon, title, attention, more, close);
    }

    iconText(iconKey, kind) {
        if (kind === 'terminal') return '>_';
        const labels = { claude: 'C', kimi: 'K', qwen: 'Q', qoder: 'Q', opencode: 'O' };
        return labels[iconKey] || '✦';
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
        const menu = document.createElement('section');
        menu.className = `workspace-popover workspace-popover-${this.openMenu}`;
        menu.dataset.menu = this.openMenu;
        menu.setAttribute('role', 'dialog');
        menu.setAttribute('aria-label', this.menuLabel());
        if (this.openMenu === 'pane' && this.openTrigger) {
            // The pane menu's trigger lives on a nameplate anywhere across the
            // chrome row: anchor directly under it. The exact left is set after
            // mount (measured width), right-aligned to the trigger so it never
            // overflows the window's right edge — a transform would fight the
            // popover-in animation's translateY.
            const rect = this.openTrigger.getBoundingClientRect();
            menu.dataset.anchorRight = String(rect.right);
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
        if (this.openMenu === 'workspaces') this.renderWorkspaceList(menu);
        if (this.openMenu === 'create') this.renderCreateMenu(menu);
        if (this.openMenu === 'theme') this.renderThemeMenu(menu);
        if (this.openMenu === 'pane') this.renderPaneMenu(menu, this.paneMenuWorkspace);
        this.portalRoot.replaceChildren(menu);
        if (menu.dataset.anchorRight) {
            const right = Number(menu.dataset.anchorRight);
            const left = Math.max(44, right - menu.offsetWidth);
            menu.style.left = `${left}px`;
            delete menu.dataset.anchorRight;
        }
        window.queueMicrotask(() => menu.querySelector('button:not(:disabled)')?.focus());
    }

    menuLabel() {
        return { workspaces: '工作区', create: '新建工作区', theme: '主题', pane: '工作区操作' }[this.openMenu] || '菜单';
    }

    renderWorkspaceList(menu) {
        menu.appendChild(this.heading('工作区'));
        const effectiveIds = new Set((this.layout?.panes ?? []).map((pane) => pane.workspaceId));
        const requestedPaneIds = new Set(this.catalog.workspaces.filter((item) => item.paneId).map((item) => item.paneId));
        const paneOrder = new Map((this.layout?.panes ?? []).map((pane, index) => [pane.paneId, index + 1]));
        for (const workspace of this.catalog.workspaces) {
            let location = '后台';
            if (effectiveIds.has(workspace.workspaceId)) location = `第 ${paneOrder.get(workspace.paneId) ?? '?'} 列`;
            else if (workspace.paneId && requestedPaneIds.has(workspace.paneId)) location = '临时收编';
            const label = [location, ATTENTION_LABELS[workspace.attentionKind]].filter(Boolean).join(' · ');
            menu.appendChild(this.menuRow(
                `${workspace.providerName ? workspace.providerName + ' · ' : ''}${workspace.title}`,
                label,
                () => {
                    Bridge.sendWorkspaceLayoutIntent('activate', workspace.workspaceId);
                    this.closeMenu(false);
                }
            ));
        }
    }

    renderCreateMenu(menu) {
        menu.appendChild(this.heading('新建工作区'));
        const capacity = this.capacityChecker?.() ?? null;
        const segments = document.createElement('div');
        segments.className = 'workspace-segments';
        for (const [value, label] of [['focused', '当前列'], ['new_right', '右侧新列']]) {
            const button = document.createElement('button');
            button.type = 'button';
            button.textContent = label;
            button.dataset.selected = this.createPlacement === value ? 'true' : 'false';
            // Layered gating: the C# 4-column count (maxPanes) and the
            // frontend pixel capacity both disable new-right; only capacity
            // carries the explanatory title. The frontend gate is the first
            // defensive line, the C# count the second.
            const maxPanesBlocked = value === 'new_right' && (this.layout?.panes.length ?? 1) >= this.catalog.maxPanes;
            const capacityBlocked = value === 'new_right' && capacity !== null && !capacity.fitsAgent;
            button.disabled = maxPanesBlocked || capacityBlocked;
            if (capacityBlocked) button.title = '窗口宽度不足以容纳新列';
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

    renderPaneMenu(menu, workspace) {
        if (!workspace) return;
        menu.appendChild(this.heading(workspace.title));
        const capacity = this.capacityChecker?.() ?? null;
        const newPaneKind = workspace.kind === 'terminal' ? 'terminal' : 'agent';
        const capacityFits = capacity === null
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
        for (const entry of workspace.availableTargetPanes || []) {
            menu.appendChild(this.menuRow(`移到第 ${entry.column} 列`, '', () => {
                Bridge.sendWorkspaceLayoutIntent('move_to_pane', workspace.workspaceId, entry.paneId);
                this.closeMenu(false);
            }));
        }
        if (workspace.canSwap) menu.appendChild(this.menuRow('交换两列', '', () => {
            Bridge.sendWorkspaceLayoutIntent('swap');
            this.closeMenu(false);
        }));
        if (workspace.canCollapse) menu.appendChild(this.menuRow('收回单列', '', () => {
            Bridge.sendWorkspaceLayoutIntent('collapse_single');
            this.closeMenu(false);
        }));
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
