using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amiss.Shell;
using Amiss.Shell.Episodes;
using Amiss.Shell.Evaluation;
using Amiss.Shell.Ports;
using Amiss.Shell.Simulation;
using Amiss.Shell.Tutor;
using NUnit.Framework;
using UnityEngine;

namespace Amiss.Tests
{
    /// <summary>
    /// The device-side model path: one candidate per accepted proposal, no
    /// mutation on any rejection, a separate user acceptance, one provenance
    /// record per admitted reply, and a readable fail-closed state before the
    /// policy and key exist.
    /// </summary>
    public class TutorTransportTests
    {
        const string SentinelKey = "amiss-test-sentinel-key-0000000000000000";
        const string EpisodeId = "fake-discrete";
        const string LiveTrialId = "trial-live";

        static ValidationContext Context(LocalGates gates = null)
        {
            var context = new ValidationContext
            {
                EpisodeId = EpisodeId,
                CurrentTrialId = LiveTrialId
            };
            context.Trials[LiveTrialId] = new TrialState
            {
                TrialId = LiveTrialId,
                TrialRevision = 1,
                Phase = "setup",
                Acceptance = "accepted",
                Activity = "running",
                SetupFamilyId = "discrete-count",
                TargetParameters = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["count"] = 3L
                }
            };
            context.ComparisonTrialIds.Add(LiveTrialId);
            context.Gates = gates ?? new LocalGates();
            context.PolicyTokens = new PolicyTokenSet
            {
                EpisodeId = EpisodeId,
                StageId = "stage-a",
                PolicyVersion = "synthetic-policy-v1",
                Tokens =
                {
                    new Dictionary<string, object>
                    {
                        ["token_id"] = "token-action",
                        ["kind"] = "allowed_action",
                        ["value_id"] = "action-propose"
                    },
                    new Dictionary<string, object>
                    {
                        ["token_id"] = "token-family",
                        ["kind"] = "setup_family",
                        ["value_id"] = "discrete-count"
                    }
                }
            };
            context.Declarations["trial.propose/create"] = new ToolDeclaration(
                "action-propose", "trial.propose", "create", "discrete-count",
                new Dictionary<string, IReadOnlyDictionary<string, object>>(StringComparer.Ordinal)
                {
                    ["count"] = new Dictionary<string, object>
                    {
                        ["minimum"] = 0L,
                        ["maximum"] = 10L
                    }
                },
                new Dictionary<string, object>(StringComparer.Ordinal) { ["count"] = 3L },
                new[] { "discrete-state" },
                new[] { "finite-state", "positive-count" });
            return context;
        }

