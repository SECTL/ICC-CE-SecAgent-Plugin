using Ink_Canvas.Plugins;
using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Ink_Canvas.SecAgent.Plugin;

internal sealed class SecAgentBridge : IDisposable
{
    private const string VersionTool = "get_iccce_version_status";
    private const string ListPathsTool = "list_iccce_setting_paths";
    private const string ReadSettingsTool = "read_iccce_settings";
    private const string UpdateSettingsTool = "update_iccce_settings";
    private const string CurrentScreenshotTool = "get_iccce_current_screenshot";
    private const string WhiteboardScreenshotTool = "get_iccce_whiteboard_screenshot";
    private const string WhiteboardStatusTool = "get_iccce_whiteboard_status";
    private const string SwitchWhiteboardPageTool = "switch_iccce_whiteboard_page";
    private const string AddWhiteboardPageTool = "add_iccce_whiteboard_page";
    private const string DeleteWhiteboardPageTool = "delete_iccce_whiteboard_page";
    private const string InsertSvgTool = "insert_iccce_svg";

    private readonly HttpListener _listener = new();
    private readonly IPluginHost _host;
    private readonly SettingsBridge _settings = new();
    private readonly IccceVisualBridge _visuals = new();
    private readonly Func<IccceSvgCompatibilityStatus> _getSvgCompatibility;
    private CancellationTokenSource _cts;

    public SecAgentBridge(IPluginHost host, Func<IccceSvgCompatibilityStatus> getSvgCompatibility)
    {
        _host = host;
        _getSvgCompatibility = getSvgCompatibility ?? throw new ArgumentNullException(nameof(getSvgCompatibility));
    }
    public bool IsRunning => _cts is not null;

