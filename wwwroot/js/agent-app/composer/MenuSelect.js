// MenuSelect.ts — compact custom dropdown for the composer footer.
//
// A ghost trigger that only takes the space of its current value plus a small
// chevron; the option list opens on demand above the trigger. Replaces the
// native <select>, whose system chrome is both wide and unthemeable inside
// WebView. All colors come from Agent tokens.
const CHEVRON_SVG = '<svg class="agent-menu-select-chevron" viewBox="0 0 10 6" width="8" height="5" aria-hidden="true" focusable="false"><path d="M1 1l4 4 4-4" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"/></svg>';
const CHECK_SVG = '<svg viewBox="0 0 12 10" width="11" height="9" aria-hidden="true" focusable="false"><path d="M1 5l3.5 3.5L11 1" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>';
export class MenuSelect {
    static openInstance = null;
    element;
    valueSpan;
    onSelect;
    ariaName;
    items = [];
    currentValue = '';
    popup = null;
    activeIndex = -1;
    onDocumentPointerDown = (event) => {
        const target = event.target;
        if (!target)
            return;
        if (this.element.contains(target) || this.popup?.contains(target))
            return;
        this.close({ refocus: false });
    };
    onWindowResize = () => this.close();
    // Capture-phase window scroll sees the popup's own overflow scrolling too;
    // only close for movement outside the popup. Resolve Node through the
    // owner document so this works no matter how the host sets up globals.
    onWindowScroll = (event) => {
        const view = this.element.ownerDocument.defaultView;
        if (view && this.popup && event.target instanceof view.Node && this.popup.contains(event.target))
            return;
        this.close();
    };
    constructor(onSelect, ariaLabel) {
        this.onSelect = onSelect;
        this.ariaName = ariaLabel ?? '';
        this.element = document.createElement('button');
        this.element.type = 'button';
        this.element.className = 'agent-menu-select';
        this.element.setAttribute('aria-haspopup', 'listbox');
        this.element.setAttribute('aria-expanded', 'false');
        this.valueSpan = document.createElement('span');
        this.valueSpan.className = 'agent-menu-select-value';
        this.element.appendChild(this.valueSpan);
        this.element.insertAdjacentHTML('beforeend', CHEVRON_SVG);
        this.element.addEventListener('click', () => this.toggle());
        this.element.addEventListener('keydown', (event) => this.onTriggerKeydown(event));
    }
    get value() {
        return this.currentValue;
    }
    get isOpen() {
        return this.popup !== null;
    }
    setItems(items, currentValue) {
        this.items = items;
        this.setValue(currentValue);
        if (this.popup)
            this.renderOptions();
    }
    setValue(value) {
        this.currentValue = value;
        const item = this.items.find((candidate) => candidate.value === value);
        this.valueSpan.textContent = item?.label || value || 'default';
        this.element.title = item?.title || item?.label || '';
        // The accessible name carries both the control name and the current value;
        // a bare aria-label would hide the value from screen readers.
        if (this.ariaName)
            this.element.setAttribute('aria-label', this.ariaName + ': ' + this.valueSpan.textContent);
        if (this.popup)
            this.renderOptions();
    }
    setDisabled(disabled) {
        this.element.disabled = disabled;
        if (disabled)
            this.close({ refocus: false });
    }
    destroy() {
        this.close({ refocus: false });
    }
    toggle() {
        if (this.popup)
            this.close();
        else
            this.open();
    }
    open() {
        if (this.popup || this.element.disabled || this.items.length === 0)
            return;
        // Menus are mutually exclusive: opening one closes any other open menu.
        MenuSelect.openInstance?.close();
        MenuSelect.openInstance = this;
        this.popup = document.createElement('div');
        this.popup.className = 'agent-menu-select-popup';
        this.popup.setAttribute('role', 'listbox');
        this.popup.addEventListener('keydown', (event) => this.onPopupKeydown(event));
        document.body.appendChild(this.popup);
        this.renderOptions();
        this.positionPopup();
        this.element.setAttribute('aria-expanded', 'true');
        document.addEventListener('pointerdown', this.onDocumentPointerDown, true);
        window.addEventListener('resize', this.onWindowResize);
        window.addEventListener('scroll', this.onWindowScroll, true);
        const active = this.popup.children[Math.max(0, this.activeIndex)];
        active?.focus();
    }
    close(options) {
        if (!this.popup)
            return;
        // Removing a focused option strands focus on <body>; hand it back to the
        // trigger unless the caller knows focus is going elsewhere (outside click,
        // disable, destroy).
        const refocus = options?.refocus
            ?? (this.popup.contains(document.activeElement) && !this.element.disabled);
        if (MenuSelect.openInstance === this)
            MenuSelect.openInstance = null;
        this.popup.remove();
        this.popup = null;
        this.activeIndex = -1;
        this.element.setAttribute('aria-expanded', 'false');
        document.removeEventListener('pointerdown', this.onDocumentPointerDown, true);
        window.removeEventListener('resize', this.onWindowResize);
        window.removeEventListener('scroll', this.onWindowScroll, true);
        if (refocus)
            this.element.focus();
    }
    renderOptions() {
        if (!this.popup)
            return;
        this.popup.innerHTML = '';
        this.activeIndex = Math.max(0, this.items.findIndex((item) => item.value === this.currentValue));
        this.items.forEach((item, index) => {
            const option = document.createElement('div');
            option.className = 'agent-menu-select-option';
            option.dataset.value = item.value;
            option.setAttribute('role', 'option');
            option.setAttribute('aria-selected', String(item.value === this.currentValue));
            option.tabIndex = -1;
            if (item.title)
                option.title = item.title;
            const label = document.createElement('span');
            label.textContent = item.label;
            option.appendChild(label);
            if (item.value === this.currentValue)
                option.insertAdjacentHTML('beforeend', CHECK_SVG);
            option.addEventListener('click', () => this.select(item.value));
            option.addEventListener('mousemove', () => this.setActive(index, false));
            this.popup.appendChild(option);
        });
    }
    positionPopup() {
        if (!this.popup)
            return;
        const rect = this.element.getBoundingClientRect();
        this.popup.style.left = Math.max(4, Math.min(rect.left, window.innerWidth - this.popup.offsetWidth - 4)) + 'px';
        this.popup.style.bottom = window.innerHeight - rect.top + 6 + 'px';
    }
    setActive(index, focus) {
        if (!this.popup || index < 0 || index >= this.popup.children.length)
            return;
        this.activeIndex = index;
        [...this.popup.children].forEach((child, childIndex) => {
            child.classList.toggle('agent-menu-select-option-active', childIndex === index);
        });
        const active = this.popup.children[index];
        if (typeof active.scrollIntoView === 'function')
            active.scrollIntoView({ block: 'nearest' });
        if (focus)
            active.focus();
    }
    select(value) {
        this.close();
        this.element.focus();
        if (value)
            this.onSelect(value);
    }
    onTriggerKeydown(event) {
        if (event.key === 'ArrowDown' || event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            this.open();
        }
    }
    onPopupKeydown(event) {
        switch (event.key) {
            case 'ArrowDown':
                event.preventDefault();
                this.setActive(Math.min(this.items.length - 1, this.activeIndex + 1), true);
                break;
            case 'ArrowUp':
                event.preventDefault();
                this.setActive(Math.max(0, this.activeIndex - 1), true);
                break;
            case 'Home':
                event.preventDefault();
                this.setActive(0, true);
                break;
            case 'End':
                event.preventDefault();
                this.setActive(this.items.length - 1, true);
                break;
            case 'Enter':
            case ' ':
                event.preventDefault();
                if (this.activeIndex >= 0)
                    this.select(this.items[this.activeIndex].value);
                break;
            case 'Escape':
                event.preventDefault();
                this.close();
                this.element.focus();
                break;
            case 'Tab':
                // Focus the trigger before the popup (and the focused option) is
                // removed, so native Tab navigation continues from the trigger
                // instead of stranding focus on <body>.
                this.element.focus();
                this.close({ refocus: false });
                break;
        }
    }
}
