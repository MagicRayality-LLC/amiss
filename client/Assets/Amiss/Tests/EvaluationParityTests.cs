using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amiss.Shell.Evaluation;
using Amiss.Shell.Tutor;
using NUnit.Framework;
using UnityEngine;

namespace Amiss.Tests
{
    /// <summary>
    /// Drives the device validator mirror and the device canonical encoder over
    /// the shared vector file that the Python oracle generates. Both languages
    /// must reproduce the same canonical bytes, the same request hash, and the
    /// same validation outcome for every case; a divergence fails here rather
    /// than on a headset.
    /// </summary>
    public class EvaluationParityTests
    {
        static IReadOnlyDictionary<string, object> vectors;

        static IReadOnlyDictionary<string, object> Vectors
        {
            get
            {
                if (vectors != null)
                    return vectors;
                var path = VectorPath();
                Assert.IsTrue(File.Exists(path),
                    "schema/vectors/admission-vectors.json must sit next to the client tree");
                var parsed = CanonicalJsonReader.Parse(File.ReadAllText(path, Encoding.UTF8));
                vectors = (IReadOnlyDictionary<string, object>)parsed;
                return vectors;
            }
        }

        static string VectorPath()
        {
            var repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            return Path.Combine(repoRoot, "schema", "vectors", "admission-vectors.json");
        }

        static IReadOnlyList<object> List(object value) =>
            value is IEnumerable<object> items ? items.ToList() : new List<object>();

        static IReadOnlyDictionary<string, object> Map(object value) =>
            value as IReadOnlyDictionary<string, object> ?? new Dictionary<string, object>();

        static string Text(object value) => value as string ?? "";

        // ------------------------------------------------------------- encoding

        [Test]
        public void CanonicalEncoderMatchesEverySharedCorpusEntry()
        {
            var corpus = List(Vectors["canonicalisation"]);
            Assert.GreaterOrEqual(corpus.Count, 7);
            foreach (var raw in corpus)
            {
                var entry = Map(raw);
                var name = Text(entry["name"]);
                var actual = CanonicalJson.Text(entry["value"]);
                Assert.AreEqual(Text(entry["canonical_json"]), actual, name);
                Assert.AreEqual(Text(entry["sha256"]), CanonicalJson.Sha256Hex(entry["value"]), name);
            }
        }

        [TestCase(0.1, "0.1")]
        [TestCase(1.0, "1.0")]
        [TestCase(0.0, "0.0")]
        [TestCase(-0.0, "-0.0")]
        [TestCase(1e-7, "1e-07")]
        [TestCase(1e16, "1e+16")]
        [TestCase(1234567890123.0, "1234567890123.0")]
        [TestCase(2.5, "2.5")]
        [TestCase(-3.25, "-3.25")]
        [TestCase(-0.0, "-0.0")]
        public void DoubleEncodingMatchesPythonJsonDumps(double value, string expected)
        {
            Assert.AreEqual(expected, CanonicalJson.Number(value));
        }

        [Test]
        public void ObjectKeysAreSortedByUtf8Bytes()
        {
            var value = new Dictionary<string, object>
            {
                ["b"] = 1L,
                ["a"] = 2L,
                ["C"] = 3L
            };
            Assert.AreEqual("{\"C\":3,\"a\":2,\"b\":1}", CanonicalJson.Text(value));
        }

        [Test]
        public void NonFiniteNumbersAreRefused()
        {
            Assert.Throws<ArgumentException>(() => CanonicalJson.Number(double.NaN));
            Assert.Throws<ArgumentException>(() => CanonicalJson.Number(double.PositiveInfinity));
        }

        // -------------------------------------------------------------- request

