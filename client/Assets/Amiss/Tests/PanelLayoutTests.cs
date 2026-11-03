using System;
using System.Collections.Generic;
using System.Linq;
using Amiss.Contracts;
using Amiss.Shell;
using Amiss.Shell.Ports;
using Amiss.Shell.Room;
using Amiss.Shell.Simulation;
using Amiss.Shell.UI;
using NUnit.Framework;
using UnityEngine;

namespace Amiss.Tests
{
    /// <summary>
    /// The fixed-slot page budgets of design 05 section 4.2. These tests are
    /// the maintained guard for the T7F defect: slot text needed more lines
    /// than its row owned, so rows painted over each other. A slot height is
    /// now derived from its declared line budget, and every state the shell
    /// renders is checked against that budget.
    /// </summary>
    public class PanelLayoutTests
    {
        [Test]
        public void OldTypedSlotHeightsOverflowThePageBudget()
        {
            // The geometry that produced the T7F overlap: a 34 pt status slot
            // at 100 px, a 28 pt safety slot at 42 px, a 68 px readout slot,
            // two 70 px cards, and 3 px of spacing between thirteen rows.
            const float oldStatus = 100f;
            const float oldSafety = 42f;
            const float oldReadout = 68f;
            const float oldCards = 2f * 70f;
            var oldColumn = 52f + oldStatus + oldSafety + oldReadout + 42f + 64f + oldCards +
                            58f + 40f + 68f + 58f + 58f + 24f;
            var oldTotal = oldColumn + 3f * 12f;
            Assert.Greater(oldTotal, PanelLayout.ContentHeight,
                "the T7F column must overflow the page for this guard to mean anything");

            // The same rows now hold their declared text, and the page fits.
            Assert.AreEqual(3, PanelLayout.MaxCards);
            Assert.IsTrue(PanelLayout.Fits(1), "one card: " + PanelLayout.Margin(1));
            Assert.IsTrue(PanelLayout.Fits(2), "two cards: " + PanelLayout.Margin(2));
            Assert.IsTrue(PanelLayout.Fits(3), "three cards: " + PanelLayout.Margin(3));
            Assert.IsFalse(PanelLayout.Fits(4), "a fourth card needs a paged card area");
            Assert.Greater(PanelLayout.Margin(2), 0f);
        }

        [Test]
        public void EverySlotRowIsTallEnoughForItsLineBudget()
        {
            foreach (var slot in PanelLayout.Column)
            {
                if (slot == PanelSlot.Tabs || slot == PanelSlot.Controls || slot == PanelSlot.Cards)
                    continue;
                var budget = PanelLayout.LineBudget(slot);
                Assert.Greater(budget, 0, slot + " declares no line budget");
                var needed = budget * PanelLayout.LineHeight(CorePanelPresentation.FontSize(slot));
                Assert.GreaterOrEqual(PanelLayout.Height(slot) + 0.01f, needed,
                    slot + " is shorter than its own line budget");
            }
        }

        [Test]
        public void LibraryWithTwoCardsFitsThePage()
        {
            var presentation = Presentation(ShellSessionState.Library, declarations: 2);
            Assert.IsNull(presentation.FirstOverflowingSlot(), presentation.FirstOverflowingSlot());
            Assert.AreEqual(2, presentation.Cards.Count);
        }

        [Test]
        public void EveryRoomSetupStateFitsThePage()
        {
            foreach (var status in new[]
                     {
                         RoomPreparationStatus.Idle,
                         RoomPreparationStatus.Explanation,
                         RoomPreparationStatus.PermissionRequested,
                         RoomPreparationStatus.SpaceSetupReady,
                         RoomPreparationStatus.SpaceSetupRequested,
                         RoomPreparationStatus.SceneLoading,
                         RoomPreparationStatus.AwaitingConfirmation,
                         RoomPreparationStatus.Confirmed,
                         RoomPreparationStatus.Restricted
                     })
            {
                var preparation = RoomIn(status);
                var presentation = Presentation(ShellSessionState.RoomPreparation,
                    preparation: preparation);
                Assert.IsNull(presentation.FirstOverflowingSlot(),
                    status + ": " + presentation.FirstOverflowingSlot());
            }
        }

        [Test]
        public void LiveTrialWithReadoutAndTraceFitsThePage()
        {
            var runtime = new ScriptedRuntime();
            runtime.Publish(Readout(), Summary());
            var presentation = Presentation(ShellSessionState.SetupAccepted, runtime: runtime,
                tutor: new ScriptedTutor());
            Assert.IsNull(presentation.FirstOverflowingSlot(), presentation.FirstOverflowingSlot());
            StringAssert.Contains("readout.primary", presentation.Readout);
            StringAssert.Contains("K_thermal", presentation.Readout);
            StringAssert.Contains("Trial trial-local-0001", presentation.Trial);
            StringAssert.Contains("RECORDED", presentation.Subtitle);
        }

