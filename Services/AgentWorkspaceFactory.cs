using System.Text.Json.Nodes;
using PSX.Models;

namespace PSX.Services;

public sealed class AgentWorkspaceSessionHandle : IDisposable
{
    public required IAgentWorkspaceSession Session { get; init; }
    public required AgentWorkspaceEventSink EventSink { get; init; }

    public void Dispose()
    {
        Session.Dispose();
        EventSink.Dispose();
    }
}

public interface IAgentWorkspaceFactory
{
    AgentWorkspaceSessionHandle Create(
        Guid workspaceId,
        IAcpAgentProvider? provider,
        AgentThread thread,
        Func<JsonObject, bool> beforeEvent);
}

public sealed class AgentWorkspaceFactory : IAgentWorkspaceFactory
{
    private readonly IAgentBridgeService _rootBridge;
    private readonly ITabManagementService _tabManagementService;
    private readonly ITerminalBridgeService _terminalBridgeService;
    private readonly IAgentThreadStore _threadStore;
    private readonly IAgentDirectoryPicker _directoryPicker;
    private readonly IAgentProviderRegistry _providerRegistry;

    public AgentWorkspaceFactory(
        IAgentBridgeService rootBridge,
        ITabManagementService tabManagementService,
        ITerminalBridgeService terminalBridgeService,
        IAgentThreadStore threadStore,
        IAgentDirectoryPicker directoryPicker,
        IAgentProviderRegistry providerRegistry)
    {
        _rootBridge = rootBridge;
        _tabManagementService = tabManagementService;
        _terminalBridgeService = terminalBridgeService;
        _threadStore = threadStore;
        _directoryPicker = directoryPicker;
        _providerRegistry = providerRegistry;
    }

    public AgentWorkspaceSessionHandle Create(
        Guid workspaceId,
        IAcpAgentProvider? provider,
        AgentThread thread,
        Func<JsonObject, bool> beforeEvent)
    {
        var eventSink = new AgentWorkspaceEventSink(
            workspaceId,
            _rootBridge,
            beforeEvent);
        IAgentWorkspaceSession session = provider == null
            ? new TranscriptOnlyAgentWorkspaceSession(
                workspaceId,
                eventSink,
                _threadStore,
                thread)
            : new AcpAgentSessionService(
                workspaceId,
                eventSink,
                _tabManagementService,
                _terminalBridgeService,
                _threadStore,
                _directoryPicker,
                _providerRegistry,
                provider,
                thread);

        return new AgentWorkspaceSessionHandle
        {
            Session = session,
            EventSink = eventSink
        };
    }
}
