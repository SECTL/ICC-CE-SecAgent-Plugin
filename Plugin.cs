using Ink_Canvas.Plugins;
using System;
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
    private readonly IPluginHost _host;
    private readonly SecAgentBridge _bridge;

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
            _host.Log($"ICC-CE HTTP 服务已启动：{ServerUrl}");
        }
        catch (Exception ex)
        {
            _host.LogError("ICC-CE HTTP 服务启动失败", ex);
        }
        return Task.CompletedTask;
    }

    public void Start() => _bridge.Start();
    public void Dispose() => _bridge.Dispose();
}

public sealed record SecAgentRegistrationStatus(string ServerUrl, bool ServerRunning);
