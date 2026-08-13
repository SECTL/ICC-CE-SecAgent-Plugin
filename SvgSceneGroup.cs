using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ink_Canvas.SecAgent.Plugin;

/// <summary>
/// One selectable canvas item containing all editable paths from one Markdown insertion.
/// The rows and lines remain separate children for erasing, while the host moves/scales the
/// group as a single element.
/// </summary>
public sealed class SvgSceneGroup : Border
{
    // A fully transparent WPF brush can be skipped by hit testing depending on the
    // visual tree and the element underneath it. One alpha unit is visually invisible
    // but gives the complete insertion rectangle a stable mouse target.
    private static readonly Brush HitTestBrush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

    private readonly Canvas _content;
    private readonly JsonObject _scene;
    private readonly double _scale;

    public string SerializedScene => SerializeCurrentScene();
    public int ElementCount => _content.Children.OfType<SvgSceneElement>().Count();

    public SvgSceneGroup(JsonElement scene, double scale = 1)
    {
        _scene = JsonNode.Parse(scene.GetRawText()) as JsonObject
            ?? throw new ArgumentException("editableScene must be an object", nameof(scene));
        var sourceWidth = ReadPositive(scene, "width", 1200);
        var sourceHeight = ReadPositive(scene, "height", 800);
        var actualScale = Math.Max(0.05, scale);
        _scale = actualScale;

        Width = Math.Max(1, sourceWidth * actualScale);
        Height = Math.Max(1, sourceHeight * actualScale);
        Background = HitTestBrush;
        BorderThickness = new Thickness(0);
        ClipToBounds = false;
        Focusable = false;

        _content = new Canvas
        {
            Width = Width,
            Height = Height,
            // Keep the whole SVG frame hit-testable, including transparent areas. The host
            // binds move/select handlers to the group, so a transparent canvas lets the user
            // drag the insertion by its bounding box rather than only by painted pixels.
            Background = HitTestBrush,
            ClipToBounds = false,
            IsHitTestVisible = true
        };
        Child = _content;

        if (!scene.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("editableScene.elements must be an array", nameof(scene));

        foreach (var rawElement in elements.EnumerateArray())
        {
            if (rawElement.ValueKind != JsonValueKind.Object) continue;
            // Older Markdown scenes stored Mermaid as one kind=svg browser element. Expand
            // that payload into native WPF paths during construction so opening an old saved
            // page also avoids WebBrowser airspace and remains compatible with erasing.
            if (string.Equals(ReadString(rawElement, "kind", ""), "svg", StringComparison.OrdinalIgnoreCase)
                && SvgSceneImporter.TryImportElements(ReadString(rawElement, "svg", ""), out var imported))
            {
                var offsetX = ReadNumber(rawElement, "x", 0);
                var offsetY = ReadNumber(rawElement, "y", 0);
                foreach (var importedNode in imported.OfType<JsonObject>())
                {
                    importedNode["x"] = ReadNumber(importedNode, "x", 0) + offsetX;
                    importedNode["y"] = ReadNumber(importedNode, "y", 0) + offsetY;
                    using var importedDocument = JsonDocument.Parse(importedNode.ToJsonString());
                    AddSceneElement(importedDocument.RootElement.Clone(), actualScale);
                }
                continue;
            }

            AddSceneElement(rawElement.Clone(), actualScale);
        }
    }

    public static SvgSceneGroup FromSerializedScene(string serializedScene, double scale = 1)
    {
        using var document = JsonDocument.Parse(serializedScene);
        var root = document.RootElement.Clone();
        var savedScale = ReadPositive(root, "scale", scale);
        return new SvgSceneGroup(root, savedScale);
    }

    public SvgSceneElement[] GetSceneElements()
        => _content.Children.OfType<SvgSceneElement>().ToArray();

    /// <summary>
    /// Forces the group and its private Canvas through one complete layout pass.  The host
    /// inserts this control directly into InkCanvas and immediately displays a selection
    /// frame; without an explicit arrange WPF may leave the group at 0x0 until a later tool
    /// change invalidates the parent layout.
    /// </summary>
    public void ForceLayout()
    {
        var width = double.IsFinite(Width) && Width > 0 ? Width : 1;
        var height = double.IsFinite(Height) && Height > 0 ? Height : 1;
        var size = new Size(width, height);

        Measure(size);
        Arrange(new Rect(new Point(0, 0), size));
        _content.Measure(size);
        _content.Arrange(new Rect(new Point(0, 0), size));
        _content.UpdateLayout();
        UpdateLayout();
        InvalidateVisual();
    }

    public bool RemoveSceneElement(SvgSceneElement element)
    {
        if (element is null || !_content.Children.Contains(element)) return false;
        _content.Children.Remove(element);
        return true;
    }

    private string SerializeCurrentScene()
    {
        var elements = new JsonArray();
        _scene["scale"] = _scale;
        foreach (var element in GetSceneElements().ToArray())
        {
            // Area erasing uses a cheap live clip while the pointer is moving. Materialize
            // the accumulated rectangles exactly once when the host reads history/save
            // state, then omit fully erased rows from the persisted scene.
            element.CommitPendingAreaErase();
            if (!element.HasVisualContent)
            {
                _content.Children.Remove(element);
                continue;
            }
            if (JsonNode.Parse(element.SerializedElement) is not JsonObject json) continue;
            var left = Canvas.GetLeft(element);
            var top = Canvas.GetTop(element);
            if (!double.IsNaN(left)) json["x"] = left / Math.Max(0.05, _scale);
            if (!double.IsNaN(top)) json["y"] = top / Math.Max(0.05, _scale);
            elements.Add(json);
        }
        _scene["elements"] = elements;
        return _scene.ToJsonString();
    }

    private void AddSceneElement(JsonElement rawElement, double actualScale)
    {
        var element = new SvgSceneElement(rawElement, actualScale);
        var position = SvgSceneElement.ReadPosition(rawElement, actualScale);
        Canvas.SetLeft(element, position.Left);
        Canvas.SetTop(element, position.Top);
        _content.Children.Add(element);
    }

    private static string ReadString(JsonElement element, string name, string fallback)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback : fallback;

    private static double ReadNumber(JsonElement element, string name, double fallback)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : fallback;

    private static double ReadNumber(JsonObject element, string name, double fallback)
        => element[name]?.GetValue<double>() is double number && double.IsFinite(number) ? number : fallback;

    private static double ReadPositive(JsonElement element, string name, double fallback)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var number)
           && double.IsFinite(number)
           && number > 0
            ? number : fallback;
}
