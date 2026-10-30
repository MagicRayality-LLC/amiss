using Amiss.Shell.Ports;

namespace Amiss.Shell
{
    public sealed class UnboundSimulationRuntime : SimulationRuntimePort
    {
        public bool Available => false;
    }

    public sealed class UnboundGuideRunner : GuideRunnerPort
    {
        public bool Available => false;
    }

    public sealed class UnboundTutorTransport : TutorTransportPort
    {
        public bool Available => false;
    }

    public sealed class UnboundPersistenceAdapter : PersistenceAdapterPort
    {
        public bool Available => false;
    }
}
