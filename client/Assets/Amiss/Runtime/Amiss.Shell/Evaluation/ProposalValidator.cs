using System;
using System.Collections.Generic;
using System.Linq;
using Amiss.Contracts;

namespace Amiss.Shell.Evaluation
{
    /// <summary>
    /// One episode declaration of an executable action, mirroring the headless
    /// oracle's <c>ToolDeclaration</c>. Values come from the episode, never from
    /// model prose.
    /// </summary>
    public sealed class ToolDeclaration
    {
        public ToolDeclaration(string actionId, string tool, string operation,
            string setupFamilyId = null,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> controllableInputs = null,
            IReadOnlyDictionary<string, object> defaultParameters = null,
            IEnumerable<string> downstreamEffectIds = null,
            IEnumerable<string> invariantIds = null, bool confirmationRequired = false)
        {
            ActionId = actionId ?? "";
            Tool = tool ?? "";
            Operation = operation ?? "";
            SetupFamilyId = setupFamilyId;
            ControllableInputs = controllableInputs ??
                new Dictionary<string, IReadOnlyDictionary<string, object>>();
            DefaultParameters = defaultParameters ?? new Dictionary<string, object>();
            DownstreamEffectIds = (downstreamEffectIds ?? Array.Empty<string>()).ToList();
            InvariantIds = (invariantIds ?? new[] { "finite-state" }).ToList();
            ConfirmationRequired = confirmationRequired;
        }

        public string ActionId { get; }
        public string Tool { get; }
        public string Operation { get; }
        public string SetupFamilyId { get; }
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> ControllableInputs { get; }
        public IReadOnlyDictionary<string, object> DefaultParameters { get; }
        public IReadOnlyList<string> DownstreamEffectIds { get; }
        public IReadOnlyList<string> InvariantIds { get; }
        public bool ConfirmationRequired { get; }
    }

    /// <summary>Local mechanical gates. They never come from model text.</summary>
    public sealed class LocalGates
    {
        public bool UserSpeaking;
        public bool UserSwinging;
        public bool CooldownActive;
        public bool BackoffActive;
        public bool TtsAvailable = true;
        public bool SceneValid = true;
        public bool NumericalStateOk = true;
        public bool StorageAvailable = true;
        public bool HardSafetyHold;
        public bool GenerationCommitted = true;
        public HashSet<string> SurfaceIds = new(StringComparer.Ordinal);
        public HashSet<string> HighlightIds = new(StringComparer.Ordinal);
        public HashSet<string> ReplayEventIds = new(StringComparer.Ordinal);
        public HashSet<string> ReplaySpeedIds = new(StringComparer.Ordinal);
        public HashSet<string> ChartSlotIds = new(StringComparer.Ordinal);
        public HashSet<string> ChartIds = new(StringComparer.Ordinal);
        public HashSet<string> SafetyCandidateIds = new(StringComparer.Ordinal);
        public HashSet<string> PlaybackCompletedSolicitationIds = new(StringComparer.Ordinal);

        public LocalGates Clone() => (LocalGates)MemberwiseClone();
    }

    /// <summary>
    /// One committed measurement as the citation gate reads it. Applicability,
    /// the optional display tuple, and the trial's required display components
    /// are the same facts the oracle projects; a masked or incomplete reading
    /// invalidates the whole reply exactly as it does headlessly.
    /// </summary>
    public sealed class MeasurementState
    {
        public string MeasurementId { get; set; } = "";
        public string Applicability { get; set; } = "valid";
        public string DisplayTupleId { get; set; }
        public List<string> DisplayComponents { get; } = new();
        public List<string> RequiredDisplayComponents { get; } = new();
    }

    /// <summary>Declarative state of one Trial, as the validator reads it.</summary>
    public sealed class TrialState
    {
        public string TrialId { get; set; } = "";
        public int TrialRevision { get; set; } = 1;
        public string Phase { get; set; } = "setup";
        public string Acceptance { get; set; } = "accepted";
        public string Activity { get; set; } = "running";
        public string SetupFamilyId { get; set; } = "";
        public Dictionary<string, object> TargetParameters { get; set; } = new(StringComparer.Ordinal);
        public List<MeasurementState> Measurements { get; } = new();

        public IEnumerable<string> MeasurementIds => Measurements.Select(item => item.MeasurementId);
    }

    /// <summary>Guide-issued policy tokens, exactly as the oracle projects them.</summary>
    public sealed class PolicyTokenSet
    {
        public string EpisodeId { get; set; } = "";
        public string StageId { get; set; } = "";
        public string PolicyVersion { get; set; } = "";
        public List<Dictionary<string, object>> Tokens { get; } = new();
    }

    public class ValidationContext
    {
        public string EpisodeId { get; set; } = "";
        public string CurrentTrialId { get; set; } = "";
        public List<string> ComparisonTrialIds { get; } = new();
        public Dictionary<string, TrialState> Trials { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, object> Preferences { get; set; } = new(StringComparer.Ordinal)
        {
            ["mute"] = false,
            ["tips_enabled"] = true
        };
        public PolicyTokenSet PolicyTokens { get; set; } = new();
        public Dictionary<string, ToolDeclaration> Declarations { get; } = new(StringComparer.Ordinal);
        public LocalGates Gates { get; set; } = new();
        public List<Dictionary<string, object>> IdempotencyRecords { get; } = new();

        public TrialState Current =>
            Trials.TryGetValue(CurrentTrialId, out var trial) ? trial : null;

        public IReadOnlyList<Dictionary<string, object>> CurrentRefs()
        {
            var refs = new List<Dictionary<string, object>>();
            foreach (var trialId in ComparisonTrialIds)
                if (Trials.TryGetValue(trialId, out var trial))
                    refs.Add(new Dictionary<string, object>
                    {
                        ["trial_id"] = trial.TrialId,
                        ["trial_revision"] = trial.TrialRevision
                    });
            return refs;
        }

        public ToolDeclaration DeclarationFor(string tool, string operation) =>
            Declarations.TryGetValue(tool + "/" + operation, out var declaration)
                ? declaration
                : null;

        /// <summary>
        /// A copy of the mutation-bearing state. Step 7 restores it when an
        /// execution or postcondition fails, so no rejected or partially
        /// applied command leaves a Trial, a ref, or a candidate behind.
        /// </summary>
        public ValidationSnapshot Snapshot() => new(
            Trials.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(),
                StringComparer.Ordinal),
            new List<string>(ComparisonTrialIds),
            IdempotencyRecords.Count,
            CurrentTrialId);

        public void Restore(ValidationSnapshot snapshot)
        {
            if (snapshot == null)
                return;
            Trials.Clear();
            foreach (var pair in snapshot.Trials)
                Trials[pair.Key] = pair.Value;
            ComparisonTrialIds.Clear();
            ComparisonTrialIds.AddRange(snapshot.ComparisonTrialIds);
            while (IdempotencyRecords.Count > snapshot.IdempotencyRecordCount)
                IdempotencyRecords.RemoveAt(IdempotencyRecords.Count - 1);
            CurrentTrialId = snapshot.CurrentTrialId;
        }
    }

    /// <summary>The mutation-bearing state captured before step 7 executes.</summary>
    public sealed class ValidationSnapshot
    {
        public ValidationSnapshot(Dictionary<string, TrialState> trials,
            List<string> comparisonTrialIds, int idempotencyRecordCount, string currentTrialId)
        {
            Trials = trials;
            ComparisonTrialIds = comparisonTrialIds;
            IdempotencyRecordCount = idempotencyRecordCount;
            CurrentTrialId = currentTrialId ?? "";
        }

