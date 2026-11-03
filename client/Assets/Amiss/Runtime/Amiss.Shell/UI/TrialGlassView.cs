using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Amiss.Shell.UI
{
    /// <summary>
    /// Builds and fills the wall glass of <see cref="TrialGlassPresentation"/>:
    /// the large Kelvin digits, the Celsius/Fahrenheit line, and the
    /// temperature-over-time graph. It copies text out of the presentation and
    /// paints the graph's texture; it never composes a value.
    ///
    /// The view hangs under the same world-space canvas as the fixed column, so
    /// the glass keeps the one render path it already uses. Its root object is
    /// switched off whenever the wall set is not the active surface.
    /// </summary>
    public sealed class TrialGlassView
    {
        static readonly Color Background = new Color(0.03f, 0.035f, 0.045f, 1f);
        static readonly Color Text = new Color(0.92f, 0.94f, 0.97f, 1f);
        static readonly Color Muted = new Color(0.62f, 0.66f, 0.72f, 1f);
        static readonly Color Accent = new Color(0.361f, 0.882f, 0.902f, 1f);

        static readonly Color32 PlotBackground = new(8, 10, 14, 255);
        static readonly Color32 PlotFrame = new(58, 66, 76, 255);
        static readonly Color32 PlotLine = new(92, 225, 230, 255);

        GameObject root;
        GameObject kelvinRow;
        GameObject secondaryRow;
        GameObject stateRow;
        GameObject chartRow;
        TMP_Text kelvinValue;
        TMP_Text kelvinUnit;
        TMP_Text secondary;
        TMP_Text stateLine;
        TMP_Text axisTop;
        TMP_Text axisBottom;
        TMP_Text axisStart;
        TMP_Text axisEnd;
        Texture2D texture;
        Color32[] pixels;
        string paintedKey = "";

        public RectTransform Root => root != null ? root.GetComponent<RectTransform>() : null;

        public TMP_Text KelvinDigits => kelvinValue;
        public TMP_Text SecondaryLine => secondary;
        public TMP_Text StateText => stateLine;

        /// <summary>The glass elements actually built, in render order.</summary>
        public System.Collections.Generic.IReadOnlyList<string> ElementIds
        {
            get
            {
                var ids = new System.Collections.Generic.List<string>(2);
                if (kelvinRow != null || stateRow != null)
                    ids.Add(TrialGlassPresentation.ReadoutElement);
                if (chartRow != null)
                    ids.Add(TrialGlassPresentation.ChartElement);
                return ids;
            }
        }

        public void Build(Transform parent)
        {
            var canvas = parent.GetComponentInChildren<Canvas>();
            if (canvas == null)
            {
                var canvasGo = new GameObject("TrialGlassCanvas", typeof(Canvas), typeof(RectTransform));
                canvasGo.transform.SetParent(parent, false);
                canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
            }

            root = new GameObject("TrialGlassRoot", typeof(RectTransform));
            root.transform.SetParent(canvas.transform, false);
            var rect = root.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(TrialGlassLayout.Inset, TrialGlassLayout.Inset);
            rect.offsetMax = new Vector2(-TrialGlassLayout.Inset, -TrialGlassLayout.Inset);
            var layout = root.AddComponent<VerticalLayoutGroup>();
            layout.spacing = TrialGlassLayout.RowGap;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            kelvinRow = NewRow("TemperatureKelvin", root.transform,
                TrialGlassLayout.KelvinRowHeight, out var kelvinLayout);
            kelvinLayout.spacing = 14f;
            kelvinValue = NewText("KelvinValue", kelvinRow.transform,
                TrialGlassLayout.KelvinFontSize, FontStyles.Bold, Accent);
            kelvinValue.alignment = TextAlignmentOptions.BottomLeft;
            kelvinValue.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            kelvinUnit = NewText("KelvinUnit", kelvinRow.transform,
                TrialGlassLayout.KelvinUnitFontSize, FontStyles.Normal, Text);
            kelvinUnit.alignment = TextAlignmentOptions.BottomLeft;
            kelvinUnit.text = "K";

            secondaryRow = NewRow("TemperatureSecondary", root.transform,
                TrialGlassLayout.SecondaryRowHeight, out _);
            secondary = NewText("CelsiusFahrenheit", secondaryRow.transform,
                TrialGlassLayout.SecondaryFontSize, FontStyles.Normal, Text);

            stateRow = NewRow("TemperatureState", root.transform,
                TrialGlassLayout.StateRowHeight, out _);
            stateLine = NewText("ReadoutState", stateRow.transform,
                TrialGlassLayout.StateFontSize, FontStyles.Normal, Muted);

            BuildChart(root.transform);
            paintedKey = TemperatureChartModel.Empty.Key;
            SetVisible(false);
        }

        /// <summary>Shows or hides the whole glass; nothing else on the screen moves.</summary>
        public void SetVisible(bool visible)
        {
            if (root == null || root.activeSelf == visible)
                return;
            root.SetActive(visible);
        }

        /// <summary>Copies one presentation into the glass; no value is composed here.</summary>
        public void Render(TrialGlassPresentation presentation)
        {
            if (presentation == null || root == null)
                return;
            var shown = presentation.HasValues;
            kelvinRow.SetActive(shown);
            secondaryRow.SetActive(shown);
            stateRow.SetActive(!shown);
            kelvinValue.text = presentation.KelvinValue;
            kelvinUnit.gameObject.SetActive(shown);
            secondary.text = presentation.SecondaryLine;
            stateLine.text = presentation.StateLine;

            var chart = presentation.Chart ?? TemperatureChartModel.Empty;
            axisTop.text = chart.TopLabel;
            axisBottom.text = chart.BottomLabel;
            axisStart.text = chart.StartLabel;
            axisEnd.text = chart.EndLabel;
            if (chart.Key == paintedKey)
                return;
            paintedKey = chart.Key;
            Paint(presentation.History, chart);
        }

        void BuildChart(Transform parent)
        {
            chartRow = new GameObject("TemperatureChart", typeof(RectTransform));
            chartRow.transform.SetParent(parent, false);
            var element = chartRow.AddComponent<LayoutElement>();
            element.flexibleHeight = 1f;
            element.minHeight = 120f;

            var plotGo = new GameObject("TemperatureChartPlot",
                typeof(RectTransform), typeof(RawImage));
            plotGo.transform.SetParent(chartRow.transform, false);
            var plotRect = plotGo.GetComponent<RectTransform>();
            plotRect.anchorMin = Vector2.zero;
            plotRect.anchorMax = Vector2.one;
            plotRect.offsetMin = new Vector2(TrialGlassLayout.AxisLabelWidth,
                TrialGlassLayout.AxisBottomInset);
            plotRect.offsetMax = Vector2.zero;
            pixels = new Color32[TrialGlassLayout.ChartTextureWidth *
                                 TrialGlassLayout.ChartTextureHeight];
            texture = new Texture2D(TrialGlassLayout.ChartTextureWidth,
                TrialGlassLayout.ChartTextureHeight, TextureFormat.RGBA32, false)
            {
                name = "TemperatureChartTexture",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            var image = plotGo.GetComponent<RawImage>();
            image.texture = texture;
            image.raycastTarget = false;

            axisTop = AxisLabel("AxisMaximum", chartRow.transform, new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(0f, -2f), TextAlignmentOptions.TopLeft);
            axisBottom = AxisLabel("AxisMinimum", chartRow.transform, new Vector2(0f, 0f),
                new Vector2(0f, 0f), new Vector2(0f, TrialGlassLayout.AxisBottomInset),
                TextAlignmentOptions.BottomLeft);
            axisStart = AxisLabel("AxisStart", chartRow.transform, new Vector2(0f, 0f),
                new Vector2(0f, 0f),
                new Vector2(TrialGlassLayout.AxisLabelWidth, 0f), TextAlignmentOptions.BottomLeft);
            axisEnd = AxisLabel("AxisEnd", chartRow.transform, new Vector2(1f, 0f),
                new Vector2(1f, 0f), Vector2.zero, TextAlignmentOptions.BottomRight);

            Paint(null, TemperatureChartModel.Empty);
        }

        TMP_Text AxisLabel(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 position, TextAlignmentOptions alignment)
        {
            var text = NewText(name, parent, TrialGlassLayout.AxisLabelFontSize,
                FontStyles.Normal, Muted);
            text.alignment = alignment;
            var rect = text.rectTransform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = anchorMin;
            rect.anchoredPosition = position;
            rect.sizeDelta = name == "AxisStart" || name == "AxisEnd"
                ? new Vector2(180f, TrialGlassLayout.AxisLabelHeight)
                : new Vector2(TrialGlassLayout.AxisLabelWidth - 6f,
                    TrialGlassLayout.AxisLabelHeight);
            return text;
        }

        void Paint(TemperatureHistoryBuffer history, TemperatureChartModel chart)
        {
            var width = TrialGlassLayout.ChartTextureWidth;
            var height = TrialGlassLayout.ChartTextureHeight;
            for (var index = 0; index < pixels.Length; index++)
                pixels[index] = PlotBackground;

            var frame = TrialGlassLayout.ChartFramePixels;
            for (var x = 0; x < width; x++)
            {
                for (var edge = 0; edge < frame; edge++)
                {
                    pixels[edge * width + x] = PlotFrame;
                    pixels[(height - 1 - edge) * width + x] = PlotFrame;
                }
            }
            for (var y = 0; y < height; y++)
            {
                for (var edge = 0; edge < frame; edge++)
                {
                    pixels[y * width + edge] = PlotFrame;
                    pixels[y * width + width - 1 - edge] = PlotFrame;
                }
            }

            if (history != null && chart.HasSamples && history.Count > 1)
            {
                var plotWidth = width - 1 - 2 * frame;
                var plotHeight = height - 1 - 2 * frame;
                var previous = PlotPoint(history.SampleAt(0), chart, frame, plotWidth, plotHeight);
                for (var index = 1; index < history.Count; index++)
                {
                    var current = PlotPoint(history.SampleAt(index), chart, frame,
                        plotWidth, plotHeight);
                    DrawLine(previous, current, width);
                    previous = current;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false);
        }

        static Vector2Int PlotPoint(TemperatureHistorySample sample,
            TemperatureChartModel chart, int frame, int plotWidth, int plotHeight)
        {
            var x = frame + (int)Math.Round(chart.NormalizedX(sample.ElapsedSeconds) * plotWidth);
            var y = frame + (int)Math.Round(chart.NormalizedY(sample.Kelvin) * plotHeight);
            return new Vector2Int(x, y);
        }

        /// <summary>One-pixel polyline step in the plot's own frame, clamped to it.</summary>
        void DrawLine(Vector2Int from, Vector2Int to, int width)
        {
            var height = TrialGlassLayout.ChartTextureHeight;
            var dx = Math.Abs(to.x - from.x);
            var dy = Math.Abs(to.y - from.y);
            var stepX = from.x < to.x ? 1 : -1;
            var stepY = from.y < to.y ? 1 : -1;
            var error = dx - dy;
            var x = from.x;
            var y = from.y;
            while (true)
            {
                if (x >= 0 && x < width && y >= 0 && y < height)
                    pixels[y * width + x] = PlotLine;
                if (x == to.x && y == to.y)
                    return;
                var doubled = 2 * error;
                if (doubled > -dy)
                {
                    error -= dy;
                    x += stepX;
                }
                if (doubled < dx)
                {
                    error += dx;
                    y += stepY;
                }
            }
        }

        static GameObject NewRow(string name, Transform parent, float height,
            out HorizontalLayoutGroup layout)
        {
            var row = new GameObject(name, typeof(RectTransform), typeof(HorizontalLayoutGroup));
            row.transform.SetParent(parent, false);
            layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;
            var element = row.AddComponent<LayoutElement>();
            element.minHeight = height;
            element.preferredHeight = height;
            element.flexibleHeight = 0f;
            return row;
        }

        static TMP_Text NewText(string name, Transform parent, float size, FontStyles style,
            Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<TextMeshProUGUI>();
            text.fontSize = size;
            text.fontStyle = style;
            text.color = color;
            text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;
            return text;
        }
    }
}
