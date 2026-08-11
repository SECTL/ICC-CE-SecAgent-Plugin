using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace Ink_Canvas.SecAgent.Plugin;

/// <summary>
/// One selectable canvas item containing all editable paths from one Markdown insertion.
/// The rows and lines remain separate children for erasing, while the host moves/scales the
/// group as a single element.
/// </summary>
public sealed class SvgSceneGroup : Border
{
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
        Background = System.Windows.Media.Brushes.Transparent;
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
            Background = System.Windows.Media.Brushes.Transparent,
            ClipToBounds = false,
            IsHitTestVisible = true
        };
        Child = _content;

        if (!scene.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("editableScene.elements must be an array", nameof(scene));

        foreach (var rawElement in elements.EnumerateArray())
        {
            if (rawElement.ValueKind != JsonValueKind.Object) continue;
            var element = new SvgSceneElement(rawElement.Clone(), actualScale);
            var position = SvgSceneElement.ReadPosition(rawElement, actualScale);
            Canvas.SetLeft(element, position.Left);
            Canvas.SetTop(element, position.Top);
            _content.Children.Add(element);
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
        foreach (var element in GetSceneElements())
        {
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

    private static double ReadPositive(JsonElement element, string name, double fallback)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var number)
           && double.IsFinite(number)
           && number > 0
            ? number : fallback;
}