        public Dictionary<string, TrialState> Trials { get; }
        public List<string> ComparisonTrialIds { get; }
        public int IdempotencyRecordCount { get; }
        public string CurrentTrialId { get; }
    }

    /// <summary>One Trial's declarative state, copied for rollback.</summary>
    public static class TrialStateCopy
    {
        public static TrialState Clone(this TrialState source)
        {
            var copy = new TrialState
            {
                TrialId = source.TrialId,
                TrialRevision = source.TrialRevision,
                Phase = source.Phase,
                Acceptance = source.Acceptance,
                Activity = source.Activity,
                SetupFamilyId = source.SetupFamilyId,
                TargetParameters = new Dictionary<string, object>(source.TargetParameters,
                    StringComparer.Ordinal)
            };
            copy.Measurements.AddRange(source.Measurements);
            return copy;
        }
    }

    /// <summary>
    /// The one execution seam of the validator. A Trial mutation is applied by
    /// the runtime that owns Trial state; presentation and speech are applied
    /// by the shell. The executor must return the actual receipt, not a claim,
    /// and may throw to force the atomic step-7 rejection.
    /// </summary>
    public interface ICommandExecutor
    {
        IReadOnlyDictionary<string, object> Execute(
            IReadOnlyDictionary<string, object> command, ToolDeclaration declaration);
    }

    /// <summary>
    /// An executor whose own state must roll back with the command. The
    /// validator opens a transaction before step 7 and rolls it back when the
    /// execution or its postcondition fails, so a rejected command leaves no
    /// candidate, no mutation, and no unpublished evidence behind.
    /// </summary>
    public interface ITransactionalExecutor
    {
        object BeginTransaction();
        void Commit(object mark);
        void Rollback(object mark);
    }

    /// <summary>
    /// A local refusal raised by an executor before any mutation. It carries
    /// the stable reason code of 04 section 3.3, so the rejection names the
    /// real cause instead of a generic unavailable executor.
    /// </summary>
    public sealed class ExecutionRefusedException : Exception
    {
        public ExecutionRefusedException(string reasonCode, string message) : base(message)
        {
            ReasonCode = reasonCode ?? "execution_unavailable";
        }

        public string ReasonCode { get; }
    }

    /// <summary>
    /// The device mirror of the seven-step proposal validator in
    /// <c>simulation/validator.py</c>. It is not a second semantics: the shared
    /// vector file drives both implementations over the same cases and the same
    /// expected outcomes, and both suites fail if either drifts.
    ///
    /// Steps: 1 structure and fingerprint, 2 durable idempotency, 3 target/refs/
    /// citations/tokens, 4 origin/preferences/confirmation, 5 declaration and
    /// one-input delta, 6 numeric invariants and safety, 7 execute and verify.
    /// Rejections mutate nothing; a new Trial candidate is only ever created in
    /// <c>setup</c> with <c>acceptance = proposed</c> and tick 0.
    /// </summary>
    public sealed class ProposalValidator
    {
        public const string DomainSeparator = "amiss.tool-proposal\0";

        static readonly Dictionary<string, string> ToolDiffKinds = new(StringComparer.Ordinal)
        {
            ["state.read"] = "none",
            ["trial.propose"] = "trial_state",
            ["trial.orchestrate"] = "trial_state",
            ["presentation.request"] = "presentation",
            ["speech.emit"] = "voice"
        };

        static readonly HashSet<string> ZeroRevisionOperations = new(StringComparer.Ordinal)
        {
            "snapshot", "ledger", "measurements", "create", "derive",
            "begin_measurement", "present", "highlight_surface", "replay_slow", "open_chart",
            "show_subtitle", "answer", "proactive", "safety"
        };

        static readonly HashSet<string> ResultParameterIds = new(StringComparer.Ordinal)
        {
            "measurement", "measurement_value", "result", "result_value", "temperature", "conclusion"
        };

        static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ToolOperations =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["state.read"] = new[] { "snapshot", "ledger", "measurements" },
                ["trial.propose"] = new[] { "create", "derive" },
                ["trial.orchestrate"] = new[]
                {
                    "start", "pause", "resume", "begin_measurement", "archive", "present"
                },
                ["presentation.request"] = new[]
                {
                    "highlight_surface", "replay_slow", "open_chart", "show_subtitle"
                },
                ["speech.emit"] = new[] { "answer", "proactive", "safety" }
            };

        static readonly IReadOnlyList<string> RequiredProposalFields = new[]
        {
            "tool_schema_version", "call_id", "origin", "tool", "target", "basis_trial_ref",
            "refs", "parameters"
        };

        static readonly IReadOnlyList<string> OptionalProposalFields = new[] { "confirmation_id" };

        readonly ValidationContext context;
        readonly ICommandExecutor executor;

