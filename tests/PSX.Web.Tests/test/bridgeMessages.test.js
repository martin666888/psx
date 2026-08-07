import assert from 'node:assert/strict';
import { beforeEach, describe, it } from 'vitest';
import { installAgentRuntime } from './agentHarness.js';

// Completeness gate for the bridge message constants: every key/value is
// asserted literally so an accidental edit cannot slip through as a
// "harmless" rename. Values must match the C# bridge contract.
describe('Bridge message type constants', () => {
  let sendType;
  let eventType;

  beforeEach(() => {
    installAgentRuntime();
    sendType = globalThis.BridgeSendType;
    eventType = globalThis.BridgeEventType;
  });

  it('mirrors the JS -> C# payload types exactly', () => {
    assert.deepEqual({ ...sendType }, {
      Input: 'input',
      Resize: 'resize',
      Title: 'title',
      PasteRequest: 'paste_request',
      Ready: 'ready',
      PaneFocus: 'pane_focus',
      PaneRatiosCommit: 'pane_ratios_commit',
      PaneMove: 'pane_move',
      WorkspaceLayoutIntent: 'workspace_layout_intent',
      WorkspaceCreate: 'workspace_create',
      ThemeAction: 'theme_action',
      AgentSubmit: 'agent_submit',
      AgentUploadAttachment: 'agent_upload_attachment',
      AgentCommand: 'agent_command',
      AgentPermissionResponse: 'agent_permission_response',
      AgentQuestionResponse: 'agent_question_response',
      AgentElicitationResponse: 'agent_elicitation_response'
    });
  });

  it('mirrors the C# -> JS event types exactly', () => {
    assert.deepEqual({ ...eventType }, {
      Settings: 'settings',
      AppearanceSettings: 'appearance_settings',
      Create: 'create',
      Switch: 'switch',
      Output: 'output',
      Resize: 'resize',
      PasteResponse: 'paste_response',
      Close: 'close',
      ViewMode: 'view_mode',
      WorkspaceActivated: 'workspace_activated',
      WorkspaceLayout: 'workspace_layout',
      PaneZoomToggle: 'pane_zoom_toggle',
      WorkspaceCatalog: 'workspace_catalog',
      WorkspaceNotice: 'workspace_notice',
      ThemeCatalog: 'theme_catalog',
      AgentWorkspaceCreated: 'agent_workspace_created',
      AgentWorkspaceClosed: 'agent_workspace_closed',
      AgentWorkspaceLimitReached: 'agent_workspace_limit_reached',
      AgentProviders: 'agent_providers',
      AgentReady: 'agent_ready',
      AgentState: 'agent_state',
      RuntimeStatus: 'runtime_status',
      RuntimeUpdateStatus: 'runtime_update_status',
      AgentThreadLoaded: 'agent_thread_loaded',
      AgentThreads: 'agent_threads',
      AgentHistoryError: 'agent_history_error',
      AgentHistoryInvalidated: 'agent_history_invalidated',
      AgentThreadOpenError: 'agent_thread_open_error',
      AgentProfile: 'agent_profile',
      AgentUsageReport: 'agent_usage_report',
      AgentConfigReport: 'agent_config_report',
      AgentCommands: 'agent_commands',
      AgentCommandRejected: 'agent_command_rejected',
      AgentModes: 'agent_modes',
      AgentConfigOptions: 'agent_config_options',
      AgentUsageUpdate: 'agent_usage_update',
      AgentModeCurrent: 'agent_mode_current',
      AgentAttachmentUploaded: 'agent_attachment_uploaded',
      AgentAttachmentFailed: 'agent_attachment_failed',
      CommandResult: 'command_result',
      UserMessage: 'user_message',
      RunFinished: 'run_finished',
      AssistantDelta: 'assistant_delta',
      AssistantMessageDone: 'assistant_message_done',
      ThinkingStarted: 'thinking_started',
      ThinkingFinished: 'thinking_finished',
      ThinkingDelta: 'thinking_delta',
      ToolStarted: 'tool_started',
      ToolUpdated: 'tool_updated',
      ToolDelta: 'tool_delta',
      ToolFinished: 'tool_finished',
      PermissionRequest: 'permission_request',
      PermissionResolved: 'permission_resolved',
      QuestionRequest: 'question_request',
      ElicitationRequest: 'elicitation_request',
      PermissionCancelled: 'permission_cancelled',
      ElicitationCancelled: 'elicitation_cancelled',
      RunFailed: 'run_failed',
      ResumeFailed: 'resume_failed',
      PlanUpdate: 'plan_update',
      RawTerminalFallback: 'raw_terminal_fallback'
    });
  });

  it('freezes both tables against runtime mutation', () => {
    assert.equal(Object.isFrozen(sendType), true);
    assert.equal(Object.isFrozen(eventType), true);
  });
});
