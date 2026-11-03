using System;

namespace Amiss.Shell.UI
{
    /// <summary>
    /// The one declared geometry of the fixed-slot TV page (design 05 section
    /// 4.2). Slot ids, order, and ownership live in
    /// <see cref="CorePanelController"/>; this type owns only the budgets
    /// inside that page: the page size, the declared font size of every slot,
    /// how many text lines each slot may occupy, and the heights that follow
    /// from them.
    ///
    /// A slot height is derived from its line budget, never typed by hand, and
    /// <see cref="ColumnHeight"/> must fit <see cref="ContentHeight"/>.
    /// <see cref="Fits"/> is the single check that the whole page holds its
    /// text; the presentation never shrinks, clips, or silently truncates a
    /// slot to hide an overflow.
    /// </summary>
    public static class PanelLayout
    {
        /// <summary>Logical page size of the screen surface (1 logical px = 1 mm).</summary>
        public const float PageWidth = 1450f;
        public const float PageHeight = 820f;

        public const float EdgeInset = 8f;
        public const float HorizontalInset = 6f;
        public const float VerticalInset = 8f;
        public const float Spacing = 1f;

        /// <summary>
        /// LiberationSans SDF line height over its point size (98.8916 / 86 in
        /// the shipped font asset). Text Mesh Pro advances one line per font
        /// size times this factor.
        /// </summary>
        public const float LineHeightFactor = 1.15f;

        public const float TitleFontSize = 36f;
        public const float StatusFontSize = 22f;
        public const float ReadoutFontSize = 22f;
        public const float CardFontSize = 20f;
        public const float ChartFontSize = 16f;
        public const float FooterFontSize = 15f;
        public const float ButtonFontSize = 22f;

        /// <summary>Width available to slot text after the page insets.</summary>
        public const float ContentWidth = PageWidth - 2f * HorizontalInset;

        /// <summary>Height available to the slot column after the page insets.</summary>
        public const float ContentHeight = PageHeight - 2f * VerticalInset;

        /// <summary>Width of the readout column inside the shared readout band.</summary>
        public const float ReadoutColumnWidth = 0.72f * ContentWidth;

        /// <summary>Width of the stage progress column inside the same band.</summary>
        public const float ProgressColumnWidth = ContentWidth - ReadoutColumnWidth - ReadoutGap;

        public const float ReadoutGap = 10f;

        public const int TitleLines = 1;
        public const int StatusLines = 3;
        public const int SafetyLines = 2;
        public const int ReadoutLines = 8;
        public const int ProgressLines = 5;
        public const int TrialLines = 3;
        public const int CardLines = 2;
        public const int ChartStatusLines = 1;
        public const int SubtitleLines = 2;
        public const int FooterLines = 2;

        /// <summary>Fixed-height control rows; buttons are not text slots.</summary>
        public const float TabsRowHeight = 34f;
        public const float ControlsRowHeight = 34f;

        /// <summary>
        /// Cards the page budget carries at the declared font sizes. A third
        /// installed declaration needs a paged card area, which is a contract
        /// change; <see cref="Fits"/> reports the overflow instead of shrinking
        /// the layout.
        /// </summary>
        public const int MaxCards = 3;

        public static float LineHeight(float fontSize) => fontSize * LineHeightFactor;

        public static float SlotHeight(float fontSize, int lines) => lines * LineHeight(fontSize);

        /// <summary>One library card row: question plus availability line.</summary>
        public static float CardHeight => SlotHeight(CardFontSize, CardLines);

