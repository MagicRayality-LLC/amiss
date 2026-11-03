using UnityEngine;

namespace Amiss.Shell.Simulation
{
    /// <summary>
    /// The scheduler side of the runtime. It owns only wall-clock pacing: it
    /// requests at most the declared whole fixed Ticks per scheduling slice,
    /// sheds any excess instead of building Tick debt, and freezes both
    /// accumulation and advance while the runtime is paused. Presentation
    /// consumes the published views with an interpolation fraction; neither
    /// the driver nor the presentation can write ball state.
    /// </summary>
    public sealed class SimulationTickDriver : MonoBehaviour
    {
        const float FixedDelta = FrozenWriterConfiguration.FixedDeltaTimeSeconds;

        DeviceSimulationRuntime runtime;
        BallPresentation presentation;
        float pendingSeconds;

        public void Initialize(DeviceSimulationRuntime deviceRuntime)
        {
            runtime = deviceRuntime;
            if (presentation == null)
                presentation = new BallPresentation();
        }

        public int PresentedBallCount => presentation?.BallCount ?? 0;

        void Update()
        {
            if (runtime == null)
                return;
            if (presentation == null)
                presentation = new BallPresentation();

            if (!runtime.HasTrial)
            {
                pendingSeconds = 0f;
                presentation.Sync(null, null, 0f);
                return;
            }

            if (runtime.PauseRequested)
            {
                pendingSeconds = 0f;
            }
            else
            {
                pendingSeconds += Time.unscaledDeltaTime;
                var due = Mathf.FloorToInt(pendingSeconds / FixedDelta);
                if (due > FrozenWriterConfiguration.MaxTicksPerSchedulingSlice)
                {
                    due = FrozenWriterConfiguration.MaxTicksPerSchedulingSlice;
                    pendingSeconds = 0f;
                }
                else if (due > 0)
                {
                    pendingSeconds -= due * FixedDelta;
                }
                if (due > 0)
                    runtime.Step(due);
            }

            var alpha = runtime.PauseRequested ? 1f : Mathf.Clamp01(pendingSeconds / FixedDelta);
            presentation.Sync(runtime.PreviousView, runtime.CurrentView, alpha);
        }

        void OnDestroy()
        {
            presentation?.Clear();
        }
    }
}
