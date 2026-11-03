using System;
using System.Collections.Generic;
using System.Globalization;
using Amiss.Shell.Simulation;

namespace Amiss.Shell.UI
{
    /// <summary>
    /// Everything the wall glass shows while the wall set is visible: the
    /// producer's three-scale temperature readout and one temperature-over-time
    /// graph, and nothing else. The hub, the room scan, the settings, and every
    /// shell control stay on their own surface, so this presentation has no
    /// title, status, safety, progress, Trial, card, tab, subtitle, control,
    /// footer, energy, bat, or profile element to render.
    ///
    /// The readout copies the producer's display tuple; the C and F figures are
    /// the tuple's own integers, never a second conversion. When the readout is
    /// not applicable or absent, the block carries the readout's existing state
    /// sentence and the graph keeps its axes only, with no invented number.
    /// </summary>
    public sealed class TrialGlassPresentation
    {
        public const string ReadoutElement = "temperature.readout";
        public const string ChartElement = "temperature.chart";

        /// <summary>The glass carries these two elements, in this order, and no other.</summary>
        public static readonly IReadOnlyList<string> Elements =
            Array.AsReadOnly(new[] { ReadoutElement, ChartElement });

        public bool WallSetVisible { get; init; }
        public bool HasValues { get; init; }
        public string KelvinValue { get; init; } = "";
        public string CelsiusValue { get; init; } = "";
        public string FahrenheitValue { get; init; } = "";

        /// <summary>
        /// The readout's own not-applicable or unavailable sentence, shown
        /// where the digits would be. Empty while a display tuple exists.
        /// </summary>
        public string StateLine { get; init; } = "";

        public TemperatureChartModel Chart { get; init; } = TemperatureChartModel.Empty;

        /// <summary>
        /// The buffer the chart draws from. A view reads it in place so the
        /// drawing path copies no samples and allocates nothing per sample.
        /// </summary>
        public TemperatureHistoryBuffer History { get; init; }

        public string SecondaryLine => HasValues
            ? CelsiusValue + " °C   ·   " + FahrenheitValue + " °F"
            : "";

        public static TrialGlassPresentation Build(ShellSessionState state,
            TemperatureReadoutSnapshot readout, TemperatureHistoryBuffer history)
        {
            var measurement = readout?.Measurement;
            var tuple = readout?.Display;
            var applicable = measurement != null && measurement.Applicable && tuple != null;
            return new TrialGlassPresentation
            {
                WallSetVisible = EpisodeLaunchGate.WallSetVisible(state),
                HasValues = applicable,
                KelvinValue = applicable ? Text(tuple.Kelvin) : "",
                CelsiusValue = applicable ? Text(tuple.Celsius) : "",
                FahrenheitValue = applicable ? Text(tuple.Fahrenheit) : "",
                StateLine = applicable ? "" : TemperatureReadoutProjection.TemperatureLine(measurement),
                Chart = TemperatureChartModel.From(history),
                History = history
            };
        }

        /// <summary>
        /// Recomposes every visible value, so the glass repaints only when a
        /// number or a drawn sample actually changed.
        /// </summary>
        public static string Key(TrialGlassPresentation presentation)
        {
            if (presentation == null)
                return "";
            return presentation.WallSetVisible + "\n" + presentation.KelvinValue + "\n" +
                   presentation.CelsiusValue + "\n" + presentation.FahrenheitValue + "\n" +
                   presentation.StateLine + "\n" + presentation.Chart.Key;
        }

        static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The wall glass graph belongs to the live Trial. A running Trial appends
    /// its producer samples; once no Trial runs the graph is emptied, so a
    /// stopped Trial's line cannot survive into the pre-trial glass. The
    /// readout follows the same runtime state and turns back into its
    /// not-applicable sentence on its own.
    /// </summary>
    public static class TrialGlassFeed
    {
        public static void Sync(TemperatureHistoryBuffer history, bool trialRunning,
            TemperatureReadoutSnapshot readout)
        {
            if (history == null)
                return;
            if (trialRunning)
                history.Feed(readout);
            else if (!history.IsEmpty)
                history.Reset();
        }
    }