        [Test]
        public void RequestAnchorHashMatchesThePythonBuilder()
        {
            var anchor = Map(Vectors["request"]);
            var request = Map(anchor["request"]);
            Assert.AreEqual(Text(anchor["request_sha256"]),
                EvaluationRequestBuilder.RequestHash(request));
            Assert.AreEqual(Text(anchor["context_payload_sha256"]),
                CanonicalJson.Sha256Hex(anchor["context_payload"]));
            Assert.AreEqual(EvaluationContract.ModelId, Text(request["model"]));
            Assert.AreEqual(EvaluationContract.EndpointHost,
                Text(Map(anchor["provenance"])["endpoint_host"]));
        }

        [Test]
        public void ProvenanceRecordIsClosedAndSchemaShaped()
        {
            var anchor = Map(Vectors["request"]);
            var provenance = Map(anchor["provenance"]);
            Assert.That(ModelProvenance.Errors(provenance), Is.Empty);
            Assert.AreEqual("live", Text(provenance["mode"]));

            var tampered = provenance.ToDictionary(pair => pair.Key, pair => pair.Value,
                StringComparer.Ordinal);
            tampered["api_key"] = "amiss-test-sentinel-key-0000000000000000";
            Assert.IsNotEmpty(ModelProvenance.Errors(tampered));
        }

        [Test]
        public void TraceLineCarriesModelModeAndRequestId()
        {
            var provenance = Map(Map(Vectors["request"])["provenance"]);
            var line = ModelProvenance.TraceLine(provenance);
            StringAssert.Contains("muse-spark-1.3", line);
            StringAssert.Contains("live", line);
            StringAssert.Contains(Text(provenance["request_id"]), line);

            var recorded = provenance.ToDictionary(pair => pair.Key, pair => pair.Value,
                StringComparer.Ordinal);
            recorded["mode"] = "recorded";
            StringAssert.Contains("RECORDED", ModelProvenance.TraceLine(recorded));
        }

        // ------------------------------------------------------------ admission

        [Test]
        public void EverySharedAdmissionCaseMatchesTheOracleOutcome()
        {
            var contexts = Map(Vectors["contexts"]);
            var cases = List(Vectors["admission"]);
            Assert.GreaterOrEqual(cases.Count, 15);
            foreach (var raw in cases)
            {
                var item = Map(raw);
                var name = Text(item["name"]);
                var context = BuildContext(Map(contexts[Text(item["context_sha256"])]));
                var validator = new ProposalValidator(context);
                var proposals = List(Map(item["response"])["tool_proposals"]);
                var results = validator.ValidateReply(proposals);
                var expected = List(Map(item["expect"])["results"]);
                Assert.AreEqual(expected.Count, results.Count, name);
                for (var index = 0; index < expected.Count; index++)
                {
                    var want = Map(expected[index]);
                    var got = results[index];
                    Assert.AreEqual(Text(want["call_id"]),
                        ProposalValidator.Text(got, "call_id"), name + " call id");
                    Assert.AreEqual(Text(want["outcome"]),
                        ProposalValidator.Text(got, "outcome"), name + " outcome");
                    Assert.AreEqual(want.GetValueOrDefault("reason_code") as string,
                        ProposalValidator.Text(got, "reason_code").Length == 0
                            ? null
                            : ProposalValidator.Text(got, "reason_code"), name + " reason");
                    Assert.AreEqual(want.GetValueOrDefault("rejection_stage") as string,
                        ProposalValidator.Text(got, "rejection_stage").Length == 0
                            ? null
                            : ProposalValidator.Text(got, "rejection_stage"), name + " stage");
                    Assert.AreEqual(want["replay"], got["replay"], name + " replay");
                    Assert.AreEqual(Text(want["fingerprint"]),
                        ProposalValidator.Text(got, "fingerprint"), name + " fingerprint");
                }
            }
        }