    public void Start()
    {
        if (_cts is not null) return;
        _listener.Prefixes.Add(SecAgentController.ServerUrl + "/");
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = ListenAsync(_cts.Token);
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            _ = HandleAsync(context, cancellationToken);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Access-Control-Allow-Origin"] = "*";
        try
        {
            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (context.Request.HttpMethod == "OPTIONS")
            {
                context.Response.StatusCode = 204;
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/health")
            {
                var compatibility = _getSvgCompatibility();
                await WriteJsonAsync(context, 200, new JsonObject
                {
                    ["apiVersion"] = 1,
                    ["name"] = "iccce",
                    ["version"] = "0.1.0",
                    ["status"] = "ok",
                    ["capabilities"] = new JsonObject
                    {
                        ["insertSvg"] = compatibility.IsSupported,
                        ["svg"] = compatibility.ToJson()
                    }
                }, cancellationToken);
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/tools")
            {
                await WriteJsonAsync(context, 200, new JsonObject { ["apiVersion"] = 1, ["tools"] = Tools() }, cancellationToken);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path.StartsWith("/tools/", StringComparison.Ordinal))
            {
                var name = Uri.UnescapeDataString(path["/tools/".Length..]);
                using var document = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: cancellationToken);
                var result = await CallToolAsync(name, document.RootElement);
                await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = true, ["result"] = result }, cancellationToken);
                return;
            }

            await WriteJsonAsync(context, 404, new JsonObject { ["ok"] = false, ["error"] = "Not found" }, cancellationToken);
        }
        catch (Exception ex)
        {
            _host.LogError("ICC-CE HTTP 请求失败", ex);
            await WriteJsonAsync(context, 400, new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["message"] = ex.Message } }, cancellationToken);
        }
        finally { context.Response.Close(); }
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, int statusCode, JsonNode body, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
    }

    private JsonArray Tools()
    {
        var tools = new JsonArray();
        if (_getSvgCompatibility().IsSupported)
            tools.Add(Tool(InsertSvgTool, "向当前白板插入一个 SVG 元素，插入后默认选中，可移动、等比例缩放和删除。", InsertSvgSchema()));
        tools.Add(Tool(VersionTool, "获取 ICC-CE 当前版本、配置路径和运行状态。", EmptySchema()));
        tools.Add(Tool(ListPathsTool, "列出 ICC-CE 可读写设置路径；可用 prefix 缩小范围。", PrefixSchema()));
        tools.Add(Tool(ReadSettingsTool, "读取 ICC-CE Settings.json 或指定设置路径；默认隐藏敏感值。", ReadSchema()));
        tools.Add(Tool(UpdateSettingsTool, "安全更新 ICC-CE 设置；更新前必须先读取目标字段。", UpdateSchema()));
        tools.Add(Tool(CurrentScreenshotTool, "获取 ICC-CE 当前墨迹截图，可选择是否包含屏幕背景。", ScreenshotSchema()));
        tools.Add(Tool(WhiteboardScreenshotTool, "获取指定 ICC-CE 白板页的墨迹截图，可选择是否包含屏幕背景。", WhiteboardScreenshotSchema()));
        tools.Add(Tool(WhiteboardStatusTool, "获取 ICC-CE 白板当前页、总页数和可用操作。", EmptySchema()));
        tools.Add(Tool(SwitchWhiteboardPageTool, "切换 ICC-CE 白板当前页，页码从 1 开始。", PageSchema()));
        tools.Add(Tool(AddWhiteboardPageTool, "在当前页后新增一个 ICC-CE 白板页。", EmptySchema()));
        tools.Add(Tool(DeleteWhiteboardPageTool, "删除 ICC-CE 白板当前页，至少保留一页。", EmptySchema()));
        return tools;
    }

    private static JsonObject Tool(string name, string description, JsonObject schema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema,
        ["hidden"] = true
    };

    private static JsonObject EmptySchema() => new()
    {
        ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false
    };

    private static JsonObject PrefixSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["prefix"] = new JsonObject { ["type"] = "string", ["description"] = "可选，例如 appearance 或 canvas。" } },
        ["additionalProperties"] = false
    };

    private static JsonObject ReadSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string", ["description"] = "点号分隔路径；空字符串读取完整设置。" } },
        ["additionalProperties"] = false
    };

    private static JsonObject UpdateSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["patch"] = new JsonObject { ["type"] = "object", ["description"] = "递归增量更新。" },
            ["path"] = new JsonObject { ["type"] = "string" },
            ["value"] = new JsonObject { ["description"] = "path 指定字段的新值。" },
            ["reason"] = new JsonObject { ["type"] = "string" }
        },
        ["additionalProperties"] = false,
        ["oneOf"] = new JsonArray(
            new JsonObject { ["required"] = new JsonArray("patch") },
            new JsonObject { ["required"] = new JsonArray("path", "value") })
    };

    private static JsonObject ScreenshotSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["include_screen_background"] = new JsonObject { ["type"] = "boolean", ["default"] = false, ["description"] = "false 仅返回透明墨迹层，true 返回桌面背景叠加墨迹" }
        },
        ["additionalProperties"] = false
    };

    private static JsonObject WhiteboardScreenshotSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["page"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["description"] = "可选，1 开始的白板页码；省略时使用当前页" },
            ["include_screen_background"] = new JsonObject { ["type"] = "boolean", ["default"] = false, ["description"] = "false 仅返回透明墨迹层，true 返回桌面背景叠加墨迹" }
        },
        ["additionalProperties"] = false
    };

    private static JsonObject PageSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["page"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["description"] = "1 开始的白板页码" }
        },
        ["required"] = new JsonArray("page"),
        ["additionalProperties"] = false
    };

    private static JsonObject InsertSvgSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["svg"] = new JsonObject { ["type"] = "string", ["description"] = "完整 SVG 字符串。" },
            ["name"] = new JsonObject { ["type"] = "string", ["description"] = "可选的元素名称。" },
            ["width"] = new JsonObject { ["type"] = "number", ["minimum"] = 160, ["maximum"] = 1800 },
            ["height"] = new JsonObject { ["type"] = "number", ["minimum"] = 100, ["maximum"] = 1400 }
        },
        ["required"] = new JsonArray("svg"),
        ["additionalProperties"] = false
    };

    private Task<JsonNode> CallToolAsync(string name, JsonElement arguments) => name switch
    {
        VersionTool => Task.FromResult<JsonNode>(_settings.VersionStatus()),
        ListPathsTool => Task.FromResult<JsonNode>(_settings.ListPaths(ReadString(arguments, "prefix"))),
        ReadSettingsTool => Task.FromResult<JsonNode>(_settings.Read(ReadString(arguments, "path"), false)),
        UpdateSettingsTool => Task.FromResult<JsonNode>(_settings.Update(arguments)),
        CurrentScreenshotTool => _visuals.GetCurrentScreenshotAsync(ReadBool(arguments, "include_screen_background")),
        WhiteboardScreenshotTool => _visuals.GetWhiteboardScreenshotAsync(ReadNullableInt(arguments, "page"), ReadBool(arguments, "include_screen_background")),
        WhiteboardStatusTool => _visuals.GetWhiteboardStatusAsync(),
        SwitchWhiteboardPageTool => _visuals.SwitchWhiteboardPageAsync(ReadRequiredInt(arguments, "page")),
        AddWhiteboardPageTool => _visuals.AddWhiteboardPageAsync(),
        DeleteWhiteboardPageTool => _visuals.DeleteWhiteboardPageAsync(),
        InsertSvgTool => InsertSvgIfSupportedAsync(arguments),
        _ => throw new ArgumentException($"未知工具：{name}")
    };

    private Task<JsonNode> InsertSvgIfSupportedAsync(JsonElement arguments)
    {
        var compatibility = _getSvgCompatibility();
        if (!compatibility.IsSupported)
            throw new InvalidOperationException($"当前 CE 不支持 SVG 插入：{compatibility.Reason} 缺少：{string.Join("、", compatibility.MissingCapabilities)}");
        return _visuals.InsertSvgAsync(ReadRequiredString(arguments, "svg"), ReadString(arguments, "name"), ReadNullableDouble(arguments, "width"), ReadNullableDouble(arguments, "height"));
    }

    private static string ReadString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : "";

    private static bool ReadBool(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True && value.GetBoolean();

    private static int? ReadNullableInt(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : null;

    private static int ReadRequiredInt(JsonElement args, string name) =>
        ReadNullableInt(args, name) ?? throw new ArgumentException($"缺少整数参数：{name}");

    private static string ReadRequiredString(JsonElement args, string name)
        => ReadString(args, name) is { Length: > 0 } value ? value : throw new ArgumentException($"缺少字符串参数：{name}");

    private static double? ReadNullableDouble(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number : null;

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        _cts?.Dispose();
        _cts = null;
    }
}
