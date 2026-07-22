// AgentShellLayoutController.ts — shell-level responsive layout coordination.
//
// The Shell owns two related responsive rules. Below 1000px, both context
// panels temporarily collapse and manually reopened panels are exclusive. At
// wider sizes, History remains a dock only while the full reading column fits
// beside it; otherwise History temporarily yields and the conversation keeps
// its comfortable width instead of leaving a mirrored blank strip on the
// right. Plan remains an overlay and never participates in this calculation.
const WIDE_QUERY = '(min-width: 1000px)';
const READING_MAX_WIDTH = 920;
const DOCK_GAP = 16;
const VIEWPORT_PADDING = 24;
export class AgentShellLayoutController {
    container;
    host;
    mql = null;
    narrow = false;
    historyReadingConstrained = false;
    cleanup = [];
    onMqlChange = () => this.applyMode();
    onResize = () => this.applyMode();
    constructor(container, host) {
        this.container = container;
        this.host = host;
    }
    mount() {
        this.host.onHistoryOpenChanged((open) => this.onHistoryOpenChanged(open));
        this.host.onHistoryWidthChanged(() => this.applyMode());
        this.host.onActivePlanVisibilityChanged((visible) => this.onPlanVisibilityChanged(visible));
        this.mql = window.matchMedia(WIDE_QUERY);
        this.mql.addEventListener?.('change', this.onMqlChange);
        window.addEventListener('resize', this.onResize);
        this.cleanup.push(() => window.removeEventListener('resize', this.onResize));
        this.applyMode();
        const onKeydown = (event) => this.onKeydown(event);
        document.addEventListener('keydown', onKeydown);
        this.cleanup.push(() => document.removeEventListener('keydown', onKeydown));
    }
    dispose() {
        this.mql?.removeEventListener?.('change', this.onMqlChange);
        for (const off of this.cleanup.splice(0))
            off();
    }
    applyMode() {
        const narrow = this.mql ? !this.mql.matches : false;
        const historyReadingConstrained = !narrow && this.historyWouldSqueezeReading();
        if (this.narrow === narrow && this.historyReadingConstrained === historyReadingConstrained)
            return;
        this.narrow = narrow;
        this.historyReadingConstrained = historyReadingConstrained;
        // Presentation only: History remains a dock and Plan remains a context
        // card. This class never turns either panel into a drawer.
        this.container.classList.toggle('agent-shell-narrow', narrow);
        this.container.classList.toggle('agent-shell-history-collapsed-for-reading', historyReadingConstrained);
        this.host.setResponsiveLayout(narrow, historyReadingConstrained);
    }
    historyWouldSqueezeReading() {
        // Keep the 920px reading column intact whenever it can sit to the right of
        // the dock with one dock gap and the normal right viewport padding. This
        // deliberately does not reserve a mirrored History-width strip on the
        // right: the column is centered when possible and otherwise yields left.
        const requiredWidth = this.host.historyWidth() + DOCK_GAP + READING_MAX_WIDTH + VIEWPORT_PADDING;
        return this.viewportWidth() < requiredWidth;
    }
    viewportWidth() {
        const documentWidth = document.documentElement?.clientWidth ?? 0;
        return documentWidth > 0 ? documentWidth : window.innerWidth;
    }
    onHistoryOpenChanged(open) {
        if (!open)
            return;
        if (this.narrow && this.host.isActivePlanVisible())
            this.host.closeActivePlan();
        // User-initiated History opening inside the reading-constrained range is
        // allowed. CSS then gives it the available width without changing the
        // persisted preference; we do not immediately re-collapse it here.
    }
    onPlanVisibilityChanged(visible) {
        if (visible && this.narrow && this.host.isHistoryOpen()) {
            this.host.closeHistory();
        }
    }
    onKeydown(event) {
        if (event.key !== 'Escape' || !this.narrow || event.defaultPrevented)
            return;
        if (this.host.isHistoryOpen()) {
            this.host.closeHistory();
            this.host.focusHistoryToggle();
        }
        else if (this.host.isActivePlanVisible()) {
            this.host.closeActivePlan();
            this.host.focusActivePlanToggle();
        }
    }
}
