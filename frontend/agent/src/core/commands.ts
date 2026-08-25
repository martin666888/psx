// commands.ts — pure slash-command parsing, normalization and validation.
//
// Faithful port of the pure helpers in wwwroot/js/agent/commands.js
// (_setAgentCommands mapping, _parseLeadingSlashCommand, _validateSubmissionCommand).
// DOM menu rendering and Bridge dispatch stay in the legacy engine for Phase 3;
// only the decision logic moves here. validateSubmissionCommand must stay in
// lockstep with the AcpAgentSessionService authorization gate.

export interface AgentCommand {
  source: string;
  name: string;
  label: string;
  agent: true;
}

export interface PsxCommand {
  name: string;
  command: string;
  label?: string;
  source?: string;
  fill?: string;
}

export interface ParsedSlashCommand {
  name: string;
  arguments: string;
}

export type SubmissionValidation =
  | { allowed: true; text: string; psxCommand?: string }
  | { allowed: false; command: string; reason: 'attachments_not_allowed' | 'unsupported' | 'commands_loading' };

interface RawAgentCommand {
  name?: unknown;
  description?: unknown;
}

/** Normalizes the wire `agent_commands` list into displayable AgentCommand[]. */
export function normalizeAgentCommands(commands: unknown, assistantName: string): AgentCommand[] {
  void assistantName;
  const list = Array.isArray(commands) ? commands : [];
  return list
    .map((command): RawAgentCommand => {
      if (typeof command === 'string') return { name: command };
      return (command as RawAgentCommand) || {};
    })
    .filter((command): command is { name: string; description?: unknown } =>
      typeof command.name === 'string' && command.name.trim() !== '')
    .map((command) => {
      const trimmed = command.name.trim();
      const name = trimmed.startsWith('/') ? trimmed : '/' + trimmed;
      return {
        source: assistantName + ' Agent',
        name,
        // Empty label = PSX fallback; the composer menu localizes it at render.
        label: typeof command.description === 'string' && command.description ? command.description : '',
        agent: true as const
      };
    });
}

export function parseLeadingSlashCommand(text: unknown): ParsedSlashCommand | null {
  const trimmed = String(text ?? '').trim();
  if (!trimmed.startsWith('/')) return null;

  const separator = trimmed.search(/\s/);
  if (separator < 0) {
    return { name: trimmed, arguments: '' };
  }
  return {
    name: trimmed.slice(0, separator),
    arguments: trimmed.slice(separator).trimStart()
  };
}

/**
 * Decides whether a composer submission is an allowed command or plain text.
 * Mirrors the legacy _validateSubmissionCommand exactly: inline `/text` that
 * matches no known command is only rejected as a command, never treated as one.
 */
export function validateSubmissionCommand(
  text: string,
  attachmentIds: readonly string[],
  psxCommands: readonly PsxCommand[],
  agentCommands: readonly AgentCommand[],
  agentCommandsReady: boolean
): SubmissionValidation {
  const parsed = parseLeadingSlashCommand(text);
  if (!parsed) return { allowed: true, text };

  const psxCommand = psxCommands.find((command) => {
    const commandName = command.name.trim().split(/\s/, 1)[0];
    return commandName.toLowerCase() === parsed.name.toLowerCase();
  });
  const agentCommand = agentCommands.find(
    (command) => command.name.toLowerCase() === parsed.name.toLowerCase());
  const matched = psxCommand || agentCommand;

  if (attachmentIds.length > 0) {
    return { allowed: false, command: parsed.name, reason: 'attachments_not_allowed' };
  }

  if (!matched) {
    return {
      allowed: false,
      command: parsed.name,
      reason: agentCommandsReady ? 'unsupported' : 'commands_loading'
    };
  }

  const canonicalName = matched.name.trim().split(/\s/, 1)[0];
  return {
    allowed: true,
    text: parsed.arguments ? canonicalName + ' ' + parsed.arguments : canonicalName,
    psxCommand: psxCommand?.command || ''
  };
}
