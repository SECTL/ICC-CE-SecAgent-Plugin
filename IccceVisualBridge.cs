using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
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