        [Test]
        public void AcceptedProposalCreatesOnlyATickZeroProposedCandidate()
        {
            var contexts = Map(Vectors["contexts"]);
            var target = List(Vectors["admission"])
                .Select(Map)
                .First(item => Text(item["name"]).StartsWith("accepted proposal",
                    StringComparison.Ordinal));
            var context = BuildContext(Map(contexts[Text(target["context_sha256"])]));
            var store = new TrialCandidateStore();
            var validator = new ProposalValidator(context,
                new DeviceCommandExecutor(store, () => 1, context));
            var results = validator.ValidateReply(List(Map(target["response"])["tool_proposals"]));

            Assert.AreEqual("executed", ProposalValidator.Text(results[0], "outcome"));
            Assert.AreEqual(1, store.Candidates.Count);
            var candidate = store.Candidates.First();
            Assert.AreEqual("setup", candidate.Phase);
            Assert.AreEqual("proposed", candidate.Acceptance);
            Assert.AreEqual(0, candidate.Tick);
            Assert.AreEqual(1, context.Trials[candidate.TrialId].TrialRevision);
        }

        [Test]
        public void ValidationIsDeterministicAcrossRepeatedRuns()
        {
            var contexts = Map(Vectors["contexts"]);
            var first = List(Vectors["admission"]).Select(Map).First();
            var proposals = List(Map(first["response"])["tool_proposals"]);
            var declarative = Map(contexts[Text(first["context_sha256"])]);

            string Run()
            {
                var validator = new ProposalValidator(BuildContext(declarative));
                var results = validator.ValidateReply(proposals);
                return string.Join("|", results.Select(result =>
                    ProposalValidator.Text(result, "call_id") + ":" +
                    ProposalValidator.Text(result, "fingerprint") + ":" +
                    ProposalValidator.Text(result, "outcome")));
            }

            Assert.AreEqual(Run(), Run());
        }

        // ------------------------------------------------------------ machinery

        [Test]
        public void TwoProposalsInOneReplyAreValidatedInOrder()
        {
            var contexts = Map(Vectors["contexts"]);
            var target = List(Vectors["admission"]).Select(Map)
                .First(item => Text(item["name"]).StartsWith("two proposals",
                    StringComparison.Ordinal));
            var validator = new ProposalValidator(BuildContext(
                Map(contexts[Text(target["context_sha256"])])));
            var results = validator.ValidateReply(List(Map(target["response"])["tool_proposals"]));
            Assert.AreEqual(2, results.Count);
            CollectionAssert.AreEqual(new[] { "call-first", "call-second" },
                results.Select(result => ProposalValidator.Text(result, "call_id")));
        }

        [Test]
        public void AStaleUnitRejectsTheWholeReply()
        {
            var contexts = Map(Vectors["contexts"]);
            var target = List(Vectors["admission"]).Select(Map)
                .First(item => Text(item["name"]).StartsWith("two proposals",
                    StringComparison.Ordinal));
            var validator = new ProposalValidator(BuildContext(
                Map(contexts[Text(target["context_sha256"])])));
            var proposals = List(Map(target["response"])["tool_proposals"]).Select(Map).ToList();
            var stale = proposals[1].ToDictionary(pair => pair.Key, pair => pair.Value,
                StringComparer.Ordinal);
            stale["refs"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["trial_id"] = "trial-a",
                    ["trial_revision"] = 42L
                }
            };
            var results = validator.ValidateReply(new object[] { proposals[0], stale });
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual("rejected", ProposalValidator.Text(results[0], "outcome"));
            Assert.AreEqual("stale_trial_ref", ProposalValidator.Text(results[0], "reason_code"));
        }

