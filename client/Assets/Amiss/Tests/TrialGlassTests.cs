using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Amiss.Shell;
using Amiss.Shell.Simulation;
using Amiss.Shell.UI;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Amiss.Tests
{
    /// <summary>
    /// The wall glass of the running trial (founder directive 2026-11-02):
    /// the temperature in three scales and one temperature-over-time graph, and
    /// nothing else. The hub keeps its own surface, so the fixed column is not
    /// part of this glass.
    /// </summary>
    public class TrialGlassTests
    {
        [Test]
        public void WallStatesCarryOnlyTheReadoutAndTheGraph()
        {
            var host = new GameObject("TrialGlassTest");
            try
            {
                var column = new PanelView();
                column.Build(host.transform, 1, _ => { }, () => { }, () => { }, () => { },
                    () => { }, _ => { }, false);
                var glass = new TrialGlassView();
                glass.Build(host.transform);

                foreach (var state in new[]
                         {
                             ShellSessionState.RoomConfirmed,
                             ShellSessionState.SetupAccepted
                         })
                {
                    var presentation = TrialGlassPresentation.Build(state,
                        Readout("trial-a", 20, 1.1f), History(1.1f));
                    Assert.IsTrue(presentation.WallSetVisible, state.ToString());
                    CollectionAssert.AreEqual(
                        new[] { TrialGlassPresentation.ReadoutElement, TrialGlassPresentation.ChartElement },
                        TrialGlassPresentation.Elements, state.ToString());

                    column.SetVisible(false);
                    glass.SetVisible(true);
                    glass.Render(presentation);

                    Assert.IsFalse(column.Content.gameObject.activeInHierarchy,
                        state + ": the fixed column is not on the wall glass");
                    foreach (var (slot, _) in column.SlotTexts())
                    {
                        if (slot == PanelSlot.Cards)
                            continue;
                        Assert.IsFalse(column.Slot(slot).gameObject.activeInHierarchy,
                            state + ": " + slot + " must not render on the wall glass");
                    }
                    foreach (var card in column.CardTexts)
                        Assert.IsFalse(card.gameObject.activeInHierarchy,
                            state + ": card text must not render on the wall glass");
                    Assert.IsTrue(glass.Root.gameObject.activeInHierarchy);
                    CollectionAssert.AreEqual(TrialGlassPresentation.Elements, glass.ElementIds);
                    CollectionAssert.AreEquivalent(new[]
                    {
                        "TemperatureKelvin", "TemperatureSecondary", "TemperatureState",
                        "TemperatureChart"
                    }, glass.Root.Cast<Transform>().Select(child => child.name).ToArray(),
                        state + ": no other element is built on the glass");
                    Assert.IsFalse(string.IsNullOrEmpty(glass.KelvinDigits.text));
                    AssertScaleStatementIsNotOnTheGlass(glass, state.ToString());
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void EveryOtherSessionStateKeepsTheFixedColumn()
        {
            foreach (var state in new[]
                     {
                         ShellSessionState.Library,
                         ShellSessionState.CardSelected,
                         ShellSessionState.StartBlocked,
                         ShellSessionState.StartReady,
                         ShellSessionState.RoomPreparation,
                         ShellSessionState.Restricted,
                         ShellSessionState.MountFailed
                     })
            {
                var presentation = TrialGlassPresentation.Build(state, null, null);
                Assert.IsFalse(presentation.WallSetVisible, state.ToString());
            }
        }

        [Test]
        public void KnownMeasurementShowsTheProducerTupleInThreeScales()
        {
            var readout = Readout("trial-a", 20, 1.1f);
            Assert.AreEqual(300, readout.Display.Kelvin, "the T1b scale point is 300 K");
            Assert.AreEqual(27, readout.Display.Celsius);
            Assert.AreEqual(80, readout.Display.Fahrenheit);

            var presentation = TrialGlassPresentation.Build(ShellSessionState.SetupAccepted,
                readout, null);
            Assert.IsTrue(presentation.HasValues);
            Assert.AreEqual("300", presentation.KelvinValue);
            Assert.AreEqual(readout.Display.Celsius.ToString(CultureInfo.InvariantCulture),
                presentation.CelsiusValue);
            Assert.AreEqual(readout.Display.Fahrenheit.ToString(CultureInfo.InvariantCulture),
                presentation.FahrenheitValue);
            Assert.AreEqual("27 °C   ·   80 °F", presentation.SecondaryLine);
            Assert.AreEqual("", presentation.StateLine);

            var host = new GameObject("TrialGlassNumbers");
            try
            {
                var glass = new TrialGlassView();
                glass.Build(host.transform);
                glass.SetVisible(true);
                glass.Render(presentation);
                Assert.AreEqual("300", glass.KelvinDigits.text);
                Assert.AreEqual("27 °C   ·   80 °F", glass.SecondaryLine.text);
                AssertScaleStatementIsNotOnTheGlass(glass, "values");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void TheChartTakesEveryPixelTheGlassDoesNotNeed()
        {
            var host = new GameObject("TrialGlassLayout");
            try
            {
                var glass = new TrialGlassView();
                glass.Build(host.transform);
                glass.SetVisible(true);
                glass.Render(TrialGlassPresentation.Build(ShellSessionState.SetupAccepted,
                    Readout("trial-a", 20, 1.1f), History(1.1f)));

                var rows = glass.Root.Cast<Transform>()
                    .Where(child => child.gameObject.activeSelf)
                    .ToArray();
                var chart = rows.Single(child => child.name == "TemperatureChart");
                var fixedRows = rows.Where(child => child != chart).ToArray();
                Assert.IsNotEmpty(fixedRows, "the fixed rows are still built");
                foreach (var row in fixedRows)
                {
                    var element = row.GetComponent<LayoutElement>();
                    Assert.IsNotNull(element, row.name + " declares its height");
                    Assert.AreEqual(0f, element.flexibleHeight,
                        row.name + " keeps its declared height");
                    Assert.Greater(element.preferredHeight, 0f, row.name + " declares its height");
                }

                var chartElement = chart.GetComponent<LayoutElement>();
                Assert.AreEqual(1f, chartElement.flexibleHeight,
                    "the graph is the only element that takes the remaining page height");

                var spacing = glass.Root.GetComponent<VerticalLayoutGroup>().spacing;
                var used = fixedRows.Sum(row => row.GetComponent<LayoutElement>().preferredHeight) +
                           spacing * (rows.Length - 1) +
                           chartElement.minHeight;
                Assert.LessOrEqual(used, TrialGlassLayout.ContentHeight,
                    "the declared rows, the gaps, and the graph's minimum height fit the page");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// Founder directive 2026-11-02: the running trial's glass carries the
        /// three scales and the graph only. The permanent scale statement stays
        /// in the readout projection and the evidence export, never on the glass.
        /// </summary>
        static void AssertScaleStatementIsNotOnTheGlass(TrialGlassView glass, string state)
        {
            var texts = glass.Root.GetComponentsInChildren<TMP_Text>(true);
            Assert.IsNotEmpty(texts, state + ": the glass renders its own text elements");
            foreach (var text in texts)
            {
                StringAssert.DoesNotContain(TemperatureReadoutProjection.ScaleLabel, text.text,
                    state + ": " + text.name + " must not carry the scale statement");
            }
        }

        [Test]
        public void NotApplicableGlassKeepsItsStateSentenceAndAnEmptyGraph()
        {
            var buffer = new TemperatureHistoryBuffer();
            var readout = NotApplicable("trial-a", 20);
            var presentation = TrialGlassPresentation.Build(ShellSessionState.RoomConfirmed,
                readout, buffer);

            Assert.IsFalse(presentation.HasValues);
            Assert.AreEqual("", presentation.KelvinValue);
            Assert.AreEqual("", presentation.SecondaryLine);
            Assert.AreEqual(TemperatureReadoutProjection.TemperatureLine(readout.Measurement),
                presentation.StateLine);
            StringAssert.Contains("not applicable", presentation.StateLine);
            Assert.IsFalse(presentation.Chart.HasSamples);
            Assert.AreEqual("", presentation.Chart.TopLabel);
            Assert.AreEqual("", presentation.Chart.EndLabel);

            var host = new GameObject("TrialGlassNotApplicable");
            try
            {
                var glass = new TrialGlassView();
                glass.Build(host.transform);
                glass.SetVisible(true);
                glass.Render(presentation);
                Assert.AreEqual(presentation.StateLine, glass.StateText.text);
                Assert.IsFalse(glass.KelvinDigits.gameObject.activeInHierarchy,
                    "no number is invented without a display tuple");
                AssertScaleStatementIsNotOnTheGlass(glass, "not applicable");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }

            var absent = TrialGlassPresentation.Build(ShellSessionState.RoomConfirmed, null,
                new TemperatureHistoryBuffer());
            Assert.IsFalse(absent.HasValues);
            Assert.AreEqual(TemperatureReadoutProjection.NoProducer, absent.StateLine);
        }

        [Test]
        public void StoppingTheTrialReturnsTheGlassToItsPreTrialState()
        {
            var buffer = History(1.1f);
            Assert.IsFalse(buffer.IsEmpty);

            TrialGlassFeed.Sync(buffer, trialRunning: false, readout: null);

            Assert.IsTrue(buffer.IsEmpty, "no sample of the stopped trial survives");
            var presentation = TrialGlassPresentation.Build(ShellSessionState.RoomConfirmed,
                null, buffer);
            Assert.IsFalse(presentation.HasValues);
            Assert.AreEqual(TemperatureReadoutProjection.NoProducer, presentation.StateLine);
            Assert.IsFalse(presentation.Chart.HasSamples);
            Assert.AreEqual("", presentation.Chart.TopLabel);
            Assert.AreEqual("", presentation.Chart.EndLabel);
        }

        [Test]
        public void ChartAxisFollowsBufferedKelvinAndTrialTime()
        {
            var buffer = new TemperatureHistoryBuffer();
            Assert.IsTrue(buffer.Feed(Readout("trial-a", 0, 1.0f)));
            Assert.IsTrue(buffer.Feed(Readout("trial-a", 20, 1.2f)));

            var chart = TemperatureChartModel.From(buffer);
            Assert.IsTrue(chart.HasSamples);
            Assert.AreEqual(buffer.MinKelvin, chart.AxisMinKelvin, 1e-9);
            Assert.AreEqual(buffer.MaxKelvin, chart.AxisMaxKelvin, 1e-9);
            Assert.AreEqual(0.0, chart.NormalizedX(0.0), 1e-9);
            Assert.AreEqual(1.0, chart.NormalizedX(chart.AxisSpanSeconds), 1e-9);
            Assert.AreEqual(0.0, chart.NormalizedY(chart.AxisMinKelvin), 1e-9);
            Assert.AreEqual(1.0, chart.NormalizedY(chart.AxisMaxKelvin), 1e-9);
            Assert.AreEqual(5.0, chart.AxisSpanSeconds, 1e-9,
                "the opening window keeps a readable X scale");

            var flat = new TemperatureHistoryBuffer();
            Assert.IsTrue(flat.Feed(Readout("trial-b", 0, 1.1f)));
            var flatChart = TemperatureChartModel.From(flat);
            Assert.AreEqual(TrialGlassLayout.MinimumChartSpanKelvin,
                flatChart.AxisMaxKelvin - flatChart.AxisMinKelvin, 1e-9,
                "a single sample is centred in the declared minimum span");
            Assert.AreEqual(0.5, flatChart.NormalizedY(flat.SampleAt(0).Kelvin), 1e-9,
                "the single sample sits in the middle of that span");
        }

        static TemperatureHistoryBuffer History(float speed)
        {
            var buffer = new TemperatureHistoryBuffer();
            Assert.IsTrue(buffer.Feed(Readout("trial-a", 0, speed)));
            Assert.IsTrue(buffer.Feed(Readout("trial-a", 20, speed)));
            return buffer;
        }

        static TemperatureReadoutSnapshot Readout(string trialId, int tick, float speed)
        {
            var measurement = TemperatureMeasurementFace.Measure(
                "measurement-" + tick, ScalePointBalls(speed),
                new MeasurementWindow("window-" + tick, 0, tick,
                    tick * FrozenWriterConfiguration.FixedDeltaTimeSeconds, tick + 1),
                initialTK: 300.0);
            return new TemperatureReadoutSnapshot(trialId, tick, "view-" + tick, measurement,
                new TemperatureEnergyLedger(1.0, 1.0, 1.0, 0.0), 0);
        }

        static TemperatureReadoutSnapshot NotApplicable(string trialId, int tick)
        {
            var balls = new List<PublishedBall>
            {
                new("ball-000", Vector3.zero, Vector3.one, 1f, 0.05f, "free")
            };
            var measurement = TemperatureMeasurementFace.Measure("measurement-na", balls,
                new MeasurementWindow("window-na", 0, tick,
                    tick * FrozenWriterConfiguration.FixedDeltaTimeSeconds, tick + 1),
                initialTK: null);
            return new TemperatureReadoutSnapshot(trialId, tick, "view-na", measurement,
                new TemperatureEnergyLedger(1.0, 1.0, 1.0, 0.0), 0);
        }

        static IReadOnlyList<PublishedBall> ScalePointBalls(float speed)
        {
            var directions = new[]
            {
                new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f),
                new Vector3(0f, 0f, 1f)
            };
            var balls = new List<PublishedBall>();
            for (var index = 0; index < 12; index++)
            {
                var sign = index % 2 == 0 ? 1f : -1f;
                balls.Add(new PublishedBall("ball-" + index.ToString("D2"), Vector3.zero,
                    directions[(index / 2) % 3] * (sign * speed), 1f, 0.05f, "free"));
            }
            return balls;
        }
    }
}