    /// <summary>
    /// The declared axis window of the temperature-over-time graph. Kelvin is
    /// the only Y axis; X is trial elapsed time from zero.
    /// </summary>
    public sealed class TemperatureChartModel
    {
        public static readonly TemperatureChartModel Empty = new(0, 0, 0.0, 0.0, 0.0);

        TemperatureChartModel(int count, long revision, double axisMinKelvin,
            double axisMaxKelvin, double axisSpanSeconds)
        {
            Count = count;
            Revision = revision;
            AxisMinKelvin = axisMinKelvin;
            AxisMaxKelvin = axisMaxKelvin;
            AxisSpanSeconds = axisSpanSeconds;
            Key = count.ToString(CultureInfo.InvariantCulture) + "/" +
                  revision.ToString(CultureInfo.InvariantCulture) + "/" +
                  axisMinKelvin.ToString("R", CultureInfo.InvariantCulture) + "/" +
                  axisMaxKelvin.ToString("R", CultureInfo.InvariantCulture) + "/" +
                  axisSpanSeconds.ToString("R", CultureInfo.InvariantCulture);
        }

        public int Count { get; }
        public long Revision { get; }
        public bool HasSamples => Count > 0;
        public double AxisMinKelvin { get; }
        public double AxisMaxKelvin { get; }
        public double AxisSpanSeconds { get; }

        /// <summary>Stable identity of everything the drawn frame depends on.</summary>
        public string Key { get; }

        /// <summary>The chart of one trial's buffer; axes only while it is empty.</summary>
        public static TemperatureChartModel From(TemperatureHistoryBuffer history)
        {
            if (history == null || history.IsEmpty)
                return Empty;

            // Y rule: the exact buffered minimum and maximum, widened to the
            // declared minimum span only when the trace is narrower than it.
            var min = history.MinKelvin;
            var max = history.MaxKelvin;
            if (max - min < TrialGlassLayout.MinimumChartSpanKelvin)
            {
                var center = (min + max) * 0.5;
                min = center - TrialGlassLayout.MinimumChartSpanKelvin * 0.5;
                max = center + TrialGlassLayout.MinimumChartSpanKelvin * 0.5;
            }

            // X rule: trial elapsed time from zero, at least the declared
            // opening seconds so the first samples keep a readable scale.
            var span = Math.Max(history.LastElapsedSeconds,
                TrialGlassLayout.MinimumChartSpanSeconds);
            return new TemperatureChartModel(history.Count, history.Revision, min, max, span);
        }

        /// <summary>Horizontal plot position, 0 at trial start and 1 at the axis end.</summary>
        public double NormalizedX(double elapsedSeconds)
        {
            if (AxisSpanSeconds <= 0.0)
                return 0.0;
            return Clamp01(elapsedSeconds / AxisSpanSeconds);
        }

        /// <summary>Vertical plot position, 0 at the axis minimum and 1 at its maximum.</summary>
        public double NormalizedY(double kelvin)
        {
            var span = AxisMaxKelvin - AxisMinKelvin;
            if (span <= 0.0)
                return 0.0;
            return Clamp01((kelvin - AxisMinKelvin) / span);
        }

        public string TopLabel => HasSamples ? Number(AxisMaxKelvin) + " K" : "";
        public string BottomLabel => HasSamples ? Number(AxisMinKelvin) + " K" : "";
        public string StartLabel => "0 s";
        public string EndLabel => HasSamples ? Number(AxisSpanSeconds) + " s" : "";

        static double Clamp01(double value) => value < 0.0 ? 0.0 : value > 1.0 ? 1.0 : value;

        static string Number(double value) =>
            value.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