        static Dictionary<string, object> ProposeProposal(string callId = "call-propose", long count = 4)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["tool_schema_version"] = "1.0.0",
                ["call_id"] = callId,
                ["origin"] = "model",
                ["tool"] = "trial.propose",
                ["target"] = new Dictionary<string, object>
                {
                    ["kind"] = "new_trial",
                    ["episode_id"] = EpisodeId,
                    ["source_trial_ref"] = null
                },
                ["basis_trial_ref"] = null,
                ["refs"] = new List<object>(),
                ["parameters"] = new Dictionary<string, object>
                {
                    ["operation"] = "create",
                    ["setup_family_token_id"] = "token-family",
                    ["target_parameters"] = new Dictionary<string, object> { ["count"] = count },
                    ["input_delta"] = new Dictionary<string, object>
                    {
                        ["parameter_id"] = "count",
                        ["to_value"] = count,
                        ["downstream_effect_ids"] = new List<object> { "discrete-state" }
                    },
                    ["expected_invariant_ids"] = new List<object>
                    {
                        "finite-state", "positive-count"
                    }
                }
            };
        }

        static Dictionary<string, object> LiveRef() => new(StringComparer.Ordinal)
        {
            ["trial_id"] = LiveTrialId,
            ["trial_revision"] = 1L
        };

        static Dictionary<string, object> ReadProposal(string callId = "call-read", int revision = 1)
        {
            var reference = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["trial_id"] = LiveTrialId,
                ["trial_revision"] = (long)revision
            };
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["tool_schema_version"] = "1.0.0",
                ["call_id"] = callId,
                ["origin"] = "model",
                ["tool"] = "state.read",
                ["target"] = new Dictionary<string, object>
                {
                    ["kind"] = "existing",
                    ["trial_ref"] = reference
                },
                ["basis_trial_ref"] = reference,
                ["refs"] = new List<object> { reference },
                ["parameters"] = new Dictionary<string, object>
                {
                    ["operation"] = "snapshot",
                    ["target_trial_ref"] = reference
                }
            };
        }

        // -------------------------------------------------------- candidate path

        [Test]
        public void AcceptedProposalCreatesOnlyATickZeroCandidate()
        {
            var context = Context();
            var store = new TrialCandidateStore();
            var validator = new ProposalValidator(context,
                new DeviceCommandExecutor(store, () => 1, context));

            var results = validator.ValidateReply(new object[] { ProposeProposal() });

            Assert.AreEqual("executed", ProposalValidator.Text(results[0], "outcome"));
            Assert.AreEqual(1, store.Candidates.Count);
            var candidate = store.Candidates.First();
            Assert.AreEqual("setup", candidate.Phase);
            Assert.AreEqual("proposed", candidate.Acceptance);
            Assert.AreEqual(0, candidate.Tick);
            Assert.AreEqual(LiveTrialId, context.CurrentTrialId,
                "the live Trial is not preempted by a candidate");
        }

        [Test]
        public void CandidateNeedsASeparateExplicitUserAcceptance()
        {
            var context = Context();
            var store = new TrialCandidateStore();
            var validator = new ProposalValidator(context,
                new DeviceCommandExecutor(store, () => 1, context));
            validator.ValidateReply(new object[] { ProposeProposal() });
            var candidate = store.Candidates.First();

            Assert.AreEqual("proposed", candidate.Acceptance);
            Assert.IsTrue(store.TryAccept(candidate.TrialId, out var reason), reason);
            Assert.AreEqual("accepted", store.Get(candidate.TrialId).Acceptance);
            Assert.AreNotEqual(candidate.TrialRevision, store.Get(candidate.TrialId).TrialRevision,
                "acceptance is a committed Trial revision");
            Assert.IsFalse(store.TryAccept(candidate.TrialId, out var again));
            Assert.AreEqual("already_decided", again);
        }

        [Test]
        public void RejectedStaleReplyMutatesNothing()
        {
            var context = Context();
            var store = new TrialCandidateStore();
            var validator = new ProposalValidator(context,
                new DeviceCommandExecutor(store, () => 1, context));

            var results = validator.ValidateReply(new object[] { ReadProposal(revision: 42) });

            Assert.AreEqual("rejected", ProposalValidator.Text(results[0], "outcome"));
            Assert.AreEqual("stale_trial_ref", ProposalValidator.Text(results[0], "reason_code"));
            Assert.AreEqual(0, store.Candidates.Count);
            Assert.AreEqual(1, context.CurrentRefs()[0]["trial_revision"]);
        }

        [Test]
        public void DuplicateReplayDoesNotCreateASecondCandidate()
        {
            var context = Context();
            var store = new TrialCandidateStore();
            var validator = new ProposalValidator(context,
                new DeviceCommandExecutor(store, () => 1, context));
            validator.ValidateReply(new object[] { ProposeProposal() });
            var first = context.IdempotencyRecords.Count;

            var replay = validator.ValidateReply(new object[] { ProposeProposal() });

            Assert.AreEqual("executed", ProposalValidator.Text(replay[0], "outcome"));
            Assert.AreEqual(true, replay[0]["replay"]);
            Assert.AreEqual(1, store.Candidates.Count);
            Assert.AreEqual(first, context.IdempotencyRecords.Count);
        }

        [Test]
        public void ProposalsFillTheDeclaredComparisonSetAndThenStop()
        {
            var context = Context();
            var store = new TrialCandidateStore();
            var executor = new DeviceCommandExecutor(store, () => 1, context);
            var validator = new ProposalValidator(context, executor);
            validator.ValidateReply(new object[] { ProposeProposal("call-a") });
            validator.ValidateReply(new object[] { ProposeProposal("call-b", 5) });

            Assert.AreEqual(2, store.Candidates.Count);
            foreach (var candidate in store.Candidates)
                Assert.AreEqual("proposed", candidate.Acceptance,
                    "no candidate is accepted without the user command");

            // One live Trial plus two candidates fills three of the four declared
            // comparison slots, so the fourth candidate is still admitted.
            var fourth = validator.ValidateReply(new object[] { ProposeProposal("call-c", 6) });
            Assert.AreEqual("executed", ProposalValidator.Text(fourth[0], "outcome"));
            Assert.AreEqual(3, store.Candidates.Count);

            // The comparison set is full: the next proposal is refused at step 7
            // with no partial state, so no candidate is ever created silently.
            var refused = validator.ValidateReply(new object[] { ProposeProposal("call-d", 7) });
            Assert.AreEqual("rejected", ProposalValidator.Text(refused[0], "outcome"));
            Assert.AreEqual("execution_postcondition_failed",
                ProposalValidator.Text(refused[0], "reason_code"));
            Assert.AreEqual(3, store.Candidates.Count);
        }

        // -------------------------------------------------------- fail closed

        [Test]
        public async Task WithoutPolicyAndKeyThePathFailsClosedWithAReadableReason()
        {
            var transport = new DeviceTutorTransport(new ProposalValidator(Context()));
            Assert.IsFalse(transport.Available);
            StringAssert.Contains("no approved instructor policy", transport.TraceLine);
            StringAssert.Contains("default path", transport.FailClosedDetail);

            var admission = await transport.Request(Payload(), "request-a", "turn-a", "answer");
            Assert.IsFalse(admission.Admitted);
            Assert.AreEqual("no_policy", admission.Failure.ReasonCode);
            StringAssert.Contains("default path", admission.Failure.Readable);
        }

        [Test]
        public async Task WithoutAKeyTheLivePathReportsTheCredentialButNotItsValue()
        {
            var policy = new PolicyEnvelope("synthetic-policy-v1", "Synthetic fixture policy.");
            var transport = new DeviceTutorTransport(new ProposalValidator(Context()),
                key: new ModelKeyHandle(), policy: policy);
            Assert.IsTrue(transport.Available);
            StringAssert.Contains("no Meta model key", transport.FailClosedLine);

            var admission = await transport.Request(Payload(), "request-a", "turn-a", "answer");
            Assert.IsFalse(admission.Admitted);
            Assert.AreEqual("no_credential", admission.Failure.ReasonCode);
            StringAssert.DoesNotContain(SentinelKey, admission.Failure.Readable);
        }

        [Test]
        public void AdmittedReplyPublishesOneProvenanceAndAVisibleTraceLine()
        {
            var context = Context();
            var request = new Dictionary<string, object>
            {
                ["request_contract_version"] = "1.0.0",
                ["turn_id"] = "turn-a",
                ["request_id"] = "request-a",
                ["origin"] = "answer",
                ["model"] = EvaluationContract.ModelId,
                ["refs"] = new List<object> { LiveRef() },
                ["context_payload"] = Payload(),
                ["policy_envelope"] = new Dictionary<string, object>
                {
                    ["policy_version"] = "synthetic-policy-v1",
                    ["system"] = "Synthetic fixture policy."
                }
            };
            var response = new Dictionary<string, object>
            {
                ["response_contract_version"] = "1.0.0",
                ["request_id"] = "request-a",
                ["refs"] = new List<object> { LiveRef() },
                ["speech_units"] = new List<object>(),
                ["tool_proposals"] = new List<object> { ReadProposal() }
            };
            var provenance = ModelProvenance.Build(request, response,
                "2026-11-01T12:00:00Z", "2026-11-01T12:00:01Z", null, null, "recorded");

            var transport = new DeviceTutorTransport(new ProposalValidator(context));
            var admission = transport.Admit(request, response, provenance, "recorded");

            Assert.IsTrue(admission.Admitted);
            StringAssert.Contains("muse-spark-1.3", transport.TraceLine);
            StringAssert.Contains("recorded", transport.TraceLine);
            StringAssert.Contains("RECORDED", transport.TraceLine);
            StringAssert.Contains("request-a", transport.TraceLine);
            Assert.That(ModelProvenance.Errors(transport.LastProvenance), Is.Empty);
        }

        [Test]
        public void ARejectedReplyPublishesNoProvenanceAndLeavesTheTraceClosed()
        {
            var context = Context();
            var request = new Dictionary<string, object>
            {
                ["request_id"] = "request-a",
                ["refs"] = new List<object> { LiveRef() },
                ["context_payload"] = Payload()
            };
            var response = new Dictionary<string, object>
            {
                ["response_contract_version"] = "1.0.0",
                ["request_id"] = "request-a",
                ["refs"] = new List<object> { LiveRef() },
                ["speech_units"] = new List<object>(),
                ["tool_proposals"] = new List<object> { ReadProposal(revision: 9) }
            };
            var transport = new DeviceTutorTransport(new ProposalValidator(context));
            var admission = transport.Admit(request, response, null, "live");

            Assert.IsTrue(admission.Admitted);
            Assert.That(admission.Rejections, Is.Not.Empty);
            Assert.IsNull(transport.LastProvenance);
            StringAssert.Contains("closed", transport.TraceLine);
        }

        [Test]
        public async Task SentinelKeyNeverAppearsInTraceProvenanceOrAdmissionOutput()
        {
            var context = Context();
            var transport = new DeviceTutorTransport(new ProposalValidator(context),
                sender: new OfflineSender(),
                key: new ModelKeyHandle(SentinelKey),
                policy: new PolicyEnvelope("synthetic-policy-v1", "Synthetic fixture policy."));
            var admission = await transport.Request(Payload(), "request-a", "turn-a", "answer");
            StringAssert.DoesNotContain(SentinelKey, transport.TraceLine);
            StringAssert.DoesNotContain(SentinelKey, admission.Failure?.Readable ?? "");
            Assert.IsNull(transport.LastProvenance);
            Assert.AreEqual("offline", admission.Failure.Kind);
        }

        [Test]
        public async Task AKeyedRequestWithNoNetworkPathFailsClosedAsOffline()
        {
            var transport = new DeviceTutorTransport(new ProposalValidator(Context()),
                sender: new OfflineSender(),
                key: new ModelKeyHandle(SentinelKey),
                policy: new PolicyEnvelope("synthetic-policy-v1", "Synthetic fixture policy."));
            var admission = await transport.Request(Payload(), "request-a", "turn-a", "answer");
            Assert.IsFalse(admission.Admitted);
            Assert.AreEqual("network_service", admission.Failure.ReasonCode);
            StringAssert.Contains("network", admission.Failure.Readable);
        }

        sealed class OfflineSender : IModelSender
        {
            public Task<ModelReply> Send(string endpointAddress, byte[] body, string authorization,
                double connectDeadlineSeconds, double deadlineSeconds,
                CancellationToken cancellation = default) =>
                Task.FromException<ModelReply>(new SenderUnavailableException("no_network"));
        }

        static Dictionary<string, object> Payload() => new(StringComparer.Ordinal)
        {
            ["context_payload_version"] = "1.0.0",
            ["comparison_package"] = new List<object>(),
            ["refs"] = new List<object> { LiveRef() },
            ["policy_tokens"] = new Dictionary<string, object>
            {
                ["episode_id"] = EpisodeId,
                ["stage_id"] = "stage-a",
                ["policy_version"] = "synthetic-policy-v1",
                ["refs"] = new List<object> { LiveRef() },
                ["tokens"] = new List<object>()
            },
            ["compressed_session_context"] = new Dictionary<string, object>
            {
                ["session_duration_seconds"] = 0.0,
                ["preferences"] = new Dictionary<string, object>
                {
                    ["mute"] = false,
                    ["tips_enabled"] = true,
                    ["guidance_preference_id"] = "default"
                },
                ["minimal_profile"] = null
            },
            ["current_user_utterance"] = new Dictionary<string, object>
            {
                ["utterance_id"] = "utterance-a",
                ["text"] = "Synthetic utterance."
            }
        };

        // ------------------------------------------------------- request shape

        [Test]
        public void TheRequestBuilderRefusesAPayloadThatCarriesPolicy()
        {
            var payload = Payload();
            payload["system"] = "Synthetic fixture policy.";
            Assert.Throws<EvaluationContractException>(() => EvaluationRequestBuilder.Build(payload,
                new PolicyEnvelope("synthetic-policy-v1", "Synthetic fixture policy."),
                "request-a", "turn-a", "answer"));
        }

        [Test]
        public void TheRequestBuilderRefusesAMissingPolicyAndAnUnknownOrigin()
        {
            Assert.Throws<EvaluationContractException>(() => EvaluationRequestBuilder.Build(Payload(),
                null, "request-a", "turn-a", "answer"));
            Assert.Throws<EvaluationContractException>(() => EvaluationRequestBuilder.Build(Payload(),
                new PolicyEnvelope("synthetic-policy-v1", "Synthetic fixture policy."),
                "request-a", "turn-a", "script"));
        }

        [Test]
        public void TheLogViewReplacesThePolicyBodyWithItsHash()
        {
            var policy = new PolicyEnvelope("synthetic-policy-v1", "Synthetic fixture policy.");
            var request = EvaluationRequestBuilder.Build(Payload(), policy, "request-a", "turn-a",
                "answer");
            var record = EvaluationRequestBuilder.RedactForLog(request);
            var text = CanonicalJson.Text(record);
            StringAssert.DoesNotContain("Synthetic fixture policy.", text);
            StringAssert.Contains(policy.Digest(), text);
            Assert.IsFalse(record.ContainsKey("policy_envelope"));
        }

        [Test]
        public void ThePortIsTheOnlyTutorFaceTheEpisodeSees()
        {
            var module = new Shell.Episodes.WhatIsTemperatureEpisodeModule(
                new UnboundSimulationRuntime(), new UnboundGuideRunner(),
                new UnboundTutorTransport(), new UnboundPersistenceAdapter());
            Assert.IsFalse(module.TutorBound);
            StringAssert.Contains("closed", new UnboundTutorTransport().TraceLine);
        }

        // ------------------------------------------------- device key storage

        [Test]
        public void TheDeviceKeyStoreRoundTripsAndClearsWithoutRenderingTheValue()
        {
            const string preferenceKey = "amiss.test.model_key.roundtrip";
            var store = new PlayerPrefsModelKeyStore(preferenceKey);
            store.Clear();
            try
            {
                Assert.IsFalse(store.Present);
                Assert.AreEqual("", store.MaskedTail);
                Assert.IsTrue(store.Save(" " + SentinelKey + " "));
                Assert.IsTrue(store.Present);
                Assert.AreEqual(SentinelKey, store.OpenHandle().Use());
                Assert.AreEqual("••••0000", store.MaskedTail);
                StringAssert.DoesNotContain(SentinelKey, store.MaskedTail);
                StringAssert.DoesNotContain(SentinelKey, store.ToString());
                StringAssert.DoesNotContain(SentinelKey, store.OpenHandle().ToString());

                store.Clear();
                Assert.IsFalse(store.Present);
                Assert.IsFalse(store.OpenHandle().Present);
                Assert.IsFalse(store.Save("   "));
                Assert.IsFalse(store.Present);
            }
            finally
            {
                store.Clear();
            }
        }

        [Test]
        public void KeyImportParsesBothValuesAndFailsClosedWithoutPartialValues()
        {
            var importFile = "meta_model_key" + "= abc-123 \r\n# a comment\n" +
                "wit_client_token" + "=xyz_789\n";
            Assert.IsTrue(KeyImport.TryParse(importFile, out var meta, out var wit));
            Assert.AreEqual("abc-123", meta);
            Assert.AreEqual("xyz_789", wit);

            // One value alone is a valid partial provisioning.
            Assert.IsTrue(KeyImport.TryParse("wit_client_token" + "=tok\n", out meta, out wit));
            Assert.AreEqual("", meta);
            Assert.AreEqual("tok", wit);

            // Every malformed shape refuses the whole import and yields no
            // partial value the caller could store by mistake.
            Assert.IsFalse(KeyImport.TryParse("", out meta, out wit));
            Assert.AreEqual("", meta);
            Assert.AreEqual("", wit);
            Assert.IsFalse(KeyImport.TryParse("bogus=1\nmeta_model_key=abc\n", out meta, out wit));
            Assert.AreEqual("", meta);
            Assert.IsFalse(KeyImport.TryParse("meta_model_key=ab cd\n", out meta, out wit));
            Assert.IsFalse(KeyImport.TryParse(
                "meta_model_key" + "=\n" + "wit_client_token" + "=tok\n", out meta, out wit));
            Assert.AreEqual("", wit);
            Assert.IsFalse(KeyImport.TryParse(
                "meta_model_key=one\nmeta_model_key=two\n", out meta, out wit));
            Assert.IsFalse(KeyImport.TryParse(
                "meta_model_key=" + new string('a', KeyImport.MaxValueChars + 1) + "\n",
                out meta, out wit));
            Assert.IsFalse(KeyImport.TryParse("no separator here\n", out meta, out wit));
        }

        [Test]
        public async Task ATransportWithNoStoreAndNoHandleStaysKeylessSafe()
        {
            var transport = new DeviceTutorTransport(new ProposalValidator(Context()),
                sender: new OfflineSender(),
                policy: new PolicyEnvelope("synthetic-policy-v1", "Synthetic fixture policy."));

            Assert.IsFalse(transport.KeyPresent);
            Assert.AreEqual("", transport.MaskedTail);
            var admission = await transport.Request(Payload(), "request-a", "turn-a", "answer");

            Assert.IsFalse(admission.Admitted);
            Assert.AreEqual("no_credential", admission.Failure.ReasonCode,
                "the sender is never reached without a key");
            StringAssert.DoesNotContain(SentinelKey, admission.Failure.Readable);
        }

        [Test]
        public async Task TheStoredKeySentinelNeverReachesTraceEvidenceOrAdmissionOutput()
        {
            var store = new MemoryKeyStore(SentinelKey);
            var transport = new DeviceTutorTransport(new ProposalValidator(Context()),
                sender: new OfflineSender(),
                policy: new PolicyEnvelope("synthetic-policy-v1", "Synthetic fixture policy."),
                keyStore: store);

            Assert.IsTrue(transport.KeyPresent);
            var admission = await transport.Request(Payload(), "request-a", "turn-a", "answer");
            var rendered = new[]
            {
                store.ToString(),
                store.MaskedTail,
                store.OpenHandle().ToString(),
                transport.TraceLine,
                transport.FailClosedDetail,
                transport.MaskedTail,
                admission.Failure == null ? "" : admission.Failure.Readable,
                admission.Failure == null ? "" : CanonicalJson.Text(admission.Failure.ToRecord())
            };
            foreach (var text in rendered)
                StringAssert.DoesNotContain(SentinelKey, text);
        }

        // ------------------------------------------------- context payload

        [Test]
        public void TheContextPayloadCarriesTheDeclaredGroupsAndNoPolicyOrKey()
        {
            var context = Context();
            var payload = ContextPayloadBuilder.Build(Source(context));

            foreach (var field in new[]
                     {
                         "context_payload_version", "comparison_package", "refs", "policy_tokens",
                         "compressed_session_context", "current_user_utterance"
                     })
                Assert.IsTrue(payload.ContainsKey(field), field);
            var projection = (IReadOnlyDictionary<string, object>)
                ((IEnumerable<object>)payload["comparison_package"]).First();
            foreach (var field in new[]
                     {
                         "identity", "space", "measurements", "progress", "policy_tokens",
                         "diagnostics", "episode_extension"
                     })
                Assert.IsTrue(projection.ContainsKey(field), field);
            var measurement = (IReadOnlyDictionary<string, object>)
                ((IEnumerable<object>)projection["measurements"]).First();
            Assert.AreEqual("valid", ProposalValidator.Text(measurement, "applicability"));
            Assert.IsTrue(measurement.ContainsKey("display_tuple"),
                "a citable measurement carries its complete display tuple");

            var text = CanonicalJson.Text(payload);
            StringAssert.DoesNotContain(SentinelKey, text);
            StringAssert.DoesNotContain("Synthetic fixture policy.", text);
            StringAssert.DoesNotContain("\"system\"", text);
            Assert.LessOrEqual(CanonicalJson.Bytes(payload).Length,
                ContextPayloadBuilder.MaxPayloadBytes);
            Assert.AreEqual("", ProposalValidator.Text(
                (IReadOnlyDictionary<string, object>)payload["current_user_utterance"], "text"),
                "the utterance field stays empty until the voice turn exists");
        }

        [Test]
        public void TheTeacherReadsTheCurrentStatisticsNumbersOfTheTrial()
        {
            var context = Context();
            var payload = ContextPayloadBuilder.Build(Source(context));

            var projection = (IReadOnlyDictionary<string, object>)
                ((IEnumerable<object>)payload["comparison_package"]).First();
            var extension = (IReadOnlyDictionary<string, object>)projection["episode_extension"];
            Assert.AreEqual(EpisodeId, ProposalValidator.Text(extension, "namespace"));
            var facts = (IReadOnlyDictionary<string, object>)extension["facts"];

            var space = (IReadOnlyDictionary<string, object>)facts["space_sizing"];
            Assert.AreEqual(3, (int)space["computed_n"]);
            Assert.AreEqual(true, (bool)space["is_opening_override_n"]);

            var particles = (IReadOnlyDictionary<string, object>)facts["particles"];
            Assert.AreEqual(3, (int)particles["n_total"]);
            Assert.AreEqual(3, (int)particles["n_free"]);
            Assert.AreEqual(0, (int)particles["n_held"]);
            var group = (IReadOnlyDictionary<string, object>)
                ((IEnumerable<object>)particles["groups"]).First();
            Assert.AreEqual(3, (int)group["count_free"]);
            Assert.AreEqual(1.1, (double)group["v_rms_m_per_s"], 1e-12);

            var energy = (IReadOnlyDictionary<string, object>)facts["energy_ledger"];
            Assert.AreEqual(3.0, (double)energy["initial_total_ke_j"], 1e-12);
            Assert.AreEqual(1.0, (double)energy["work_events_cumulative_ke_j"], 1e-12);
            Assert.AreEqual(0.5, (double)energy["sandbag_absorbed_ke_cumulative_j"], 1e-12);

            // The current temperature is readable as the unrounded instant
            // value and as the producer-owned K/C/F display tuple.
            var readout = (IReadOnlyDictionary<string, object>)facts["temperature_readout"];
            Assert.AreEqual("valid", ProposalValidator.Text(readout, "status"));
            Assert.AreEqual(300.0, (double)readout["instant_k"], 1e-12);
            Assert.AreEqual(11, (int)readout["sample_count"]);
            var display = (IReadOnlyDictionary<string, object>)readout["display_tuple"];
            Assert.AreEqual(300, (int)display["k"]);
            Assert.AreEqual(27, (int)display["c"]);
            Assert.AreEqual(80, (int)display["f"]);
            Assert.AreEqual(1.0, (double)display["ratio_to_baseline_k"], 1e-12);

            var kinematics =
                (IReadOnlyDictionary<string, object>)facts["kinematics_and_equilibrium"];
            Assert.AreEqual(1.0, (double)kinematics["mean_speed_mps"], 1e-12);
            Assert.AreEqual(1.1, (double)kinematics["v_rms_mps"], 1e-12);
            Assert.AreEqual(1.0,
                (double)((IEnumerable<object>)kinematics["v_cm_vec"]).First(), 1e-12);

            var props = (IReadOnlyDictionary<string, object>)facts["prop_summary"];
            var counts = (IReadOnlyDictionary<string, object>)props["contact_counts_by_prop"];
            Assert.AreEqual(1, (int)counts[BatTool.PropId]);
            var latest = (IReadOnlyDictionary<string, object>)props["latest_contact"];
            Assert.AreEqual(1.0, (double)latest["delta_energy_j"], 1e-12);
            Assert.AreEqual(BatTool.PropId, ProposalValidator.Text(latest, "prop_id"));

            // The wire request the model receives carries those same numbers.
            var request = EvaluationRequestBuilder.Build(payload,
                new PolicyEnvelope("synthetic-policy-v1", "Synthetic fixture policy."),
                "request-a", "turn-a", "answer");
            StringAssert.Contains("\"instant_k\":300.0", CanonicalJson.Text(request));
            var wire = Encoding.UTF8.GetString(MetaChatCompletionCodec.RequestBytes(request));
            StringAssert.Contains("instant_k", wire);
            StringAssert.Contains("work_events_cumulative_ke_j", wire);
            Assert.LessOrEqual(CanonicalJson.Bytes(payload).Length,
                ContextPayloadBuilder.MaxPayloadBytes);
        }

        [Test]
        public void TheContextPayloadRefusesAnOversizedUtteranceAndNoTrial()
        {
            var context = Context();
            var oversized = Source(context);
            oversized.UtteranceText = new string('a', ContextPayloadBuilder.MaxUtteranceBytes + 1);
            var error = Assert.Throws<EvaluationContractException>(
                () => ContextPayloadBuilder.Build(oversized));
            Assert.AreEqual("utterance_too_large", error.ReasonCode);
            StringAssert.DoesNotContain("aaaa", error.Message);

            var noTrial = Source(context);
            noTrial.TrialId = "";
            var missing = Assert.Throws<EvaluationContractException>(
                () => ContextPayloadBuilder.Build(noTrial));
            Assert.AreEqual("no_trial", missing.ReasonCode);
        }

        // ------------------------------------------------- trigger + admission

        [Test]
        public async Task TheDefinedLocalActionIssuesOneWellFormedGuideDefaultRequest()
        {
            var context = Context();
            var sender = new EchoReplySender("Synthetic spoken answer.");
            var transport = new DeviceTutorTransport(new ProposalValidator(context),
                sender: sender,
                policy: new PolicyEnvelope("synthetic-policy-v1", "Synthetic fixture policy."),
                keyStore: new MemoryKeyStore(SentinelKey));
            var conversation = new TutorConversation(transport, context, () => Source(context));

            var admission = await conversation.Ask();

            Assert.IsTrue(admission.Admitted, admission.Failure?.Readable);
            Assert.AreEqual(1, sender.Calls);
            Assert.AreEqual(EvaluationContract.EndpointAddress, sender.Endpoint);
            Assert.AreEqual(SentinelKey, sender.Authorization);
            var request = sender.LastRequest;
            Assert.AreEqual("guide_default", ProposalValidator.Text(request, "origin"));
            Assert.AreEqual(EvaluationContract.ModelId, ProposalValidator.Text(request, "model"));
            Assert.AreEqual(TutorConversation.LocalOrigin,
                ProposalValidator.Text(request, "origin"));
            Assert.IsTrue(request.ContainsKey("context_payload"));
            Assert.AreEqual("Synthetic spoken answer.", conversation.LastSubtitle);
            Assert.AreEqual("Synthetic spoken answer.", transport.LastSpeech.First());
        }

        [Test]
        public async Task AnAdmittedQuestionSuppliesTheNextYesWithItsExchange()
        {
            var context = Context();
            var sender = new EchoReplySender("Are you asking about the group temperature?");
            var transport = new DeviceTutorTransport(new ProposalValidator(context),
                sender: sender,
                policy: new PolicyEnvelope("synthetic-policy-v1", "Synthetic fixture policy."),
                keyStore: new MemoryKeyStore(SentinelKey));
            var conversation = new TutorConversation(transport, context, () => Source(context));

            await conversation.Ask(TutorConversation.AnswerOrigin, "utterance-question",
                "Can we compare the groups?");
            var second = await conversation.Ask(TutorConversation.AnswerOrigin,
                "utterance-yes", "yes");

            Assert.IsTrue(second.Admitted, second.Failure?.Readable);
            Assert.AreEqual(2, conversation.Memory.RecentExchanges.Count);
            var request = (IReadOnlyDictionary<string, object>)sender.LastRequest["context_payload"];
            var compressed = (IReadOnlyDictionary<string, object>)request["compressed_session_context"];
            var exchanges = ((IEnumerable<object>)compressed["recent_exchanges"]).Cast<
                IReadOnlyDictionary<string, object>>().ToArray();
            Assert.AreEqual(1, exchanges.Length,
                "the second request precedes and therefore cannot contain its own reply");
            Assert.AreEqual("Can we compare the groups?", exchanges[0]["user_text"]);
            Assert.AreEqual("Are you asking about the group temperature?",
                ((IEnumerable<object>)exchanges[0]["agent_speech"]).First());
            Assert.AreEqual("yes", ProposalValidator.Text(
                (IReadOnlyDictionary<string, object>)request["current_user_utterance"], "text"));
        }

        [Test]
        public async Task AFailedReplyDoesNotCreateARecentExchange()
        {
            var context = Context();
            var transport = new DeviceTutorTransport(new ProposalValidator(context),
                sender: new OfflineSender(), keyStore: new MemoryKeyStore(SentinelKey),
                policy: new PolicyEnvelope("synthetic-policy-v1", "Synthetic fixture policy."));
            var conversation = new TutorConversation(transport, context, () => Source(context));

            var admission = await conversation.Ask(TutorConversation.AnswerOrigin,
                "utterance-question", "Can we compare the groups?");

            Assert.IsFalse(admission.Admitted);
            Assert.IsEmpty(conversation.Memory.RecentExchanges);
        }

        [Test]
        public async Task AnInvalidCitationRejectsTheWholeReplyWithoutSpeechOrTools()
        {
            var context = Context();
            var candidates = new TrialCandidateStore();
            var validator = new ProposalValidator(context,
                new DeviceCommandExecutor(candidates, () => 1, context));
            var transport = new DeviceTutorTransport(validator,
                sender: new EchoReplySender("This sentence quotes a missing measurement.",
                    citationMeasurementId: "measurement-that-does-not-exist"),
                policy: new PolicyEnvelope("synthetic-policy-v1", "Synthetic fixture policy."),
                keyStore: new MemoryKeyStore(SentinelKey));
            var conversation = new TutorConversation(transport, context, () => Source(context));

            var admission = await conversation.Ask();

            Assert.IsFalse(admission.Admitted);
            Assert.AreEqual("invalid_citation", admission.Failure.Kind);
            Assert.AreEqual("citation_not_in_context", admission.Failure.ReasonCode);
            Assert.IsEmpty(admission.AcceptedSpeech);
            Assert.IsEmpty(admission.Results, "no unit executes from a partially invalid reply");
            Assert.AreEqual("", conversation.LastSubtitle);
            Assert.AreEqual("", transport.LastSpeech.FirstOrDefault() ?? "");
            Assert.AreEqual(0, candidates.Candidates.Count);
        }

        [Test]
        public async Task AStaleProposalUnitRejectsTheWholeReplyIncludingItsSpeech()
        {
            var context = Context();
            var beforeRefs = CanonicalJson.Text(context.CurrentRefs());
            var transport = new DeviceTutorTransport(new ProposalValidator(context),
                sender: new EchoReplySender("Speech that must not surface.", proposals: new List<object>
                {
                    ReadProposal("call-read", 1),
                    ReadProposal("call-stale", 42)
                }),
                policy: new PolicyEnvelope("synthetic-policy-v1", "Synthetic fixture policy."),
                keyStore: new MemoryKeyStore(SentinelKey));
            var conversation = new TutorConversation(transport, context, () => Source(context));

            var admission = await conversation.Ask();

            Assert.IsFalse(admission.Admitted);
            Assert.AreEqual("invalid_reply", admission.Failure.Kind);
            Assert.IsEmpty(admission.AcceptedSpeech);
            Assert.AreEqual("", conversation.LastSubtitle);
            Assert.AreEqual(beforeRefs, CanonicalJson.Text(context.CurrentRefs()));
        }

        [Test]
        public async Task ATransportFailureLeavesTheTrialStateAndSubtitleUntouched()
        {
            var context = Context();
            var beforeRefs = CanonicalJson.Text(context.CurrentRefs());
            var transport = new DeviceTutorTransport(new ProposalValidator(context),
                sender: new OfflineSender(),
                policy: new PolicyEnvelope("synthetic-policy-v1", "Synthetic fixture policy."),
                keyStore: new MemoryKeyStore(SentinelKey));
            var conversation = new TutorConversation(transport, context, () => Source(context));

            var admission = await conversation.Ask();

            Assert.IsFalse(admission.Admitted);
            Assert.AreEqual("network_service", admission.Failure.ReasonCode);
            Assert.AreEqual(beforeRefs, CanonicalJson.Text(context.CurrentRefs()));
            Assert.AreEqual("", conversation.LastSubtitle);
            StringAssert.Contains("network", conversation.LastStatus);
            StringAssert.DoesNotContain(SentinelKey, conversation.LastStatus);
        }

        // ------------------------------------------------------ live wait shape

        [Test]
        public async Task AReplyThatArrivesAfterSeveralFramesIsAdmitted()
        {
            var frames = 0;
            var elapsed = 0.0;
            var wait = new ModelRequestWait(() => frames >= 3, () => elapsed,
                EvaluationContract.ModelDeadlineSeconds);
            var yields = 0;
            Func<Task> frame = () =>
            {
                yields++;
                frames++;
                elapsed += 1.0 / 72.0;
                return Task.CompletedTask;
            };

            var state = await ModelRequestWait.AwaitAsync(wait, frame);

            Assert.AreEqual(ModelRequestWait.State.Done, state,
                "a reply that arrives after three frames is kept, not timed out");
            Assert.AreEqual(3, yields, "one frame is handed back per pending step");
            Assert.Less(elapsed, EvaluationContract.ModelDeadlineSeconds);
        }

        [Test]
        public async Task ARequestThatNeverCompletesExpiresAtTheDeadlineWithoutSpinning()
        {
            var elapsed = 0.0;
            var polls = 0;
            var wait = new ModelRequestWait(() =>
            {
                polls++;
                return false;
            }, () => elapsed, EvaluationContract.ModelDeadlineSeconds);
            var yields = 0;
            Func<Task> frame = () =>
            {
                yields++;
                elapsed += 0.25;
                return Task.CompletedTask;
            };

            var state = await ModelRequestWait.AwaitAsync(wait, frame);

            Assert.AreEqual(ModelRequestWait.State.Expired, state);
            Assert.AreEqual(120, yields, "each pending step costs exactly one frame, so it never spins");
            Assert.AreEqual(121, polls);
            Assert.AreEqual(EvaluationContract.ModelDeadlineSeconds, elapsed, 1e-9,
                "the deadline is the real 30 seconds, not one frame's worth of frame time");
        }

        [Test]
        public async Task ACancelledWaitStopsBeforeTheDeadline()
        {
            var wait = new ModelRequestWait(() => false, () => 1.0,
                EvaluationContract.ModelDeadlineSeconds, () => true);
            var yields = 0;

            var state = await ModelRequestWait.AwaitAsync(wait, () =>
            {
                yields++;
                return Task.CompletedTask;
            });

            Assert.AreEqual(ModelRequestWait.State.Cancelled, state,
                "cancellation stops the wait without consuming the deadline");
            Assert.AreEqual(0, yields);
        }

        [Test]
        public async Task ACancelledModelCallIsDroppedAsANetworkFailureNotAnException()
        {
            var transport = new DeviceTutorTransport(new ProposalValidator(Context()),
                sender: new CancelledSender(),
                key: new ModelKeyHandle(SentinelKey),
                policy: new PolicyEnvelope("synthetic-policy-v1", "Synthetic fixture policy."));

            var admission = await transport.Request(Payload(), "request-a", "turn-a", "answer");

            Assert.IsFalse(admission.Admitted);
            Assert.AreEqual("timeout", admission.Failure.Kind);
            Assert.AreEqual("network_service", admission.Failure.ReasonCode);
            Assert.IsFalse(transport.InFlight, "a dropped request releases the single-flight slot");
        }

        [Test]
        public async Task ASecondAskWhileOneIsInFlightIsRefusedAndSendsNothing()
        {
            var context = Context();
            var sender = new GatedSender();
            var transport = new DeviceTutorTransport(new ProposalValidator(context),
                sender: sender,
                policy: new PolicyEnvelope("synthetic-policy-v1", "Synthetic fixture policy."),
                keyStore: new MemoryKeyStore(SentinelKey));
            var conversation = new TutorConversation(transport, context, () => Source(context));

            var first = conversation.Ask();
            Assert.IsFalse(first.IsCompleted, "the live path is awaited, not run to completion inline");
            Assert.IsTrue(transport.InFlight);

            var second = await conversation.Ask();

            Assert.IsFalse(second.Admitted);
            Assert.AreEqual("request_in_flight", second.Failure.ReasonCode);
            Assert.AreEqual(1, sender.Calls, "no second request leaves the device");
            Assert.IsTrue(transport.InFlight, "the refused ask did not take the in-flight slot");
            Assert.IsFalse(first.IsCompleted, "the first request is still the one in flight");

            sender.Release();
            var completed = await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.AreSame(first, completed, "the in-flight request completes once its reply arrives");
            var admission = await first;
            Assert.IsTrue(admission.Admitted, admission.Failure?.Readable);
            Assert.IsFalse(transport.InFlight);
        }

        static ContextPayloadSource Source(ValidationContext context)
        {
            var window = new MeasurementWindow("window-00000010", 0, 10, 0.5f, 11);
            var measurement = new TemperatureMeasurement("temperature-00000010", window,
                TemperatureMeasurement.ValidStatus, 3, 3, 0, 3.0, 3.0, 0.0, 0.0,
                new[] { 1.0, 1.0, 1.0 }, 1.0, 1.1, 300.0, 300.0, true,
                TemperatureDisplayTuple.FromKelvin(300.0, 300.0), "3N_free", 2,
                "1.0.0", "1.0.0");
            var ledger = new TemperatureEnergyLedger(3.0, 3.0, 3.0, 0.0, 0.0, 0.5,
                new[]
                {
                    new EnergyContactEntry("contact-0001", BatTool.PropId, "ball-0001",
                        0, 4, 1.0, "separated")
                });
            return new ContextPayloadSource
            {
                EpisodeId = EpisodeId,
                EpisodeModuleVersion = "1.0.0",
                EpisodeModelVersion = "1.0.0",
                AppBuildVersion = "test-build",
                SessionId = "session-test",
                DiagnosticWallTimestamp = "2026-11-03T00:00:00Z",
                TrialId = LiveTrialId,
                TrialRevision = 1,
                Tick = 10,
                ContactCommits = 0,
                CollisionRepresentationId = "collision-test",
                RegionSizeM = new Vector3(2f, 2.5f, 2f),
                EffectiveVolumeM3 = 10.0,
                Measurement = measurement,
                Ledger = ledger,
                Sizing = TemperatureSpaceSizing.Compute(10.0,
                    isOpeningOverrideN: true, openingBallCount: 3),
                BallCount = 3,
                PolicyTokens = context.PolicyTokens
            };
        }

        sealed class MemoryKeyStore : IModelKeyStore
        {
            string key;

            public MemoryKeyStore(string key = null)
            {
                this.key = key;
            }

            public bool Present => !string.IsNullOrEmpty(key);
            public string MaskedTail => PlayerPrefsModelKeyStore.Mask(key);

            public bool Save(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return false;
                key = value.Trim();
                return true;
            }

            public void Clear() => key = null;

            public ModelKeyHandle OpenHandle() => new(key);

            public override string ToString() =>
                "MemoryKeyStore(" + (Present ? "present" : "absent") + ")";
        }

        /// <summary>
        /// Echoes the exact request id and refs, so the reply is structurally
        /// admissible; only the speech citation content varies per test.
        /// </summary>
        sealed class EchoReplySender : IModelSender
        {
            readonly string speech;
            readonly string citationMeasurementId;
            readonly IReadOnlyList<object> proposals;

            public EchoReplySender(string speech, string citationMeasurementId = null,
                IReadOnlyList<object> proposals = null)
            {
                this.speech = speech;
                this.citationMeasurementId = citationMeasurementId;
                this.proposals = proposals;
            }

            public int Calls { get; private set; }
            public string Endpoint { get; private set; } = "";
            public string Authorization { get; private set; } = "";
            public IReadOnlyDictionary<string, object> LastRequest { get; private set; }

            public Task<ModelReply> Send(string endpointAddress, byte[] body, string authorization,
                double connectDeadlineSeconds, double deadlineSeconds,
                CancellationToken cancellation = default) =>
                Task.FromResult(Build(endpointAddress, body, authorization));

            /// <summary>Builds the reply synchronously, for senders that gate delivery.</summary>
            public ModelReply Build(string endpointAddress, byte[] body, string authorization)
            {
                Calls++;
                Endpoint = endpointAddress;
                Authorization = authorization;
                LastRequest = (IReadOnlyDictionary<string, object>)CanonicalJsonReader.Parse(
                    Encoding.UTF8.GetString(body));
                var citations = new List<object>();
                if (!string.IsNullOrEmpty(citationMeasurementId))
                    citations.Add(new Dictionary<string, object>
                    {
                        ["measurement_id"] = citationMeasurementId,
                        ["trial_ref"] = LiveRef()
                    });
                var response = new Dictionary<string, object>
                {
                    ["response_contract_version"] = "1.0.0",
                    ["request_id"] = ProposalValidator.Text(LastRequest, "request_id"),
                    ["refs"] = LastRequest["refs"],
                    ["speech_units"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["text"] = speech,
                            ["citations"] = citations
                        }
                    },
                    ["tool_proposals"] = proposals != null
                        ? new List<object>(proposals)
                        : new List<object>()
                };
                return new ModelReply(response, 0.2, "2026-11-03T00:00:00Z",
                    "2026-11-03T00:00:01Z", 10, 5);
            }
        }

        /// <summary>
        /// Holds its reply until the test releases it, so one ask can be in
        /// flight while a second ask is made. No network, no timer.
        /// </summary>
        sealed class GatedSender : IModelSender
        {
            readonly TaskCompletionSource<ModelReply> gate =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            readonly EchoReplySender reply = new("Synthetic spoken answer.");

            public int Calls { get; private set; }

            public Task<ModelReply> Send(string endpointAddress, byte[] body, string authorization,
                double connectDeadlineSeconds, double deadlineSeconds,
                CancellationToken cancellation = default)
            {
                Calls++;
                pending = reply.Build(endpointAddress, body, authorization);
                return gate.Task;
            }

            ModelReply pending;

            public void Release() => gate.TrySetResult(pending);
        }

        /// <summary>
        /// Reports cancellation the way the platform sender does, so the
        /// transport must drop it as a typed network failure rather than throw.
        /// </summary>
        sealed class CancelledSender : IModelSender
        {
            public Task<ModelReply> Send(string endpointAddress, byte[] body, string authorization,
                double connectDeadlineSeconds, double deadlineSeconds,
                CancellationToken cancellation = default) =>
                Task.FromException<ModelReply>(new OperationCanceledException());
        }
    }
}
