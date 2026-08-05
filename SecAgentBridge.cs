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

    private readonly HttpListener _listener = new();
    private readonly IPluginHost _host;
    private readonly SettingsBridge _settings = new();
    private CancellationTokenSource _cts;

    public SecAgentBridge(IPluginHost host) => _host = host;
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
                await WriteJsonAsync(context, 200, new JsonObject { ["apiVersion"] = 1, ["name"] = "iccce", ["version"] = "0.1.0", ["status"] = "ok" }, cancellationToken);
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
                var result = CallTool(name, document.RootElement);
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

    private static JsonArray Tools() => new(
        Tool(VersionTool, "获取 ICC-CE 当前版本、配置路径和运行状态。", EmptySchema()),
        Tool(ListPathsTool, "列出 ICC-CE 可读写设置路径；可用 prefix 缩小范围。", PrefixSchema()),
        Tool(ReadSettingsTool, "读取 ICC-CE Settings.json 或指定设置路径；默认隐藏敏感值。", ReadSchema()),
        Tool(UpdateSettingsTool, "安全更新 ICC-CE 设置；更新前必须先读取目标字段。", UpdateSchema()));

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

    private JsonNode CallTool(string name, JsonElement arguments) => name switch
    {
        VersionTool => _settings.VersionStatus(),
        ListPathsTool => _settings.ListPaths(ReadString(arguments, "prefix")),
        ReadSettingsTool => _settings.Read(ReadString(arguments, "path"), false),
        UpdateSettingsTool => _settings.Update(arguments),
        _ => throw new ArgumentException($"未知工具：{name}")
    };

    private static string ReadString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : "";

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        _cts?.Dispose();
        _cts = null;
    }
}
