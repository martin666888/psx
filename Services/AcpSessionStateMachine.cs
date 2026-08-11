namespace PSX.Services;

internal enum AcpSessionPhase
{
    Ready,
    Running,
    Stopping,
    Restoring,
    Restored,
    TranscriptOnly,
    AuthRequired,
    RecoveryPending,
    Error,
    Fallback
}

internal sealed class AcpSessionStateMachine
{
    private readonly object _sync = new();
    private AcpSessionPhase _phase = AcpSessionPhase.Ready;
    private bool _isRunning;

    public AcpSessionPhase Phase
    {
        get { lock (_sync) return _phase; }
    }

    public bool IsRunning
    {
        get { lock (_sync) return _isRunning; }
        set { lock (_sync) _isRunning = value; }
    }

    public string WireStatus
    {
        get { lock (_sync) return ToWireStatus(_phase); }
    }

    public void TransitionToWire(string status)
    {
        var phase = status switch
        {
            "ready" => AcpSessionPhase.Ready,
            "running" => AcpSessionPhase.Running,
            "stopping" => AcpSessionPhase.Stopping,
            "restoring" => AcpSessionPhase.Restoring,
            "restored" => AcpSessionPhase.Restored,
            "transcript_only" => AcpSessionPhase.TranscriptOnly,
            "auth_required" => AcpSessionPhase.AuthRequired,
            "recovery_pending" => AcpSessionPhase.RecoveryPending,
            "error" => AcpSessionPhase.Error,
            "fallback" => AcpSessionPhase.Fallback,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown ACP session status.")
        };
        lock (_sync)
            _phase = phase;
    }

    internal static string ToWireStatus(AcpSessionPhase phase) => phase switch
    {
        AcpSessionPhase.Ready => "ready",
        AcpSessionPhase.Running => "running",
        AcpSessionPhase.Stopping => "stopping",
        AcpSessionPhase.Restoring => "restoring",
        AcpSessionPhase.Restored => "restored",
        AcpSessionPhase.TranscriptOnly => "transcript_only",
        AcpSessionPhase.AuthRequired => "auth_required",
        AcpSessionPhase.RecoveryPending => "recovery_pending",
        AcpSessionPhase.Error => "error",
        AcpSessionPhase.Fallback => "fallback",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown ACP session phase.")
    };
}