        public ProposalValidator(ValidationContext context, ICommandExecutor executor = null)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.executor = executor;
        }

        /// <summary>Rejections and accepted commands, in reply order.</summary>
        public IReadOnlyList<IReadOnlyDictionary<string, object>> ValidateReply(
            IEnumerable<object> proposals)
        {
            // The whole-reply rule of 06 section 6: one structurally invalid unit
            // or one unit with a stale/out-of-context ref rejects the entire reply
            // before any unit executes. Nothing is salvaged unit by unit.
            var admitted = new List<object>();
            foreach (var proposal in proposals)
            {
                Dictionary<string, object> canonical;
                try
                {
                    canonical = Canonicalize(proposal);
                }
                catch (EvaluationContractException)
                {
                    return new[] { Validate(proposal) };
                }
                var errors = StructuralErrors(canonical);
                if (errors.Count > 0)
                    return new[] { Validate(proposal) };
                var fingerprint = Fingerprint(canonical);
                var existing = Lookup(Scope(canonical), Text(canonical, "call_id"));
                if (existing == null)
                {
                    var reason = Step3(canonical);
                    if (reason != null)
                        return new[] { Persist(canonical, fingerprint, reason, "step_3") };
                }
                admitted.Add(proposal);
            }
            var results = new List<IReadOnlyDictionary<string, object>>();
            foreach (var proposal in admitted)
                results.Add(Validate(proposal));
            return results;
        }

        public IReadOnlyDictionary<string, object> Validate(object proposal)
        {
            Dictionary<string, object> canonical;
            try
            {
                canonical = Canonicalize(proposal);
            }
            catch (EvaluationContractException error)
            {
                return Result(Fallback(canonicalOf: proposal), "", "rejected",
                    reason: error.ReasonCode, stage: "step_1");
            }

            var errors = StructuralErrors(canonical);
            var fingerprint = Fingerprint(canonical);
            if (errors.Count > 0)
            {
                var structuralReason = errors.Any(item => item.Contains(
                    "unsupported major version", StringComparison.Ordinal))
                    ? "unsupported_contract_version"
                    : "malformed_parameters";
                return Result(canonical, fingerprint, "rejected", reason: structuralReason,
                    stage: "step_1");
            }

            var scope = Scope(canonical);
            var existing = Lookup(scope, Text(canonical, "call_id"));
            if (existing != null)
            {
                if (Text(existing, "fingerprint") == fingerprint)
                    return ReplayFirstResult(existing);
                return Result(canonical, fingerprint, "rejected",
                    reason: "idempotency_conflict", stage: "step_2");
            }

            var parameters = Map(canonical, "parameters");
            var tool = Text(canonical, "tool");

            var reason = Step3(canonical);
            if (reason != null)
                return Persist(canonical, fingerprint, reason, "step_3");

            var declaration = context.DeclarationFor(tool, Text(parameters, "operation"));
            if (declaration == null)
                return Persist(canonical, fingerprint, "episode_precondition_failed", "step_5");

            reason = Step4(canonical, declaration, fingerprint);
            if (reason != null)
                return Persist(canonical, fingerprint, reason, "step_4");
            reason = Step5(canonical, declaration);
            if (reason != null)
                return Persist(canonical, fingerprint,
                    reason, reason == "numeric_invariant_failed" ? "step_6" : "step_5");
            reason = Step6(canonical, declaration);
            if (reason != null)
                return Persist(canonical, fingerprint, reason, "step_6");

            return Execute(canonical, fingerprint, declaration);
        }

        // ---------------------------------------------------------------- step 1

        Dictionary<string, object> Canonicalize(object proposal)
        {
            if (proposal is not IReadOnlyDictionary<string, object> source)
                throw new EvaluationContractException("malformed_parameters",
                    "the proposal must be an object");
            var value = source.ToDictionary(pair => pair.Key, pair => Normalize(pair.Value),
                StringComparer.Ordinal);
            if (value.TryGetValue("refs", out var rawRefs) && rawRefs is IEnumerable<object> refs)
            {
                var unique = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var item in refs)
                    unique[CanonicalJson.Text(item)] = item;
                value["refs"] = unique.OrderBy(pair => TextOf(pair.Value, "trial_id"),
                        StringComparer.Ordinal)
                    .ThenBy(pair => IntOf(pair.Value, "trial_revision"))
                    .Select(pair => pair.Value).ToList();
            }
            if (value.TryGetValue("parameters", out var rawParameters) &&
                rawParameters is IReadOnlyDictionary<string, object> parametersSource)
            {
                var parameters = parametersSource.ToDictionary(pair => pair.Key,
                    pair => Normalize(pair.Value), StringComparer.Ordinal);
                if (parameters.TryGetValue("text", out var text) && text is string textValue)
                    parameters["text"] = textValue.Normalize(System.Text.NormalizationForm.FormC);
                foreach (var field in new[] { "measurement_ids", "expected_invariant_ids" })
                    if (parameters.TryGetValue(field, out var list) && list is IEnumerable<object> items)
                        parameters[field] = SortedIds(items);
                if (parameters.TryGetValue("input_delta", out var rawDelta) &&
                    rawDelta is IReadOnlyDictionary<string, object> deltaSource)
                {
                    var delta = deltaSource.ToDictionary(pair => pair.Key, pair => Normalize(pair.Value),
                        StringComparer.Ordinal);
                    if (delta.TryGetValue("downstream_effect_ids", out var effects) &&
                        effects is IEnumerable<object> effectItems)
                        delta["downstream_effect_ids"] = SortedIds(effectItems);
                    foreach (var field in new[] { "from_value", "to_value" })
                        if (delta.TryGetValue(field, out var number))
                            delta[field] = Normalize(number);
                    parameters["input_delta"] = delta;
                }
                if (parameters.TryGetValue("target_parameters", out var targets) &&
                    targets is IReadOnlyDictionary<string, object> targetSource)
                    parameters["target_parameters"] = targetSource.ToDictionary(pair => pair.Key,
                        pair => Normalize(pair.Value), StringComparer.Ordinal);
                value["parameters"] = parameters;
            }
            return value;
        }

        static object Normalize(object value)
        {
            switch (value)
            {
                case IReadOnlyDictionary<string, object> map:
                {
                    var result = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var pair in map)
                        result[pair.Key] = Normalize(pair.Value);
                    return result;
                }
                case IEnumerable<object> list when value is not string:
                    return list.Select(Normalize).ToList();
                case double number:
                {
                    if (double.IsNaN(number) || double.IsInfinity(number))
                        throw new EvaluationContractException("malformed_parameters",
                            "non-finite number");
                    if (number == 0.0)
                        return 0L;
                    if (Math.Truncate(number) == number && Math.Abs(number) <= 9007199254740991.0)
                        return (long)number;
                    return number;
                }
                case float number:
                    return Normalize((double)number);
                default:
                    return value;
            }
        }

        static List<string> SortedIds(IEnumerable<object> values)
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var item in values)
                if (item is string text)
                    set.Add(text);
            return set.ToList();
        }

        List<string> StructuralErrors(Dictionary<string, object> canonical)
        {
            var errors = new List<string>();
            foreach (var field in RequiredProposalFields)
                if (!canonical.ContainsKey(field))
                    errors.Add("the proposal is missing " + field);
            foreach (var key in canonical.Keys)
                if (!RequiredProposalFields.Contains(key) && !OptionalProposalFields.Contains(key))
                    errors.Add("the proposal carries an unknown field: " + key);
            if (errors.Count > 0)
                return errors;

            var version = Text(canonical, "tool_schema_version");
            if (!Identifiers.IsSameMajor(version, FrameworkApiVersions.ToolSchema))
                errors.Add("unsupported major version: " + version);

            var callId = Text(canonical, "call_id");
            if (!Identifiers.IsIdentifier(callId))
                errors.Add("the call id is not a usable identifier");

            var origin = Text(canonical, "origin");
            if (origin != "model" && origin != "guide_default")
                errors.Add("the origin is not model or guide_default");

            var tool = Text(canonical, "tool");
            if (!ToolOperations.ContainsKey(tool))
            {
                errors.Add("the tool is not one of the five closed tools");
                return errors;
            }

            if (canonical["parameters"] is not IReadOnlyDictionary<string, object> parameters)
            {
                errors.Add("the parameters are not an object");
                return errors;
            }
            var operation = Text(parameters, "operation");
            if (!ToolOperations[tool].Contains(operation))
                errors.Add("the operation does not belong to the tool");
            errors.AddRange(ParameterShapeErrors(tool, operation, parameters));

            if (canonical["target"] is not IReadOnlyDictionary<string, object> target)
            {
                errors.Add("the target is not an object");
                return errors;
            }
            var targetKind = Text(target, "kind");
            if (targetKind == "new_trial")
            {
                if (tool != "trial.propose")
                    errors.Add("new_trial is valid only for trial.propose");
                var source = target.TryGetValue("source_trial_ref", out var raw) ? raw : null;
                var basis = canonical["basis_trial_ref"];
                if (operation == "create")
                {
                    if (source != null || basis != null)
                        errors.Add("create requires a null source and basis ref");
                    if (parameters.TryGetValue("input_delta", out var delta) &&
                        delta is IReadOnlyDictionary<string, object> deltaMap &&
                        deltaMap.ContainsKey("from_value"))
                        errors.Add("from_value is forbidden for create");
                }
                else if (operation == "derive")
                {
                    if (source == null || basis == null)
                        errors.Add("derive requires source and basis refs");
                    else if (TrialRefText(source) != TrialRefText(basis))
                        errors.Add("the derive basis must equal its source");
                    if (parameters.TryGetValue("input_delta", out var delta) &&
                        delta is IReadOnlyDictionary<string, object> deltaMap &&
                        !deltaMap.ContainsKey("from_value"))
                        errors.Add("from_value is required for derive");
                }
            }
            else if (targetKind == "existing")
            {
                if (tool == "trial.propose")
                    errors.Add("trial.propose requires a new_trial target");
                if (!target.TryGetValue("trial_ref", out var targetRef) || targetRef == null)
                    errors.Add("the target ref is absent");
                else if (canonical["basis_trial_ref"] == null)
                    errors.Add("the basis ref is required for an existing target");
                if (targetRef != null && parameters.TryGetValue("target_trial_ref", out var parameterRef) &&
                    parameterRef != null && TrialRefText(parameterRef) != TrialRefText(targetRef))
                    errors.Add("the parameter target ref must equal the envelope target");
            }
            else
            {
                errors.Add("the target kind is not existing or new_trial");
            }

            foreach (var field in new[] { "basis_trial_ref" })
            {
                var value = canonical[field];
                if (value != null && !TrialRefValid(value))
                    errors.Add(field + " is not a trial ref");
            }
            if (parameters.TryGetValue("text", out var text) && text is string textValue &&
                System.Text.Encoding.UTF8.GetByteCount(textValue) > 4 * 1024)
                errors.Add("the text exceeds 4 KiB UTF-8");
            // A model-origin safety unit is not structurally malformed: it is a
            // valid request refused by the step-4 origin gate, exactly as the
            // oracle's own structural filter does.
            return errors;
        }

        IReadOnlyList<string> ParameterShapeErrors(string tool, string operation,
            IReadOnlyDictionary<string, object> parameters)
        {
            var errors = new List<string>();
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "operation" };
            switch (operation)
            {
                case "snapshot":
                    allowed.UnionWith(new[] { "target_trial_ref" });
                    RequireRef(errors, parameters, "target_trial_ref");
                    break;
                case "ledger":
                    allowed.UnionWith(new[] { "target_trial_ref", "after_sequence", "max_entries" });
                    RequireRef(errors, parameters, "target_trial_ref");
                    RequireInteger(errors, parameters, "after_sequence", 0);
                    RequireInteger(errors, parameters, "max_entries", 1);
                    break;
                case "measurements":
                    allowed.UnionWith(new[] { "target_trial_ref", "measurement_ids" });
                    RequireRef(errors, parameters, "target_trial_ref");
                    RequireIdentifierArray(errors, parameters, "measurement_ids");
                    break;
                case "create":
                case "derive":
                    allowed.UnionWith(new[]
                    {
                        "setup_family_token_id", "target_parameters", "input_delta",
                        "expected_invariant_ids"
                    });
                    RequireIdentifier(errors, parameters, "setup_family_token_id");
                    RequireTargetParameters(errors, parameters);
                    RequireInputDelta(errors, parameters, operation == "derive");
                    RequireIdentifierArray(errors, parameters, "expected_invariant_ids");
                    break;
                case "start":
                case "pause":
                case "resume":
                case "begin_measurement":
                case "archive":
                case "present":
                    allowed.Add("target_trial_ref");
                    RequireRef(errors, parameters, "target_trial_ref");
                    break;
                case "highlight_surface":
                    allowed.UnionWith(new[] { "surface_id", "highlight_id", "requested_state" });
                    RequireIdentifier(errors, parameters, "surface_id");
                    RequireIdentifier(errors, parameters, "highlight_id");
                    if (!(parameters.TryGetValue("requested_state", out var state) &&
                          state is string stateText && (stateText == "on" || stateText == "off")))
                        errors.Add("requested_state is not on or off");
                    break;
                case "replay_slow":
                    allowed.UnionWith(new[] { "event_id", "replay_speed_id" });
                    RequireIdentifier(errors, parameters, "event_id");
                    RequireIdentifier(errors, parameters, "replay_speed_id");
                    break;
                case "open_chart":
                    allowed.UnionWith(new[] { "slot_id", "chart_id" });
                    RequireIdentifier(errors, parameters, "slot_id");
                    RequireIdentifier(errors, parameters, "chart_id");
                    break;
                case "show_subtitle":
                    allowed.UnionWith(new[]
                    {
                        "subtitle_id", "text", "source_global_revision", "citations"
                    });
                    RequireIdentifier(errors, parameters, "subtitle_id");
                    RequireSpeechCommon(errors, parameters);
                    break;
                case "answer":
                    allowed.UnionWith(new[]
                    {
                        "text", "source_global_revision", "citations", "solicitation_token_id"
                    });
                    RequireSpeechCommon(errors, parameters);
                    if (parameters.TryGetValue("solicitation_token_id", out var solicitation) && solicitation != null)
                        RequireIdentifier(errors, parameters, "solicitation_token_id");
                    break;
                case "proactive":
                    allowed.UnionWith(new[]
                    {
                        "text", "source_global_revision", "citations", "candidate_token_id",
                        "solicitation_token_id"
                    });
                    RequireSpeechCommon(errors, parameters);
                    RequireIdentifier(errors, parameters, "candidate_token_id");
                    if (parameters.TryGetValue("solicitation_token_id", out var proactiveSolicitation) &&
                        proactiveSolicitation != null)
                        RequireIdentifier(errors, parameters, "solicitation_token_id");
                    break;
                case "safety":
                    allowed.UnionWith(new[]
                    {
                        "text", "source_global_revision", "citations", "safety_candidate_id"
                    });
                    RequireSpeechCommon(errors, parameters);
                    RequireIdentifier(errors, parameters, "safety_candidate_id");
                    break;
                default:
                    return errors;
            }
            foreach (var key in parameters.Keys)
                if (!allowed.Contains(key))
                    errors.Add("the parameters carry an unknown field: " + key);
            return errors;
        }

        static void RequireSpeechCommon(List<string> errors,
            IReadOnlyDictionary<string, object> parameters)
        {
            if (!(parameters.TryGetValue("text", out var text) && text is string))
                errors.Add("speech requires text");
            RequireInteger(errors, parameters, "source_global_revision", 1);
            if (!parameters.TryGetValue("citations", out var citations) ||
                citations is not IEnumerable<object> list)
            {
                errors.Add("speech requires a citations array");
                return;
            }
            foreach (var item in list)
            {
                if (item is not IReadOnlyDictionary<string, object> citation)
                {
                    errors.Add("a citation is not an object");
                    continue;
                }
                if (!(citation.TryGetValue("measurement_id", out var measurementId) &&
                      measurementId is string measurementText &&
                      Identifiers.IsIdentifier(measurementText)))
                    errors.Add("a citation has no usable measurement id");
                if (!citation.TryGetValue("trial_ref", out var trialRef) || !TrialRefValid(trialRef))
                    errors.Add("a citation has no trial ref");
            }
        }

        static void RequireRef(List<string> errors, IReadOnlyDictionary<string, object> parameters,
            string field)
        {
            if (!parameters.TryGetValue(field, out var value) || !TrialRefValid(value))
                errors.Add(field + " is not a trial ref");
        }

        static void RequireIdentifier(List<string> errors,
            IReadOnlyDictionary<string, object> parameters, string field)
        {
            if (!(parameters.TryGetValue(field, out var value) && value is string text &&
                  Identifiers.IsIdentifier(text)))
                errors.Add(field + " is not a usable identifier");
        }

        static void RequireIdentifierArray(List<string> errors,
            IReadOnlyDictionary<string, object> parameters, string field)
        {
            if (!parameters.TryGetValue(field, out var value) || value is not IEnumerable<object> list)
            {
                errors.Add(field + " is not an identifier array");
                return;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in list)
            {
                if (item is not string text || !Identifiers.IsIdentifier(text))
                    errors.Add(field + " carries an unusable identifier");
                else if (!seen.Add(text))
                    errors.Add(field + " carries a duplicate identifier");
            }
        }

        static void RequireInteger(List<string> errors, IReadOnlyDictionary<string, object> parameters,
            string field, long minimum)
        {
            if (!parameters.TryGetValue(field, out var value))
            {
                errors.Add(field + " is missing");
                return;
            }
            if (value is not long integer || integer < minimum)
                errors.Add(field + " is not an integer of at least " + minimum);
        }

        static void RequireTargetParameters(List<string> errors,
            IReadOnlyDictionary<string, object> parameters)
        {
            if (!parameters.TryGetValue("target_parameters", out var value) ||
                value is not IReadOnlyDictionary<string, object> map || map.Count == 0)
            {
                errors.Add("target_parameters is not a non-empty scalar object");
                return;
            }
            foreach (var pair in map)
                if (pair.Value is not (string or bool or long or double))
                    errors.Add("target_parameters carries a non-scalar value");
        }

        static void RequireInputDelta(List<string> errors,
            IReadOnlyDictionary<string, object> parameters, bool requireFromValue)
        {
            if (!parameters.TryGetValue("input_delta", out var value) ||
                value is not IReadOnlyDictionary<string, object> delta)
            {
                errors.Add("input_delta is not an object");
                return;
            }
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "parameter_id", "to_value", "unit", "from_value", "downstream_effect_ids"
            };
            foreach (var key in delta.Keys)
                if (!allowed.Contains(key))
                    errors.Add("input_delta carries an unknown field: " + key);
            if (!(delta.TryGetValue("parameter_id", out var parameterId) &&
                  parameterId is string id && Identifiers.IsIdentifier(id)))
                errors.Add("input_delta has no usable parameter id");
            if (!delta.TryGetValue("to_value", out var toValue) ||
                toValue is not (string or bool or long or double))
                errors.Add("input_delta has no scalar to_value");
            if (requireFromValue)
            {
                if (!delta.TryGetValue("from_value", out var fromValue) ||
                    fromValue is not (string or bool or long or double))
                    errors.Add("input_delta has no scalar from_value");
            }
            if (delta.TryGetValue("unit", out var unit) && unit != null &&
                !(unit is string text && Identifiers.IsIdentifier(text)))
                errors.Add("input_delta unit is not a usable identifier");
            RequireIdentifierArray(errors, delta, "downstream_effect_ids");
        }

        static bool TrialRefValid(object value) =>
            value is IReadOnlyDictionary<string, object> map &&
            map.TryGetValue("trial_id", out var trialId) && trialId is string id &&
            Identifiers.IsIdentifier(id) &&
            map.TryGetValue("trial_revision", out var revision) && revision is long number &&
            number >= 1;

        static string TrialRefText(object value) =>
            value is IReadOnlyDictionary<string, object> map
                ? TextOf(map, "trial_id") + "#" + IntOf(map, "trial_revision")
                : "";

        // ---------------------------------------------------- fingerprint/scope

        public static string Fingerprint(IReadOnlyDictionary<string, object> canonical)
        {
            var value = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var field in new[]
                     {
                         "tool_schema_version", "origin", "tool", "target", "basis_trial_ref",
                         "refs", "parameters"
                     })
                value[field] = canonical.TryGetValue(field, out var item) ? item : null;
            return CanonicalJson.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(
                DomainSeparator + CanonicalJson.Text(value)));
        }

        Dictionary<string, object> Scope(IReadOnlyDictionary<string, object> canonical)
        {
            var target = Map(canonical, "target");
            if (Text(target, "kind") == "existing")
            {
                var trialId = TextOf(Map(target, "trial_ref"), "trial_id");
                var episode = context.Trials.TryGetValue(trialId, out var trial)
                    ? context.EpisodeId
                    : context.EpisodeId;
                return new Dictionary<string, object>
                {
                    ["episode_id"] = episode,
                    ["target_trial_id"] = trialId
                };
            }
            var source = target.TryGetValue("source_trial_ref", out var raw) ? raw : null;
            return new Dictionary<string, object>
            {
                ["episode_id"] = Text(target, "episode_id"),
                ["target_trial_id"] = source == null
                    ? null
                    : TextOf(source, "trial_id")
            };
        }

        Dictionary<string, object> Lookup(IReadOnlyDictionary<string, object> scope, string callId)
        {
            foreach (var record in context.IdempotencyRecords)
            {
                if (Text(record, "call_id") != callId)
                    continue;
                var recordScope = Map(record, "idempotency_scope");
                if (Text(recordScope, "episode_id") != Text(scope, "episode_id"))
                    continue;
                var recordTarget = Text(recordScope, "target_trial_id");
                var scopeTarget = Text(scope, "target_trial_id");
                if (!string.Equals(recordTarget ?? "", scopeTarget ?? "", StringComparison.Ordinal))
                    continue;
                return record;
            }
            return null;
        }

        IReadOnlyDictionary<string, object> ReplayFirstResult(IReadOnlyDictionary<string, object> record)
        {
            var replay = new Dictionary<string, object>(StringComparer.Ordinal);
            if (record.TryGetValue("first_result", out var raw) &&
                raw is IReadOnlyDictionary<string, object> first)
                foreach (var pair in first)
                    replay[pair.Key] = pair.Value;
            replay["replay"] = true;
            return replay;
        }

        // ------------------------------------------------------------- steps 3-6

        string Step3(IReadOnlyDictionary<string, object> canonical)
        {
            var target = Map(canonical, "target");
            if (Text(target, "kind") == "new_trial" &&
                Text(target, "episode_id") != context.EpisodeId)
                return "unknown_trial";

            var refsToCheck = new List<object>();
            if (canonical.TryGetValue("refs", out var rawRefs) && rawRefs is IEnumerable<object> refs)
                refsToCheck.AddRange(refs);
            if (Text(target, "kind") == "existing" &&
                target.TryGetValue("trial_ref", out var targetRef) && targetRef != null)
                refsToCheck.Add(targetRef);
            if (Text(target, "kind") == "new_trial" &&
                target.TryGetValue("source_trial_ref", out var source) && source != null)
                refsToCheck.Add(source);
            if (canonical.TryGetValue("basis_trial_ref", out var basis) && basis != null)
                refsToCheck.Add(basis);

            foreach (var item in refsToCheck)
            {
                var reason = CheckRef(item);
                if (reason != null)
                    return reason;
            }

            var parameters = Map(canonical, "parameters");
            if (Text(canonical, "tool") == "state.read" &&
                Text(parameters, "operation") == "measurements")
            {
                var trialId = TextOf(Map(target, "trial_ref"), "trial_id");
                if (context.Trials.TryGetValue(trialId, out var trial) &&
                    parameters.TryGetValue("measurement_ids", out var rawIds) &&
                    rawIds is IEnumerable<object> ids)
                    foreach (var item in ids)
                        if (item is string id && !trial.MeasurementIds.Contains(id))
                            return "citation_not_in_context";
            }
            return CheckCitations(parameters);
        }

        string CheckRef(object value)
        {
            if (value is not IReadOnlyDictionary<string, object> reference)
                return "unknown_trial";
            var trialId = TextOf(reference, "trial_id");
            if (!context.Trials.TryGetValue(trialId, out var trial))
                return "unknown_trial";
            if (!context.ComparisonTrialIds.Contains(trialId))
                return "ref_outside_comparison";
            if (trial.TrialRevision != IntOf(reference, "trial_revision"))
                return "stale_trial_ref";
            return null;
        }

        /// <summary>
        /// Mirrors the oracle's <c>admit_speech</c> checks and its reason mapping:
        /// an unknown or ambiguous measurement is <c>citation_not_in_context</c>,
        /// a masked/inapplicable one is <c>masked_measurement_citation</c>, and a
        /// missing or incomplete declared display tuple is
        /// <c>display_tuple_unavailable</c>.
        /// </summary>
        string CheckCitations(IReadOnlyDictionary<string, object> parameters)
        {
            if (!parameters.TryGetValue("citations", out var raw) || raw is not IEnumerable<object> list)
                return null;
            var masked = false;
            foreach (var item in list)
            {
                if (item is not IReadOnlyDictionary<string, object> citation)
                    return "citation_not_in_context";
                var measurementId = Text(citation, "measurement_id");
                var citedTrialId = citation.TryGetValue("trial_ref", out var trialRef)
                    ? TextOf(trialRef, "trial_id")
                    : "";
                var matches = new List<(TrialState Trial, MeasurementState Measurement)>();
                foreach (var trial in context.Trials.Values)
                    foreach (var measurement in trial.Measurements)
                        if (measurement.MeasurementId == measurementId)
                            matches.Add((trial, measurement));
                if (matches.Count != 1)
                    return "citation_not_in_context";
                var (owner, found) = matches[0];
                if (owner.TrialId != citedTrialId)
                    return "stale_trial_ref";
                if (found.Applicability != "valid")
                {
                    masked = true;
                    continue;
                }
                if (found.RequiredDisplayComponents.Count > 0)
                {
                    var citedTuple = citation.TryGetValue("display_tuple_id", out var tuple)
                        ? tuple as string
                        : null;
                    if (citedTuple == null || citedTuple != found.DisplayTupleId)
                        return "display_tuple_unavailable";
                    if (!found.RequiredDisplayComponents.All(found.DisplayComponents.Contains))
                        return "display_tuple_unavailable";
                }
            }
            return masked ? "masked_measurement_citation" : null;
        }

        /// <summary>
        /// Whole-reply speech admission of 06 section 6 step 2: the unit's
        /// citations resolve against the same immutable context before any
        /// subtitle or tool effect. A null return admits the unit; any other
        /// value rejects the complete reply.
        /// </summary>
        public string SpeechUnitError(IReadOnlyDictionary<string, object> unit)
        {
            if (unit == null)
                return "malformed_parameters";
            if (System.Text.Encoding.UTF8.GetByteCount(Text(unit, "text")) >
                EvaluationContract.MaxSpeechTextBytes)
                return "malformed_parameters";
            return CheckCitations(unit);
        }

        string Step4(IReadOnlyDictionary<string, object> canonical, ToolDeclaration declaration,
            string fingerprint)
        {
            var parameters = Map(canonical, "parameters");
            var operation = Text(parameters, "operation");
            if (!HasToken("allowed_action", declaration.ActionId))
                return "token_invalid";
            if (Text(canonical, "tool") == "trial.propose")
            {
                var tokenId = Text(parameters, "setup_family_token_id");
                var issued = context.PolicyTokens.Tokens.FirstOrDefault(item =>
                    Text(item, "kind") == "setup_family" && Text(item, "token_id") == tokenId);
                if (issued == null || Text(issued, "value_id") != declaration.SetupFamilyId)
                    return "token_invalid";
            }
            if (operation == "safety" && Text(canonical, "origin") != "guide_default")
                return "safety_boundary_failed";
            if (Text(canonical, "tool") == "speech.emit")
            {
                if (Bool(context.Preferences, "mute"))
                    return "preference_blocked";
                if (operation == "proactive")
                {
                    if (!HasToken("proactive_candidate", Text(parameters, "candidate_token_id")))
                        return "token_invalid";
                    if (context.Gates.CooldownActive || context.Gates.BackoffActive ||
                        context.Gates.UserSpeaking || context.Gates.UserSwinging ||
                        !Bool(context.Preferences, "tips_enabled"))
                        return "preference_blocked";
                }
                if (parameters.TryGetValue("solicitation_token_id", out var solicitation) &&
                    solicitation is string solicitationId && solicitationId.Length > 0)
                {
                    var issued = context.PolicyTokens.Tokens.FirstOrDefault(item =>
                        Text(item, "kind") == "solicitation" &&
                        Text(item, "token_id") == solicitationId);
                    if (issued == null)
                        return "token_invalid";
                    if (!context.Gates.PlaybackCompletedSolicitationIds.Contains(Text(issued, "value_id")))
                        return "preference_blocked";
                }
                if (operation == "safety" &&
                    !context.Gates.SafetyCandidateIds.Contains(Text(parameters, "safety_candidate_id")))
                    return "safety_boundary_failed";
            }
            if (declaration.ConfirmationRequired)
                return Confirmation(canonical, fingerprint);
            return null;
        }

        /// <summary>
        /// Exact single-use confirmation binding of 04 section 3.4. A grant
        /// authorizes one call id and one fingerprint over one exact ref set;
        /// a model's own confirmation text has no authority.
        /// </summary>
        string Confirmation(IReadOnlyDictionary<string, object> canonical, string fingerprint)
        {
            var confirmationId = Text(canonical, "confirmation_id");
            if (confirmationId.Length == 0)
                return "confirmation_required";
            if (!grants.TryGetValue(confirmationId, out var grant) ||
                revokedChallengeIds.Contains(confirmationId))
                return "confirmation_stale";
            if (Text(grant, "call_id") != Text(canonical, "call_id"))
                return "confirmation_fingerprint_mismatch";
            if (Text(grant, "fingerprint") != fingerprint)
                return "confirmation_fingerprint_mismatch";
            if (CanonicalJson.Text(grant.GetValueOrDefault("refs")) !=
                CanonicalJson.Text(canonical.GetValueOrDefault("refs")) ||
                CanonicalJson.Text(grant.GetValueOrDefault("basis_trial_ref")) !=
                CanonicalJson.Text(canonical.GetValueOrDefault("basis_trial_ref")))
                return "confirmation_stale";
            var current = new List<object>(context.CurrentRefs());
            if (CanonicalJson.Text(grant.GetValueOrDefault("refs")) != CanonicalJson.Text(current))
                return "confirmation_stale";
            return null;
        }

        readonly Dictionary<string, IReadOnlyDictionary<string, object>> grants =
            new(StringComparer.Ordinal);
        readonly HashSet<string> revokedChallengeIds = new(StringComparer.Ordinal);

        public void RegisterGrant(IReadOnlyDictionary<string, object> grant)
        {
            if (grant == null || Text(grant, "record_kind") != "grant")
                throw new EvaluationContractException("malformed_parameters",
                    "invalid confirmation grant");
            var challengeId = Text(grant, "challenge_id");
            grants[challengeId] = grant;
            revokedChallengeIds.Remove(challengeId);
        }

        public void RevokeGrant(string challengeId) => revokedChallengeIds.Add(challengeId ?? "");

        bool HasToken(string kind, string valueId) =>
            context.PolicyTokens.Tokens.Any(item =>
                Text(item, "kind") == kind && Text(item, "value_id") == valueId);

        string Step5(IReadOnlyDictionary<string, object> canonical, ToolDeclaration declaration)
        {
            var parameters = Map(canonical, "parameters");
            var operation = Text(parameters, "operation");
            var target = Map(canonical, "target");
            if (Text(target, "kind") == "existing")
            {
                var trialId = TextOf(Map(target, "trial_ref"), "trial_id");
                if (context.Trials.TryGetValue(trialId, out var trial))
                {
                    var legal = operation switch
                    {
                        "start" => trial.Phase == "setup",
                        "pause" => trial.Activity == "running" && trial.Phase != "archived" &&
                                   trial.Phase != "abandoned",
                        "resume" => trial.Activity == "paused",
                        "begin_measurement" => trial.Phase != "setup" && trial.Phase != "archived" &&
                                               trial.Phase != "abandoned",
                        "archive" => trial.Phase == "measured" && context.Gates.GenerationCommitted,
                        "present" => context.ComparisonTrialIds.Contains(trialId),
                        _ => (bool?)null
                    };
                    if (operation == "start" && trial.Acceptance != "accepted")
                        return "proposal_not_accepted";
                    if (legal.HasValue && !legal.Value)
                        return "invalid_transition";
                }
            }
            if (Text(canonical, "tool") == "presentation.request")
            {
                var checks = operation switch
                {
                    "highlight_surface" => context.Gates.SurfaceIds.Contains(
                                               Text(parameters, "surface_id")) &&
                                           context.Gates.HighlightIds.Contains(
                                               Text(parameters, "highlight_id")),
                    "replay_slow" => context.Gates.ReplayEventIds.Contains(
                                         Text(parameters, "event_id")) &&
                                     context.Gates.ReplaySpeedIds.Contains(
                                         Text(parameters, "replay_speed_id")),
                    "open_chart" => context.Gates.ChartSlotIds.Contains(Text(parameters, "slot_id")) &&
                                    context.Gates.ChartIds.Contains(Text(parameters, "chart_id")),
                    "show_subtitle" => true,
                    _ => true
                };
                if (!checks)
                    return "episode_precondition_failed";
            }
            if (Text(canonical, "tool") != "trial.propose")
                return null;

            var targets = Map(parameters, "target_parameters");
            foreach (var key in targets.Keys)
                if (ResultParameterIds.Contains(key))
                    return "result_quantity_forbidden";
            if (!SameKeySet(targets.Keys, declaration.ControllableInputs.Keys))
                return "episode_precondition_failed";

            var delta = Map(parameters, "input_delta");
            var parameterId = Text(delta, "parameter_id");
            if (!targets.ContainsKey(parameterId) ||
                !Equals(Normalize(delta["to_value"]), Normalize(targets[parameterId])))
                return "input_delta_invalid";
            var declaredEffects = new HashSet<string>(declaration.DownstreamEffectIds,
                StringComparer.Ordinal);
            var declaredDelta = new HashSet<string>(Strings(delta, "downstream_effect_ids"),
                StringComparer.Ordinal);
            if (!declaredEffects.SetEquals(declaredDelta))
                return "input_delta_invalid";
            var declaredInvariants = new HashSet<string>(declaration.InvariantIds, StringComparer.Ordinal);
            var expectedInvariants = new HashSet<string>(
                Strings(parameters, "expected_invariant_ids"), StringComparer.Ordinal);
            if (!declaredInvariants.SetEquals(expectedInvariants))
                return "numeric_invariant_failed";

            Dictionary<string, object> baseline;
            if (operation == "create")
            {
                baseline = declaration.DefaultParameters.ToDictionary(pair => pair.Key,
                    pair => pair.Value, StringComparer.Ordinal);
            }
            else
            {
                var sourceId = TextOf(Map(target, "source_trial_ref"), "trial_id");
                if (!context.Trials.TryGetValue(sourceId, out var source))
                    return "episode_precondition_failed";
                baseline = source.TargetParameters.ToDictionary(pair => pair.Key, pair => pair.Value,
                    StringComparer.Ordinal);
                if (source.SetupFamilyId != declaration.SetupFamilyId)
                    return "episode_precondition_failed";
                if (delta.TryGetValue("from_value", out var fromValue) &&
                    !Equals(Normalize(fromValue), Normalize(baseline.GetValueOrDefault(parameterId))))
                    return "input_delta_invalid";
            }
            var expected = new Dictionary<string, object>(baseline, StringComparer.Ordinal)
            {
                [parameterId] = delta["to_value"]
            };
            if (!SameValue(targets, expected))
                return "input_delta_invalid";
            return null;
        }

        string Step6(IReadOnlyDictionary<string, object> canonical, ToolDeclaration declaration)
        {
            if (!context.Gates.SceneValid || !context.Gates.NumericalStateOk)
                return "numeric_invariant_failed";
            if (!context.Gates.StorageAvailable && Text(canonical, "tool") == "trial.propose")
                return "numeric_invariant_failed";
            if (context.Gates.HardSafetyHold)
                return "safety_boundary_failed";
            if (Text(canonical, "tool") != "trial.propose")
                return null;
            var parameters = Map(canonical, "parameters");
            var delta = Map(parameters, "input_delta");
            foreach (var pair in Map(parameters, "target_parameters"))
            {
                if (!declaration.ControllableInputs.TryGetValue(pair.Key, out var rule))
                    continue;
                var value = Normalize(pair.Value);
                if (value is double number && (double.IsNaN(number) || double.IsInfinity(number)))
                    return "numeric_invariant_failed";
                if (value is long or double && !(value is bool))
                {
                    var numeric = Convert.ToDouble(value);
                    if (rule.TryGetValue("minimum", out var minimum) && minimum != null &&
                        numeric < Convert.ToDouble(minimum))
                        return "numeric_invariant_failed";
                    if (rule.TryGetValue("maximum", out var maximum) && maximum != null &&
                        numeric > Convert.ToDouble(maximum))
                        return "numeric_invariant_failed";
                }
                if (rule.TryGetValue("unit", out var unit) && unit != null &&
                    pair.Key == Text(delta, "parameter_id") &&
                    !Equals(delta.GetValueOrDefault("unit") ?? null, unit))
                    return "numeric_invariant_failed";
            }
            return null;
        }

        // ---------------------------------------------------------------- step 7

        IReadOnlyDictionary<string, object> Execute(Dictionary<string, object> canonical,
            string fingerprint, ToolDeclaration declaration)
        {
            var command = Command(canonical, fingerprint);
            IReadOnlyDictionary<string, object> receipt;
            var rollback = context.Snapshot();
            var transaction = (executor as ITransactionalExecutor)?.BeginTransaction();
            try
            {
                receipt = executor != null ? executor.Execute(command, declaration) : LocalEffect(command);
            }
            catch (ExecutionRefusedException refusal)
            {
                (executor as ITransactionalExecutor)?.Rollback(transaction);
                context.Restore(rollback);
                // Mirrors the oracle: only these two codes stand as themselves;
                // every other executor refusal is a postcondition failure.
                var code = refusal.ReasonCode is "execution_postcondition_failed"
                    or "execution_unavailable"
                    ? refusal.ReasonCode
                    : "execution_postcondition_failed";
                return Persist(canonical, fingerprint, code, "step_7");
            }
            catch (Exception)
            {
                (executor as ITransactionalExecutor)?.Rollback(transaction);
                context.Restore(rollback);
                return Persist(canonical, fingerprint, "execution_unavailable", "step_7");
            }
            if (!VerifyReceipt(command, receipt, declaration))
            {
                (executor as ITransactionalExecutor)?.Rollback(transaction);
                context.Restore(rollback);
                return Persist(canonical, fingerprint, "execution_postcondition_failed", "step_7");
            }
            (executor as ITransactionalExecutor)?.Commit(transaction);
            var result = Result(canonical, fingerprint, "executed",
                reason: null, stage: null, acceptedCommandId: Text(command, "command_id"),
                receipt: receipt);
            // The first result is durable: an exact retry returns this record
            // with replay = true and never executes a second time.
            context.IdempotencyRecords.Add(Record(result));
            if (declaration.ConfirmationRequired)
            {
                var confirmationId = Text(canonical, "confirmation_id");
                if (confirmationId.Length > 0)
                    grants.Remove(confirmationId);
            }
            return result;
        }

        IReadOnlyDictionary<string, object> LocalEffect(IReadOnlyDictionary<string, object> command)
        {
            var parameters = Map(command, "parameters");
            var operation = Text(parameters, "operation");
            var refs = (IEnumerable<object>)command["refs"];
            var before = refs.FirstOrDefault();
            var kind = operation == "present"
                ? "presentation"
                : ToolDiffKinds[Text(command, "tool")];
            var changed = new List<object> { operation };
            return new Dictionary<string, object>
            {
                ["before_trial_ref"] = before,
                ["after_trial_ref"] = before,
                ["trial_revision_delta"] = 0L,
                ["global_revision"] = null,
                ["diff"] = new Dictionary<string, object>
                {
                    ["kind"] = kind,
                    ["changed_field_ids"] = changed
                },
                ["postcondition_evidence_ids"] = new List<object> { operation + "-verified" }
            };
        }

        bool VerifyReceipt(IReadOnlyDictionary<string, object> command,
            IReadOnlyDictionary<string, object> receipt, ToolDeclaration declaration)
        {
            if (receipt == null)
                return false;
            var operation = Text(Map(command, "parameters"), "operation");
            var tool = Text(command, "tool");
            var expectedDelta = ZeroRevisionOperations.Contains(operation) ? 0 : 1;
            if (Long(receipt, "trial_revision_delta") != expectedDelta)
                return false;
            var expectedKind = operation == "present" ? "presentation" : ToolDiffKinds[tool];
            if (Text(Map(receipt, "diff"), "kind") != expectedKind)
                return false;
            var before = receipt.GetValueOrDefault("before_trial_ref");
            var after = receipt.GetValueOrDefault("after_trial_ref");
            if (tool != "trial.propose" && expectedDelta == 0 && before != null && after != null &&
                CanonicalJson.Text(before) != CanonicalJson.Text(after))
                return false;
            if (tool == "trial.propose" && !CandidateVerified(command, declaration))
                return false;
            return true;
        }

        /// <summary>
        /// A new-Trial candidate exists only in <c>setup</c> with
        /// <c>acceptance = proposed</c> at tick 0. The device runtime reports
        /// that through the same validation context it just mutated.
        /// </summary>
        bool CandidateVerified(IReadOnlyDictionary<string, object> command,
            ToolDeclaration declaration)
        {
            var trialId = CandidateTrialId(Text(command, "call_id"));
            if (!context.Trials.TryGetValue(trialId, out var trial))
                return true;
            return trial.Phase == "setup" && trial.Acceptance == "proposed";
        }

        public static string CandidateTrialId(string callId) =>
            "trial-" + CanonicalJson.Sha256Hex(System.Text.Encoding.ASCII.GetBytes(callId ?? ""))
                .Substring(0, 24);

        IReadOnlyDictionary<string, object> Command(IReadOnlyDictionary<string, object> canonical,
            string fingerprint)
        {
            var digest = CanonicalJson.Sha256Hex(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["scope"] = Scope(canonical),
                ["call_id"] = Text(canonical, "call_id"),
                ["fingerprint"] = fingerprint
            });
            return new Dictionary<string, object>
            {
                ["command_id"] = "command-" + digest.Substring(0, 24),
                ["idempotency_scope"] = Scope(canonical),
                ["call_id"] = Text(canonical, "call_id"),
                ["fingerprint"] = fingerprint,
                ["tool"] = Text(canonical, "tool"),
                ["parameters"] = canonical["parameters"],
                ["refs"] = canonical["refs"]
            };
        }

        // -------------------------------------------------------------- results

        IReadOnlyDictionary<string, object> Persist(Dictionary<string, object> canonical,
            string fingerprint, string reason, string stage)
        {
            var result = Result(canonical, fingerprint, "rejected", reason: reason, stage: stage);
            context.IdempotencyRecords.Add(Record(result));
            return result;
        }

        Dictionary<string, object> Record(IReadOnlyDictionary<string, object> result) =>
            new(StringComparer.Ordinal)
            {
                ["idempotency_scope"] = result["idempotency_scope"],
                ["call_id"] = result["call_id"],
                ["fingerprint"] = result["fingerprint"],
                ["first_result"] = result
            };

        Dictionary<string, object> Result(IReadOnlyDictionary<string, object> canonical,
            string fingerprint, string outcome, string reason = null, string stage = null,
            string acceptedCommandId = null,
            IReadOnlyDictionary<string, object> receipt = null, bool replay = false) =>
            new(StringComparer.Ordinal)
            {
                ["tool_schema_version"] = Text(canonical, "tool_schema_version"),
                ["idempotency_scope"] = Scope(canonical),
                ["call_id"] = Text(canonical, "call_id"),
                ["fingerprint"] = fingerprint,
                ["outcome"] = outcome,
                ["reason_code"] = reason,
                ["target"] = canonical.GetValueOrDefault("target"),
                ["basis_trial_ref"] = canonical.GetValueOrDefault("basis_trial_ref"),
                ["refs"] = canonical.GetValueOrDefault("refs"),
                ["accepted_command_id"] = acceptedCommandId,
                ["execution_receipt"] = receipt,
                ["rejection_stage"] = stage,
                ["replay"] = replay
            };

        Dictionary<string, object> Fallback(object canonicalOf)
        {
            var canonical = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["tool_schema_version"] = "1.0.0",
                ["call_id"] = "invalid-call",
                ["origin"] = "model",
                ["tool"] = "state.read",
                ["target"] = new Dictionary<string, object>
                {
                    ["kind"] = "new_trial",
                    ["episode_id"] = context.EpisodeId,
                    ["source_trial_ref"] = null
                },
                ["basis_trial_ref"] = null,
                ["refs"] = new List<object>(),
                ["parameters"] = new Dictionary<string, object>
                {
                    ["operation"] = "snapshot",
                    ["target_trial_ref"] = new Dictionary<string, object>
                    {
                        ["trial_id"] = "invalid",
                        ["trial_revision"] = 1L
                    }
                }
            };
            if (canonicalOf is IReadOnlyDictionary<string, object> proposal)
                foreach (var field in new[]
                         {
                             "tool_schema_version", "call_id", "origin", "tool", "target",
                             "basis_trial_ref", "refs"
                         })
                    if (proposal.TryGetValue(field, out var value))
                        canonical[field] = value;
            return canonical;
        }

        // -------------------------------------------------------------- helpers

        public static IReadOnlyDictionary<string, object> Map(
            IReadOnlyDictionary<string, object> source, string field) =>
            source != null && source.TryGetValue(field, out var value) &&
            value is IReadOnlyDictionary<string, object> map
                ? map
                : new Dictionary<string, object>();

        public static string Text(IReadOnlyDictionary<string, object> source, string field) =>
            source != null && source.TryGetValue(field, out var value) && value is string text
                ? text
                : "";

        static string TextOf(object value, string field) =>
            value is IReadOnlyDictionary<string, object> map ? Text(map, field) : "";

        static int IntOf(object value, string field) =>
            value is IReadOnlyDictionary<string, object> map &&
            map.TryGetValue(field, out var item) && item is long number
                ? (int)number
                : 0;

        static long Long(IReadOnlyDictionary<string, object> source, string field) =>
            source.TryGetValue(field, out var value) && value is long number ? number : long.MinValue;

        static bool Bool(IReadOnlyDictionary<string, object> source, string field) =>
            source != null && source.TryGetValue(field, out var value) && value is bool flag && flag;

        static IEnumerable<string> Strings(IReadOnlyDictionary<string, object> source, string field) =>
            source != null && source.TryGetValue(field, out var value) &&
            value is IEnumerable<object> list
                ? list.OfType<string>()
                : Array.Empty<string>();

        static bool SameKeySet(IEnumerable<string> left, IEnumerable<string> right) =>
            new HashSet<string>(left, StringComparer.Ordinal)
                .SetEquals(new HashSet<string>(right, StringComparer.Ordinal));

        static bool SameValue(IReadOnlyDictionary<string, object> left,
            IReadOnlyDictionary<string, object> right) =>
            left.Count == right.Count && left.All(pair =>
                right.TryGetValue(pair.Key, out var value) &&
                Equals(Normalize(value), Normalize(pair.Value)));

        static bool Contains(IReadOnlyList<string> values, string value)
        {
            for (var index = 0; index < values.Count; index++)
                if (string.Equals(values[index], value, StringComparison.Ordinal))
                    return true;
            return false;
        }
    }
}
