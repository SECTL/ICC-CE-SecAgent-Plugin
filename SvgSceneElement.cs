using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Ink_Canvas.SecAgent.Plugin;

/// <summary>
/// One independently selectable item from the editable scene embedded in a hand-drawn SVG.
/// The host treats this as an image-like canvas item, so it reuses its existing move, rotate,
/// proportional-scale, delete and selection-overlay behaviour.
/// </summary>
public sealed class SvgSceneElement : Border
{
    private const double HitPadding = 4;
    private readonly double _scale;

    public string SerializedElement { get; private set; }
    public string SceneKind { get; }

    public SvgSceneElement(JsonElement element, double scale = 1)
    {
        SerializedElement = element.GetRawText();
        _scale = Math.Max(0.05, scale);
        SceneKind = ReadString(element, "kind", "text");
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Focusable = false;
        ClipToBounds = false;
        Build(element, _scale);
    }

    public static SvgSceneElement FromSerializedElement(string serializedElement, double scale = 1)
    {
        using var document = JsonDocument.Parse(serializedElement);
        return new SvgSceneElement(document.RootElement.Clone(), scale);
    }

    /// <summary>
    /// Tests the actual rendered path rather than the Border's full selection bounds.
    /// This lets the host distinguish point/stroke erasing from rectangular geometry erasing.
    /// </summary>
    public bool HitTestLocalPoint(Point point, double tolerance = 4)
    {
        if (Child is Rectangle || SceneKind == "svg")
            return new Rect(0, 0, Width, Height).Contains(point);

        var geometry = GetRenderedGeometry(tolerance);
        return geometry?.FillContains(point) == true;
    }

    /// <summary>
    /// Tests whether the actual rendered path intersects a local eraser rectangle.
    /// </summary>
    public bool IntersectsLocalRect(Rect rectangle, double tolerance = 4)
    {
        if (rectangle.IsEmpty) return false;
        if (Child is Rectangle || SceneKind == "svg")
        {
            var bounds = new Rect(0, 0, Width, Height);
            bounds.Inflate(tolerance, tolerance);
            return bounds.IntersectsWith(rectangle);
        }

        var geometry = GetRenderedGeometry(tolerance);
        if (geometry is null) return false;
        try
        {
            var intersection = Geometry.Combine(geometry, new RectangleGeometry(rectangle), GeometryCombineMode.Intersect, null);
            return intersection.GetArea() > 0.01;
        }
        catch
        {
            var bounds = geometry.Bounds;
            bounds.Inflate(tolerance, tolerance);
            return bounds.IntersectsWith(rectangle);
        }
    }

