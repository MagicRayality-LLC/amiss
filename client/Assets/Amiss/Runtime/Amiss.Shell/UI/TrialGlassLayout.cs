namespace Amiss.Shell.UI
{
    /// <summary>
    /// Declared geometry of the trial glass: one three-scale temperature block
    /// and one temperature-over-time graph, on the same page as the fixed
    /// column (design 05 section 4.1 declares the 1.45 m × 0.82 m screen).
    ///
    /// Every number here is a presentation choice, not a measurement constant
    /// or a design clause: the K digits are sized to stay readable from across
    /// the room, the secondary line sits under them, and the graph takes every
    /// remaining page pixel. Colors come from the shelf's existing teal-on-dark
    /// family, so no new art asset is introduced.
    /// </summary>
    public static class TrialGlassLayout
    {
        public const float Inset = 48f;
        public const float PageWidth = PanelLayout.PageWidth;
        public const float PageHeight = PanelLayout.PageHeight;
        public const float ContentWidth = PageWidth - 2f * Inset;
        public const float ContentHeight = PageHeight - 2f * Inset;

        public const float KelvinFontSize = 170f;
        public const float KelvinUnitFontSize = 64f;
        public const float SecondaryFontSize = 54f;
        public const float StateFontSize = 30f;
        public const float AxisLabelFontSize = 20f;

        public const float KelvinRowHeight = 200f;
        public const float SecondaryRowHeight = 76f;
        public const float StateRowHeight = 80f;
        public const float RowGap = 8f;

        public const float AxisLabelWidth = 104f;
        public const float AxisLabelHeight = 24f;
        public const float AxisBottomInset = 26f;

        public const int ChartTextureWidth = 512;
        public const int ChartTextureHeight = 192;
        public const int ChartFramePixels = 2;

        /// <summary>
        /// Y axis rule: the axis spans the buffered minimum and maximum kelvin.
        /// A single sample, or a flat trace narrower than this declared window,
        /// is centred on ±half this window so the line stays visible. The axis
        /// never pads beyond the buffered values otherwise.
        /// </summary>
        public const double MinimumChartSpanKelvin = 1.0;

        /// <summary>
        /// X axis rule: trial elapsed time from zero. The first seconds of a
        /// Trial keep this declared window so the trace does not jump in scale.
        /// </summary>
        public const double MinimumChartSpanSeconds = 5.0;
    }
}
