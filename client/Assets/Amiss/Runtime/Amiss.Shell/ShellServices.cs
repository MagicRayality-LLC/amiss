using Amiss.Contracts;
using Amiss.Shell.Fixtures;
using Amiss.Shell.Ports;
using Amiss.Shell.Room;
using UnityEngine;

namespace Amiss.Shell
{
    /// <summary>
    /// Composition root of the six runtime responsibilities inside the
    /// headset shell. The shell itself owns session, room, presentation, and
    /// input normalization here; the simulation runtime, guide, tutor
    /// transport, and persistence seams are exposed as ports whose bindings
    /// arrive with their owning build steps and stay silent until then.
    /// </summary>
    public sealed class ShellServices : MonoBehaviour
    {
        public HeadsetShellSession Session { get; private set; }
        public EpisodeModuleHost EpisodeHost { get; private set; }
        public SimulationRuntimePort SimulationRuntime { get; private set; }
        public GuideRunnerPort GuideRunner { get; private set; }
        public TutorTransportPort TutorTransport { get; private set; }
        public PersistenceAdapterPort PersistenceAdapter { get; private set; }
        public RoomPreparationController Room { get; private set; }

        void Awake()
        {
            EpisodeHost = new EpisodeModuleHost();
            SimulationRuntime = new UnboundSimulationRuntime();
            GuideRunner = new UnboundGuideRunner();
            TutorTransport = new UnboundTutorTransport();
            PersistenceAdapter = new UnboundPersistenceAdapter();
            Session = new HeadsetShellSession(
                new FrameworkContractFixtures.DeclarationSource(),
                ShellCapabilityManifest.ThisBuild(),
                EpisodeHost,
                FrameworkContractFixtures.ResolveCriticalAsset);
            Room = GetComponent<RoomPreparationController>();

            BuildIdentity.Log();
        }
    }
}
