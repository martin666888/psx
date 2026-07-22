// bridge-port.ts — the workspace-scoped outbound bridge surface.
//
// Wraps the object returned by the global Bridge.createAgentScope(workspaceId).
// Feature controllers depend on this interface, never on the global Bridge, so
// they stay testable and scoped to a single workspace.

export interface AgentBridgePort {
  sendAgentMessage(text: string, attachments?: string[]): void;
  uploadAgentAttachment(payload: AgentAttachmentUploadPayload): void;
  sendAgentCommand(command: string, value?: string | boolean, requestId?: string): void;
  sendAgentPermissionResponse(requestId: string, value: string): void;
  sendAgentQuestionResponse(requestId: string, value: string): void;
  sendAgentElicitationResponse(requestId: string, value: string): void;
}
