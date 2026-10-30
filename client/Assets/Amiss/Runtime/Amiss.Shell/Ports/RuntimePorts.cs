namespace Amiss.Shell.Ports
{
    // Seams the shell exposes for the remaining runtime responsibilities.
    // Each port is owned by a later build step; until then the shell runs
    // with the port silent and nothing behind it executes. The dependency
    // direction is fixed: platform adapters live in this client tree, pure
    // contract logic lives in simulation/ and schema/ at the repository root,
    // and nothing here may write physical state, measurements, stages, or
    // saves directly.

    /// <summary>
    /// Sole owner of physical facts and Trials. On this build the port is
    /// present but no writer is registered: the deterministic headless core
    /// lives in simulation/, and the on-device scientific writer choice is a
    /// later, measured decision. User actions and accepted proposals reach a
    /// future runtime only as validated, Tick-stamped commands.
    /// </summary>
    public interface SimulationRuntimePort
    {
        /// <summary>True once a runtime binding is registered in this build.</summary>
        bool Available { get; }
        bool PauseRequested { get; }
        string PauseReason { get; }
        void RequestPause(string reason);
        void RequestResume();
    }

    /// <summary>
    /// Local guide: stage, accumulated evidence, allowed-action set, and
    /// policy token issuance/revocation. Mechanical execution is pure logic
    /// in simulation/; the on-device binding arrives with the episode.
    /// </summary>
    public interface GuideRunnerPort
    {
        bool Available { get; }
    }

    /// <summary>
    /// Tutor model transport: recorded fixture, hosted, or local-live binding
    /// behind one port. Model replies carry no execution right; they only
    /// propose, and validation decides. Arrives with the judge-path build
    /// step; offline/default play never waits for it.
    /// </summary>
    public interface TutorTransportPort
    {
        bool Available { get; }
    }

    /// <summary>
    /// Complete-generation local persistence: saves, recovery, quotas, and
    /// deletion. Arrives with the persistence build step; progress feeds stay
    /// empty until then.
    /// </summary>
    public interface PersistenceAdapterPort
    {
        bool Available { get; }
    }

    public sealed class UnavailablePort
    {
        public const string Reason = "This shell build does not bind this responsibility yet.";
    }
}