        /// <summary>
        /// Height of one slot row. The readout and progress slots share one
        /// band and are laid out side by side, so the band takes the taller of
        /// the two and <see cref="PanelSlot.Progress"/> itself costs nothing.
        /// The four chart tabs and the active chart line split their row
        /// horizontally, so the row takes the taller of the two.
        /// </summary>
        public static float Height(PanelSlot slot, int cardCount = 1)
        {
            switch (slot)
            {
                case PanelSlot.Title: return SlotHeight(TitleFontSize, TitleLines);
                case PanelSlot.Status: return SlotHeight(StatusFontSize, StatusLines);
                case PanelSlot.Safety: return SlotHeight(ReadoutFontSize, SafetyLines);
                case PanelSlot.Readout:
                    return Math.Max(SlotHeight(ReadoutFontSize, ReadoutLines),
                        SlotHeight(ReadoutFontSize, ProgressLines));
                case PanelSlot.Progress: return 0f;
                case PanelSlot.Trial: return SlotHeight(ReadoutFontSize, TrialLines);
                case PanelSlot.ChartStatus: return SlotHeight(ChartFontSize, ChartStatusLines);
                case PanelSlot.Subtitle: return SlotHeight(CardFontSize, SubtitleLines);
                case PanelSlot.Footer: return SlotHeight(FooterFontSize, FooterLines);
                case PanelSlot.Tabs: return TabsRowHeight;
                case PanelSlot.Controls: return ControlsRowHeight;
                case PanelSlot.Cards: return CardHeight * Math.Max(0, cardCount);
                default: throw new ArgumentOutOfRangeException(nameof(slot));
            }
        }

        /// <summary>
        /// Rows of the fixed column in render order. The order follows the
        /// approved wireframe of design 05 section 4.2; the readout and stage
        /// progress slots share one band, and the four chart-slot tabs sit on
        /// the chart status row, so neither adds a row of its own.
        /// </summary>
        public static readonly PanelSlot[] Column =
        {
            PanelSlot.Title,
            PanelSlot.Status,
            PanelSlot.Safety,
            PanelSlot.Readout,
            PanelSlot.Trial,
            PanelSlot.Cards,
            PanelSlot.Tabs,
            PanelSlot.ChartStatus,
            PanelSlot.Subtitle,
            PanelSlot.Controls,
            PanelSlot.Footer
        };

        /// <summary>
        /// Total height of the column with <paramref name="cardCount"/> cards,
        /// including inter-row spacing.
        /// </summary>
        public static float ColumnHeight(int cardCount)
        {
            var total = 0f;
            foreach (var slot in Column)
                total += Height(slot, cardCount);
            return total + Spacing * (Column.Length - 1);
        }

        /// <summary>True when the whole column fits the declared page.</summary>
        public static bool Fits(int cardCount) =>
            cardCount >= 0 && cardCount <= MaxCards && ColumnHeight(cardCount) <= ContentHeight;

        /// <summary>Pixels left over on the page; negative means an overflow.</summary>
        public static float Margin(int cardCount) => ContentHeight - ColumnHeight(cardCount);

        /// <summary>
        /// Worst-case lines a column can hold at <paramref name="width"/>, used
        /// by the layout budget check for the side-by-side band.
        /// </summary>
        public static int LineBudget(PanelSlot slot)
        {
            switch (slot)
            {
                case PanelSlot.Title: return TitleLines;
                case PanelSlot.Status: return StatusLines;
                case PanelSlot.Safety: return SafetyLines;
                case PanelSlot.Readout: return ReadoutLines;
                case PanelSlot.Progress: return ProgressLines;
                case PanelSlot.Trial: return TrialLines;
                case PanelSlot.Cards: return CardLines;
                case PanelSlot.ChartStatus: return ChartStatusLines;
                case PanelSlot.Subtitle: return SubtitleLines;
                case PanelSlot.Footer: return FooterLines;
                default: return 0;
            }
        }

        /// <summary>
        /// Width at the right of the chart row reserved for the active chart
        /// line, so the four slot tabs and the chart status share one band.
        /// </summary>
        public const float ChartStatusWidth = 0.32f * ContentWidth;

        /// <summary>Slot text width; the band slots split the row.</summary>
        public static float SlotWidth(PanelSlot slot) => slot switch
        {
            PanelSlot.Readout => ReadoutColumnWidth,
            PanelSlot.Progress => ProgressColumnWidth,
            PanelSlot.ChartStatus => ChartStatusWidth,
            _ => ContentWidth
        };
    }

    /// <summary>Rows of the fixed panel column, in render order.</summary>
    public enum PanelSlot
    {
        Title,
        Status,
        Safety,
        Readout,
        Progress,
        Trial,
        Cards,
        Tabs,
        ChartStatus,
        Subtitle,
        Controls,
        Footer
    }
}
