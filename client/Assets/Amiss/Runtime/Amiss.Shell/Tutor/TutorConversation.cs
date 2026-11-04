using System;
using System.Threading;
using System.Threading.Tasks;
using Amiss.Shell.Evaluation;
using UnityEngine;

namespace Amiss.Shell.Tutor
{
    /// <summary>
    /// The one local tutor turn of this build: it reads the live committed
    /// Trial into the validator's mirror, builds the bounded Context Payload,
    /// sends it through the existing transport port, and exposes only the
    /// admitted whole reply. A missing key, a missing Trial, an oversized
    /// context, or a transport failure is a typed, key-free status; nothing
    /// throws out of <see cref="Ask"/> and no reply is fabricated.
    ///
    /// Design 06 section 5.1 fixes the request origin set. The panel issues
    /// <see cref="LocalOrigin"/>; a voice turn supplies <see cref="AnswerOrigin"/>
    /// with the dictation utterance on this same seam (06 section 1).
    /// </summary>
    public sealed class TutorConversation
    {
        public const string LocalOrigin = "guide_default";

        /// <summary>The voice-triggered origin; design 06 section 5.1.</summary>
        public const string AnswerOrigin = "answer";

        public const string NoSpeechStatus = "Model: reply admitted with no speech.";

        readonly DeviceTutorTransport transport;
        readonly ValidationContext context;
        readonly Func<ContextPayloadSource> source;

        int sequence;

        public TutorConversation(DeviceTutorTransport transport, ValidationContext context,
            Func<ContextPayloadSource> source, SessionMemory memory = null)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            Memory = memory ?? new SessionMemory();
        }

        public SessionMemory Memory { get; }

        /// <summary>The last typed, key-free status line of this control.</summary>
        public string LastStatus { get; private set; } = "";

        /// <summary>The exact first accepted speech text of the last reply.</summary>
        public string LastSubtitle { get; private set; } = "";

        public AdmissionResult LastAdmission { get; private set; }

        public async Task<AdmissionResult> Ask(string origin = LocalOrigin,
            string utteranceId = "utterance-none", string utteranceText = "",
            CancellationToken cancellation = default)
        {
            var requestId = "request-" + (++sequence).ToString("D4");
            var turnId = "turn-" + sequence.ToString("D4");

            ContextPayloadSource state;
            try
            {
                state = source();
            }
            catch (Exception error)
            {
                Debug.Log("[Amiss] tutor context unavailable: " + error.GetType().Name);
                return Reject("context_unavailable", requestId,
                    "No live Trial state is readable, so no model request was built: " +
                    error.GetType().Name + ".");
            }

            state.UtteranceId = utteranceId ?? "utterance-none";
            state.UtteranceText = utteranceText ?? "";
            RefreshContext(state);
            state.SessionMemory = Memory;
            Memory.ObserveStage(state.PolicyTokens?.StageId, state.Tick,
                state.TrialId, state.TrialRevision);
            var memoryRefs = new[] { new TrialReference(state.TrialId, state.TrialRevision) };
            var memoryStage = state.PolicyTokens?.StageId ?? "unguided";
            var memoryPolicy = state.PolicyTokens?.PolicyVersion ?? "none";

            System.Collections.Generic.Dictionary<string, object> payload;
            try
            {
                payload = ContextPayloadBuilder.Build(state);
            }
            catch (EvaluationContractException error)
            {
                Debug.Log("[Amiss] tutor payload build failed code=" + error.ReasonCode);
                return Reject(error.ReasonCode, requestId, error.Message);
            }

            Debug.Log("[Amiss] tutor ask " + requestId + " origin=" + origin +
                " turn=" + turnId);
            var admission = await transport.Request(payload, requestId, turnId, origin,
                cancellation);
            LastAdmission = admission;
            if (!admission.Admitted)
            {
                LastSubtitle = "";
                LastStatus = admission.Failure != null && !string.IsNullOrEmpty(
                    admission.Failure.Readable)
                    ? admission.Failure.Readable
                    : transport.TraceLine;
                Debug.Log("[Amiss] tutor ask " + requestId + " refused code=" +
                    (admission.Failure != null ? admission.Failure.ReasonCode : "unknown") +
                    " status=" + LastStatus);
                return admission;
            }

            if (admission.AcceptedSpeech.Count > 0)
                Memory.RecordExchange(turnId,
                    string.IsNullOrEmpty(state.UtteranceText) ? null : state.UtteranceText,
                    admission.AcceptedSpeech, memoryRefs, memoryStage, memoryPolicy);

            LastSubtitle = admission.AcceptedSpeech.Count > 0
                ? admission.AcceptedSpeech[0]
                : "";
            LastStatus = LastSubtitle.Length > 0 ? transport.TraceLine : NoSpeechStatus;
            Debug.Log("[Amiss] tutor ask " + requestId + " admitted speech=" +
                admission.AcceptedSpeech.Count);
            return admission;
        }

