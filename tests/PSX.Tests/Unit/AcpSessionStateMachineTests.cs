using PSX.Services;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class AcpSessionStateMachineTests
{
    [TestMethod]
    [DataRow("ready", "Ready")]
    [DataRow("running", "Running")]
    [DataRow("stopping", "Stopping")]
    [DataRow("restoring", "Restoring")]
    [DataRow("restored", "Restored")]
    [DataRow("transcript_only", "TranscriptOnly")]
    [DataRow("auth_required", "AuthRequired")]
    [DataRow("recovery_pending", "RecoveryPending")]
    [DataRow("error", "Error")]
    [DataRow("fallback", "Fallback")]
    public void TransitionToWire_AllPublicStatusesUseOneMapper(string wireStatus, string phaseName)
    {
        var state = new AcpSessionStateMachine();

        state.TransitionToWire(wireStatus);

        Assert.AreEqual(phaseName, state.Phase.ToString());
        Assert.AreEqual(wireStatus, state.WireStatus);
    }

    [TestMethod]
    public void TransitionToWire_UnknownStatusIsRejected()
    {
        var state = new AcpSessionStateMachine();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => state.TransitionToWire("unknown"));
        Assert.AreEqual("ready", state.WireStatus);
    }

    [TestMethod]
    public void IsRunning_IsOrthogonalToRecoveryAndPresentationPhase()
    {
        var state = new AcpSessionStateMachine();
        state.IsRunning = true;
        state.TransitionToWire("recovery_pending");

        Assert.IsTrue(state.IsRunning);
        Assert.AreEqual(AcpSessionPhase.RecoveryPending, state.Phase);
        Assert.AreEqual("recovery_pending", state.WireStatus);
    }
}
