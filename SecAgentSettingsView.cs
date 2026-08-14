using System;
using System.Windows;
using System.Windows.Controls;

namespace Ink_Canvas.SecAgent.Plugin;

public sealed class SecAgentSettingsView : UserControl
{
    private readonly SecAgentController _controller;
    private readonly TextBlock _serverText = new();
    private readonly TextBlock _svgCompatibilityText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _messageText = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.72 };
    private readonly Button _startButton = new() { Content = "启动 / 重启 HTTP 服务", HorizontalAlignment = HorizontalAlignment.Left };

    public SecAgentSettingsView(SecAgentController controller)
    {
        _controller = controller;
        _startButton.Click += StartButtonOnClick;

        var panel = new StackPanel { Margin = new Thickness(32), Width = 720 };
        panel.Children.Add(new TextBlock { Text = "SecAgent HTTP 服务", FontSize = 28, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock
        {
            Text = "ICC-CE 提供普通 HTTP JSON API。对应的 SecAgent 连接插件会在服务可用时注册工具和 Skill。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
            Margin = new Thickness(0, 8, 0, 16)
        });
        panel.Children.Add(new Separator());
        panel.Children.Add(new TextBlock { Text = "HTTP 服务", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 8) });
        panel.Children.Add(_serverText);
        panel.Children.Add(new TextBlock { Text = "地址：http://127.0.0.1:18790", Margin = new Thickness(0, 8, 0, 16) });
        panel.Children.Add(_startButton);
        panel.Children.Add(_messageText);
        panel.Children.Add(new Separator { Margin = new Thickness(0, 24, 0, 16) });
        panel.Children.Add(new TextBlock { Text = "SVG 插入适配", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(_svgCompatibilityText);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        RefreshStatus();
    }

    private void StartButtonOnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _controller.Start();
            _messageText.Text = "HTTP 服务已启动。请同时启用 SecAgent ICC-CE 连接插件。";
        }
        catch (Exception ex)
        {
            _messageText.Text = $"启动失败：{ex.Message}";
        }
        finally { RefreshStatus(); }
    }

    private void RefreshStatus()
    {
        _serverText.Text = _controller.GetStatus().ServerRunning ? "✓ 正在运行" : "✗ 未运行";
        var compatibility = _controller.GetCompatibilityStatus();
        if (compatibility.IsSupported)
        {
            _svgCompatibilityText.Text = $"✓ 当前 CE 支持手写 SVG 插入（CE 版本：{compatibility.HostVersion}）。\n{compatibility.Reason}";
            _svgCompatibilityText.Foreground = System.Windows.Media.Brushes.ForestGreen;
        }
        else
        {
            var missing = compatibility.MissingCapabilities.Count == 0
                ? "正在等待 CE 主窗口完成初始化。"
                : $"缺少：{string.Join("、", compatibility.MissingCapabilities)}";
            _svgCompatibilityText.Text = $"✗ 当前 CE 暂不支持手写 SVG 插入（CE 版本：{compatibility.HostVersion}）。\n{compatibility.Reason}\n{missing}\n请升级 CE 后重启插件；在适配完成前，插件不会向模型提供 insert_iccce_svg 工具。";
            _svgCompatibilityText.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
    }
}
