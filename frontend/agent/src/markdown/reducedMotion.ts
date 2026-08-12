const REDUCED_MOTION_QUERY = '(prefers-reduced-motion: reduce)';

let activeWindow: Window | null = null;
let mediaQuery: MediaQueryList | null = null;
let reducedMotion = false;
const subscribers = new Set<() => void>();

function detachMediaQuery(): void {
  mediaQuery?.removeEventListener('change', handleMediaChange);
  mediaQuery = null;
  activeWindow = null;
}

function handleMediaChange(event: MediaQueryListEvent): void {
  if (reducedMotion === event.matches) return;
  reducedMotion = event.matches;
  for (const subscriber of subscribers) subscriber();
}

function ensureMediaQuery(): void {
  const currentWindow = typeof window === 'undefined' ? null : window;
  if (currentWindow === activeWindow && mediaQuery) return;

  detachMediaQuery();
  if (!currentWindow) {
    reducedMotion = false;
    return;
  }

  activeWindow = currentWindow;
  mediaQuery = currentWindow.matchMedia(REDUCED_MOTION_QUERY);
  reducedMotion = mediaQuery.matches;
  mediaQuery.addEventListener('change', handleMediaChange);
}

export function subscribeReducedMotion(subscriber: () => void): () => void {
  ensureMediaQuery();
  subscribers.add(subscriber);
  return () => {
    subscribers.delete(subscriber);
    if (subscribers.size === 0) detachMediaQuery();
  };
}

export function getReducedMotionSnapshot(): boolean {
  ensureMediaQuery();
  return reducedMotion;
}
