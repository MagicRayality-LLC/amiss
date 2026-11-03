using System;
using System.Collections.Generic;
using System.Linq;
using Amiss.Shell.Evaluation;

namespace Amiss.Shell.Tutor
{
    /// <summary>
    /// One proposed Trial candidate: the only shape a model <c>trial.propose</c>
    /// may create (04 section 2.4). It exists in <c>setup</c> with
    /// <c>acceptance = proposed</c> at Tick 0, never preempts the live Trial, and
    /// becomes live only through the separate explicit user acceptance command.
    /// </summary>
    public sealed class TrialCandidate
    {
        public string TrialId { get; init; } = "";
        public int TrialRevision { get; init; } = 1;
        public string Phase { get; init; } = "setup";
        public string Acceptance { get; init; } = "proposed";
        public string Activity { get; init; } = "running";
        public string SetupFamilyId { get; init; } = "";
        public IReadOnlyDictionary<string, object> TargetParameters { get; init; } =
            new Dictionary<string, object>();
        public string SourceTrialId { get; init; } = "";
        public int Tick => 0;

        public Dictionary<string, object> Ref => new(StringComparer.Ordinal)
        {
            ["trial_id"] = TrialId,
            ["trial_revision"] = TrialRevision
        };
    }

    /// <summary>
    /// Owns the proposed candidates of one session. It refuses a duplicate id,
    /// a full comparison set, a non-finite parameter, and a result-quantity
    /// parameter, so no proposal can become a measurement or a state vector.
    /// Acceptance and abandonment are explicit local commands, never tool calls.
    /// </summary>
    public sealed class TrialCandidateStore
    {
        public const int MaxComparisonWidth = 4;

        static readonly HashSet<string> ForbiddenParameterIds = new(StringComparer.Ordinal)
        {
            "measurement", "measurement_value", "result", "result_value", "temperature",
            "conclusion"
        };

        readonly Dictionary<string, TrialCandidate> candidates = new(StringComparer.Ordinal);

        public IReadOnlyCollection<TrialCandidate> Candidates => candidates.Values.ToList();

        public TrialCandidate Get(string trialId) =>
            trialId != null && candidates.TryGetValue(trialId, out var candidate)
                ? candidate
                : null;

        public string Refusal { get; private set; } = "";

        public bool TryPropose(string trialId, string setupFamilyId,
            IReadOnlyDictionary<string, object> targetParameters, string sourceTrialId,
            int existingTrialCount, out TrialCandidate candidate, out string reason)
        {
            candidate = null;
            reason = "";
            if (string.IsNullOrEmpty(trialId))
            {
                reason = "trial_id_missing";
                Refusal = reason;
                return false;
            }
            if (candidates.ContainsKey(trialId))
            {
                reason = "duplicate_trial_id";
                Refusal = reason;
                return false;
            }
            if (existingTrialCount + candidates.Count >= MaxComparisonWidth)
            {
                reason = "comparison_set_full";
                Refusal = reason;
                return false;
            }
            if (string.IsNullOrEmpty(setupFamilyId) ||
                targetParameters == null || targetParameters.Count == 0)
            {
                reason = "setup_family_missing";
                Refusal = reason;
                return false;
            }
            foreach (var pair in targetParameters)
            {
                if (ForbiddenParameterIds.Contains(pair.Key))
                {
                    reason = "result_quantity_forbidden";
                    Refusal = reason;
                    return false;
                }
                var finite = pair.Value switch
                {
                    double number => !double.IsNaN(number) && !double.IsInfinity(number),
                    float number => !float.IsNaN(number) && !float.IsInfinity(number),
                    int or long or short or byte or bool or string => true,
                    _ => false
                };
                if (!finite)
                {
                    reason = "scientific_parameter_invalid";
                    Refusal = reason;
                    return false;
                }
            }
            candidate = new TrialCandidate
            {
                TrialId = trialId,
                SetupFamilyId = setupFamilyId,
                TargetParameters = new Dictionary<string, object>(targetParameters,
                    StringComparer.Ordinal),
                SourceTrialId = sourceTrialId ?? ""
            };
            candidates[trialId] = candidate;
            Refusal = "";
            return true;
        }

        /// <summary>Captures the candidates so a failed step 7 restores them.</summary>
        public CandidateSnapshot Snapshot() =>
            new(new Dictionary<string, TrialCandidate>(candidates, StringComparer.Ordinal));

        public void Restore(CandidateSnapshot snapshot)
        {
            if (snapshot == null)
                return;
            candidates.Clear();
            foreach (var pair in snapshot.Candidates)
                candidates[pair.Key] = pair.Value;
        }

