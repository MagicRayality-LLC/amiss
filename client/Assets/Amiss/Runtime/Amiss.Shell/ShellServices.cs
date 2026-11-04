using System;
using System.Collections.Generic;
using System.Globalization;
using Amiss.Contracts;
using Amiss.Shell.Episodes;
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
        readonly string sessionId = "session-" + Guid.NewGuid().ToString("N");

        public HeadsetShellSession Session { get; private set; }
        public EpisodeModuleHost EpisodeHost { get; private set; }
        public SimulationRuntimePort SimulationRuntime { get; private set; }
        public GuideRunnerPort GuideRunner { get; private set; }
        public TutorTransportPort TutorTransport { get; private set; }
        public PersistenceAdapterPort PersistenceAdapter { get; private set; }
        public RoomPreparationController Room { get; private set; }
        public UI.CorePanelController Panel { get; private set; }

        void Awake()
        {
            var runtime = new Simulation.DeviceSimulationRuntime();
            SimulationRuntime = runtime;
            SessionMemory = new Tutor.SessionMemory();
            runtime.SessionEventCommitted = (kind, trialId, tick, revision) =>
                SessionMemory.RecordEvent(kind, tick, trialId, revision);

            // The evaluation tutor path is assembled from the bundled public
            // policy pack. The Meta model key is the person's own evaluation
            // credential: it exists only once they save it in the device
            // settings, so the live path opens when the policy loads and a key
            // is stored, and it stays closed with a readable typed reason until
            // then. The recorded path and the default path continue unchanged
            // in either case.
            Policy = Evaluation.BundledPolicyPack.Load();
            var evaluationContext = new Evaluation.ValidationContext
            {
                EpisodeId = Episodes.WhatIsTemperatureEpisode.EpisodeId
            };
            Candidates = new Tutor.TrialCandidateStore();
            EvaluationContext = evaluationContext;
            TutorValidator = new Evaluation.ProposalValidator(evaluationContext,
                new Tutor.DeviceCommandExecutor(Candidates, () => 0, evaluationContext));
            EvaluationKeys = new Tutor.PlayerPrefsModelKeyStore();
            Tutor = new Tutor.DeviceTutorTransport(TutorValidator, policy: Policy.Loaded
                ? Policy.Envelope()
                : null, keyStore: EvaluationKeys);
            TutorTransport = Tutor;

            RecordPolicyEvidence();

            GuideRunner = new UnboundGuideRunner();
            PersistenceAdapter = new UnboundPersistenceAdapter();
            EpisodeHost = new EpisodeModuleHost(
                new Dictionary<string, Func<IEpisodeModule>>
                {
                    [WhatIsTemperatureEpisode.EpisodeId] = () =>
                        new WhatIsTemperatureEpisodeModule(
                            SimulationRuntime,
                            GuideRunner,
                            TutorTransport,
                            PersistenceAdapter)
                });
            Session = new HeadsetShellSession(
                new WhatIsTemperatureEpisode.DeclarationSource(),
                ShellCapabilityManifest.ThisBuild(),
                EpisodeHost,
                WhatIsTemperatureEpisode.ResolveCriticalAsset);
            Room = GetComponent<RoomPreparationController>();
            TutorConversation = new Tutor.TutorConversation(Tutor, EvaluationContext,
                LiveTutorState, SessionMemory);
            VoiceRig = gameObject.AddComponent<Voice.WitVoiceRig>();
            VoiceStore = new Voice.PlayerPrefsVoiceConsentStore();
            VoiceControl = new Voice.VoiceController(VoiceRig.Dictation, VoiceRig.Speech,
                new Voice.TutorTurnAdapter(TutorConversation, text => Panel?.ShowSubtitle(text)),
                VoiceStore.Load(), () => Time.realtimeSinceStartupAsDouble,
                () => Panel == null || !Panel.TipsOff, IdleFallbackLine);
            Panel = new UI.CorePanelController(SimulationRuntime, GuideRunner, Room, VoiceControl);
            gameObject.AddComponent<Voice.VoiceLoop>().Initialize(VoiceControl, VoiceStore,
                () => Room != null && Room.Recovery != null && Room.Recovery.SafetyHold,
                SessionMemory);
            var driver = gameObject.AddComponent<Simulation.SimulationTickDriver>();
            driver.Initialize(runtime);

            // The bat reads only the adapter's adjudicated samples. Production
            // installs the adapter's declared candidate profile
            // (quest-pro-candidate-unmeasured, uncalibrated_declared_not_measured),
            // so accepted device samples are admitted and can produce work; the
            // limits themselves stay unmeasured and the readout labels them so.
            var toolAdapter = GetComponentInChildren<Input.OvrToolInputAdapter>();
            if (toolAdapter != null)
            {
                Bat = gameObject.AddComponent<Input.BatToolController>();
                Bat.Initialize(toolAdapter, runtime);
            }

            // The sandbag owns its tool on the same runtime host, polls it
            // after the runtime steps, and disperses on measured absorption.
            // Placement stays with the stage/wearer: without it the poll is a
            // no-op and no bag floats.
            Sandbag = gameObject.AddComponent<Simulation.SandbagToolController>();
            Sandbag.Initialize(runtime);

            // The palm cooling field pushes its pose to the writer while both
            // palms stay open and facing. The live hand prefabs do not carry
            // it, so install it here; binding keeps the tool decoupled from
            // the runtime's concrete type.
            Cooling.PalmCoolingInstaller.Install(transform);
            foreach (var cooling in GetComponentsInChildren<Cooling.PalmCoolingTool>(true))
                cooling.Bind(runtime);

            BuildIdentity.Log();
        }

        /// <summary>
        /// Writes one bounded record of what this build loaded for the policy
        /// and whether the transport bound. It holds versions, digests, and
        /// the assembled hash, never the policy body or a credential, so the
        /// device state can be read without a wearer.
        /// </summary>
        void RecordPolicyEvidence()
        {
            try
            {
                var evidence = Evaluation.PolicyPackEvidence.Create(
                    Policy, Tutor.TraceLine, Tutor.KeyPresent, Tutor.Available);
                var path = evidence.WriteAtomic(
                    Application.persistentDataPath);
                Debug.Log("[Amiss] policy-pack " + evidence.load_outcome +
                          " policy=" + evidence.policy_version +
                          " reports=" + path);
            }
            catch (Exception error)
            {
                Debug.LogWarning("[Amiss] policy-pack evidence not written: " + error.Message);
            }
        }

        /// <summary>The normalized-input bridge to the P0 bat, or null when unbound.</summary>
        public Input.BatToolController Bat { get; private set; }

        /// <summary>The bridge from the floating bag to the P0 sandbag tool.</summary>
        public Simulation.SandbagToolController Sandbag { get; private set; }

        /// <summary>The device evaluation transport; closed until its key exists.</summary>
        public Tutor.DeviceTutorTransport Tutor { get; private set; }

        /// <summary>
        /// The verified public policy pack, or a refused pack that carries the
        /// reasons. Loaded once at startup and read-only afterwards.
        /// </summary>
        public Evaluation.PolicyPack Policy { get; private set; }

        /// <summary>The device validation mirror the model reply path uses.</summary>
        public Evaluation.ProposalValidator TutorValidator { get; private set; }

        /// <summary>The validator's live Trial mirror, refreshed from committed state.</summary>
        public Evaluation.ValidationContext EvaluationContext { get; private set; }

        /// <summary>The device-local evaluation key store; never renders the value.</summary>
        public Tutor.IModelKeyStore EvaluationKeys { get; private set; }

        /// <summary>The one local tutor turn.</summary>
        public Tutor.TutorConversation TutorConversation { get; private set; }

        public Tutor.SessionMemory SessionMemory { get; private set; }

        /// <summary>The device voice rig: two Wit adapters and the saved token.</summary>
        public Voice.WitVoiceRig VoiceRig { get; private set; }

        /// <summary>The sole writer of voice state (design 06 section 1).</summary>
        public Voice.VoiceController VoiceControl { get; private set; }

        /// <summary>The bounded local consent record of design 06 section 4.1.</summary>
        public Voice.PlayerPrefsVoiceConsentStore VoiceStore { get; private set; }

        /// <summary>Proposed Trial candidates; only an explicit user accept makes one startable.</summary>
        public Tutor.TrialCandidateStore Candidates { get; private set; }

        /// <summary>
        /// The defined local tutor action of this build: one guide_default
        /// request from committed Trial state, awaited while the shell keeps
        /// running. A missing key, no Trial, a request already in flight, or a
        /// transport failure yields a typed status; the Trial keeps running.
        /// </summary>
        public async System.Threading.Tasks.Task<Evaluation.AdmissionResult> AskTutor()
        {
            var admission = await TutorConversation.Ask(
                Amiss.Shell.Tutor.TutorConversation.LocalOrigin);
            Panel?.ShowSubtitle(TutorConversation.LastSubtitle);
            return admission;
        }

        /// <summary>
        /// Records one major local wearer action for the teacher's session
        /// memory: a prop grab or release. The event carries the current Trial
        /// identity and Tick; the visual prop never writes Trial state.
        /// </summary>
        public void RecordUserAction(string kind)
        {
            if (SessionMemory == null || string.IsNullOrEmpty(kind))
                return;
            var summary = SimulationRuntime != null ? SimulationRuntime.TrialSummary : null;
            if (summary == null || string.IsNullOrEmpty(summary.TrialId))
                return;
            var revision = 1 + (SimulationRuntime.Readout != null
                ? SimulationRuntime.Readout.ContactCommits
                : 0);
            SessionMemory.RecordEvent(kind, summary.Tick, summary.TrialId, revision);
        }

        /// <summary>
        /// The local check-in used only when no proactive model reply is
        /// available. It names the latest recorded action and the current
        /// reading when the producer has one, so the fallback still follows
        /// the session instead of repeating one fixed sentence.
        /// </summary>
        public string IdleFallbackLine()
        {
            var summary = SimulationRuntime != null ? SimulationRuntime.TrialSummary : null;
            if (summary == null)
                return "";
            var measurement = SimulationRuntime.Readout != null
                ? SimulationRuntime.Readout.Measurement
                : null;
            var reading = measurement != null && measurement.Applicable &&
                          measurement.Display != null
                ? measurement.Display.Kelvin + " K"
                : "";
            var lastKind = SessionMemory != null && SessionMemory.SessionEvents.Count > 0
                ? SessionMemory.SessionEvents[SessionMemory.SessionEvents.Count - 1].Kind
                : "";
            switch (lastKind)
            {
                case "bat_grabbed":
                case "bat_released":
                    return reading.Length > 0
                        ? "You have the bat. The reading is " + reading +
                          ". Swing it through the balls and watch what happens."
                        : "You have the bat. Swing it through the balls and watch the number.";
                case "cloth_bag_grabbed":
                case "cloth_bag_released":
                    return "You have the cloth bag. A ball that hits it loses energy, " +
                           "so watch the reading.";
                case "bat_hit":
                    return reading.Length > 0
                        ? "That bat strike changed the energy in the room. The reading is " +
                          reading + ". What do you notice now?"
                        : "That bat strike changed the energy in the room. " +
                          "What do you notice now?";
                case "fist_hit":
                    return reading.Length > 0
                        ? "That hand hit changed the energy in the room. The reading is " +
                          reading + ". What do you notice now?"
                        : "That hand hit changed the energy in the room. " +
                          "What do you notice now?";
                case "cloth_bag_absorbed":
                    return "A ball hit the cloth bag and lost energy, so watch the reading.";
                case "effective_action":
                    return reading.Length > 0
                        ? "That hit changed the energy in the room. The reading is " +
                          reading + ". What do you notice now?"
                        : "That hit changed the energy in the room. What do you notice now?";
                default:
                    return reading.Length > 0
                        ? "The reading is " + reading +
                          ". What do you notice about how the balls move?"
                        : "Look at the number on the screen. What do you notice about the balls?";
            }
        }

        /// <summary>
        /// The live committed state one request is built from. Read-only: it
        /// copies the producer's values and never writes Trial or room state.
        /// </summary>
        Tutor.ContextPayloadSource LiveTutorState()
        {
            var summary = SimulationRuntime != null ? SimulationRuntime.TrialSummary : null;
            var readout = SimulationRuntime != null ? SimulationRuntime.Readout : null;
            var confirmation = Room != null && Room.Preparation != null
                ? Room.Preparation.Confirmation
                : null;
            var sizing = confirmation != null
                ? TemperatureSpaceSizing.Compute(confirmation.EffectiveVolumeM3)
                : null;
            var virtualBoundary = confirmation != null &&
                                  confirmation.BoundaryLabel != null &&
                                  confirmation.BoundaryLabel.IndexOf("virtual",
                                      StringComparison.OrdinalIgnoreCase) >= 0;
            return new Tutor.ContextPayloadSource
            {
                EpisodeId = WhatIsTemperatureEpisode.EpisodeId,
                EpisodeModuleVersion = "1.0.0",
                EpisodeModelVersion = TemperatureModelDeclaration.ModelVersion,
                AppBuildVersion = Application.version,
                SessionId = sessionId,
                DiagnosticWallTimestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ",
                    CultureInfo.InvariantCulture),
                TrialId = summary != null ? summary.TrialId : "",
                TrialRevision = 1 + (readout != null ? readout.ContactCommits : 0),
                Tick = summary != null ? summary.Tick : 0,
                ContactCommits = readout != null ? readout.ContactCommits : 0,
                Paused = summary != null && summary.Paused,
                CollisionRepresentationId = summary != null ? summary.CollisionRepresentationId : "",
                CollisionRepresentationVersion =
                    Amiss.Shell.Room.CollisionRepresentation.CurrentFormatVersion,
                RegionSizeM = confirmation != null ? confirmation.Region.size : Vector3.zero,
                EffectiveVolumeM3 = confirmation != null ? confirmation.EffectiveVolumeM3 : 0.0,
                VirtualBoundary = virtualBoundary,
                Measurement = readout != null ? readout.Measurement : null,
                Ledger = readout != null ? readout.Ledger : null,
                Sizing = sizing,
                BallCount = summary != null ? summary.BallCount : 0,
                PolicyTokens = EvaluationContext.PolicyTokens,
                Muted = Panel != null && Panel.AudioMuted,
                TipsEnabled = Panel == null || !Panel.TipsOff,
                UtteranceId = "utterance-none",
                UtteranceText = ""
            };
        }
    }
}