        [Test]
        public void TextSlotsThatShareABandStayInsideItsHeight()
        {
            // Slots that render in one row must fit the row that is actually
            // allocated. The T7F follow-up defect was the chart line sitting
            // outside the band it shared with the four chart tabs.
            foreach (var slot in PanelLayout.Column)
            {
                if (slot == PanelSlot.Progress || slot == PanelSlot.Cards)
                    continue;
                var lines = PanelLayout.LineBudget(slot);
                if (lines == 0)
                    continue;
                var needed = lines * PanelLayout.LineHeight(CorePanelPresentation.FontSize(slot));
                var band = slot == PanelSlot.ChartStatus
                    ? PanelLayout.Height(PanelSlot.Tabs)
                    : PanelLayout.Height(slot);
                Assert.GreaterOrEqual(band + 0.01f, needed,
                    slot + " does not fit the band it is rendered in");
            }
        }

        [Test]
        public void ChartLineFitsBesideTheTabs()
        {
            var presentation = Presentation(ShellSessionState.Library);
            var used = CorePanelPresentation.Lines(presentation.ChartStatus,
                PanelLayout.ChartFontSize, PanelLayout.ChartStatusWidth);
            Assert.LessOrEqual(used, PanelLayout.ChartStatusLines, presentation.ChartStatus);
            Assert.LessOrEqual(PanelLayout.TabsRowHeight,
                PanelLayout.Height(PanelSlot.Tabs) + 0.01f);
        }

        [Test]
        public void SafetyLinesFitTheirSlot()
        {
            var lines = new[]
            {
                CorePanelController.SafetyBanner + "\nSAFETY HOLD: reconfirm, then explicitly resume.",
                CorePanelController.SafetyBanner + "\nLocal pause: explicit resume required.",
                CorePanelController.SafetyBanner + "\nSafety: no active hold."
            };
            foreach (var text in lines)
                Assert.LessOrEqual(CorePanelPresentation.Lines(text, PanelLayout.ReadoutFontSize,
                    PanelLayout.ContentWidth), PanelLayout.SafetyLines, text);
        }