        /// <summary>
        /// The explicit user acceptance. It is the only path from
        /// <c>proposed</c> to a startable candidate, and it is a local command:
        /// no tool call can substitute for it.
        /// </summary>
        public bool TryAccept(string trialId, out string reason)
        {
            reason = "";
            if (trialId == null || !candidates.TryGetValue(trialId, out var candidate))
            {
                reason = "unknown_trial";
                Refusal = reason;
                return false;
            }
            if (candidate.Acceptance != "proposed")
            {
                reason = "already_decided";
                Refusal = reason;
                return false;
            }
            candidates[trialId] = new TrialCandidate
            {
                TrialId = candidate.TrialId,
                TrialRevision = candidate.TrialRevision + 1,
                Phase = "setup",
                Acceptance = "accepted",
                Activity = candidate.Activity,
                SetupFamilyId = candidate.SetupFamilyId,
                TargetParameters = candidate.TargetParameters,
                SourceTrialId = candidate.SourceTrialId
            };
            Refusal = "";
            return true;
        }

        public bool TryAbandon(string trialId, out string reason)
        {
            reason = "";
            if (trialId == null || !candidates.Remove(trialId))
            {
                reason = "unknown_trial";
                Refusal = reason;
                return false;
            }
            Refusal = "";
            return true;
        }
    }

    /// <summary>Immutable copy of the proposed candidates, for step-7 rollback.</summary>
    public sealed class CandidateSnapshot
    {
        public CandidateSnapshot(Dictionary<string, TrialCandidate> candidates)
        {
            Candidates = candidates;
        }

        public Dictionary<string, TrialCandidate> Candidates { get; }
    }

    /// <summary>
    /// Projects the device's own Trial state into the declarative context the
    /// validator mirror reads. Values come from committed local state only; no
    /// model reply can add a Trial, a ref, a token, or a gate.
    /// </summary>
    public sealed class DeviceValidationContext : ValidationContext
    {
        public DeviceValidationContext(string episodeId, string currentTrialId,
            IEnumerable<TrialCandidate> candidates, PolicyTokenSet tokens,
            IEnumerable<ToolDeclaration> declarations, LocalGates gates,
            string setupFamilyId, IReadOnlyDictionary<string, object> targetParameters)
        {
            EpisodeId = episodeId ?? "";
            CurrentTrialId = currentTrialId ?? "";
            if (!string.IsNullOrEmpty(CurrentTrialId))
            {
                var live = new TrialState
                {
                    TrialId = CurrentTrialId,
                    Phase = "setup",
                    Acceptance = "accepted",
                    Activity = "running",
                    SetupFamilyId = setupFamilyId ?? "",
                    TargetParameters = targetParameters == null
                        ? new Dictionary<string, object>(StringComparer.Ordinal)
                        : new Dictionary<string, object>(targetParameters, StringComparer.Ordinal)
                };
                Trials[CurrentTrialId] = live;
                ComparisonTrialIds.Add(CurrentTrialId);
            }
            foreach (var candidate in candidates ?? Enumerable.Empty<TrialCandidate>())
            {
                Trials[candidate.TrialId] = new TrialState
                {
                    TrialId = candidate.TrialId,
                    TrialRevision = candidate.TrialRevision,
                    Phase = candidate.Phase,
                    Acceptance = candidate.Acceptance,
                    Activity = candidate.Activity,
                    SetupFamilyId = candidate.SetupFamilyId,
                    TargetParameters = new Dictionary<string, object>(
                        candidate.TargetParameters, StringComparer.Ordinal)
                };
                ComparisonTrialIds.Add(candidate.TrialId);
            }
            PolicyTokens = tokens ?? new PolicyTokenSet { EpisodeId = EpisodeId };
            Gates = gates ?? new LocalGates();
            foreach (var declaration in declarations ?? Enumerable.Empty<ToolDeclaration>())
                Declarations[declaration.Tool + "/" + declaration.Operation] = declaration;
        }
    }

    /// <summary>
    /// Applies one accepted command to the device Trial state and returns the
    /// actual receipt. A Trial mutation is `trial.propose` or `trial.orchestrate`;
    /// presentation and speech change presentation only and never a revision.
    /// </summary>
    public sealed class DeviceCommandExecutor : ICommandExecutor, ITransactionalExecutor
    {
        readonly TrialCandidateStore candidates;
        readonly Func<int> existingTrialCount;
        readonly ValidationContext validationContext;

        /// <summary>
        /// <paramref name="validationContext"/> is the live context when the
        /// device also feeds proposal validation. A proposed candidate is
        /// published into it, so the very next reply sees the candidate at its
        /// committed revision instead of a stale view.
        /// </summary>
        public DeviceCommandExecutor(TrialCandidateStore candidates, Func<int> existingTrialCount,
            ValidationContext validationContext = null)
        {
            this.candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
            this.existingTrialCount = existingTrialCount ?? (() => 0);
            this.validationContext = validationContext;
        }

