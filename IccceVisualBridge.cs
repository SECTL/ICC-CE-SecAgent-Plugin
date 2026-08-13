using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ink_Canvas.SecAgent.Plugin;

internal sealed class IccceVisualBridge
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public Task<JsonNode> GetCurrentScreenshotAsync(bool includeScreenBackground)
        => OnUiAsync(window => CaptureCurrentAsync(window, includeScreenBackground, "iccce-current-screenshot"));

    public Task<JsonNode> GetWhiteboardScreenshotAsync(int? page, bool includeScreenBackground)
        => OnUiAsync(window => CaptureWhiteboardAsync(window, page, includeScreenBackground));

    public Task<JsonNode> GetWhiteboardStatusAsync()
        => OnUiAsync(window => Task.FromResult<JsonNode>(GetStatus(window)));

    public Task<JsonNode> SwitchWhiteboardPageAsync(int page)
        => OnUiAsync(window => Task.FromResult<JsonNode>(SwitchToPage(window, page)));

    public Task<JsonNode> AddWhiteboardPageAsync()
        => OnUiAsync(window => Task.FromResult<JsonNode>(InvokePageAction(window, "AddWhiteboardPage", "added")));

    public Task<JsonNode> DeleteWhiteboardPageAsync()
        => OnUiAsync(window => Task.FromResult<JsonNode>(InvokePageAction(window, "DeleteWhiteboardPage", "deleted")));

    public Task<JsonNode> InsertSvgAsync(string svg, string name, double? width, double? height)
        => OnUiAsync(window => Task.FromResult<JsonNode>(InsertSvg(window, svg, name, width, height)));

    private static JsonObject InsertSvg(object window, string svg, string name, double? requestedWidth, double? requestedHeight)
    {
        Diag(window, $"INSERT_START len={svg?.Length ?? 0} name={name ?? "<null>"} requested=({requestedWidth},{requestedHeight}) " +
            $"window={window?.GetType().FullName ?? "null"} assembly={window?.GetType().Assembly.Location ?? "<unknown>"}");
        if (string.IsNullOrWhiteSpace(svg)) throw new ArgumentException("svg 不能为空。", nameof(svg));
        if (svg.Length > 20 * 1024 * 1024) throw new ArgumentException("svg 不能超过 20 MiB。", nameof(svg));
        if (Regex.IsMatch(svg, @"<script\b|\bon[a-z]+\s*=", RegexOptions.IgnoreCase))
            throw new ArgumentException("SVG 不允许包含脚本或事件处理属性。", nameof(svg));

        var canvas = ReadField<InkCanvas>(window, "inkCanvas")
            ?? throw new InvalidOperationException("ICC-CE 当前没有可用的白板画布。");
        var (svgWidth, svgHeight) = ReadSvgSize(svg);
        Diag(window, $"INSERT_CANVAS mode={canvas.EditingMode} children={canvas.Children.Count} strokes={canvas.Strokes.Count} " +
            $"actual=({canvas.ActualWidth:0.##}x{canvas.ActualHeight:0.##}) svgSize=({svgWidth:0.##}x{svgHeight:0.##}) " +
            $"editableMetadata={svg.Contains("secagent-editable-scene", StringComparison.OrdinalIgnoreCase)}");
        if (TryReadEditableScene(svg, out var editableScene))
            return InsertEditableSceneGroup(window, canvas, editableScene, name, requestedWidth, requestedHeight, svgWidth, svgHeight);

        if (SvgSceneImporter.TryImport(svg, out editableScene))
            return InsertEditableSceneGroup(window, canvas, editableScene, name, requestedWidth, requestedHeight, svgWidth, svgHeight);

        throw new ArgumentException($"SVG 只支持可转换为 WPF 矢量的基础图元：path、rect、circle、ellipse、line、polyline、polygon、text。包含 foreignObject、复杂滤镜或脚本的 SVG 请先转换为路径。{SvgSceneImporter.LastError}", nameof(svg));
    }

