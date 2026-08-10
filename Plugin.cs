using Ink_Canvas.Plugins;
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Ink_Canvas.SecAgent.Plugin;

[PluginEntrance]
public sealed class Plugin : PluginBase
{
    private SecAgentController _controller;

    public override void Initialize(IPluginHost host)
    {
        base.Initialize(host);
        _controller = new SecAgentController(host);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) _ = _controller.StartAsync();
        else dispatcher.BeginInvoke(new Action(async () => await _controller.StartAsync()), DispatcherPriority.Loaded);
    }

    public override object GetSettingsView() => new SecAgentSettingsView(_controller);
    public override void Shutdown() => _controller?.Dispose();
}

public sealed class SecAgentController : IDisposable
{
    public const string ServerUrl = "http://127.0.0.1:18790";
    private const string SecAgentServerUrl = "http://127.0.0.1:42189";
    private const string ConnectorPluginId = "iccce-connector";
    private static readonly TimeSpan AutoInstallRetryDelay = TimeSpan.FromSeconds(10);

    private readonly IPluginHost _host;
    private readonly SecAgentBridge _bridge;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private CancellationTokenSource _autoInstallCancellation;

    public SecAgentController(IPluginHost host)
    {
        _host = host;
        _bridge = new SecAgentBridge(host);
    }

    public SecAgentRegistrationStatus GetStatus() => new(ServerUrl, _bridge.IsRunning);

    public Task StartAsync()
    {
        try
        {
            _bridge.Start();
            _autoInstallCancellation = new CancellationTokenSource();
            _ = EnsureConnectorInstalledAsync(_autoInstallCancellation.Token);
            _host.Log($"ICC-CE HTTP 服务已启动：{ServerUrl}");
        }
        catch (Exception ex)
        {
            _host.LogError("ICC-CE HTTP 服务启动失败", ex);
        }
        return Task.CompletedTask;
    }

    private async Task EnsureConnectorInstalledAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var healthResponse = await _httpClient.GetAsync($"{SecAgentServerUrl}/health", cancellationToken);
                if (!healthResponse.IsSuccessStatusCode) throw new InvalidOperationException($"SecAgent HTTP 健康检查失败（HTTP {(int)healthResponse.StatusCode}）");

                using var health = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync(cancellationToken));
                if (!health.RootElement.TryGetProperty("name", out var serviceName) || serviceName.GetString() != "secagent") throw new InvalidOperationException("目标 HTTP 服务不是 SecAgent");
                if (!health.RootElement.TryGetProperty("status", out var status) || status.GetString() != "ok") throw new InvalidOperationException("SecAgent HTTP 服务未就绪");

                using var pluginsResponse = await _httpClient.GetAsync($"{SecAgentServerUrl}/plugins", cancellationToken);
                if (!pluginsResponse.IsSuccessStatusCode) throw new InvalidOperationException($"SecAgent 插件列表请求失败（HTTP {(int)pluginsResponse.StatusCode}）");
                using var plugins = JsonDocument.Parse(await pluginsResponse.Content.ReadAsStringAsync(cancellationToken));
                if (IsConnectorEnabled(plugins.RootElement))
                {
                    _host.Log("ICC-CE SecAgent Connector 已安装并启用");
                    return;
                }

                var body = JsonSerializer.Serialize(new { pluginId = ConnectorPluginId });
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var installResponse = await _httpClient.PostAsync($"{SecAgentServerUrl}/plugins/install", content, cancellationToken);
                var installBody = await installResponse.Content.ReadAsStringAsync(cancellationToken);
                using var installResult = JsonDocument.Parse(installBody);
                if (!installResponse.IsSuccessStatusCode
                    || !installResult.RootElement.TryGetProperty("ok", out var ok)
                    || ok.ValueKind != JsonValueKind.True)
                    throw new InvalidOperationException($"SecAgent Connector auto-install failed (HTTP {(int)installResponse.StatusCode}): {installBody}");

                _host.Log("已请求 SecAgent 自动安装 ICC-CE Connector");
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _host.Log($"等待 SecAgent 并自动安装 Connector：{ex.Message}");
            }

            try { await Task.Delay(AutoInstallRetryDelay, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }

    private static bool IsConnectorEnabled(JsonElement root)
    {
        if (!root.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array) return false;
        foreach (var plugin in plugins.EnumerateArray())
        {
            if (plugin.TryGetProperty("id", out var id) && id.GetString() == ConnectorPluginId
                && plugin.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True) return true;
        }
        return false;
    }

    public void Start() => _bridge.Start();

    public void Dispose()
    {
        try { _autoInstallCancellation?.Cancel(); } catch { }
        _autoInstallCancellation?.Dispose();
        _autoInstallCancellation = null;
        _httpClient.Dispose();
        _bridge.Dispose();
    }
}

public sealed record SecAgentRegistrationStatus(string ServerUrl, bool ServerRunning);
