using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Amiss.Shell.Evaluation
{
    /// <summary>
    /// The local-live judge path (C) behind the tutor transport port. It holds
    /// the person's key in memory only, assembles the policy envelope at the
    /// send boundary, enforces the 08 section 7 deadlines, and returns either
    /// one bounded reply with one provenance record or one typed failure.
    ///
    /// With no key and no policy envelope the path fails closed with a readable
    /// typed error. That is the expected device behaviour until the public
    /// policy slice lands, and nothing about it pretends to be a live reply.
    /// </summary>
    public sealed class EvaluationDirectTransport
    {
        readonly IModelSender sender;
        readonly ModelKeyHandle key;
        readonly PolicyEnvelope policy;

        public EvaluationDirectTransport(IModelSender sender, ModelKeyHandle key,
            PolicyEnvelope policy)
        {
            this.sender = sender ?? throw new ArgumentNullException(nameof(sender));
            this.key = key ?? new ModelKeyHandle();
            this.policy = policy;
        }

        public const string Mode = "live";

        public ModelKeyHandle Key => key;

        public PolicyEnvelope Policy => policy;

        public IReadOnlyDictionary<string, object> BuildRequest(
            IReadOnlyDictionary<string, object> contextPayload,
            string requestId, string turnId, string origin) =>
            EvaluationRequestBuilder.Build(contextPayload, policy, requestId, turnId, origin);

        public async Task<TransportOutcome> Send(IReadOnlyDictionary<string, object> request,
            CancellationToken cancellation = default)
        {
            var requestId = request != null && request.TryGetValue("request_id", out var id)
                ? id as string ?? ""
                : "";
            if (policy == null || request == null ||
                !request.TryGetValue("policy_envelope", out var envelope) || envelope == null)
            {
                Debug.Log("[Amiss] model live path closed: no policy envelope (request " +
                    requestId + ")");
                return TransportOutcome.Failed(Mode, requestId, new TransportFailure(
                    "no_policy", "no_policy",
                    "No policy envelope is supplied, so the live path is closed.", requestId));
            }
            if (!key.Present)
            {
                Debug.Log("[Amiss] model live path closed: no key held (request " +
                    requestId + ")");
                return TransportOutcome.Failed(Mode, requestId, new TransportFailure(
                    "no_credential", "no_credential",
                    "No evaluation model key is held; return to recorded mode.", requestId));
            }

            var authorization = key.Use();
            var body = EvaluationRequestBuilder.RequestBytes(request);
            ModelReply reply;
            try
            {
                reply = await sender.Send(EvaluationContract.EndpointAddress, body, authorization,
                    EvaluationContract.ConnectDeadlineSeconds, EvaluationContract.ModelDeadlineSeconds,
                    cancellation);
            }
            catch (SenderUnavailableException)
            {
                return TransportOutcome.Failed(Mode, requestId, new TransportFailure(
                    "offline", "network_service",
                    "No network path is available; the tutor stays silent.", requestId));
            }
            catch (SenderRefusedException)
            {
                return TransportOutcome.Failed(Mode, requestId, new TransportFailure(
                    "remote_refusal", "network_service",
                    "The model service refused the request.", requestId));
            }
            catch (OperationCanceledException)
            {
                return TransportOutcome.Failed(Mode, requestId, new TransportFailure(
                    "timeout", "network_service",
                    "The model request was cancelled before the complete reply arrived and " +
                    "was dropped.", requestId));
            }
            catch (TimeoutException)
            {
                return TransportOutcome.Failed(Mode, requestId, new TransportFailure(
                    "timeout", "network_service",
                    "The complete model response missed the 30 s deadline and was dropped.",
                    requestId));
            }
            finally
            {
                authorization = null;
            }

            if (reply == null || reply.Body == null)
                return TransportOutcome.Failed(Mode, requestId, new TransportFailure(
                    "malformed_response", "network_service",
                    "The model returned no bounded structured response.", requestId));
            if (reply.ElapsedSeconds > EvaluationContract.ModelDeadlineSeconds)
                return TransportOutcome.Failed(Mode, requestId, new TransportFailure(
                    "timeout", "network_service",
                    "The complete model response missed the 30 s deadline and was dropped.", requestId));
            if (CanonicalJson.Bytes(reply.Body).Length > EvaluationContract.MaxResponseBytes)
                return TransportOutcome.Failed(Mode, requestId, new TransportFailure(
                    "malformed_response", "network_service",
                    "The model response exceeds the bounded response size.", requestId));

            var provenance = ModelProvenance.Build(request, reply.Body, reply.TsStart, reply.TsEnd,
                reply.InputTokens, reply.OutputTokens, "live");
            return TransportOutcome.Sent(Mode, requestId, reply.Body, provenance);
        }
    }

    /// <summary>
    /// Judge path A: replay the exact captured request and response. Replay
    /// verifies the fixture envelope and its integrity digest, recomputes the
    /// live request, and refuses a stale or modified fixture instead of
    /// rebinding it to current refs. The provenance keeps <c>mode = recorded</c>
    /// and the original capture timestamps, so a recorded reply is visibly
    /// recorded and never passes as live.
    /// </summary>
    public sealed class RecordedFixtureTransport
    {
        readonly Dictionary<string, IReadOnlyDictionary<string, object>> fixtures =
            new(StringComparer.Ordinal);

        public RecordedFixtureTransport(IEnumerable<IReadOnlyDictionary<string, object>> fixtures)
        {
            foreach (var fixture in fixtures ?? Enumerable.Empty<IReadOnlyDictionary<string, object>>())
            {
                var scenarioId = ProposalValidator.Text(fixture, "scenario_id");
                if (scenarioId.Length > 0)
                    this.fixtures[scenarioId] = fixture;
            }
        }

        public const string Mode = "recorded";

        public IReadOnlyCollection<string> ScenarioIds => fixtures.Keys.ToList();

        /// <summary>The closed fixture checks: fields, integrity, and nested records.</summary>
        public static IReadOnlyList<string> FixtureErrors(IReadOnlyDictionary<string, object> fixture)
        {
            var errors = new List<string>();
            if (fixture == null)
                return new[] { "the fixture is missing" };
            var required = new[]
            {
                "fixture_format_version", "scenario_id", "policy_version", "contract_versions",
                "deterministic_recipe", "request", "response", "provenance", "integrity"
            };
            foreach (var field in required)
                if (!fixture.ContainsKey(field))
                    errors.Add("the fixture is missing " + field);
            foreach (var key in fixture.Keys)
                if (!required.Contains(key))
                    errors.Add("the fixture carries an unknown field: " + key);
            if (errors.Count > 0)
                return errors;

            var request = ProposalValidator.Map(fixture, "request");
            var response = ProposalValidator.Map(fixture, "response");
            var provenance = ProposalValidator.Map(fixture, "provenance");
            var integrity = ProposalValidator.Map(fixture, "integrity");
            if (ProposalValidator.Text(provenance, "mode") != "recorded")
                errors.Add("a recorded fixture requires recorded provenance mode");
            if (ProposalValidator.Text(request, "model") != ProposalValidator.Text(provenance, "model"))
                errors.Add("the provenance model does not match the request model");
            if (ProposalValidator.Text(request, "request_id") !=
                ProposalValidator.Text(response, "request_id"))
                errors.Add("the response request id does not match the request");
            if (ProposalValidator.Text(request, "request_id") !=
                ProposalValidator.Text(provenance, "request_id"))
                errors.Add("the provenance request id does not match the request");
            if (CanonicalJson.Text(request.GetValueOrDefault("refs")) !=
                CanonicalJson.Text(response.GetValueOrDefault("refs")))
                errors.Add("the response refs do not match the request refs");
            var initialRef = ProposalValidator.Map(fixture, "deterministic_recipe")
                .GetValueOrDefault("initial_trial_ref");
            var requestRefs = request.GetValueOrDefault("refs") as IEnumerable<object>;
            if (initialRef == null || requestRefs == null ||
                !requestRefs.Any(item => CanonicalJson.Text(item) == CanonicalJson.Text(initialRef)))
                errors.Add("the recipe initial trial ref is absent from the request refs");
            var policyEnvelope = ProposalValidator.Map(request, "policy_envelope");
            if (ProposalValidator.Text(fixture, "policy_version") !=
                ProposalValidator.Text(policyEnvelope, "policy_version"))
                errors.Add("the fixture policy version does not match the request envelope");
            if (ProposalValidator.Text(provenance, "prompt_hash") !=
                EvaluationRequestBuilder.RequestHash(request))
                errors.Add("the prompt hash does not match the canonical request");
            if (ProposalValidator.Text(provenance, "response_hash") !=
                EvaluationRequestBuilder.ResponseHash(response))
                errors.Add("the response hash does not match the canonical response");

            var payload = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in fixture)
                if (pair.Key != "integrity")
                    payload[pair.Key] = pair.Value;
            if (ProposalValidator.Text(integrity, "digest") != CanonicalJson.Sha256Hex(payload))
                errors.Add("the integrity digest does not match the canonical fixture payload");
            errors.AddRange(ModelProvenance.Errors(provenance));
            return errors;
        }

        public TransportOutcome Send(IReadOnlyDictionary<string, object> request, string scenarioId)
        {
            var requestId = request != null && request.TryGetValue("request_id", out var id)
                ? id as string ?? ""
                : "";
            if (scenarioId == null || !fixtures.TryGetValue(scenarioId, out var fixture))
                return TransportOutcome.Failed(Mode, requestId, new TransportFailure(
                    "fixture_unavailable", "fixture_unavailable",
                    "No recorded fixture is bundled for this scenario.", requestId));
            var errors = FixtureErrors(fixture);
            if (errors.Count > 0)
                return TransportOutcome.Failed(Mode, requestId, new TransportFailure(
                    "fixture_integrity", "fixture_integrity",
                    "The recorded fixture failed its integrity or contract check.", requestId));
            var fixtureRequest = ProposalValidator.Map(fixture, "request");
            if (CanonicalJson.Text(fixtureRequest) != CanonicalJson.Text(request))
                return TransportOutcome.Failed(Mode, requestId, new TransportFailure(
                    "fixture_stale", "fixture_stale",
                    "The recorded request does not match the current context; " +
                    "the fixture was not rebound.", requestId));
            return TransportOutcome.Sent(Mode, requestId, ProposalValidator.Map(fixture, "response"),
                ProposalValidator.Map(fixture, "provenance"));
        }
    }

    /// <summary>
    /// Whole-reply admission of 06 section 6: identity and bounds first, then
    /// the seven-step validator over every unit. A reply that fails admission
    /// produces no provenance and no accepted command, so a stale, malformed,
    /// or duplicated reply mutates nothing.
    /// </summary>
    public sealed class ModelReplyAdmission
    {
        readonly ProposalValidator validator;

        public ModelReplyAdmission(ProposalValidator validator)
        {
            this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        }

        public AdmissionResult Admit(TransportOutcome outcome)
        {
            if (outcome == null)
                return AdmissionResult.Rejected(new TransportFailure("malformed_response",
                    "malformed_parameters", "no reply"), "live");
            if (!outcome.Ok)
                return AdmissionResult.Rejected(outcome.Failure, outcome.Mode);
            var request = outcome.Response;
            if (request == null)
                return AdmissionResult.Rejected(new TransportFailure("malformed_response",
                    "malformed_parameters", "no response body"), outcome.Mode);

            var proposals = request.TryGetValue("tool_proposals", out var raw) &&
                            raw is IEnumerable<object> list
                ? list.ToList()
                : new List<object>();
            var results = validator.ValidateReply(proposals);
            return AdmissionResult.AdmittedReplies(outcome.Mode, outcome.Provenance, results);
        }

        /// <summary>
        /// Admits one reply against its original request: identity, original
        /// refs, and the declared bounds before any unit executes.
        /// </summary>
        public AdmissionResult Admit(IReadOnlyDictionary<string, object> request,
            IReadOnlyDictionary<string, object> response,
            IReadOnlyDictionary<string, object> provenance, string mode)
        {
            var requestId = request != null && request.TryGetValue("request_id", out var id)
                ? id as string ?? ""
                : "";
            var errors = ShapeErrors(request, response);
            if (errors.Count > 0)
                return AdmissionResult.Rejected(new TransportFailure("malformed_response",
                    "malformed_parameters", string.Join("; ", errors), requestId), mode);

            // 06 section 6 step 2: every speech unit's citations resolve against
            // the same immutable context before any subtitle or tool effect.
            // One invalid citation drops the complete reply.
            var speech = new List<string>();
            if (response["speech_units"] is IEnumerable<object> units)
            {
                foreach (var unit in units)
                {
                    if (unit is not IReadOnlyDictionary<string, object> speechUnit)
                        return AdmissionResult.Rejected(new TransportFailure("invalid_citation",
                            "malformed_parameters", "A speech unit is not an object.", requestId),
                            mode);
                    var citationError = validator.SpeechUnitError(speechUnit);
                    if (citationError != null)
                        return AdmissionResult.Rejected(new TransportFailure("invalid_citation",
                            citationError,
                            "The reply cites a measurement that is not current or not citable, " +
                            "so the whole reply was dropped.", requestId), mode);
                    speech.Add(ProposalValidator.Text(speechUnit, "text"));
                }
            }

            var proposals = ((IEnumerable<object>)response["tool_proposals"]).ToList();
            var results = validator.ValidateReply(proposals);
            if (results.Count != proposals.Count)
                return AdmissionResult.Rejected(new TransportFailure("invalid_reply",
                    "malformed_parameters",
                    "One unit of the reply failed whole-reply admission, so no unit executed.",
                    requestId), mode);
            return AdmissionResult.AdmittedReplies(mode, provenance, results, speech);
        }

        public static IReadOnlyList<string> ShapeErrors(
            IReadOnlyDictionary<string, object> request,
            IReadOnlyDictionary<string, object> response)
        {
            var errors = new List<string>();
            if (response == null)
                return new[] { "the response is not an object" };
            var required = new[]
            {
                "response_contract_version", "request_id", "refs", "speech_units", "tool_proposals"
            };
            foreach (var field in required)
                if (!response.ContainsKey(field))
                    errors.Add("the response is missing " + field);
            if (errors.Count > 0)
                return errors;
            foreach (var key in response.Keys)
                if (!required.Contains(key))
                    errors.Add("the response carries an unknown field: " + key);
            if (!Identifiers.IsSameMajor(ProposalValidator.Text(response, "response_contract_version"),
                    EvaluationContract.ResponseContractVersion))
                errors.Add("the response contract major is unsupported");
            if (request != null &&
                ProposalValidator.Text(response, "request_id") !=
                ProposalValidator.Text(request, "request_id"))
                errors.Add("the response request id differs from the request");
            if (request != null &&
                CanonicalJson.Text(response.GetValueOrDefault("refs")) !=
                CanonicalJson.Text(request.GetValueOrDefault("context_payload") is
                        IReadOnlyDictionary<string, object> payload
                    ? payload.GetValueOrDefault("refs")
                    : request.GetValueOrDefault("refs")))
                errors.Add("the response refs differ from the request refs");
            if (CanonicalJson.Bytes(response).Length > EvaluationContract.MaxResponseBytes)
                errors.Add("the response exceeds the bounded response size");
            if (response.GetValueOrDefault("speech_units") is IEnumerable<object> speech)
            {
                var units = speech.ToList();
                if (units.Count > EvaluationContract.MaxSpeechUnits)
                    errors.Add("the response carries too many speech units");
                foreach (var unit in units)
                {
                    if (unit is not IReadOnlyDictionary<string, object> speechUnit)
                    {
                        errors.Add("a speech unit is not an object");
                        continue;
                    }
                    var text = ProposalValidator.Text(speechUnit, "text");
                    if (System.Text.Encoding.UTF8.GetByteCount(text) >
                        EvaluationContract.MaxSpeechTextBytes)
                        errors.Add("a speech unit exceeds the bounded text size");
                    if (speechUnit.GetValueOrDefault("citations") is IEnumerable<object> citations)
                        foreach (var citation in citations)
                            if (!RefInsideRequest(request, citation is IReadOnlyDictionary<string, object> map
                                    ? map.GetValueOrDefault("trial_ref")
                                    : null))
                                errors.Add("a speech citation is outside the request refs");
                }
            }
            else
            {
                errors.Add("the response has no speech_units array");
            }
            if (response.GetValueOrDefault("tool_proposals") is IEnumerable<object> proposals)
            {
                if (proposals.Count() > EvaluationContract.MaxProposals)
                    errors.Add("the response carries too many tool proposals");
            }
            else
            {
                errors.Add("the response has no tool_proposals array");
            }
            return errors;
        }

        static bool RefInsideRequest(IReadOnlyDictionary<string, object> request, object value)
        {
            if (request == null || value == null)
                return false;
            var refs = request.GetValueOrDefault("context_payload") is
                           IReadOnlyDictionary<string, object> payload
                ? payload.GetValueOrDefault("refs")
                : request.GetValueOrDefault("refs");
            return refs is IEnumerable<object> list &&
                   list.Any(item => CanonicalJson.Text(item) == CanonicalJson.Text(value));
        }
    }

    /// <summary>One admission outcome: accepted commands, rejections, or a typed failure.</summary>
    public sealed class AdmissionResult
    {
        AdmissionResult(bool admitted, string mode, TransportFailure failure,
            IReadOnlyDictionary<string, object> provenance,
            IReadOnlyList<IReadOnlyDictionary<string, object>> results,
            IReadOnlyList<string> speech)
        {
            Admitted = admitted;
            Mode = mode;
            Failure = failure;
            Provenance = provenance;
            Results = results;
            AcceptedSpeech = speech ?? Array.Empty<string>();
        }

        public bool Admitted { get; }
        public string Mode { get; }
        public TransportFailure Failure { get; }
        public IReadOnlyDictionary<string, object> Provenance { get; }
        public IReadOnlyList<IReadOnlyDictionary<string, object>> Results { get; }

        /// <summary>
        /// The exact accepted speech text of an admitted reply, in reply
        /// order. Empty for a rejected reply, so no partial speech surfaces.
        /// </summary>
        public IReadOnlyList<string> AcceptedSpeech { get; }

        public IEnumerable<string> AcceptedCallIds => Results
            .Where(result => ProposalValidator.Text(result, "outcome") == "executed")
            .Select(result => ProposalValidator.Text(result, "call_id"));

        public IEnumerable<(string CallId, string Reason)> Rejections => Results
            .Where(result => ProposalValidator.Text(result, "outcome") != "executed")
            .Select(result => (ProposalValidator.Text(result, "call_id"),
                ProposalValidator.Text(result, "reason_code")));

        public static AdmissionResult AdmittedReplies(string mode,
            IReadOnlyDictionary<string, object> provenance,
            IReadOnlyList<IReadOnlyDictionary<string, object>> results,
            IReadOnlyList<string> speech = null) =>
            new(true, mode, null, provenance, results, speech);

        public static AdmissionResult Rejected(TransportFailure failure, string mode) =>
            new(false, mode, failure, null, Array.Empty<IReadOnlyDictionary<string, object>>(),
                Array.Empty<string>());
    }
}
