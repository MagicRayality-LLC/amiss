using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Amiss.Shell.Evaluation;
using NUnit.Framework;

namespace Amiss.Tests
{
    /// <summary>
    /// The device mirror of the public policy pack loader. It reads the same
    /// three checked-in files the build stages, and it must reach the same
    /// verdict as the headless oracle for the same inputs.
    /// </summary>
    public class PolicyPackTests
    {
        static string RepositoryRoot =>
            Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..", ".."));

        static string PolicyFile(string name) => Path.Combine(RepositoryRoot, "policy", name);

        static string Read(string name) => File.ReadAllText(PolicyFile(name));

        static PolicyPack LoadShipped() =>
            PolicyPack.Load(Read("pack.json"), Read("stage-data.json"),
                Read("instructor-policy.json"));

        [Test]
        public void TheShippedPackVerifies()
        {
            var pack = LoadShipped();
            Assert.IsTrue(pack.Loaded, pack.ReadableFailure);
            Assert.AreEqual("amiss-hackathon-policy-v1", pack.PolicyVersion);
            Assert.AreEqual("amiss-hackathon-vertical-slice", pack.PackId);
            Assert.AreEqual("what_is_temperature", pack.EpisodeId);
            Assert.AreEqual("1.0.0", pack.PackFormatVersion);
            Assert.AreEqual("1.0.0", pack.StageDataFormatVersion);
            Assert.That(pack.Errors, Is.Empty);
        }

        [Test]
        public void ThePackNamedVersionMatchesTheFrameworkAxis()
        {
            var pack = LoadShipped();
            Assert.AreEqual(Amiss.Contracts.FrameworkApiVersions.StageDataFormat,
                pack.StageDataFormatVersion);
        }

        [Test]
        public void TheDeviceAndTheVectorAgree()
        {
            var document = CanonicalJsonReader.Parse(
                File.ReadAllText(Path.Combine(RepositoryRoot, "schema", "vectors",
                    "policy-pack-vector.json"))) as IReadOnlyDictionary<string, object>;
            Assert.IsNotNull(document);

            var pack = LoadShipped();
            Assert.AreEqual(ProposalValidator.Text(document, "policy_version"), pack.PolicyVersion);
            Assert.AreEqual(ProposalValidator.Text(document, "pack_id"), pack.PackId);
            Assert.AreEqual(ProposalValidator.Text(document, "pack_digest"), pack.PackDigest);
            Assert.AreEqual(ProposalValidator.Text(document, "stage_data_digest"),
                pack.StageDataDigest);
            Assert.AreEqual(document["system_bytes"], (long)pack.SystemBytes);
        }

        [Test]
        public void TheAssembledEnvelopeCarriesOnlyVersionAndSystem()
        {
            var envelope = LoadShipped().Envelope();
            var record = envelope.ToRecord();
            Assert.AreEqual(new[] { "policy_version", "system" },
                record.Keys.OrderBy(key => key).ToArray());
            Assert.AreEqual("amiss-hackathon-policy-v1", envelope.PolicyVersion);
            Assert.IsNotEmpty(envelope.System);
            Assert.LessOrEqual(LoadShipped().SystemBytes, PolicyPack.MaxSystemBytes);
        }

        [Test]
        public void TheSystemCarriesTheCompleteToolCatalogue()
        {
            // Founder requirement (2026-11-04): the teacher knows every tool
            // and control the person can use, and how to use it.
            var system = LoadShipped().System;
            StringAssert.Contains("Tools and controls:", system);
            foreach (var toolId in new[]
                     {
                         "bat", "fist", "cloth_bag", "palm_field", "knife_switch",
                         "wall_tv", "episode_hub"
                     })
                StringAssert.Contains(toolId + " (", system);
            StringAssert.Contains("Swing the barrel through the balls", system);
            StringAssert.Contains("Open both palms", system);
        }

        [Test]
        public void TheStageGraphIsTheP0RouteWithNoP1Stage()
        {
            var pack = LoadShipped();
            var graph = PolicyPack.Map(pack.StageData, "stage_graph");
            var stageIds = PolicyPack.List(graph, "nodes")
                .Cast<IReadOnlyDictionary<string, object>>()
                .Select(node => ProposalValidator.Text(node, "stage_id"))
                .ToArray();
            CollectionAssert.AreEqual(new[]
            {
                "stage-0-entry", "stage-1-track-one", "stage-2-heat", "stage-3-cool",
                "stage-4-group", "stage-5-drift", "stage-7-transfer"
            }, stageIds);
            CollectionAssert.DoesNotContain(stageIds, "stage-6-mass");
        }

        [Test]
        public void TheActionCatalogueIsTheClosedSetTheEnvelopeDeclares()
        {
            var pack = LoadShipped();
            var declared = PolicyPack.List(PolicyPack.Map(pack.StageData, "allowed_action_set"),
                    "action_ids").Cast<string>().ToArray();
            Assert.AreEqual(18, declared.Length);
            Assert.AreEqual(declared.Length, declared.Distinct().Count());
            foreach (var action in declared)
                Assert.IsTrue(action.StartsWith("action-"), action);
        }

        // ------------------------------------------------------------- refusal

