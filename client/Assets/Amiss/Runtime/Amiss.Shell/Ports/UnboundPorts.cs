using Amiss.Shell.Ports;

namespace Amiss.Shell
{
    public sealed class UnboundSimulationRuntime : SimulationRuntimePort
    {
        public bool Available => false;
        public bool PauseRequested { get; private set; }
        public string PauseReason { get; private set; } = "";
        public void RequestPause(string reason)
        {
            PauseRequested = true;
            PauseReason = reason ?? "";
        }
        public void RequestResume()
        {
            PauseRequested = false;
            PauseReason = "";
        }

        public TrialStartOutcome TryStartTrial(TrialStartRequest request) =>
            TrialStartOutcome.Refused(request?.TrialId, TrialStartRefusal.RuntimeNotBound,
                UnavailablePort.Reason);

        public SimulationTrialSummary TrialSummary => null;

        public Simulation.TemperatureReadoutSnapshot Readout => null;

        public void StopTrial(string reason)
        {
        }
    }

    public sealed class UnboundGuideRunner : GuideRunnerPort
    {
        public bool Available => false;
    }

    public sealed class UnboundTutorTransport : TutorTransportPort
    {
        public bool Available => false;
        public string TraceLine =>
            "Model: closed — no tutor transport binding is built into this shell.";
    }

    public sealed class UnboundPersistenceAdapter : PersistenceAdapterPort
    {
        public bool Available => false;
    }
}