        /// <summary>
        /// Captures the candidate store and the published context, so a failed
        /// step 7 can drop a candidate it created instead of leaving a partial
        /// Trial behind.
        /// </summary>
        public object BeginTransaction() => new Mark(candidates.Snapshot(),
            validationContext?.Snapshot());

        public void Commit(object mark)
        {
        }

        public void Rollback(object mark)
        {
            if (mark is not Mark captured)
                return;
            candidates.Restore(captured.Candidates);
            validationContext?.Restore(captured.Context);
        }

        sealed class Mark
        {
            public Mark(CandidateSnapshot candidates, ValidationSnapshot context)
            {
                Candidates = candidates;
                Context = context;
            }

            public CandidateSnapshot Candidates { get; }
            public ValidationSnapshot Context { get; }
        }

        public string LastRefusal { get; private set; } = "";

        public IReadOnlyList<(string Tool, string Operation)> Applied { get; } =
            new List<(string, string)>();

        public IReadOnlyDictionary<string, object> Execute(
            IReadOnlyDictionary<string, object> command, ToolDeclaration declaration)
        {
            var tool = ProposalValidator.Text(command, "tool");
            var parameters = ProposalValidator.Map(command, "parameters");
            var operation = ProposalValidator.Text(parameters, "operation");
            var refs = command.GetValueOrDefault("refs") as IEnumerable<object>;
            var before = refs?.FirstOrDefault();
            var applied = (List<(string, string)>)Applied;

            if (tool == "trial.propose")
            {
                var trialId = ProposalValidator.CandidateTrialId(
                    ProposalValidator.Text(command, "call_id"));
                var targets = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var pair in ProposalValidator.Map(parameters, "target_parameters"))
                    targets[pair.Key] = pair.Value;
                var source = ProposalValidator.Map(command, "target")
                    .GetValueOrDefault("source_trial_ref");
                var sourceId = source is IReadOnlyDictionary<string, object> sourceMap
                    ? ProposalValidator.Text(sourceMap, "trial_id")
                    : "";
                if (!candidates.TryPropose(trialId, declaration.SetupFamilyId, targets, sourceId,
                        existingTrialCount(), out var candidate, out var reason))
                {
                    LastRefusal = reason;
                    throw new ExecutionRefusedException(reason, reason);
                }
                applied.Add((tool, operation));
                if (validationContext != null)
                {
                    validationContext.Trials[candidate.TrialId] = new TrialState
                    {
                        TrialId = candidate.TrialId,
                        TrialRevision = candidate.TrialRevision,
                        Phase = candidate.Phase,
                        Acceptance = candidate.Acceptance,
                        Activity = candidate.Activity,
                        SetupFamilyId = candidate.SetupFamilyId,
                        TargetParameters = new Dictionary<string, object>(
                            candidate.TargetParameters, StringComparer.Ordinal)
                    };
                    if (!validationContext.ComparisonTrialIds.Contains(candidate.TrialId))
                        validationContext.ComparisonTrialIds.Add(candidate.TrialId);
                }
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["before_trial_ref"] = before,
                    ["after_trial_ref"] = candidate.Ref,
                    ["trial_revision_delta"] = 0L,
                    ["global_revision"] = null,
                    ["diff"] = new Dictionary<string, object>
                    {
                        ["kind"] = "trial_state",
                        ["changed_field_ids"] = new List<object> { "trial_created" }
                    },
                    ["postcondition_evidence_ids"] = new List<object>(declaration.InvariantIds)
                };
            }

            if (tool == "trial.orchestrate")
            {
                LastRefusal = "device_transition_not_declared";
                applied.Add((tool, operation));
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["before_trial_ref"] = before,
                    ["after_trial_ref"] = before,
                    ["trial_revision_delta"] = 0L,
                    ["global_revision"] = null,
                    ["diff"] = new Dictionary<string, object>
                    {
                        ["kind"] = "trial_state",
                        ["changed_field_ids"] = new List<object> { operation }
                    },
                    ["postcondition_evidence_ids"] = new List<object> { operation + "-verified" }
                };
            }

            applied.Add((tool, operation));
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["before_trial_ref"] = before,
                ["after_trial_ref"] = before,
                ["trial_revision_delta"] = 0L,
                ["global_revision"] = null,
                ["diff"] = new Dictionary<string, object>
                {
                    ["kind"] = tool == "presentation.request" || operation == "present"
                        ? "presentation"
                        : "voice",
                    ["changed_field_ids"] = new List<object> { operation }
                },
                ["postcondition_evidence_ids"] = new List<object> { operation + "-verified" }
            };
        }
    }
}
