using System;
using Amiss.Contracts;
using Amiss.Shell.Ports;

namespace Amiss.Shell.Episodes
{
    /// <summary>
    /// The mounted what_is_temperature episode root. Mounting proves the
    /// declaration and lifecycle only: the simulation runtime, guide, tutor
    /// transport, and persistence seams stay unbound in this build, so every
    /// runtime request fails closed with the port's own reason instead of
    /// pretending the experiment can advance.
    /// </summary>
    public sealed class WhatIsTemperatureEpisodeModule : IEpisodeModule
    {
        public const string SimulationRuntimeUnbound = "simulation_runtime_unbound";
        public const string GuideRunnerUnbound = "guide_runner_unbound";
        public const string TrialStartNotImplemented = "episode_trial_start_not_implemented";

        readonly SimulationRuntimePort simulation;
        readonly GuideRunnerPort guide;
        readonly TutorTransportPort tutor;
        readonly PersistenceAdapterPort persistence;

        public WhatIsTemperatureEpisodeModule(
            SimulationRuntimePort simulation,
            GuideRunnerPort guide,
            TutorTransportPort tutor,
            PersistenceAdapterPort persistence)
        {
            this.simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
            this.guide = guide ?? throw new ArgumentNullException(nameof(guide));
            this.tutor = tutor ?? throw new ArgumentNullException(nameof(tutor));
            this.persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        }

        public EpisodeDeclaration Declaration => WhatIsTemperatureEpisode.Declaration();

        public bool RuntimeBound => simulation.Available && guide.Available;
        public bool TutorBound => tutor.Available;
        public bool PersistenceBound => persistence.Available;

        public LifecycleResult Load()
        {
            if (Declaration.Availability != EpisodeAvailability.Bundled)
                return LifecycleResult.Fail(
                    "episode_not_bundled",
                    "This episode is not bundled in this build.");
            return LifecycleResult.Ok();
        }

        public LifecycleResult Unload(out string[] residue)
        {
            residue = Array.Empty<string>();
            return LifecycleResult.Ok();
        }

        /// <summary>
        /// The honest runtime gate: a mounted episode still refuses to start a
        /// Trial while its simulation or guide port has no binding. The reason
        /// comes from the port, not from a fabricated episode capability.
        /// </summary>
        public LifecycleResult RequestTrialStart()
        {
            if (!simulation.Available)
                return LifecycleResult.Fail(SimulationRuntimeUnbound, UnavailablePort.Reason);
            if (!guide.Available)
                return LifecycleResult.Fail(GuideRunnerUnbound, UnavailablePort.Reason);
            return LifecycleResult.Fail(
                TrialStartNotImplemented,
                "The trial start arrives with the simulation runtime binding.");
        }
    }
}