#if false
        var element = new SvgCanvasElement(svg)
        {
            Name = "svg_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"),
            Width = Clamp(requestedWidth ?? svgWidth, 160, 1800),
            Height = Clamp(requestedHeight ?? svgHeight, 100, 1400)
        };
        InkCanvas.SetLeft(element, Math.Max(0, (canvas.ActualWidth - element.Width) / 2));
        InkCanvas.SetTop(element, Math.Max(0, (canvas.ActualHeight - element.Height) / 2));
        var transforms = new TransformGroup();
        transforms.Children.Add(new ScaleTransform(1, 1));
        transforms.Children.Add(new TranslateTransform(0, 0));
        transforms.Children.Add(new RotateTransform(0));
        element.RenderTransform = transforms;

        canvas.Select(new StrokeCollection());
        canvas.EditingMode = InkCanvasEditingMode.Select;
        canvas.Children.Add(element);
        EnsureCanvasVisibleForInsertion(canvas);
        Diag(window, $"INSERT_CANVAS_RESTORED visibility={canvas.Visibility} hit={canvas.IsHitTestVisible} children={canvas.Children.Count}");
        RefreshInsertedElementLayout(canvas, element);
        Diag(window, $"INSERT_FALLBACK_ADDED element={element.Name} size=({element.Width:0.##}x{element.Height:0.##}) " +
            $"left={InkCanvas.GetLeft(element):0.##} top={InkCanvas.GetTop(element):0.##} children={canvas.Children.Count}");
        InvokeRequired<object>(window, "BindElementEvents", element);
        CommitElementInsertHistory(window, element);
        InvokeRequired<object>(window, "SelectElement", element);
        Diag(window, $"INSERT_FALLBACK_DONE selected=true mode={canvas.EditingMode} element={element.Name}");
        return new JsonObject
        {
            ["ok"] = true,
            ["type"] = "svg",
            ["name"] = name ?? element.Name,
            ["width"] = element.Width,
            ["height"] = element.Height,
            ["selected"] = true,
            ["editableParts"] = false,
            ["note"] = "当前作为一个 SVG 元素插入，可移动、缩放和删除；内部文字/线条暂不拆分为独立白板元素。"
        };
    }