        AdmissionResult Reject(string reasonCode, string requestId, string readable)
        {
            var failure = new TransportFailure(reasonCode, reasonCode, readable, requestId);
            LastAdmission = AdmissionResult.Rejected(failure, EvaluationDirectTransport.Mode);
            LastSubtitle = "";
            LastStatus = readable;
            return LastAdmission;
        }

        /// <summary>
        /// Publishes the live committed Trial into the validator's mirror so
        /// proposal validation and speech citations resolve against the same
        /// refs, tokens, and measurement tuple the payload carries. Only the
        /// mirror changes; the owning runtime is never written here.
        /// </summary>
        void RefreshContext(ContextPayloadSource state)
        {
            context.EpisodeId = state.EpisodeId ?? "";
            context.Preferences["mute"] = state.Muted;
            context.Preferences["tips_enabled"] = state.TipsEnabled;

            var tokens = state.PolicyTokens;
            if (tokens == null || tokens.Tokens.Count == 0)
            {
                tokens = new PolicyTokenSet
                {
                    EpisodeId = context.EpisodeId,
                    StageId = "unguided",
                    PolicyVersion = "none"
                };
                tokens.Tokens.Add(new System.Collections.Generic.Dictionary<string, object>
                {
                    ["token_id"] = "unguided-stage",
                    ["kind"] = "stage",
                    ["value_id"] = "unguided"
                });
                tokens.Tokens.Add(new System.Collections.Generic.Dictionary<string, object>
                {
                    ["token_id"] = "unguided-policy",
                    ["kind"] = "policy_version",
                    ["value_id"] = "none"
                });
                state.PolicyTokens = tokens;
            }

            context.PolicyTokens = tokens;
            if (string.IsNullOrEmpty(state.TrialId))
                return;

            var revision = Math.Max(1, state.TrialRevision);
            if (!context.Trials.TryGetValue(state.TrialId, out var trial))
            {
                trial = new TrialState { TrialId = state.TrialId };
                context.Trials[state.TrialId] = trial;
            }

            trial.TrialRevision = revision;
            trial.Phase = "setup";
            trial.Acceptance = "accepted";
            trial.Activity = state.Paused ? "paused" : "running";
            trial.Measurements.Clear();
            var measurement = state.Measurement;
            if (measurement != null)
            {
                var applicable = measurement.Applicable && measurement.Display != null;
                var projected = new MeasurementState
                {
                    MeasurementId = measurement.MeasurementId,
                    Applicability = applicable ? "valid" : "not_applicable",
                    DisplayTupleId = applicable
                        ? ContextPayloadBuilder.DisplayTupleId(measurement.MeasurementId)
                        : null
                };
                if (applicable)
                {
                    foreach (var id in ContextPayloadBuilder.DisplayComponents())
                    {
                        projected.DisplayComponents.Add(id);
                        projected.RequiredDisplayComponents.Add(id);
                    }
                }

                trial.Measurements.Add(projected);
            }

            context.CurrentTrialId = state.TrialId;
            if (!context.ComparisonTrialIds.Contains(state.TrialId))
                context.ComparisonTrialIds.Add(state.TrialId);
        }
    }
}
