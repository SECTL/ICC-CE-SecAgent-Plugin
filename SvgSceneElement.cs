using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    // Area erasing is accumulated as cheap clip rectangles during pointer movement.
    // The expensive path subtraction is materialized once when the gesture ends.
    private readonly List<Rect> _pendingEraseRectangles = new();
    private GeometryGroup _pendingEraseGeometry;

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
    /// Uses the rendered layout bounds as the hit-test unit. Each Markdown source line is
    /// already one scene element, so testing a glyph outline on every mouse move adds a
    /// large amount of WPF geometry work without improving the erase semantics.
    /// </summary>
    public bool HitTestLocalPoint(Point point, double tolerance = 4)
    {
        // Markdown rows are rendered as one TextBlock per editable scene item.  A row is
        // deliberately the erase unit (the same semantic unit as one pen stroke), so text
        // uses its local bounds for hit testing rather than requiring a glyph outline.
        if (UsesBoundsHitTest())
        {
            return GetRenderedBounds(tolerance).Contains(point);
        }

        var geometry = GetRenderedGeometry(tolerance);
        return geometry?.FillContains(point) == true;
    }

    /// <summary>
    /// Tests whether the actual rendered path intersects a local eraser rectangle.
    /// </summary>
    public bool IntersectsLocalRect(Rect rectangle, double tolerance = 4)
    {
        if (rectangle.IsEmpty) return false;
        if (UsesBoundsHitTest())
        {
            return GetRenderedBounds(tolerance).IntersectsWith(rectangle);
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
    /// Applies an area-eraser footprint. During a drag this only adds a clipping rectangle;
    /// the expensive path subtraction is deferred until the gesture is committed. This
    /// preserves rubber-eraser semantics while avoiding Geometry.Combine on every pointer
    /// move for large handwriting paths.
    /// </summary>
    public bool EraseLocalRect(Rect rectangle, double tolerance = 4)
    {
        if (rectangle.IsEmpty || !GetRenderedBounds(tolerance).IntersectsWith(rectangle)) return false;

        // Legacy kind=svg and TextBlock elements are treated as one native scene unit.
        // Generated Markdown text, rules and table borders use Path and receive true
        // partial erasing below.
        if (SceneKind == "svg" || Child is TextBlock)
        {
            Child = null;
            SerializeEmptyElement();
            return true;
        }

        if (Child is not Path && Child is not Rectangle) return false;

        // A footprint covering the remaining rendered bounds can be handled without any
        // geometry operation. Partial footprints are queued and clipped immediately.
        if (rectangle.Contains(GetRenderedBounds(0)))
        {
            _pendingEraseRectangles.Clear();
            _pendingEraseGeometry = null;
            Child = null;
            SerializeEmptyElement();
            return true;
        }

        var eraseRectangle = ToChildLocalRectangle(rectangle);
        if (eraseRectangle.IsEmpty) return false;

        // Pointer events can repeat the same footprint while the stylus is stationary or
        // while the host is catching up. Do not grow the clip tree for an already-covered
        // region in this gesture.
        if (_pendingEraseRectangles.Any(existing => existing.Contains(eraseRectangle)))
            return false;

        _pendingEraseRectangles.Add(eraseRectangle);
        _pendingEraseGeometry ??= new GeometryGroup { FillRule = FillRule.Nonzero };
        _pendingEraseGeometry.Children.Add(new RectangleGeometry(eraseRectangle));
        ApplyPendingEraseClip();
        return true;
    }

    /// <summary>
    /// Converts the cheap clip accumulated during an area-erase gesture into persistent
    /// path data. It is called once per touched element at pointer-up/history read time,
    /// not once per pointer move.
    /// </summary>
    public bool CommitPendingAreaErase()
    {
        if (_pendingEraseRectangles.Count == 0) return HasVisualContent;

        try
        {
            if (Child is Rectangle rectangleShape)
            {
                Geometry rectangleSource = new RectangleGeometry(new Rect(0, 0, Width, Height));
                var originalFill = rectangleShape.Fill;
                var originalStroke = rectangleShape.Stroke;
                var strokeWidth = Math.Max(0, rectangleShape.StrokeThickness);
                if (originalStroke is not null && strokeWidth > 0)
                {
                    var outline = rectangleSource.GetWidenedPathGeometry(new Pen(originalStroke, strokeWidth), 0.1, ToleranceType.Absolute);
                    rectangleSource = Geometry.Combine(rectangleSource, outline, GeometryCombineMode.Union, null);
                }

                var rectangleRemaining = Geometry.Combine(rectangleSource, _pendingEraseGeometry, GeometryCombineMode.Exclude, null);
                if (rectangleRemaining is null || rectangleRemaining.Bounds.IsEmpty || GetGeometryArea(rectangleRemaining) <= 0.01)
                {
                    ClearPendingErase();
                    Child = null;
                    SerializeEmptyElement();
                    return false;
                }

                Child = new Path
                {
                    Data = rectangleRemaining,
                    Fill = originalFill ?? originalStroke,
                    Stroke = null,
                    StrokeThickness = 0,
                    IsHitTestVisible = false
                };
                SerializePathGeometry(rectangleRemaining, originalFill ?? originalStroke, true);
                ClearPendingErase();
                return true;
            }

            if (Child is not Path path || path.Data is null)
            {
                ClearPendingErase();
                return HasVisualContent;
            }

            // Path data is stored in source coordinates while the visible Path can be
            // scaled through RenderTransform. Convert pending rectangles to rendered
            // coordinates before subtracting, then restore source coordinates.
            var renderTransform = path.RenderTransform;
            Matrix? inverseMatrix = null;
            var source = path.Data.Clone();
            if (renderTransform is not null && !renderTransform.Value.IsIdentity)
            {
                var matrix = renderTransform.Value;
                if (!matrix.HasInverse) return HasVisualContent;
                matrix.Invert();
                inverseMatrix = matrix;
                source.Transform = renderTransform;
            }

            var wasStroke = path.Stroke is not null && path.StrokeThickness > 0;
            if (wasStroke)
            {
                source = source.GetWidenedPathGeometry(
                    new Pen(path.Stroke, path.StrokeThickness),
                    0.1,
                    ToleranceType.Absolute);
            }

            Geometry eraseGeometry = _pendingEraseGeometry;
            if (renderTransform is not null && !renderTransform.Value.IsIdentity)
            {
                eraseGeometry = _pendingEraseGeometry.Clone();
                eraseGeometry.Transform = renderTransform;
            }

            var remainingRendered = Geometry.Combine(
                source,
                eraseGeometry,
                GeometryCombineMode.Exclude,
                null);
            if (remainingRendered is null || remainingRendered.Bounds.IsEmpty || GetGeometryArea(remainingRendered) <= 0.01)
            {
                ClearPendingErase();
                Child = null;
                SerializeEmptyElement();
                return false;
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
            ClearPendingErase();
            return true;
        }
        catch
        {
            // Keep the visual clip if materialization fails; a later history read can retry
            // without losing the user's visible erase result.
            return HasVisualContent;
        }
    }

    public bool HasVisualContent
        => (Child is Path path && path.Data is not null && !path.Data.Bounds.IsEmpty)
           || Child is Rectangle
           || (Child is Canvas canvas && canvas.Children.Count > 0)
           || (Child is TextBlock text && !string.IsNullOrWhiteSpace(text.Text));

    private bool UsesBoundsHitTest()
        => Child is Rectangle || Child is TextBlock || Child is Canvas
           || SceneKind == "path" || SceneKind == "line" || SceneKind == "rect" || SceneKind == "svg";

    private Rect ToChildLocalRectangle(Rect elementRectangle)
    {
        if (Child is not Path path || path.RenderTransform is null || path.RenderTransform.Value.IsIdentity)
            return elementRectangle;
        var matrix = path.RenderTransform.Value;
        if (!matrix.HasInverse) return Rect.Empty;
        matrix.Invert();
        return new MatrixTransform(matrix).TransformBounds(elementRectangle);
    }

    private void ApplyPendingEraseClip()
    {
        if (_pendingEraseGeometry is null) return;
        if (Child is Path path && path.Data is not null)
        {
            var bounds = path.Data.Bounds;
            if (path.Stroke is not null && path.StrokeThickness > 0)
                bounds.Inflate(path.StrokeThickness / 2, path.StrokeThickness / 2);
            path.Clip = new CombinedGeometry(
                GeometryCombineMode.Exclude,
                new RectangleGeometry(bounds),
                _pendingEraseGeometry);
        }
        else if (Child is Rectangle rectangle)
        {
            rectangle.Clip = new CombinedGeometry(
                GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, Width, Height)),
                _pendingEraseGeometry);
        }
    }

    private void ClearPendingErase()
    {
        _pendingEraseRectangles.Clear();
        _pendingEraseGeometry = null;
        if (Child is Path path) path.Clip = null;
        if (Child is Rectangle rectangle) rectangle.Clip = null;
    }

    private Rect GetRenderedBounds(double tolerance)
    {
        var bounds = new Rect(0, 0, Math.Max(1, Width), Math.Max(1, Height));
        var extra = Math.Max(0, tolerance);
        if (Child is Path path && path.Data is not null)
        {
            bounds = path.Data.Bounds;
            if (path.RenderTransform is not null && !path.RenderTransform.Value.IsIdentity)
                bounds = path.RenderTransform.TransformBounds(bounds);
            if (path.Stroke is not null && path.StrokeThickness > 0)
                extra += path.StrokeThickness / 2;
        }
        else if (Child is Rectangle rectangle && rectangle.Stroke is not null && rectangle.StrokeThickness > 0)
        {
            extra += rectangle.StrokeThickness / 2;
        }
        bounds.Inflate(extra, extra);
        return bounds;
    }

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
            var kind = json["kind"]?.GetValue<string>();
            if (string.Equals(kind, "svg", StringComparison.OrdinalIgnoreCase))
                json["svg"] = "";
            else if (string.Equals(kind, "text", StringComparison.OrdinalIgnoreCase) || json["text"] is not null)
                json["text"] = "";
            else
                json["d"] = "";
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

        // Legacy kind=svg scenes are rendered into a native WPF Canvas. New scenes are
        // expanded by SvgSceneGroup before reaching this method, but this keeps direct old
        // history entries browser-free as well.
        if (!SvgSceneImporter.TryImportElements(svg, out var imported)) return;
        var nativeCanvas = new Canvas
        {
            Width = Width,
            Height = Height,
            Background = Brushes.Transparent,
            IsHitTestVisible = false
        };
        foreach (var node in imported.OfType<JsonObject>())
        {
            using var document = JsonDocument.Parse(node.ToJsonString());
            var child = new SvgSceneElement(document.RootElement.Clone(), scale);
            var position = ReadPosition(document.RootElement, scale);
            Canvas.SetLeft(child, position.Left);
            Canvas.SetTop(child, position.Top);
            nativeCanvas.Children.Add(child);
        }
        Child = nativeCanvas;
    }

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