        [Test]
        public void RenderedViewKeepsEverySlotInsideItsBudget()
        {
            var host = new GameObject("PanelLayoutTest");
            try
            {
                var view = new PanelView();
                view.Build(host.transform, 2, _ => { }, () => { }, () => { }, () => { },
                    () => { }, _ => { }, false);
                view.Render(Presentation(ShellSessionState.SetupAccepted, declarations: 2,
                    runtime: LiveRuntime(), tutor: new ScriptedTutor()));

                var checkedSlots = 0;
                foreach (var (slot, text) in view.SlotTexts())
                {
                    var budget = slot == PanelSlot.Cards
                        ? PanelLayout.CardLines
                        : PanelLayout.LineBudget(slot);
                    var width = slot == PanelSlot.Cards
                        ? PanelLayout.ContentWidth
                        : PanelLayout.SlotWidth(slot);
                    var used = CorePanelPresentation.Lines(text,
                        CorePanelPresentation.FontSize(slot), width);
                    Assert.LessOrEqual(used, budget, slot + " overflows: " + text);
                    checkedSlots++;
                }
                Assert.GreaterOrEqual(checkedSlots, PanelLayout.Column.Length - 2);

                // The column the renderer builds is the column the budget
                // declares: no row is taller than its slot height.
                var columnHeight = 0f;
                foreach (var slot in PanelLayout.Column)
                    columnHeight += PanelLayout.Height(slot, 2);
                columnHeight += PanelLayout.Spacing * (PanelLayout.Column.Length - 1);
                Assert.GreaterOrEqual(PanelLayout.ContentHeight + 0.01f, columnHeight);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        static SimulationRuntimePort LiveRuntime()
        {
            var runtime = new ScriptedRuntime();
            runtime.Publish(Readout(), Summary());
            return runtime;
        }

        static CorePanelPresentation Presentation(ShellSessionState state, int declarations = 1,
            RoomPreparation preparation = null,
            SimulationRuntimePort runtime = null, TutorTransportPort tutor = null)
        {
            var session = new HeadsetShellSession(new Source(declarations),
                ShellCapabilityManifest.ThisBuild(), new EpisodeModuleHost(), _ => true);
            Drive(session, state);
            var panel = new CorePanelController(runtime ?? new UnboundSimulationRuntime(),
                new UnboundGuideRunner(), null);
            return CorePanelPresentationBuilder.Build(session, panel,
                runtime ?? new UnboundSimulationRuntime(),
                tutor ?? new UnboundTutorTransport(), preparation,
                preparation == null ? null : new ShellRecovery(), "app=0.1.0 test");
        }

        static void Drive(HeadsetShellSession session, ShellSessionState state)
        {
            if (state == ShellSessionState.Library)
                return;
            session.SelectCard(0);
            if (state == ShellSessionState.CardSelected || state == ShellSessionState.StartBlocked)
                return;
            session.RequestStart();
            if (state == ShellSessionState.RoomPreparation)
                return;
            session.MarkRoomConfirmed();
            if (state == ShellSessionState.RoomConfirmed)
                return;
            session.MarkSetupAccepted();
        }

        static RoomPreparation RoomIn(RoomPreparationStatus status)
        {
            var preparation = new RoomPreparation();
            if (status == RoomPreparationStatus.Idle)
                return preparation;
            var id = preparation.Begin(1.0);
            if (status == RoomPreparationStatus.Explanation)
                return preparation;
            preparation.RequestPermission(id);
            if (status == RoomPreparationStatus.PermissionRequested)
                return preparation;
            if (status == RoomPreparationStatus.Restricted)
            {
                preparation.CompletePermission(id, false);
                return preparation;
            }
            preparation.CompletePermission(id, true);
            if (status == RoomPreparationStatus.SpaceSetupReady)
            {
                preparation.CompleteSceneLoad(id, null, RoomFailureReason.NoScene);
                return preparation;
            }
            if (status == RoomPreparationStatus.SpaceSetupRequested)
            {
                preparation.CompleteSceneLoad(id, null, RoomFailureReason.NoScene);
                preparation.RequestSpaceSetup(id);
                return preparation;
            }
            if (status == RoomPreparationStatus.SceneLoading)
            {
                preparation.CompleteSceneLoad(id, null, RoomFailureReason.NoScene);
                preparation.CompleteSpaceSetup(id, true);
                return preparation;
            }
            preparation.CompleteSceneLoad(id, RoomTestFixtures.Valid());
            if (status == RoomPreparationStatus.AwaitingConfirmation)
                return preparation;
            preparation.Confirm(id);
            Assert.AreEqual(RoomPreparationStatus.Confirmed, preparation.Status, status.ToString());
            return preparation;
        }

        static TemperatureReadoutSnapshot Readout()
        {
            var balls = new List<PublishedBall>();
            for (var i = 0; i < 12; i++)
            {
                var angle = i + 1.0;
                balls.Add(new PublishedBall("ball-" + i.ToString("D3"), Vector3.zero,
                    new Vector3((float)Math.Sin(angle), (float)Math.Cos(angle), 1f),
                    1f, 0.05f, "free"));
            }
            var measurement = TemperatureMeasurementFace.Measure("measurement-layout", balls,
                new MeasurementWindow("window-layout", 0, 600, 0.5f, 1201), initialTK: 300.0);
            return new TemperatureReadoutSnapshot("trial-local-0001", 7930, "view-1", measurement,
                new TemperatureEnergyLedger(10.0, 10.0, 8.0, 0.06), 0);
        }

        static SimulationTrialSummary Summary() => new("trial-local-0001", 7930, 72,
            "custom-hard-sphere-frozen-v2", "70d844987f7b3c1d9e5a", "unity",
            "custom-hard-sphere-frozen-v2", false);

        sealed class ScriptedRuntime : SimulationRuntimePort
        {
            TemperatureReadoutSnapshot readout;
            SimulationTrialSummary summary;

            public void Publish(TemperatureReadoutSnapshot value, SimulationTrialSummary trial)
            {
                readout = value;
                summary = trial;
            }

            public bool Available => true;
            public bool PauseRequested { get; private set; }
            public string PauseReason { get; private set; } = "";
            public void RequestPause(string reason) => PauseRequested = true;
            public void RequestResume() => PauseRequested = false;
            public TrialStartOutcome TryStartTrial(TrialStartRequest request) =>
                TrialStartOutcome.Refused(request?.TrialId, TrialStartRefusal.RuntimeNotBound, "");
            public SimulationTrialSummary TrialSummary => summary;
            public TemperatureReadoutSnapshot Readout => readout;
            public void StopTrial(string reason) { }
        }

        sealed class ScriptedTutor : TutorTransportPort
        {
            public bool Available => true;
            public string TraceLine =>
                "Model: RECORDED muse-spark-1.3 · request-a · policy v1 · no masked value";
        }

        sealed class Source : IEpisodeDeclarationSource
        {
            public Source(int count)
            {
                Declarations = Enumerable.Range(0, count)
                    .Select(index => Declare(index == 0
                        ? "What determines the temperature of a group of balls? Swing, grab and " +
                          "place, throw a sandbag, and predict at the panel."
                        : "A shorter question for declaration " + index + "?"))
                    .ToArray();
            }

            public IReadOnlyList<EpisodeDeclaration> Declarations { get; }

            static EpisodeDeclaration Declare(string question) => new()
            {
                EpisodeId = "what_is_temperature",
                EpisodeModuleVersion = "1.0.0",
                FrameworkApiMin = "1.0.0",
                FrameworkApiMax = "1.0.0",
                RequiredCapabilities = Array.Empty<RequiredCapability>(),
                SceneForm = "whole_room",
                MinimumVolumeM3 = 3.0,
                BelowMinimum = BelowMinimumBehavior.RefuseWithExplanation,
                RequiredInputBaseline = "controller-or-hand",
                ExpectedDurationMinutes = 15,
                CardQuestion = question,
                CardActionType = "Swing and predict",
                CompletionStageId = "stage-7",
                Availability = EpisodeAvailability.Bundled,
                Assets = Array.Empty<AssetManifestEntry>(),
                ReadableSaveVersions = Array.Empty<SaveCompatibility>()
            };
        }
    }
}
