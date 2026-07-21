// commands.ts — pure slash-command parsing, normalization and validation.
//
// Faithful port of the pure helpers in wwwroot/js/agent/commands.js
// (_setAgentCommands mapping, _parseLeadingSlashCommand, _validateSubmissionCommand).
// DOM menu rendering and Bridge dispatch stay in the legacy engine for Phase 3;
// only the decision logic moves here. validateSubmissionCommand must stay in
// lockstep with the AcpAgentSessionService authorization gate.
/** Normalizes the wire `agent_commands` list into displayable AgentCommand[]. */
export function normalizeAgentCommands(commands, assistantName) {
    const fallbackLabel = 'Send to ' + assistantName + ' Agent';
    const list = Array.isArray(commands) ? commands : [];
    return list
        .map((command) => {
        if (typeof command === 'string')
            return { name: command, description: fallbackLabel };
        return command || {};
    })
        .filter((command) => typeof command.name === 'string' && command.name.trim() !== '')
        .map((command) => {
        const trimmed = command.name.trim();
        const name = trimmed.startsWith('/') ? trimmed : '/' + trimmed;
        return {
            source: assistantName + ' Agent',
            name,
            label: typeof command.description === 'string' && command.description ? command.description : fallbackLabel,
            agent: true
        };
    });
}
export function parseLeadingSlashCommand(text) {
    const trimmed = String(text ?? '').trim();
    if (!trimmed.startsWith('/'))
        return null;
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
export function validateSubmissionCommand(text, attachmentIds, psxCommands, agentCommands, agentCommandsReady) {
    const parsed = parseLeadingSlashCommand(text);
    if (!parsed)
        return { allowed: true, text };
    const psxCommand = psxCommands.find((command) => {
        const commandName = command.name.trim().split(/\s/, 1)[0];
        return commandName.toLowerCase() === parsed.name.toLowerCase();
    });
    const agentCommand = agentCommands.find((command) => command.name.toLowerCase() === parsed.name.toLowerCase());
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