#endif
    private static bool TryReadEditableScene(string svg, out JsonElement scene)
    {
        var match = Regex.Match(svg, "<metadata\\s+id\\s*=\\s*['\\\"]secagent-editable-scene['\\\"](?<attributes>[^>]*)>(?<scene>[^<]*)</metadata>", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            scene = default;
            return false;
        }
        try
        {
            var attributes = match.Groups["attributes"].Value;
            var sceneText = match.Groups["scene"].Value.Trim();
            if (Regex.IsMatch(attributes, "data-encoding\\s*=\\s*['\\\"]base64['\\\"]", RegexOptions.IgnoreCase))
                sceneText = Encoding.UTF8.GetString(Convert.FromBase64String(sceneText));
            using var document = JsonDocument.Parse(sceneText);
            scene = document.RootElement.Clone();
            return scene.ValueKind == JsonValueKind.Object && scene.TryGetProperty("elements", out var elements) && elements.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            scene = default;
            return false;
        }
    }

    private static JsonObject InsertEditableSceneGroup(object window, InkCanvas canvas, JsonElement scene, string name, double? requestedWidth, double? requestedHeight, double fallbackWidth, double fallbackHeight)
    {
        var sourceWidth = ReadSceneNumber(scene, "width", fallbackWidth);
        var sourceHeight = ReadSceneNumber(scene, "height", fallbackHeight);
        var targetWidth = Clamp(requestedWidth ?? Math.Min(sourceWidth, 1200), 160, 1800);
        var scale = targetWidth / Math.Max(1, sourceWidth);
        if (requestedHeight.HasValue)
            scale = Math.Min(scale, Clamp(requestedHeight.Value, 100, 1400) / Math.Max(1, sourceHeight));
        scale = Math.Max(0.05, scale);
        var sceneWidth = sourceWidth * scale;
        var sceneHeight = sourceHeight * scale;
        var baseLeft = Math.Max(0, (canvas.ActualWidth - sceneWidth) / 2);
        var baseTop = Math.Max(0, (canvas.ActualHeight - sceneHeight) / 2);
        if (!scene.TryGetProperty("elements", out var rawElements) || rawElements.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("SVG 中的 editableScene 无效。", nameof(scene));
        if (rawElements.GetArrayLength() > 3000)
            throw new ArgumentException("editableScene 元素数量不能超过 3000。", nameof(scene));

        canvas.Select(new StrokeCollection());
        canvas.EditingMode = InkCanvasEditingMode.Select;
        var group = new SvgSceneGroup(scene, scale)
        {
            Name = "svgscene_group_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")
        };
        if (group.ElementCount == 0)
            throw new ArgumentException("editableScene 中没有可插入的元素。", nameof(scene));

        InkCanvas.SetLeft(group, baseLeft);
        InkCanvas.SetTop(group, baseTop);
        InvokeRequired<object>(window, "InitializeElementTransform", group);
        canvas.Children.Add(group);
        EnsureCanvasVisibleForInsertion(canvas);
        Diag(window, $"INSERT_CANVAS_RESTORED visibility={canvas.Visibility} hit={canvas.IsHitTestVisible} children={canvas.Children.Count}");
        RefreshInsertedElementLayout(canvas, group);
        Diag(window, $"INSERT_GROUP_ADDED name={group.Name} source=({sourceWidth:0.##}x{sourceHeight:0.##}) " +
            $"scale={scale:0.####} size=({group.Width:0.##}x{group.Height:0.##}) pos=({baseLeft:0.##},{baseTop:0.##}) " +
            $"count={group.ElementCount} layout=({group.ActualWidth:0.##}x{group.ActualHeight:0.##}) children={canvas.Children.Count}");
        InvokeRequired<object>(window, "BindElementEvents", group);
        CommitElementInsertHistory(window, group);
        InvokeRequired<object>(window, "SelectElement", group);
        Diag(window, $"INSERT_GROUP_DONE selected=true mode={canvas.EditingMode} group={group.Name} count={group.ElementCount}");
        return new JsonObject
        {
            ["ok"] = true,
            ["type"] = "editable-scene-group",
            ["name"] = string.IsNullOrWhiteSpace(name) ? "Markdown 手写内容" : name,
            ["width"] = sceneWidth,
            ["height"] = sceneHeight,
            ["elementCount"] = group.ElementCount,
            ["selected"] = true,
            ["selectedAll"] = true,
            ["editableParts"] = true,
            ["note"] = "已作为一个整体选中；内部仍保留独立路径，支持面积擦除和逐项处理。"
        };
    }

    private static JsonObject InsertEditableScene(object window, InkCanvas canvas, JsonElement scene, string name, double? requestedWidth, double? requestedHeight, double fallbackWidth, double fallbackHeight)
    {
        var sourceWidth = ReadSceneNumber(scene, "width", fallbackWidth);
        var sourceHeight = ReadSceneNumber(scene, "height", fallbackHeight);
        var targetWidth = Clamp(requestedWidth ?? Math.Min(sourceWidth, 1200), 160, 1800);
        var scale = targetWidth / Math.Max(1, sourceWidth);
        if (requestedHeight.HasValue) scale = Math.Min(scale, Clamp(requestedHeight.Value, 100, 1400) / Math.Max(1, sourceHeight));
        scale = Math.Max(0.05, scale);
        var sceneWidth = sourceWidth * scale;
        var sceneHeight = sourceHeight * scale;
        var baseLeft = Math.Max(0, (canvas.ActualWidth - sceneWidth) / 2);
        var baseTop = Math.Max(0, (canvas.ActualHeight - sceneHeight) / 2);
        if (!scene.TryGetProperty("elements", out var rawElements) || rawElements.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("SVG 中的 editableScene 无效。", nameof(scene));
        if (rawElements.GetArrayLength() > 3000) throw new ArgumentException("editableScene 元素数量不能超过 3000。", nameof(scene));

        canvas.Select(new StrokeCollection());
        canvas.EditingMode = InkCanvasEditingMode.Select;
        SvgSceneElement selected = null;
        var inserted = 0;
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        foreach (var sceneElement in rawElements.EnumerateArray())
        {
            if (sceneElement.ValueKind != JsonValueKind.Object) continue;
            var element = new SvgSceneElement(sceneElement.Clone(), scale)
            {
                Name = "svgscene_" + timestamp + "_" + inserted
            };
            var position = SvgSceneElement.ReadPosition(sceneElement, scale);
            InkCanvas.SetLeft(element, baseLeft + position.Left);
            InkCanvas.SetTop(element, baseTop + position.Top);
            InvokeRequired<object>(window, "InitializeElementTransform", element);
            canvas.Children.Add(element);
            InvokeRequired<object>(window, "BindElementEvents", element);
            CommitElementInsertHistory(window, element);
            if (selected is null && !string.Equals(element.SceneKind, "rect", StringComparison.OrdinalIgnoreCase)) selected = element;
            inserted++;
        }
        if (inserted == 0) throw new ArgumentException("editableScene 中没有可插入的元素。", nameof(scene));
        selected ??= canvas.Children[canvas.Children.Count - 1] as SvgSceneElement;
        RefreshInsertedElementLayout(canvas, selected);
        if (selected is not null) InvokeRequired<object>(window, "SelectElement", selected);
        return new JsonObject
        {
            ["ok"] = true,
            ["type"] = "editable-scene",
            ["name"] = string.IsNullOrWhiteSpace(name) ? "Markdown 手写内容" : name,
            ["width"] = sceneWidth,
            ["height"] = sceneHeight,
            ["elementCount"] = inserted,
            ["selected"] = selected is not null,
            ["editableParts"] = true,
            ["note"] = "已拆分为独立文字、线条和基础形状；可逐项选择、移动、等比例缩放、旋转和删除。"
        };
    }

    private static double ReadSceneNumber(JsonElement element, string name, double fallback)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var result) && double.IsFinite(result) && result > 0
            ? result : fallback;

    private static void EnsureCanvasVisibleForInsertion(InkCanvas canvas)
    {
        // In cursor mode ICC-CE legitimately collapses the InkCanvas when it has no
        // annotations. The insertion flow switches to Select before adding the child,
        // so that mode transition can collapse the canvas just before this plugin adds
        // its first scene. Restore the host surface after the child exists; otherwise
        // it will not be painted until a later tool or settings change invalidates it.
        if (canvas is null) return;
        canvas.Visibility = Visibility.Visible;
        canvas.IsHitTestVisible = true;
        canvas.InvalidateMeasure();
        canvas.InvalidateArrange();
        canvas.InvalidateVisual();
    }

    private static void RefreshInsertedElementLayout(InkCanvas canvas, FrameworkElement element)
    {
        if (canvas is null || element is null) return;

        element.Visibility = Visibility.Visible;
        element.IsHitTestVisible = true;

        // InkCanvas uses an internal arrange pass for children.  Calling UpdateLayout()
        // alone is not sufficient when this method runs synchronously from the plugin
        // dispatcher: the child can still report ActualWidth/ActualHeight == 0 until the
        // next tool change causes another arrange pass.  Arrange the fixed-size element
        // immediately so rendering, selection overlays, transparent hit areas and eraser
        // coordinate transforms all see the same rectangle before the tool call returns.
        var width = double.IsFinite(element.Width) && element.Width > 0 ? element.Width : 1;
        var height = double.IsFinite(element.Height) && element.Height > 0 ? element.Height : 1;
        var size = new System.Windows.Size(width, height);
        element.Measure(size);
        element.Arrange(new System.Windows.Rect(new System.Windows.Point(0, 0), size));
        element.UpdateLayout();

        if (element is SvgSceneGroup group)
            group.ForceLayout();

        canvas.InvalidateMeasure();
        canvas.InvalidateArrange();
        canvas.UpdateLayout();
        element.InvalidateMeasure();
        element.InvalidateArrange();
        element.Measure(size);
        element.Arrange(new System.Windows.Rect(new System.Windows.Point(0, 0), size));
        element.UpdateLayout();
        element.InvalidateVisual();
    }

    private static void Diag(object window, string message, bool error = false)
    {
        var line = $"[SecAgentDiag][Plugin] {message}";
        Debug.WriteLine(line);
        try
        {
            var assembly = window?.GetType().Assembly;
            var logType = assembly?.GetType("Ink_Canvas.Helpers.LogHelper");
            var enumType = logType?.GetNestedType("LogType", BindingFlags.Public | BindingFlags.NonPublic);
            var method = logType?.GetMethod("WriteLogToFile", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string), enumType }, null);
            if (method is null || enumType is null) return;
            var level = Enum.Parse(enumType, error ? "Error" : "Info");
            method.Invoke(null, new[] { line, level });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SecAgentDiag][Plugin] host-log-failed {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CommitElementInsertHistory(object window, FrameworkElement element)
    {
        var timeMachine = ReadField<object>(window, "timeMachine");
        if (timeMachine is not null) InvokeRequired<object>(timeMachine, "CommitElementInsertHistory", element);
    }

    private static (double Width, double Height) ReadSvgSize(string svg)
    {
        var match = Regex.Match(svg, @"viewBox\s*=\s*[^0-9-]+[-+]?\d+(?:\.\d+)?\s+[-+]?\d+(?:\.\d+)?\s+([-+]?\d+(?:\.\d+)?)\s+([-+]?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        return match.Success && double.TryParse(match.Groups[1].Value, out var width) && double.TryParse(match.Groups[2].Value, out var height)
            ? (width, height) : (1200, 800);
    }

    private static double Clamp(double value, double min, double max)
        => double.IsFinite(value) && value > 0 ? Math.Min(max, Math.Max(min, value)) : min;

    private static T ReadField<T>(object instance, string name) where T : class
    {
        for (var type = instance.GetType(); type is not null; type = type.BaseType)
            if (type.GetField(name, InstanceFlags)?.GetValue(instance) is T value) return value;
        return null;
    }

    private static Task<T> OnUiAsync<T>(Func<object, Task<T>> action)
    {
        var window = Application.Current?.MainWindow;
        if (window is null) throw new InvalidOperationException("ICC-CE 主窗口尚未就绪");
        if (window.Dispatcher.CheckAccess()) return action(window);
        return window.Dispatcher.InvokeAsync(() => action(window)).Task.Unwrap();
    }

    private static async Task<JsonNode> CaptureCurrentAsync(object window, bool includeScreenBackground, string name)
    {
        var overlay = CreateInkOverlay(window);
        return includeScreenBackground
            ? await CaptureWithBackgroundAsync(window, overlay, name)
            : ImageResult(overlay, name);
    }

    private static async Task<JsonNode> CaptureWhiteboardAsync(object window, int? requestedPage, bool includeScreenBackground)
    {
        var status = ReadStatus(window);
        var targetPage = requestedPage ?? status.CurrentPage;
        ValidatePage(targetPage, status.TotalPages);

        var originalPage = status.CurrentPage;
        try
        {
            if (targetPage != originalPage) SwitchToPageCore(window, targetPage);
            var overlay = CreateInkOverlay(window);
            var name = $"iccce-whiteboard-page-{targetPage}";
            return includeScreenBackground
                ? await CaptureWithBackgroundAsync(window, overlay, name, targetPage, status.TotalPages)
                : ImageResult(overlay, name, targetPage, status.TotalPages);
        }
        finally
        {
            if (targetPage != originalPage) SwitchToPageCore(window, originalPage);
        }
    }

    private static async Task<JsonNode> CaptureWithBackgroundAsync(object window, BitmapSource overlay, string name, int? page = null, int? totalPages = null)
    {
        var originalVisibility = GetVisibility(window);
        try
        {
            SetVisibility(window, Visibility.Hidden);
            await Task.Delay(180);

            var bitmap = InvokeRequired<Bitmap>(window, "CaptureScreenshotToBitmap");
            Bitmap result = null;
            try
            {
                var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
                result = InvokeRequired<Bitmap>(window, "OverlayInkOnCapturedBitmap", bitmap,
                    new Rectangle(virtualScreen.X, virtualScreen.Y, virtualScreen.Width, virtualScreen.Height), overlay);
                return ImageResult(result, name, page, totalPages);
            }
            finally
            {
                result?.Dispose();
                if (!ReferenceEquals(result, bitmap)) bitmap.Dispose();
            }
        }
        finally
        {
            SetVisibility(window, originalVisibility);
        }
    }

    private static BitmapSource CreateInkOverlay(object window)
    {
        var overlay = InvokeRequired<BitmapSource>(window, "CreateInkOverlayPreviewBitmapSource");
        if (overlay is not null) return overlay;

        var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
        var blank = new DrawingVisual();
        var result = new RenderTargetBitmap(Math.Max(1, screen.Width), Math.Max(1, screen.Height), 96, 96, PixelFormats.Pbgra32);
        result.Render(blank);
        result.Freeze();
        return result;
    }

    private static JsonObject GetStatus(object window)
    {
        var status = ReadStatus(window);
        return new JsonObject
        {
            ["currentPage"] = status.CurrentPage,
            ["totalPages"] = status.TotalPages,
            ["canPrevious"] = status.CurrentPage > 1,
            ["canNext"] = status.CurrentPage < status.TotalPages,
            ["canAdd"] = status.TotalPages < 99,
            ["canDelete"] = status.TotalPages > 1
        };
    }

    private static JsonObject SwitchToPage(object window, int page)
    {
        var status = ReadStatus(window);
        ValidatePage(page, status.TotalPages);
        if (page != status.CurrentPage) SwitchToPageCore(window, page);
        return GetStatus(window);
    }

    private static JsonObject InvokePageAction(object window, string methodName, string action)
    {
        InvokeRequired<object>(window, methodName);
        var result = GetStatus(window);
        result["action"] = action;
        return result;
    }

    private static void SwitchToPageCore(object window, int targetPage)
    {
        var status = ReadStatus(window);
        var guard = status.TotalPages + 1;
        while (status.CurrentPage < targetPage && guard-- > 0)
        {
            InvokeRequired<object>(window, "SwitchToNextPage");
            status = ReadStatus(window);
        }
        while (status.CurrentPage > targetPage && guard-- > 0)
        {
            InvokeRequired<object>(window, "SwitchToPreviousPage");
            status = ReadStatus(window);
        }
        if (status.CurrentPage != targetPage) throw new InvalidOperationException("ICC-CE 白板页切换失败");
    }

    private static void ValidatePage(int page, int totalPages)
    {
        if (page < 1 || page > totalPages) throw new ArgumentOutOfRangeException(nameof(page), $"白板页码必须在 1 到 {totalPages} 之间");
    }

    private static (int CurrentPage, int TotalPages) ReadStatus(object window)
    {
        var type = window.GetType();
        var current = ReadIntField(type, window, "CurrentWhiteboardIndex", 1);
        var total = ReadIntField(type, window, "WhiteboardTotalCount", 1);
        return (Math.Max(1, current), Math.Max(1, total));
    }

    private static int ReadIntField(Type type, object instance, string name, int fallback)
        => type.GetField(name, InstanceFlags)?.GetValue(instance) is int value ? value : fallback;

    private static Visibility GetVisibility(object window)
        => window is Window w ? w.Visibility : Visibility.Visible;

    private static void SetVisibility(object window, Visibility visibility)
    {
        if (window is Window w) w.Visibility = visibility;
    }

    private static T InvokeRequired<T>(object instance, string methodName, params object[] arguments)
    {
        var method = instance.GetType().GetMethod(methodName, InstanceFlags);
        if (method is null) throw new MissingMethodException(instance.GetType().FullName, methodName);
        var value = method.Invoke(instance, arguments);
        return value is T typed ? typed : default;
    }

    private static JsonObject ImageResult(BitmapSource source, string name, int? page = null, int? totalPages = null)
        => ImageObject(EncodePng(source), source.PixelWidth, source.PixelHeight, name, page, totalPages);

    private static JsonObject ImageResult(Bitmap bitmap, string name, int? page = null, int? totalPages = null)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return ImageObject(stream.ToArray(), bitmap.Width, bitmap.Height, name, page, totalPages);
    }

    private static JsonObject ImageObject(byte[] bytes, int width, int height, string name, int? page, int? totalPages)
    {
        var result = new JsonObject
        {
            ["type"] = "image",
            ["data"] = Convert.ToBase64String(bytes),
            ["mimeType"] = "image/png",
            ["name"] = name,
            ["width"] = width,
            ["height"] = height
        };
        if (page.HasValue) result["page"] = page.Value;
        if (totalPages.HasValue) result["totalPages"] = totalPages.Value;
        return result;
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(stream);
        return stream.ToArray();
    }
}
