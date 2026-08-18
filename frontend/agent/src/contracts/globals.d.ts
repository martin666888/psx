// globals.d.ts — ambient declarations for the Bridge global.
//
// Bridge.js is an ES module bundled into the Vite entry chunk, but it still
// publishes the Bridge singleton on globalThis (the same mirror the shipped
// app and the test harness rely on). These declarations let the Agent ESM
// modules reference it without importing the webview module.

declare global {
  /** The global Bridge singleton defined in frontend/webview/src/Bridge.js. */
  interface BridgeGlobal {
    sendToHost(message: BridgeOutboundMessage): void;
    onHostMessage(callback: (message: BridgeInboundMessage) => void): void;
    sendReady(): void;
    sendAgentGlobalCommand(command: string, value?: string | boolean, requestId?: string): void;
    createAgentScope(
      workspaceId: string
    ): import('./bridge-port.js').AgentBridgePort;
  }

  // `var` (not `const`) so Bridge.js can assign the globalThis mirror.
  var Bridge: BridgeGlobal;

  interface Window {
    Bridge: BridgeGlobal;
  }
}

export {};