        [Test]
        public void TheClosedSchemaRefusesScriptNetworkAndVectorCapabilities()
        {
            var contexts = Map(Vectors["contexts"]);
            var declarative = Map(contexts.Values.Select(Map).First());
            var baseProposal = new Dictionary<string, object>
            {
                ["tool_schema_version"] = "1.0.0",
                ["call_id"] = "call-closed",
                ["origin"] = "model",
                ["tool"] = "state.read",
                ["target"] = new Dictionary<string, object>
                {
                    ["kind"] = "existing",
                    ["trial_ref"] = new Dictionary<string, object>
                    {
                        ["trial_id"] = "trial-a",
                        ["trial_revision"] = 1L
                    }
                },
                ["basis_trial_ref"] = new Dictionary<string, object>
                {
                    ["trial_id"] = "trial-a",
                    ["trial_revision"] = 1L
                },
                ["refs"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["trial_id"] = "trial-a",
                        ["trial_revision"] = 1L
                    }
                },
                ["parameters"] = new Dictionary<string, object>
                {
                    ["operation"] = "snapshot",
                    ["target_trial_ref"] = new Dictionary<string, object>
                    {
                        ["trial_id"] = "trial-a",
                        ["trial_revision"] = 1L
                    }
                }
            };
            foreach (var (field, value) in new (string, object)[]
                     {
                         ("script", "synthetic-nonexecutable-text"),
                         ("url", "network-request"),
                         ("position", new List<object> { 0L, 0L, 0L }),
                         ("temperature", 3L),
                         ("trial_revision", 9L)
                     })
            {
                var proposal = baseProposal.ToDictionary(pair => pair.Key, pair => pair.Value,
                    StringComparer.Ordinal);
                proposal[field] = value;
                var validator = new ProposalValidator(BuildContext(declarative));
                var result = validator.Validate(proposal);
                Assert.AreEqual("rejected", ProposalValidator.Text(result, "outcome"), field);
                Assert.AreEqual("step_1", ProposalValidator.Text(result, "rejection_stage"),
                    field);
            }
        }

        [Test]
        public void AReplayedCallReturnsTheFirstResultFlaggedReplay()
        {
            var contexts = Map(Vectors["contexts"]);
            var target = List(Vectors["admission"]).Select(Map)
                .First(item => Text(item["name"]).StartsWith("exact replay",
                    StringComparison.Ordinal));
            var validator = new ProposalValidator(BuildContext(
                Map(contexts[Text(target["context_sha256"])])));
            var results = validator.ValidateReply(List(Map(target["response"])["tool_proposals"]));
            Assert.AreEqual("executed", ProposalValidator.Text(results[0], "outcome"));
            Assert.AreEqual(true, results[0]["replay"]);
        }

        // --------------------------------------------------------- fail closed

        [Test]
        public void ClosedTransportReportsAReadableFailClosedTrace()
        {
            var context = new ValidationContext { EpisodeId = "fake-discrete" };
            var transport = new DeviceTutorTransport(
                new ProposalValidator(context));
            Assert.IsFalse(transport.Available);
            Assert.IsTrue(transport.TraceLine.StartsWith("Model: closed", StringComparison.Ordinal));
            StringAssert.Contains("policy", transport.TraceLine);
        }

        [Test]
        public async Task NoPolicyEnvelopeFailsClosedWithATypedError()
        {
            var transport = new EvaluationDirectTransport(
                new ScriptedSender(new ModelReply(new Dictionary<string, object>(), 0.1,
                    "2026-11-01T12:00:00Z", "2026-11-01T12:00:01Z", 1, 1)),
                new ModelKeyHandle("amiss-test-sentinel-key-0000000000000000"), null);
            var request = new Dictionary<string, object>
            {
                ["request_id"] = "request-a",
                ["policy_envelope"] = null
            };
            var outcome = await transport.Send(request);
            Assert.IsFalse(outcome.Ok);
            Assert.AreEqual("no_policy", outcome.Failure.ReasonCode);
            StringAssert.Contains("policy", outcome.Failure.Readable);
        }

        [Test]
        public async Task NoKeyFailsClosedWithoutCallingTheSender()
        {
            var sender = new ScriptedSender(null);
            var transport = new EvaluationDirectTransport(sender, new ModelKeyHandle(),
                new PolicyEnvelope("synthetic-policy-v1", "Synthetic fixture policy."));
            var request = new Dictionary<string, object>
            {
                ["request_id"] = "request-a",
                ["policy_envelope"] = new Dictionary<string, object>
                {
                    ["policy_version"] = "synthetic-policy-v1",
                    ["system"] = "Synthetic fixture policy."
                }
            };
            var outcome = await transport.Send(request);
            Assert.IsFalse(outcome.Ok);
            Assert.AreEqual("no_credential", outcome.Failure.ReasonCode);
            Assert.AreEqual(0, sender.Calls);
        }

        [Test]
        public async Task LateReplyIsDroppedAsATypedTimeout()
        {
            var sender = new ScriptedSender(new ModelReply(new Dictionary<string, object>(), 30.5,
                "2026-11-01T12:00:00Z", "2026-11-01T12:00:31Z", 1, 1));
            var transport = new EvaluationDirectTransport(sender,
                new ModelKeyHandle("amiss-test-sentinel-key-0000000000000000"),
                new PolicyEnvelope("synthetic-policy-v1", "Synthetic fixture policy."));
            var outcome = await transport.Send(new Dictionary<string, object>
            {
                ["request_id"] = "request-a",
                ["policy_envelope"] = new Dictionary<string, object>
                {
                    ["policy_version"] = "synthetic-policy-v1",
                    ["system"] = "Synthetic fixture policy."
                }
            });
            Assert.IsFalse(outcome.Ok);
            Assert.AreEqual("timeout", outcome.Failure.Kind);
            Assert.IsFalse(outcome.Failure.ReasonCode.Length == 0);
        }

        [Test]
        public void TheKeyHandleNeverRendersItsValue()
        {
            const string sentinel = "amiss-test-sentinel-key-0000000000000000";
            var handle = new ModelKeyHandle(sentinel);
            Assert.IsTrue(handle.Present);
            StringAssert.DoesNotContain(sentinel, handle.ToString());
            Assert.AreEqual(sentinel, handle.Use());
            handle.Clear();
            Assert.IsFalse(handle.Present);
            Assert.Throws<InvalidOperationException>(() => handle.Use());
        }

        [Test]
        public void ARecordedReplayIsVisiblyLabelledAndKeepsCaptureTime()
        {
            var anchor = Map(Vectors["request"]);
            var fixture = RecordedFixture(anchor);
            var transport = new RecordedFixtureTransport(new[] { fixture });
            var outcome = transport.Send(Map(anchor["request"]), "synthetic-vectors");
            Assert.IsTrue(outcome.Ok, outcome.Failure?.Readable);
            Assert.AreEqual("recorded", outcome.Mode);
            Assert.AreEqual("recorded", ProposalValidator.Text(outcome.Provenance, "mode"));
            Assert.AreEqual("2026-10-29T12:00:00Z",
                ProposalValidator.Text(outcome.Provenance, "ts_start"));
            StringAssert.Contains("RECORDED", ModelProvenance.TraceLine(outcome.Provenance));
        }

        [Test]
        public void ATamperedRecordedFixtureFailsClosed()
        {
            var anchor = Map(Vectors["request"]);
            var fixture = RecordedFixture(anchor);
            var response = Map(fixture["response"]).ToDictionary(pair => pair.Key, pair => pair.Value,
                StringComparer.Ordinal);
            response["request_id"] = "request-other";
            fixture["response"] = response;
            var transport = new RecordedFixtureTransport(new[] { fixture });
            var outcome = transport.Send(Map(anchor["request"]), "synthetic-vectors");
            Assert.IsFalse(outcome.Ok);
            Assert.AreEqual("fixture_integrity", outcome.Failure.ReasonCode);
        }

        static Dictionary<string, object> RecordedFixture(IReadOnlyDictionary<string, object> anchor)
        {
            var request = Map(anchor["request"]);
            var response = Map(anchor["response"]);
            var fixture = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["fixture_format_version"] = "1.0.0",
                ["scenario_id"] = "synthetic-vectors",
                ["policy_version"] = ProposalValidator.Text(Map(request["policy_envelope"]),
                    "policy_version"),
                ["contract_versions"] = new Dictionary<string, object>
                {
                    ["framework_api_version"] = "1.0.0",
                    ["snapshot_envelope_version"] = "1.0.0",
                    ["tool_schema_version"] = "1.0.0",
                    ["save_format_version"] = "1.0.0",
                    ["stage_data_format_version"] = "1.0.0",
                    ["context_payload_version"] = "1.0.0",
                    ["fixture_format_version"] = "1.0.0",
                    ["provenance_contract_version"] = "1.0.0",
                    ["episode_model_version"] = "1.0.0"
                },
                ["deterministic_recipe"] = new Dictionary<string, object>
                {
                    ["episode_id"] = "fake-discrete",
                    ["seed"] = 0L,
                    ["initial_trial_ref"] = ((IEnumerable<object>)request["refs"]).First(),
                    ["stage_id"] = "stage-a",
                    ["input_event_ids"] = new List<object>()
                },
                ["request"] = request,
                ["response"] = response
            };
            var provenance = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["model"] = EvaluationContract.ModelId,
                ["endpoint_host"] = EvaluationContract.EndpointHost,
                ["request_id"] = request["request_id"],
                ["ts_start"] = "2026-10-29T12:00:00Z",
                ["ts_end"] = "2026-10-29T12:00:01Z",
                ["input_tokens"] = null,
                ["output_tokens"] = null,
                ["prompt_hash"] = EvaluationRequestBuilder.RequestHash(request),
                ["response_hash"] = EvaluationRequestBuilder.ResponseHash(response),
                ["mode"] = "recorded"
            };
            fixture["provenance"] = provenance;
            fixture["integrity"] = new Dictionary<string, object>
            {
                ["algorithm"] = "sha256",
                ["digest"] = CanonicalJson.Sha256Hex(fixture)
            };
            return fixture;
        }

        // -------------------------------------------------------- context build

        static ValidationContext BuildContext(IReadOnlyDictionary<string, object> declarative)
        {
            var context = new ValidationContext
            {
                EpisodeId = Text(declarative["episode_id"]),
                CurrentTrialId = Text(declarative["current_trial_id"])
            };
            foreach (var trialId in List(declarative["comparison_trial_ids"]).Select(Text))
                context.ComparisonTrialIds.Add(trialId);
            var trialMap = declarative.GetValueOrDefault("trials") is
                IReadOnlyDictionary<string, object> mapped
                ? mapped
                : new Dictionary<string, object>();
            foreach (var raw in trialMap.Values)
            {
                var trial = Map(raw);
                var state = new TrialState
                {
                    TrialId = Text(trial["trial_id"]),
                    TrialRevision = (int)(long)trial["trial_revision"],
                    Phase = Text(trial["phase"]),
                    Acceptance = Text(trial["acceptance"]),
                    Activity = Text(trial["activity"]),
                    SetupFamilyId = Text(trial["setup_family_id"])
                };
                if (trial.GetValueOrDefault("target_parameters") is
                    IReadOnlyDictionary<string, object> parameters)
                    foreach (var pair in parameters)
                        state.TargetParameters[pair.Key] = pair.Value;
                foreach (var measurementId in List(trial["measurement_ids"]).Select(Text))
                    state.Measurements.Add(new MeasurementState { MeasurementId = measurementId });
                context.Trials[state.TrialId] = state;
            }
            if (declarative.GetValueOrDefault("preferences") is
                IReadOnlyDictionary<string, object> preferences)
                context.Preferences = preferences.ToDictionary(pair => pair.Key, pair => pair.Value,
                    StringComparer.Ordinal);

            var tokens = new PolicyTokenSet
            {
                EpisodeId = Text(declarative["episode_id"])
            };
            var tokenSet = Map(declarative["policy_tokens"]);
            tokens.StageId = ProposalValidator.Text(tokenSet, "stage_id");
            tokens.PolicyVersion = ProposalValidator.Text(tokenSet, "policy_version");
            foreach (var raw in List(tokenSet["tokens"]))
                tokens.Tokens.Add(Map(raw).ToDictionary(pair => pair.Key, pair => pair.Value,
                    StringComparer.Ordinal));
            context.PolicyTokens = tokens;

            foreach (var raw in List(declarative["declarations"]))
            {
                var declaration = Map(raw);
                var inputs = new Dictionary<string, IReadOnlyDictionary<string, object>>(
                    StringComparer.Ordinal);
                if (declaration.GetValueOrDefault("controllable_inputs") is
                    IReadOnlyDictionary<string, object> controls)
                    foreach (var pair in controls)
                        inputs[pair.Key] = Map(pair.Value).ToDictionary(item => item.Key,
                            item => item.Value, StringComparer.Ordinal);
                var defaults = new Dictionary<string, object>(StringComparer.Ordinal);
                if (declaration.GetValueOrDefault("default_parameters") is
                    IReadOnlyDictionary<string, object> parameters)
                    foreach (var pair in parameters)
                        defaults[pair.Key] = pair.Value;
                var tool = new ToolDeclaration(
                    ProposalValidator.Text(declaration, "action_id"),
                    ProposalValidator.Text(declaration, "tool"),
                    ProposalValidator.Text(declaration, "operation"),
                    ProposalValidator.Text(declaration, "setup_family_id").Length == 0
                        ? null
                        : ProposalValidator.Text(declaration, "setup_family_id"),
                    inputs,
                    defaults,
                    List(declaration["downstream_effect_ids"]).Select(Text),
                    List(declaration["invariant_ids"]).Select(Text),
                    declaration.GetValueOrDefault("confirmation_required") is true);
                context.Declarations[tool.Tool + "/" + tool.Operation] = tool;
            }

            var gates = new LocalGates();
            var gateRecord = Map(declarative["gates"]);
            gates.UserSpeaking = gateRecord.GetValueOrDefault("user_speaking") is true;
            gates.UserSwinging = gateRecord.GetValueOrDefault("user_swinging") is true;
            gates.CooldownActive = gateRecord.GetValueOrDefault("cooldown_active") is true;
            gates.BackoffActive = gateRecord.GetValueOrDefault("backoff_active") is true;
            gates.TtsAvailable = gateRecord.GetValueOrDefault("tts_available") is not false;
            gates.SceneValid = gateRecord.GetValueOrDefault("scene_valid") is not false;
            gates.NumericalStateOk = gateRecord.GetValueOrDefault("numerical_state_ok") is not false;
            gates.StorageAvailable = gateRecord.GetValueOrDefault("storage_available") is not false;
            gates.HardSafetyHold = gateRecord.GetValueOrDefault("hard_safety_hold") is true;
            gates.GenerationCommitted = gateRecord.GetValueOrDefault("generation_committed") is not false;
            foreach (var id in List(gateRecord["surface_ids"]).Select(Text))
                gates.SurfaceIds.Add(id);
            foreach (var id in List(gateRecord["highlight_ids"]).Select(Text))
                gates.HighlightIds.Add(id);
            foreach (var id in List(gateRecord["replay_event_ids"]).Select(Text))
                gates.ReplayEventIds.Add(id);
            foreach (var id in List(gateRecord["replay_speed_ids"]).Select(Text))
                gates.ReplaySpeedIds.Add(id);
            foreach (var id in List(gateRecord["chart_slot_ids"]).Select(Text))
                gates.ChartSlotIds.Add(id);
            foreach (var id in List(gateRecord["chart_ids"]).Select(Text))
                gates.ChartIds.Add(id);
            foreach (var id in List(gateRecord["safety_candidate_ids"]).Select(Text))
                gates.SafetyCandidateIds.Add(id);
            foreach (var id in List(gateRecord["playback_completed_solicitation_ids"]).Select(Text))
                gates.PlaybackCompletedSolicitationIds.Add(id);
            context.Gates = gates;

            foreach (var raw in List(declarative["idempotency_records"]))
                context.IdempotencyRecords.Add(Map(raw).ToDictionary(pair => pair.Key,
                    pair => pair.Value, StringComparer.Ordinal));
            return context;
        }

        [Test]
        public void MetaChatCodecWrapsTheCanonicalRequestWithoutDuplicatingPolicy()
        {
            var request = new Dictionary<string, object>
            {
                ["request_contract_version"] = "1.0.0",
                ["request_id"] = "request-wire",
                ["model"] = EvaluationContract.ModelId,
                ["refs"] = new List<object>
                {
                    new Dictionary<string, object> { ["trial_id"] = "trial-a", ["trial_revision"] = 1 }
                },
                ["policy_envelope"] = new Dictionary<string, object>
                {
                    ["policy_version"] = "policy-a",
                    ["system"] = "Synthetic system instruction."
                }
            };

            var wire = Map(CanonicalJsonReader.Parse(
                Encoding.UTF8.GetString(MetaChatCompletionCodec.RequestBytes(request))));

            Assert.AreEqual(EvaluationContract.ModelId, Text(wire["model"]));
            Assert.AreEqual(EvaluationContract.ReasoningEffort, Text(wire["reasoning_effort"]));
            var messages = List(wire["messages"]);
            Assert.AreEqual(2, messages.Count);
            Assert.AreEqual("system", Text(Map(messages[0])["role"]));
            Assert.AreEqual("Synthetic system instruction.", Text(Map(messages[0])["content"]));
            var userContent = Map(CanonicalJsonReader.Parse(Text(Map(messages[1])["content"])));
            Assert.AreEqual("request-wire", Text(userContent["request_id"]));
            Assert.IsFalse(userContent.ContainsKey("policy_envelope"));
            Assert.AreEqual(CanonicalJson.Text(request["refs"]), CanonicalJson.Text(userContent["refs"]));
            var responseFormat = Map(wire["response_format"]);
            Assert.AreEqual("json_schema", Text(responseFormat["type"]));
            Assert.AreEqual("amiss_tutor_response",
                Text(Map(responseFormat["json_schema"])["name"]));
        }

        [Test]
        public void MetaChatCodecExtractsTheStructuredResponseAndProviderUsage()
        {
            var expected = new Dictionary<string, object>
            {
                ["response_contract_version"] = "1.0.0",
                ["request_id"] = "request-wire",
                ["refs"] = new List<object>(),
                ["speech_units"] = new List<object>(),
                ["tool_proposals"] = new List<object>()
            };
            var completion = new Dictionary<string, object>
            {
                ["choices"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["message"] = new Dictionary<string, object>
                        {
                            ["content"] = CanonicalJson.Text(expected)
                        }
                    }
                },
                ["usage"] = new Dictionary<string, object>
                {
                    ["prompt_tokens"] = 123,
                    ["completion_tokens"] = 45
                }
            };

            var actual = MetaChatCompletionCodec.Response(completion);
            var usage = MetaChatCompletionCodec.UsageTokens(completion);

            Assert.AreEqual(CanonicalJson.Text(expected), CanonicalJson.Text(actual));
            Assert.AreEqual(123, usage.input);
            Assert.AreEqual(45, usage.output);
        }

        [Test]
        public void MetaChatCodecRejectsMissingOrNonJsonMessageContent()
        {
            var missing = new Dictionary<string, object> { ["choices"] = new List<object>() };
            Assert.Throws<SenderRefusedException>(() => MetaChatCompletionCodec.Response(missing));

            var invalid = new Dictionary<string, object>
            {
                ["choices"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["message"] = new Dictionary<string, object> { ["content"] = "not JSON" }
                    }
                }
            };
            var error = Assert.Throws<SenderRefusedException>(
                () => MetaChatCompletionCodec.Response(invalid));
            StringAssert.StartsWith("malformed_response", error.Message);
        }

        sealed class ScriptedSender : IModelSender
        {
            readonly ModelReply reply;

            public ScriptedSender(ModelReply reply)
            {
                this.reply = reply;
            }

            public int Calls { get; private set; }

            public Task<ModelReply> Send(string endpointAddress, byte[] body, string authorization,
                double connectDeadlineSeconds, double deadlineSeconds,
                CancellationToken cancellation = default)
            {
                Calls++;
                return Task.FromResult(reply);
            }
        }
    }
}
