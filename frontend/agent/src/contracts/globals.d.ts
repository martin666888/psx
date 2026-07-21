// globals.d.ts — ambient declarations for the classic-script globals.
//
// BridgeMessages.js and Bridge.js are loaded as classic <script> tags before
// entry.js is dynamically imported, so the Bridge singleton lives on the global
// scope. These declarations let the Agent ESM modules reference it without
// importing (Bridge.js is not an ES module).

declare global {
  /** The global Bridge singleton defined in wwwroot/js/Bridge.js. */
  interface BridgeGlobal {
    sendToHost(message: BridgeOutboundMessage): void;
    onHostMessage(callback: (message: BridgeInboundMessage) => void): void;
    sendReady(): void;
    createAgentScope(
      workspaceId: string
    ): import('./bridge-port.js').AgentBridgePort;
  }

  const Bridge: BridgeGlobal;

  interface Window {
    Bridge: BridgeGlobal;
  }
}

export {};
