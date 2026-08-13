using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace Ink_Canvas.SecAgent.Plugin;

/// <summary>
/// Converts the SVG subset used by hand-drawn Markdown and ordinary model-generated SVG
/// snippets into native WPF scene elements. Keeping this conversion at the CE boundary means
/// the canvas never needs a WebBrowser/airspace surface for normal SVG insertion.
/// </summary>
internal static class SvgSceneImporter
{
    private static readonly Regex NumberRegex = new(@"[-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);
    public static string LastError { get; private set; } = "";

    private sealed class Style
    {
        public string Fill = "#000000";
        public string Stroke = "none";
        public double StrokeWidth = 1;
        public double FontSize = 16;
        public string FontWeight = "400";

        public Style Clone() => new()
        {
            Fill = Fill,
            Stroke = Stroke,
            StrokeWidth = StrokeWidth,
            FontSize = FontSize,
            FontWeight = FontWeight
        };
    }

    public static bool TryImport(string svg, out JsonElement scene)
    {
        scene = default;
        if (!TryImportElements(svg, out var width, out var height, out var elements)) return false;
        var root = new JsonObject
        {
            ["version"] = 1,
            ["width"] = width,
            ["height"] = height,
            ["elements"] = elements
        };
        using var document = System.Text.Json.JsonDocument.Parse(root.ToJsonString());
        scene = document.RootElement.Clone();
        return elements.Count > 0;
    }

    public static bool TryImportElements(string svg, out JsonArray elements)
    {
        elements = null;
        if (!TryImportElements(svg, out _, out _, out var imported)) return false;
        elements = imported;
        return elements.Count > 0;
    }

    private static bool TryImportElements(string svg, out double width, out double height, out JsonArray elements)
    {
        width = 800;
        height = 500;
        elements = new JsonArray();
        if (string.IsNullOrWhiteSpace(svg)) return false;

        try
        {
            var document = XDocument.Parse(svg, LoadOptions.PreserveWhitespace);
            var root = document.Root;
            if (root is null || !string.Equals(root.Name.LocalName, "svg", StringComparison.OrdinalIgnoreCase)) return false;

            var viewBox = Numbers(root.Attribute("viewBox")?.Value);
            if (viewBox.Count >= 4)
            {
                width = Positive(viewBox[2], width);
                height = Positive(viewBox[3], height);
            }
            else
            {
                width = Positive(Number(root.Attribute("width")?.Value), width);
                height = Positive(Number(root.Attribute("height")?.Value), height);
            }

            var rootTransform = Matrix.Identity;
            if (viewBox.Count >= 4)
                rootTransform.Translate(-viewBox[0], -viewBox[1]);
            Walk(root, rootTransform, new Style(), elements, width, height);
            return elements.Count > 0;
        }
        catch
        {
            LastError = "SVG XML 或根元素解析失败";
            elements = new JsonArray();
            return false;
        }
    }

    private static void Walk(XElement node, Matrix inherited, Style inheritedStyle, JsonArray elements, double sceneWidth, double sceneHeight)
    {
        var localName = node.Name.LocalName.ToLowerInvariant();
        if (localName is "defs" or "style" or "metadata" or "title" or "desc" or "clippath" or "mask" or "filter") return;

        var style = ApplyStyle(inheritedStyle, node);
        var transform = inherited;
        if (localName != "svg" || node.Parent is not null)
            transform = Multiply(inherited, ParseTransform(node.Attribute("transform")?.Value));

        if (localName == "svg" && node.Parent is not null)
        {
            var x = Number(node.Attribute("x")?.Value);
            var y = Number(node.Attribute("y")?.Value);
            if (x != 0 || y != 0) transform = Multiply(transform, Translation(x, y));
        }

        switch (localName)
        {
            case "g":
            case "svg":
                foreach (var child in node.Elements())
                    Walk(child, transform, style, elements, sceneWidth, sceneHeight);
                return;
            case "rect":
                AddGeometry(node, RectPath(node), transform, style, elements);
                return;
            case "circle":
                AddGeometry(node, EllipsePath(Number(node.Attribute("cx")?.Value), Number(node.Attribute("cy")?.Value),
                    Number(node.Attribute("r")?.Value), Number(node.Attribute("r")?.Value)), transform, style, elements);
                return;
            case "ellipse":
                AddGeometry(node, EllipsePath(Number(node.Attribute("cx")?.Value), Number(node.Attribute("cy")?.Value),
                    Number(node.Attribute("rx")?.Value), Number(node.Attribute("ry")?.Value)), transform, style, elements);
                return;
            case "line":
                AddGeometry(node, $"M {Number(node.Attribute("x1")?.Value).ToString(CultureInfo.InvariantCulture)} {Number(node.Attribute("y1")?.Value).ToString(CultureInfo.InvariantCulture)} " +
                    $"L {Number(node.Attribute("x2")?.Value).ToString(CultureInfo.InvariantCulture)} {Number(node.Attribute("y2")?.Value).ToString(CultureInfo.InvariantCulture)}", transform, style, elements);
                return;
            case "polyline":
            case "polygon":
                var points = Numbers(node.Attribute("points")?.Value);
                if (points.Count >= 4)
                {
                    var path = $"M {points[0].ToString(CultureInfo.InvariantCulture)} {points[1].ToString(CultureInfo.InvariantCulture)} " +
                               string.Join(" ", Enumerable.Range(1, points.Count / 2 - 1).Select(index =>
                                   $"L {points[index * 2].ToString(CultureInfo.InvariantCulture)} {points[index * 2 + 1].ToString(CultureInfo.InvariantCulture)}"));
                    if (localName == "polygon") path += " Z";
                    AddGeometry(node, path, transform, style, elements);
                }
                return;
            case "path":
                AddGeometry(node, node.Attribute("d")?.Value, transform, style, elements);
                return;
            case "text":
                AddText(node, transform, style, elements);
                return;
        }

        // Unsupported containers are still traversed so a harmless wrapper does not make
        // otherwise usable child paths disappear.
        foreach (var child in node.Elements())
            Walk(child, transform, style, elements, sceneWidth, sceneHeight);
    }

    private static void AddGeometry(XElement node, string pathData, Matrix transform, Style style, JsonArray elements)
    {
        if (string.IsNullOrWhiteSpace(pathData)) return;
        try
        {
            // Geometry.Parse returns a frozen geometry in WPF. Clone before applying either
            // the inherited SVG transform or the local-bounds normalization transform.
            var geometry = Geometry.Parse(pathData).Clone();
            if (!transform.IsIdentity) geometry.Transform = new MatrixTransform(transform);
            var bounds = geometry.Bounds;
            if (bounds.IsEmpty || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)) return;
            geometry = geometry.Clone();
            geometry.Transform = new TranslateTransform(-bounds.X, -bounds.Y);
            var fill = ReadPaint(node, "fill", style.Fill);
            var stroke = ReadPaint(node, "stroke", style.Stroke);
            var strokeWidth = Number(node.Attribute("stroke-width")?.Value);
            if (strokeWidth <= 0) strokeWidth = style.StrokeWidth;
            elements.Add(new JsonObject
            {
                ["kind"] = "path",
                ["role"] = "svg-shape",
                ["x"] = bounds.X,
                ["y"] = bounds.Y,
                ["width"] = Math.Max(8, bounds.Width),
                ["height"] = Math.Max(8, bounds.Height),
                ["d"] = geometry.ToString(CultureInfo.InvariantCulture),
                ["fill"] = fill,
                ["stroke"] = stroke,
                ["strokeWidth"] = strokeWidth
            });
        }
        catch (Exception ex)
        {
            LastError = $"图元 {node.Name.LocalName} 路径转换失败：{ex.Message}";
            // Unsupported SVG path syntax is skipped; no browser fallback is created.
        }
    }