    /// <summary>
    /// Subtracts an eraser rectangle from the actual path geometry. The host uses this for
    /// EraseByPoint; EraseByStroke removes the whole SvgSceneElement instead.
    /// </summary>
    public bool EraseLocalRect(Rect rectangle, double tolerance = 4)
    {
        if (SceneKind == "svg")
        {
            if (rectangle.IsEmpty || !new Rect(0, 0, Width, Height).IntersectsWith(rectangle)) return false;
            Child = null;
            SerializeEmptyElement();
            return true;
        }
        if (Child is Rectangle rectangleShape)
        {
            if (rectangle.IsEmpty) return false;
            var bounds = new Rect(0, 0, Width, Height);
            if (!bounds.IntersectsWith(rectangle)) return false;

            try
            {
                Geometry source = new RectangleGeometry(bounds);
                var originalFill = rectangleShape.Fill;
                var originalStroke = rectangleShape.Stroke;
                var strokeWidth = Math.Max(0, rectangleShape.StrokeThickness);
                if (originalStroke is not null && strokeWidth > 0)
                {
                    var outline = source.GetWidenedPathGeometry(new Pen(originalStroke, strokeWidth), 0.1, ToleranceType.Absolute);
                    source = Geometry.Combine(source, outline, GeometryCombineMode.Union, null);
                }

                var remaining = Geometry.Combine(source, new RectangleGeometry(rectangle), GeometryCombineMode.Exclude, null);
                if (remaining is null || remaining.Bounds.IsEmpty || GetGeometryArea(remaining) <= 0.01)
                {
                    Child = null;
                    SerializeEmptyElement();
                    return true;
                }

                var convertedPath = new Path
                {
                    Data = remaining,
                    Fill = originalFill ?? originalStroke,
                    Stroke = null,
                    StrokeThickness = 0,
                    IsHitTestVisible = false
                };
                Child = convertedPath;
                SerializePathGeometry(remaining, originalFill ?? originalStroke, true);
                return true;
            }
            catch
            {
                return false;
            }
        }
        if (rectangle.IsEmpty || Child is not Path path || path.Data is null) return false;

        try
        {
            // Path data from the renderer is stored in source coordinates while the
            // visual Path is scaled to the inserted scene size. Do the subtraction in
            // rendered/local coordinates, then convert the remaining geometry back to
            // source coordinates before serializing it. The old code mixed these two
            // spaces, so an eraser could visibly cross a glyph without changing it.
            var renderTransform = path.RenderTransform;
            Matrix? inverseMatrix = null;
            if (renderTransform is not null && !renderTransform.Value.IsIdentity)
            {
                var matrix = renderTransform.Value;
                if (!matrix.HasInverse) return false;
                matrix.Invert();
                inverseMatrix = matrix;
            }

            var source = path.Data.Clone();
            if (renderTransform is not null && !renderTransform.Value.IsIdentity)
                source.Transform = renderTransform;

            var wasStroke = path.Stroke is not null && path.StrokeThickness > 0;
            if (wasStroke)
            {
                source = source.GetWidenedPathGeometry(
                    new Pen(path.Stroke, path.StrokeThickness),
                    0.1,
                    ToleranceType.Absolute);
            }

            var remainingRendered = Geometry.Combine(
                source,
                new RectangleGeometry(rectangle),
                GeometryCombineMode.Exclude,
                null);
            if (remainingRendered is null || remainingRendered.Bounds.IsEmpty || GetGeometryArea(remainingRendered) <= 0.01)
            {
                Child = null;
                SerializeEmptyElement();
                return true;
            }

            var remaining = remainingRendered;
            if (inverseMatrix.HasValue)
            {
                remaining = remainingRendered.Clone();
                remaining.Transform = new MatrixTransform(inverseMatrix.Value);
            }

            path.Data = remaining;
            if (wasStroke)
            {
                path.Fill = path.Stroke;
                path.Stroke = null;
                path.StrokeThickness = 0;
            }
            SerializePathGeometry(remaining, wasStroke ? path.Fill : null, wasStroke);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool HasVisualContent
        => (Child is Path path && path.Data is not null && !path.Data.Bounds.IsEmpty)
           || Child is Rectangle
           || Child is WebBrowser;

    private static double GetGeometryArea(Geometry geometry)
    {
        try { return geometry.GetArea(); }
        catch { return geometry.Bounds.Width * geometry.Bounds.Height; }
    }

    private void SerializePathGeometry(Geometry geometry, Brush fill, bool convertedStroke)
    {
        try
        {
            if (JsonNode.Parse(SerializedElement) is not JsonObject json) return;
            var serializedGeometry = geometry.Clone();
            if (SceneKind == "rect" && Math.Abs(_scale - 1) > 0.0001)
                serializedGeometry.Transform = new ScaleTransform(1 / _scale, 1 / _scale);
            json["d"] = serializedGeometry.ToString(CultureInfo.InvariantCulture);
            if (convertedStroke)
            {
                json["kind"] = "path";
                json["fill"] = ColorString(fill, "#4c463f");
                json["stroke"] = "none";
                json["strokeWidth"] = 0;
            }
            SerializedElement = json.ToJsonString();
        }
        catch
        {
            // The visual edit remains valid even if an optional save metadata update fails.
        }
    }

    private void SerializeEmptyElement()
    {
        try
        {
            if (JsonNode.Parse(SerializedElement) is not JsonObject json) return;
            if (json["kind"]?.GetValue<string>() == "svg") json["svg"] = "";
            else json["d"] = "";
            SerializedElement = json.ToJsonString();
        }
        catch
        {
        }
    }

    private static string ColorString(Brush brush, string fallback)
        => brush is SolidColorBrush solid ? solid.Color.ToString() : fallback;

    private Geometry GetRenderedGeometry(double tolerance)
    {
        if (Child is not Path path || path.Data is null) return null;
        var geometry = path.Data.Clone();
        if (path.RenderTransform is not null && !path.RenderTransform.Value.IsIdentity)
            geometry.Transform = path.RenderTransform;

        if (path.Stroke is not null && path.StrokeThickness > 0)
        {
            var pen = new Pen(path.Stroke, Math.Max(0.1, path.StrokeThickness + tolerance * 2));
            return geometry.GetWidenedPathGeometry(pen, 0.1, ToleranceType.Absolute);
        }
        return geometry;
    }

    private void Build(JsonElement element, double scale)
    {
        switch (SceneKind)
        {
            case "path":
                BuildPath(element, scale);
                break;
            case "line":
                BuildLine(element, scale);
                break;
            case "rect":
                BuildRectangle(element, scale);
                break;
            case "svg":
                BuildSvg(element, scale);
                break;
            default:
                BuildText(element, scale);
                break;
        }
    }

    private void BuildText(JsonElement element, double scale)
    {
        Width = Math.Max(12, ReadDouble(element, "width", 220) * scale);
        Height = Math.Max(18, ReadDouble(element, "height", 32) * scale);
        var block = new TextBlock
        {
            Text = ReadString(element, "text", ""),
            Foreground = Brush(ReadString(element, "color", "#302c28"), "#302c28"),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = Math.Max(10, ReadDouble(element, "fontSize", 21) * scale),
            FontWeight = ReadDouble(element, "fontWeight", 400) >= 600 ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false
        };
        Child = block;
    }

    private void BuildPath(JsonElement element, double scale)
    {
        Width = Math.Max(8, ReadDouble(element, "width", 100) * scale);
        Height = Math.Max(8, ReadDouble(element, "height", 32) * scale);
        var data = ReadString(element, "d", "");
        if (string.IsNullOrWhiteSpace(data)) return;

        Geometry geometry;
        try
        {
            geometry = Geometry.Parse(data);
        }
        catch
        {
            return;
        }

        var path = new Path
        {
            Data = geometry,
            Fill = string.Equals(ReadString(element, "fill", "none"), "none", StringComparison.OrdinalIgnoreCase) ? null : Brush(ReadString(element, "fill", "#302c28"), "#302c28"),
            Stroke = string.Equals(ReadString(element, "stroke", "none"), "none", StringComparison.OrdinalIgnoreCase) ? null : Brush(ReadString(element, "stroke", "#4c463f"), "#4c463f"),
            StrokeThickness = Math.Max(0, ReadDouble(element, "strokeWidth", 0) * scale),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransform = new ScaleTransform(scale, scale),
            IsHitTestVisible = false
        };
        Child = path;
    }

    private void BuildSvg(JsonElement element, double scale)
    {
        Width = Math.Max(40, ReadDouble(element, "width", 800) * scale);
        Height = Math.Max(40, ReadDouble(element, "height", 500) * scale);
        var svg = ReadString(element, "svg", "");
        if (string.IsNullOrWhiteSpace(svg)) return;

        var browser = new WebBrowser
        {
            IsHitTestVisible = false,
            Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Child = browser;
        Loaded += (_, _) => browser.NavigateToString(WrapSvgDocument(svg));
    }

    private static string WrapSvgDocument(string svg)
        => "<!doctype html><html><head><meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"></head>"
         + "<body style=\"margin:0;padding:0;overflow:hidden;background:transparent;\">"
         + svg
         + "</body></html>";

    private void BuildLine(JsonElement element, double scale)
    {
        var x1 = ReadDouble(element, "x1", 0) * scale;
        var y1 = ReadDouble(element, "y1", 0) * scale;
        var x2 = ReadDouble(element, "x2", 100) * scale;
        var y2 = ReadDouble(element, "y2", 0) * scale;
        var width = Math.Max(8, Math.Abs(x2 - x1) + HitPadding * 2);
        var height = Math.Max(8, Math.Abs(y2 - y1) + HitPadding * 2);
        Width = width;
        Height = height;
        var start = new Point(x1 <= x2 ? HitPadding : width - HitPadding, y1 <= y2 ? HitPadding : height - HitPadding);
        var end = new Point(x1 <= x2 ? width - HitPadding : HitPadding, y1 <= y2 ? height - HitPadding : HitPadding);
        Child = new Path
        {
            Data = new LineGeometry(start, end),
            Stroke = Brush(ReadString(element, "stroke", "#4c463f"), "#4c463f"),
            StrokeThickness = Math.Max(0.8, ReadDouble(element, "strokeWidth", 1.7) * scale),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
    }

    private void BuildRectangle(JsonElement element, double scale)
    {
        Width = Math.Max(1, ReadDouble(element, "width", 100) * scale);
        Height = Math.Max(1, ReadDouble(element, "height", 40) * scale);
        var stroke = ReadString(element, "stroke", "none");
        Child = new Rectangle
        {
            Width = Width,
            Height = Height,
            Stroke = string.Equals(stroke, "none", StringComparison.OrdinalIgnoreCase) ? null : Brush(stroke, "#4c463f"),
            StrokeThickness = Math.Max(0, ReadDouble(element, "strokeWidth", 1) * scale),
            Fill = string.Equals(ReadString(element, "fill", "none"), "none", StringComparison.OrdinalIgnoreCase) ? null : Brush(ReadString(element, "fill", "none"), "#ffffff"),
            IsHitTestVisible = false
        };
    }

    public static (double Left, double Top) ReadPosition(JsonElement element, double scale)
    {
        if (ReadString(element, "kind", "text") == "line")
        {
            var x1 = ReadDouble(element, "x1", 0);
            var x2 = ReadDouble(element, "x2", 0);
            var y1 = ReadDouble(element, "y1", 0);
            var y2 = ReadDouble(element, "y2", 0);
            return (Math.Min(x1, x2) * scale - HitPadding, Math.Min(y1, y2) * scale - HitPadding);
        }
        return (ReadDouble(element, "x", 0) * scale, ReadDouble(element, "y", 0) * scale);
    }

    private static double ReadDouble(JsonElement element, string name, double fallback)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var result) && double.IsFinite(result)
            ? result : fallback;

    private static string ReadString(JsonElement element, string name, string fallback)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;

    private static Brush Brush(string value, string fallback)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); }
        catch { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback)); }
    }
}
