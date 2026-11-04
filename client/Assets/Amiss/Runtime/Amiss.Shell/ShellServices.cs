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
                VoiceStore.Load(), () => Time.realtimeSinceStartupAsDouble);
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

            // The palm cooling field pushes its pose to the writer while both
            // palms stay open and facing. The rig is added from the Tools menu
            // (or the desktop test rig); binding here keeps the tool decoupled
            // from the runtime's concrete type.
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
                PolicyTokens = EvaluationContext.PolicyTokens,
                Muted = Panel != null && Panel.AudioMuted,
                TipsEnabled = Panel == null || !Panel.TipsOff,
                UtteranceId = "utterance-none",
                UtteranceText = ""
            };
        }
    }
}
