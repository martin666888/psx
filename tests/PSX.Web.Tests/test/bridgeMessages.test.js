import assert from 'node:assert/strict';
import { beforeEach, describe, it } from 'node:test';
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
      AgentReady: 'agent_ready',
      AgentState: 'agent_state',
      RuntimeStatus: 'runtime_status',
      AgentThreadLoaded: 'agent_thread_loaded',
      AgentThreads: 'agent_threads',
      AgentHistoryError: 'agent_history_error',
      AgentCommands: 'agent_commands',
      AgentCommandRejected: 'agent_command_rejected',
      AgentModes: 'agent_modes',
      AgentConfigOptions: 'agent_config_options',
      AgentUsageUpdate: 'agent_usage_update',
      AgentModeCurrent: 'agent_mode_current',
      AgentAttachmentUploaded: 'agent_attachment_uploaded',
      AgentAttachmentFailed: 'agent_attachment_failed',
      AgentCleared: 'agent_cleared',
      CommandResult: 'command_result',
      UserMessage: 'user_message',
      RunFinished: 'run_finished',
      AssistantDelta: 'assistant_delta',
      AssistantMessageDone: 'assistant_message_done',
      ThinkingStarted: 'thinking_started',
      ThinkingFinished: 'thinking_finished',
      ThinkingDelta: 'thinking_delta',
      ToolStarted: 'tool_started',
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