    private static void AddText(XElement node, Matrix transform, Style style, JsonArray elements)
    {
        var text = node.Value?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        var fontSize = Number(node.Attribute("font-size")?.Value);
        if (fontSize <= 0) fontSize = style.FontSize;
        var width = Math.Max(12, text.Sum(character => character > 255 ? fontSize : fontSize * 0.62));
        var height = Math.Max(18, fontSize * 1.35);
        var x = Number(node.Attribute("x")?.Value);
        var y = Number(node.Attribute("y")?.Value) - fontSize;
        var point = transform.Transform(new Point(x, y));
        var anchor = node.Attribute("text-anchor")?.Value?.ToLowerInvariant();
        if (anchor == "middle") point.X -= width / 2;
        else if (anchor == "end") point.X -= width;
        elements.Add(new JsonObject
        {
            ["kind"] = "text",
            ["role"] = "svg-text",
            ["x"] = point.X,
            ["y"] = point.Y,
            ["width"] = width,
            ["height"] = height,
            ["text"] = text,
            ["color"] = ReadPaint(node, "fill", style.Fill),
            ["fontSize"] = fontSize,
            ["fontWeight"] = ReadString(node, "font-weight", style.FontWeight)
        });
    }

    private static Style ApplyStyle(Style inherited, XElement node)
    {
        var style = inherited.Clone();
        var declarations = (node.Attribute("style")?.Value ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Split(':', 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
        style.Fill = ReadString(node, "fill", declarations.TryGetValue("fill", out var fill) ? fill : style.Fill);
        style.Stroke = ReadString(node, "stroke", declarations.TryGetValue("stroke", out var stroke) ? stroke : style.Stroke);
        var strokeWidth = ReadString(node, "stroke-width", declarations.TryGetValue("stroke-width", out var sw) ? sw : null);
        if (strokeWidth is not null && Number(strokeWidth) > 0) style.StrokeWidth = Number(strokeWidth);
        var fontSize = ReadString(node, "font-size", declarations.TryGetValue("font-size", out var fs) ? fs : null);
        if (fontSize is not null && Number(fontSize) > 0) style.FontSize = Number(fontSize);
        style.FontWeight = ReadString(node, "font-weight", declarations.TryGetValue("font-weight", out var fw) ? fw : style.FontWeight);
        return style;
    }

    private static string ReadPaint(XElement node, string name, string fallback)
        => ReadString(node, name, fallback).Equals("currentColor", StringComparison.OrdinalIgnoreCase) ? "#000000" : ReadString(node, name, fallback);

    private static string ReadString(XElement node, string name, string fallback)
        => node.Attribute(name)?.Value?.Trim() is { Length: > 0 } value ? value : fallback;

    private static string RectPath(XElement node)
    {
        var x = Number(node.Attribute("x")?.Value);
        var y = Number(node.Attribute("y")?.Value);
        var width = Math.Max(0, Number(node.Attribute("width")?.Value));
        var height = Math.Max(0, Number(node.Attribute("height")?.Value));
        return $"M {x.ToString(CultureInfo.InvariantCulture)} {y.ToString(CultureInfo.InvariantCulture)} " +
               $"H {(x + width).ToString(CultureInfo.InvariantCulture)} V {(y + height).ToString(CultureInfo.InvariantCulture)} " +
               $"H {x.ToString(CultureInfo.InvariantCulture)} Z";
    }

    private static string EllipsePath(double cx, double cy, double rx, double ry)
    {
        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        const double kappa = 0.5522847498307936;
        return $"M {(cx + rx).ToString(CultureInfo.InvariantCulture)} {cy.ToString(CultureInfo.InvariantCulture)} " +
               $"C {(cx + rx).ToString(CultureInfo.InvariantCulture)} {(cy + ry * kappa).ToString(CultureInfo.InvariantCulture)} {(cx + rx * kappa).ToString(CultureInfo.InvariantCulture)} {(cy + ry).ToString(CultureInfo.InvariantCulture)} {cx.ToString(CultureInfo.InvariantCulture)} {(cy + ry).ToString(CultureInfo.InvariantCulture)} " +
               $"C {(cx - rx * kappa).ToString(CultureInfo.InvariantCulture)} {(cy + ry).ToString(CultureInfo.InvariantCulture)} {(cx - rx).ToString(CultureInfo.InvariantCulture)} {(cy + ry * kappa).ToString(CultureInfo.InvariantCulture)} {(cx - rx).ToString(CultureInfo.InvariantCulture)} {cy.ToString(CultureInfo.InvariantCulture)} " +
               $"C {(cx - rx).ToString(CultureInfo.InvariantCulture)} {(cy - ry * kappa).ToString(CultureInfo.InvariantCulture)} {(cx - rx * kappa).ToString(CultureInfo.InvariantCulture)} {(cy - ry).ToString(CultureInfo.InvariantCulture)} {cx.ToString(CultureInfo.InvariantCulture)} {(cy - ry).ToString(CultureInfo.InvariantCulture)} " +
               $"C {(cx + rx * kappa).ToString(CultureInfo.InvariantCulture)} {(cy - ry).ToString(CultureInfo.InvariantCulture)} {(cx + rx).ToString(CultureInfo.InvariantCulture)} {(cy - ry * kappa).ToString(CultureInfo.InvariantCulture)} {(cx + rx).ToString(CultureInfo.InvariantCulture)} {cy.ToString(CultureInfo.InvariantCulture)} Z";
    }

    private static Matrix ParseTransform(string value)
    {
        var result = Matrix.Identity;
        if (string.IsNullOrWhiteSpace(value)) return result;
        foreach (Match match in Regex.Matches(value, @"(matrix|translate|scale|rotate)\s*\(([^)]*)\)", RegexOptions.IgnoreCase))
        {
            var numbers = Numbers(match.Groups[2].Value);
            switch (match.Groups[1].Value.ToLowerInvariant())
            {
                case "matrix" when numbers.Count >= 6:
                    result = Multiply(result, new Matrix(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5]));
                    break;
                case "translate":
                    result = Multiply(result, Translation(numbers.ElementAtOrDefault(0), numbers.ElementAtOrDefault(1)));
                    break;
                case "scale":
                    result = Multiply(result, Scale(numbers.ElementAtOrDefault(0), numbers.Count > 1 ? numbers[1] : numbers.ElementAtOrDefault(0)));
                    break;
                case "rotate" when numbers.Count >= 3:
                    result = Multiply(result, Rotation(numbers[0], numbers[1], numbers[2]));
                    break;
                case "rotate" when numbers.Count >= 1:
                    result.Rotate(numbers[0]);
                    break;
            }
        }
        return result;
    }

    private static Matrix Multiply(Matrix left, Matrix right)
    {
        left.Append(right);
        return left;
    }

    private static Matrix Translation(double x, double y) => new(1, 0, 0, 1, x, y);
    private static Matrix Scale(double x, double y) => new(x, 0, 0, y, 0, 0);
    private static Matrix Rotation(double angle, double cx, double cy)
    {
        var matrix = Matrix.Identity;
        matrix.RotateAt(angle, cx, cy);
        return matrix;
    }

    private static double Number(string value)
        => Numbers(value).FirstOrDefault();

    private static List<double> Numbers(string value)
        => NumberRegex.Matches(value ?? "")
            .Select(match => double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0)
            .ToList();

    private static double Positive(double value, double fallback) => double.IsFinite(value) && value > 0 ? value : fallback;
}
