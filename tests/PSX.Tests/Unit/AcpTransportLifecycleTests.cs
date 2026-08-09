using PSX.Models;
using PSX.Services;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class AcpTransportLifecycleTests
{
    [TestMethod]
    public void Reset_CurrentGeneration_CancelsLifetimeAndCapturesRecoverySession()
    {
        using var lifecycle = new AcpTransportLifecycle();
        var generation = lifecycle.ReserveGeneration();
        using var lifetime = new CancellationTokenSource();
        var token = lifetime.Token;
        var transport = CreateTransport();
        lifecycle.Attach(generation, transport, lifetime);

        var reset = lifecycle.Reset(transport, generation, "live-session", "persisted-session");

        Assert.IsTrue(reset);
        Assert.IsNull(lifecycle.Current);
        Assert.IsTrue(token.IsCancellationRequested);
        Assert.IsTrue(lifecycle.RecoveryRequired);
        Assert.AreEqual("live-session", lifecycle.RecoverySessionId);
    }

    [TestMethod]
    public void Reset_StaleGeneration_DoesNotReplaceCurrentTransport()
    {
        using var lifecycle = new AcpTransportLifecycle();
        var generation = lifecycle.ReserveGeneration();
        var transport = CreateTransport();
        lifecycle.Attach(generation, transport, new CancellationTokenSource());

        var reset = lifecycle.Reset(transport, generation - 1, "stale", null);

        Assert.IsFalse(reset);
        Assert.AreSame(transport, lifecycle.Current);
        Assert.IsFalse(lifecycle.RecoveryRequired);
    }

    [TestMethod]
    public void ClearRecovery_RemovesCapturedSessionAndFlag()
    {
        using var lifecycle = new AcpTransportLifecycle();
        var generation = lifecycle.ReserveGeneration();
        var transport = CreateTransport();
        lifecycle.Attach(generation, transport, new CancellationTokenSource());
        Assert.IsTrue(lifecycle.Reset(transport, generation, null, "persisted"));

        lifecycle.ClearRecovery();

        Assert.IsFalse(lifecycle.RecoveryRequired);
        Assert.IsNull(lifecycle.RecoverySessionId);
    }

    private static AcpJsonRpcTransport CreateTransport()
    {
        return new AcpJsonRpcTransport(
            new AcpProcessSpec
            {
                FileName = "unused.exe",
                WorkingDirectory = Environment.CurrentDirectory
            },
            Path.Combine(Path.GetTempPath(), "psx-unused-acp.log"),
            _ => Task.FromResult<object?>(null),
            _ => Task.CompletedTask);
    }
}