        static PolicyPack LoadWith(string pack = null, string stageData = null,
            string instructor = null) =>
            PolicyPack.Load(pack ?? Read("pack.json"), stageData ?? Read("stage-data.json"),
                instructor ?? Read("instructor-policy.json"));

        [Test]
        public void AnEditedStageContentIsRefused()
        {
            var edited = Read("stage-data.json").Replace("default-stage-0-entry",
                "other-default");
            var pack = LoadWith(stageData: edited);
            Assert.IsFalse(pack.Loaded);
            Assert.IsTrue(pack.Errors.Any(error => error.Contains("file digest")),
                pack.ReadableFailure);
        }

        [Test]
        public void AnEditedProseFileIsRefused()
        {
            var edited = Read("instructor-policy.json").Replace("One experiment per session.",
                "Two experiments per session.");
            var pack = LoadWith(instructor: edited);
            Assert.IsFalse(pack.Loaded);
            Assert.IsTrue(pack.Errors.Any(error => error.Contains("file digest")),
                pack.ReadableFailure);
        }

        [Test]
        public void AnUnverifiedIntegrityStatusIsRefused()
        {
            var edited = Read("stage-data.json").Replace("\"status\": \"verified\"",
                "\"status\": \"corrupt\"");
            var pack = LoadWith(stageData: edited);
            Assert.IsFalse(pack.Loaded);
            Assert.IsTrue(pack.Errors.Any(error => error.Contains("status is not verified")),
                pack.ReadableFailure);
        }

        [Test]
        public void AnUnsupportedPackMajorIsRefused()
        {
            var edited = Read("pack.json").Replace("\"pack_format_version\": \"1.0.0\"",
                "\"pack_format_version\": \"9.0.0\"");
            var pack = LoadWith(pack: edited);
            Assert.IsFalse(pack.Loaded);
            Assert.IsTrue(pack.Errors.Any(error => error.Contains("unsupported pack format")),
                pack.ReadableFailure);
        }

        [Test]
        public void AMalformedFileIsRefusedAndNothingIsRepaired()
        {
            var pack = LoadWith(instructor: "{");
            Assert.IsFalse(pack.Loaded);
            Assert.IsNotEmpty(pack.Errors);
            Assert.IsNull(pack.StageData);
            Assert.Throws<EvaluationContractException>(() => pack.Envelope());
        }

        [Test]
        public void AnAbsentPackIsReportedAsEveryReasonAtOnce()
        {
            var pack = PolicyPack.Refused(new[] { "the bundled pack manifest is absent" });
            Assert.IsFalse(pack.Loaded);
            StringAssert.Contains("absent", pack.ReadableFailure);
            Assert.AreEqual("Policy: closed - " + pack.ReadableFailure,
                BundledPolicyPack.TraceLine(pack));
        }

        [Test]
        public void AnEmptySystemSupplyIsRefused()
        {
            // Removing every instruction line still leaves a well-formed pack,
            // so this proves the assembled-text bound rather than the digest.
            var text = Read("instructor-policy.json");
            var pack = LoadWith(instructor: text.Replace(
                "\"text\": [", "\"text_removed\": ["));
            Assert.IsFalse(pack.Loaded);
            Assert.IsTrue(pack.Errors.Any(error => error.Contains("file digest")) ||
                          pack.Errors.Any(error => error.Contains("empty")),
                pack.ReadableFailure);
        }

        [Test]
        public void TheEnvelopeIsNeverBuiltFromARefusedPack()
        {
            var refused = PolicyPack.Refused(new[] { "refused" });
            var error = Assert.Throws<EvaluationContractException>(() => refused.Envelope());
            Assert.AreEqual("no_policy", error.ReasonCode);
        }

        [Test]
        public async Task TheBundledPackBindsTheTransportAndStaysClosedWithoutAKey()
        {
            // The shipped pack loads and assembles, so the tutor port is bound.
            // No key exists on a fresh device, so the live path still fails
            // closed with the credential reason and no request leaves it.
            var pack = LoadShipped();
            var context = new ValidationContext { EpisodeId = pack.EpisodeId };
            var transport = new Amiss.Shell.Tutor.DeviceTutorTransport(
                new ProposalValidator(context), policy: pack.Envelope());

            Assert.IsTrue(transport.Available);
            Assert.IsFalse(transport.KeyPresent);
            StringAssert.Contains("no Meta model key", transport.TraceLine);
            Assert.IsEmpty(transport.FailClosedDetail.Contains("no Meta model key")
                ? "" : transport.FailClosedDetail);

            var admission = await transport.Request(Payload(), "request-a", "turn-a", "answer");
            Assert.IsFalse(admission.Admitted);
            Assert.AreEqual("no_credential", admission.Failure.ReasonCode);
            Assert.IsNull(transport.LastProvenance);
        }

        static IReadOnlyDictionary<string, object> Payload() => new Dictionary<string, object>
        {
            ["context_payload_version"] = "1.0.0",
            ["comparison_package"] = new List<object>(),
            ["refs"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["trial_id"] = "trial-a", ["trial_revision"] = 1L
                }
            },
            ["policy_tokens"] = new Dictionary<string, object>
            {
                ["episode_id"] = "what_is_temperature",
                ["stage_id"] = "stage-0-entry",
                ["policy_version"] = "amiss-hackathon-policy-v1",
                ["refs"] = new List<object>(),
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
    }
}
