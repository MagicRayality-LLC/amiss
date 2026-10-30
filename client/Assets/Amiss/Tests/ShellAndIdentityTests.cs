using System.Collections.Generic;
using System.IO;
using System.Linq;
using Amiss.Contracts;
using Amiss.Shell;
using Amiss.Shell.Fixtures;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Amiss.Tests
{
    public class LibraryCardTests
    {
        [Test]
        public void ProjectionExposesExactlyTheApprovedFields()
        {
            var expected = new HashSet<string>
            {
                nameof(LibraryCard.EpisodeId),
                nameof(LibraryCard.Question),
                nameof(LibraryCard.ExpectedDurationMinutes),
                nameof(LibraryCard.RequiredSpaceSummary),
                nameof(LibraryCard.ActionType),
                nameof(LibraryCard.Completed),
                nameof(LibraryCard.Availability),
                nameof(LibraryCard.AvailabilityReason)
            };
            var actual = typeof(LibraryCard)
                .GetProperties()
                .Select(p => p.Name)
                .ToHashSet();
            Assert.That(actual, Is.EquivalentTo(expected));
            Assert.IsFalse(actual.Contains("Conclusion"),
                "the card projection must have no conclusion field");
        }

        [Test]
        public void DeclarationHasNoConclusionFieldEither()
        {
            var actual = typeof(EpisodeDeclaration)
                .GetProperties()
                .Select(p => p.Name)
                .ToList();
            Assert.IsFalse(actual.Any(n => n.Contains("Conclusion")),
                "a conclusion cannot leak onto a card by declaration field");
        }

        [Test]
        public void ProjectionCarriesQuestionDurationSpaceActionAndCompletion()
        {
            var card = LibraryCard.Project(
                FrameworkContractFixtures.FakeDiscrete(), new EpisodeProgress());
            StringAssert.Contains("contract fixture", card.Question);
            Assert.AreEqual(5, card.ExpectedDurationMinutes);
            StringAssert.Contains("1 m3", card.RequiredSpaceSummary);
            Assert.AreEqual("Shell self-test", card.ActionType);
            Assert.IsFalse(card.Completed);
        }

        [Test]
        public void CompletionComesOnlyFromProgress()
        {
            var completed = LibraryCard.Project(
                FrameworkContractFixtures.FakeDiscrete(),
                new EpisodeProgress { CompletionStageReached = true });
            Assert.IsTrue(completed.Completed);

            var progress = new EpisodeProgress { CompletionStageReached = true };
            var declaration = FrameworkContractFixtures.FakeDiscrete();
            Assert.IsTrue(LibraryCard.Project(declaration, progress).Completed);
            Assert.IsFalse(LibraryCard.Project(declaration, null).Completed);
        }
    }

    public class VersionIdentityTests
    {
        [Test]
        public void FrameworkVersionsMatchSchemaVersionsJson()
        {
            var repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            var schemaPath = Path.Combine(repoRoot, "schema", "versions.json");
            Assert.IsTrue(File.Exists(schemaPath),
                "schema/versions.json must sit next to the client tree");

            var text = File.ReadAllText(schemaPath);
            Assert.AreEqual(Extract(text, "framework_api_version"), FrameworkApiVersions.FrameworkApi);
            Assert.AreEqual(Extract(text, "snapshot_envelope_version"), FrameworkApiVersions.SnapshotEnvelope);
            Assert.AreEqual(Extract(text, "tool_schema_version"), FrameworkApiVersions.ToolSchema);
            Assert.AreEqual(Extract(text, "save_format_version"), FrameworkApiVersions.SaveFormat);
            Assert.AreEqual(Extract(text, "stage_data_format_version"), FrameworkApiVersions.StageDataFormat);
            Assert.AreEqual(Extract(text, "context_payload_version"), FrameworkApiVersions.ContextPayload);
            Assert.AreEqual(Extract(text, "fixture_format_version"), FrameworkApiVersions.FixtureFormat);
            Assert.AreEqual(Extract(text, "provenance_contract_version"), FrameworkApiVersions.ProvenanceContract);
        }

        static string Extract(string json, string key)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                json, "\"" + key + "\": \"([^\"]+)\"");
            Assert.IsTrue(match.Success, $"versions.json is missing axis '{key}'");
            return match.Groups[1].Value;
        }

        [Test]
        public void BuildIdentityLineNamesEveryContractAxis()
        {
            var line = BuildIdentity.Compose("0.1.0", "6000.6.5f1");
            StringAssert.Contains("app=0.1.0", line);
            StringAssert.Contains("unity=6000.6.5f1", line);
            StringAssert.Contains("framework-api=" + FrameworkApiVersions.FrameworkApi, line);
            StringAssert.Contains("snapshot=" + FrameworkApiVersions.SnapshotEnvelope, line);
            StringAssert.Contains("tool=" + FrameworkApiVersions.ToolSchema, line);
            StringAssert.Contains("save=" + FrameworkApiVersions.SaveFormat, line);
            StringAssert.Contains("stage=" + FrameworkApiVersions.StageDataFormat, line);
            StringAssert.Contains("context=" + FrameworkApiVersions.ContextPayload, line);
            StringAssert.Contains("fixture=" + FrameworkApiVersions.FixtureFormat, line);
            StringAssert.Contains("provenance=" + FrameworkApiVersions.ProvenanceContract, line);
            StringAssert.Contains(
                "shell-capabilities=" + ShellCapabilityManifest.ThisBuild().ManifestVersion, line);
            StringAssert.DoesNotContain("serial", line.ToLower());
            StringAssert.DoesNotContain("device", line.ToLower());
        }

        [Test]
        public void ApplicationIdMatchesTheFrozenBuildIdentity()
        {
            Assert.AreEqual("com.magicrayality.amiss",
                PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android));
        }
    }

    public class FixtureTests
    {
        [Test]
        public void FakeDiscreteMirrorsTheHeadlessContractFixture()
        {
            var declaration = FrameworkContractFixtures.FakeDiscrete();
            Assert.AreEqual("fake-discrete", declaration.EpisodeId);
            Assert.AreEqual("1.0.0", declaration.EpisodeModuleVersion);
            Assert.AreEqual("compact_bench", declaration.SceneForm);
            Assert.AreEqual(1.0, declaration.MinimumVolumeM3);
            Assert.AreEqual(EpisodeAvailability.Bundled, declaration.Availability);
        }

        [Test]
        public void FixturePassesTheGateAgainstThisShellBuild()
        {
            var verdict = EpisodeLoadGate.Evaluate(
                FrameworkContractFixtures.FakeDiscrete(),
                ShellCapabilityManifest.ThisBuild(),
                FrameworkContractFixtures.ResolveCriticalAsset);
            Assert.IsTrue(verdict.Runnable);
            Assert.That(verdict.Rejections, Is.Empty);
        }

        [Test]
        public void AssetResolverOnlyResolvesFixtureAssets()
        {
            Assert.IsTrue(FrameworkContractFixtures.ResolveCriticalAsset("fake-discrete-asset"));
            Assert.IsFalse(FrameworkContractFixtures.ResolveCriticalAsset("some-other-asset"));
        }
    }

    public class ShellSessionTests
    {
        static (HeadsetShellSession session, EpisodeModuleHost host) Build(
            IEpisodeDeclarationSource source) =>
            (new HeadsetShellSession(
                    source, ShellCapabilityManifest.ThisBuild(), new EpisodeModuleHost(),
                    FrameworkContractFixtures.ResolveCriticalAsset),
                null);

        [Test]
        public void EntryIsTheLibraryWithOneFixtureCard()
        {
            var (session, _) = Build(new FrameworkContractFixtures.DeclarationSource());
            Assert.AreEqual(ShellSessionState.Library, session.State);
            Assert.AreEqual(1, session.Cards.Count);
            Assert.AreEqual("fake-discrete", session.Cards[0].EpisodeId);
        }

        [Test]
        public void SelectingTheFixtureCardReachesStartReady()
        {
            var (session, _) = Build(new FrameworkContractFixtures.DeclarationSource());
            session.SelectCard(0);
            Assert.AreEqual(ShellSessionState.StartReady, session.State);
            Assert.IsTrue(session.SelectedVerdict.Runnable);
        }

        [Test]
        public void StartFailsClosedWithAReadableReasonInThisBuild()
        {
            var (session, _) = Build(new FrameworkContractFixtures.DeclarationSource());
            session.SelectCard(0);
            session.RequestStart();
            Assert.AreEqual(ShellSessionState.MountFailed, session.State);
            StringAssert.Contains("No episode module", session.MountFailureReason);
        }

        [Test]
        public void ReturnToLibraryRestoresTheLibraryState()
        {
            var (session, _) = Build(new FrameworkContractFixtures.DeclarationSource());
            session.SelectCard(0);
            session.RequestStart();
            session.ReturnToLibrary();
            Assert.AreEqual(ShellSessionState.Library, session.State);
            Assert.IsNull(session.Selected);
            Assert.IsNull(session.SelectedVerdict);
            Assert.AreEqual("", session.MountFailureReason);
        }

        [Test]
        public void LockedCardStaysVisibleAndCannotStart()
        {
            var locked = DeclarationBuilders.WithAvailability(
                EpisodeAvailability.Locked, "Not available for this event build.");
            var (session, _) = Build(new DeclarationBuilders.SingleDeclarationSource(locked));
            session.SelectCard(0);
            Assert.AreEqual(ShellSessionState.StartBlocked, session.State);
            Assert.IsTrue(session.SelectedVerdict.AvailabilityBlocked);
            session.RequestStart();
            Assert.AreEqual(ShellSessionState.StartBlocked, session.State);
            session.ReturnToLibrary();
            Assert.AreEqual(ShellSessionState.Library, session.State);
        }

        [Test]
        public void NotRunnableCardShowsReadableReasons()
        {
            var broken = new EpisodeDeclaration
            {
                EpisodeId = "broken-episode",
                EpisodeModuleVersion = "1.0.0",
                FrameworkApiMin = "99.0.0",
                FrameworkApiMax = "99.0.0",
                Availability = EpisodeAvailability.Bundled
            };
            var (session, _) = Build(new DeclarationBuilders.SingleDeclarationSource(broken));
            session.SelectCard(0);
            Assert.AreEqual(ShellSessionState.StartBlocked, session.State);
            Assert.That(session.SelectedVerdict.Rejections, Is.Not.Empty);
            Assert.IsNotEmpty(session.SelectedVerdict.Rejections[0].ReadableReason);
        }

        [Test]
        public void MountFailsClosedWithoutAnyEpisodeModule()
        {
            var host = new EpisodeModuleHost();
            var result = host.Mount(FrameworkContractFixtures.FakeDiscrete());
            Assert.IsFalse(result.Success);
            Assert.AreEqual("episode_module_not_available", result.ReasonCategory);
            var residue = host.Residue;
            Assert.That(residue, Is.Empty);
        }

        [Test]
        public void UnmountWithoutMountIsClean()
        {
            var host = new EpisodeModuleHost();
            var result = host.Unmount();
            Assert.IsTrue(result.Success);
            Assert.That(host.Residue, Is.Empty);
        }
    }
}