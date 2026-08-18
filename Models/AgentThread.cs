namespace PSX.Models;

public sealed class AgentThread
{
    public string ThreadId { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "Agent Chat";
    public string Cwd { get; set; } = "";
    public string Provider { get; set; } = "claude-cli";
    public string? ClaudeSessionId { get; set; }
    public string? AcpSessionId { get; set; }
    public string? ModeId { get; set; }
    public string? AdapterVersion { get; set; }
    public bool ContainsImages { get; set; }
    public long? ContextUsedTokens { get; set; }
    public long? ContextWindowTokens { get; set; }
    public decimal? ContextCostAmount { get; set; }
    public string? ContextCostCurrency { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public List<AgentMessage> Messages { get; set; } = new();
}

public sealed class AgentThreadSummary
{
    public string ThreadId { get; set; } = "";
    public string Title { get; set; } = "Agent Chat";
    public string Cwd { get; set; } = "";
    public string? ClaudeSessionId { get; set; }
    public string? AcpSessionId { get; set; }
    public string Provider { get; set; } = "claude-cli";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class AgentThreadIndex
{
    public List<AgentThreadSummary> Threads { get; set; } = new();
}

public sealed class PsxConfig
{
    public PsxAgentConfig Agent { get; set; } = new();
}

public sealed class PsxAgentConfig
{
    public string? LastThreadId { get; set; }
    public string? LastWorkingDirectory { get; set; }
    public bool RestoreLastThread { get; set; } = true;
}
