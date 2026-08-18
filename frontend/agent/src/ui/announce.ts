// announce.ts — pane-focus live-region gate (split panes a11y).
//
// Every workspace-local live region must only announce while its pane is
// focused; unfocused panes keep rendering (they are visible) but never
// interrupt the reader. Controllers stamp the gate through view props; deep
// components read it from this context instead of prop drilling.

import { createContext, useContext } from 'react';

export const AnnounceContext = createContext(true);

/** 'polite' while the pane is focused, 'off' otherwise. */
export function useAnnounceLive(): 'polite' | 'off' {
  return useContext(AnnounceContext) ? 'polite' : 'off';
}
