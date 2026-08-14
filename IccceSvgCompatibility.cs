using Ink_Canvas.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace Ink_Canvas.SecAgent.Plugin;

/// <summary>
/// Detects the CE-side changes required by the native editable SVG bridge.
/// This is intentionally capability-based instead of version-number-based: a
/// locally built CE may have a different version string while still providing
/// (or not providing) the adapter.
/// </summary>
internal static class IccceSvgCompatibility
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static IccceSvgCompatibilityStatus Check(IPluginHost host)
    {
        var window = Application.Current?.MainWindow;
        var hostVersion = ReadHostVersion(host, window?.GetType());
        if (window is null)
        {
            return new IccceSvgCompatibilityStatus(
                false,
                false,
                hostVersion,
                "ICC-CE 主窗口尚未就绪，暂时无法检测 SVG 插入适配。",
                Array.Empty<string>());
        }

        var windowType = window.GetType();
        var missing = new List<string>();

        if (FindField(windowType, "inkCanvas") is not { } canvasField
            || !typeof(InkCanvas).IsAssignableFrom(canvasField.FieldType))
            missing.Add("白板画布入口");

        RequireMethod(windowType, "InitializeElementTransform", typeof(void), missing, "元素变换初始化适配", typeof(FrameworkElement));
        RequireMethod(windowType, "BindElementEvents", typeof(void), missing, "元素鼠标事件适配", typeof(FrameworkElement));
        RequireMethod(windowType, "SelectElement", typeof(void), missing, "元素选中适配", typeof(FrameworkElement));
        RequireMethod(windowType, "ClearSecAgentSceneElements", typeof(void), missing, "清空画布适配");
        RequireMethod(windowType, "HasSecAgentSceneElementsOnCanvas", typeof(bool), missing, "鼠标工具可见性适配");
        RequireMethod(windowType, "BeginSecAgentStrokeErase", typeof(bool), missing, "线擦 SVG 适配", typeof(System.Windows.Point));
        RequireMethod(windowType, "MoveSecAgentStrokeErase", typeof(bool), missing, "线擦 SVG 移动适配", typeof(System.Windows.Point));
        RequireMethod(windowType, "EndSecAgentStrokeErase", typeof(void), missing, "线擦 SVG 结束适配");
        RequireMethod(windowType, "DisableEraserOverlay", typeof(void), missing, "面积擦切换适配");

        var timeMachineField = FindField(windowType, "timeMachine");
        if (timeMachineField is null
            || !HasMethod(timeMachineField.FieldType, "CommitElementInsertHistory", typeof(void), typeof(UIElement)))
            missing.Add("SVG 插入撤销历史适配");

        var supported = missing.Count == 0;
        return new IccceSvgCompatibilityStatus(
            supported,
            true,
            hostVersion,
            supported
                ? "当前 CE 已提供原生 SVG 插入、选中、擦除、清空和撤销所需适配。"
                : "当前 CE 版本缺少原生 SVG 插入适配，请升级到包含 SecAgent SVG 适配的 CE 版本。",
            missing);
    }

    private static string ReadHostVersion(IPluginHost host, Type windowType)
    {
        try
        {
            // Resolve this optional service by name so the plugin can still load in
            // older SDKs that predate IAppInfoService.
            var appInfoType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("Ink_Canvas.Plugins.IAppInfoService"))
                .FirstOrDefault(type => type is not null);
            var appInfo = appInfoType is null ? null : host?.ServiceProvider?.GetService(appInfoType);
            var version = appInfoType?.GetProperty("Version")?.GetValue(appInfo)?.ToString();
            if (!string.IsNullOrWhiteSpace(version)) return version;
        }
        catch
        {
            // Older CE hosts may not expose IAppInfoService yet.
        }

        try
        {
            var appType = windowType?.Assembly.GetType("Ink_Canvas.App");
            var version = appType?.GetProperty("AppVersion", StaticFlags)?.GetValue(null)?.ToString();
            if (!string.IsNullOrWhiteSpace(version)) return version;
        }
        catch
        {
        }

        return windowType?.Assembly.GetName().Version?.ToString() ?? "未知";
    }

    private static FieldInfo FindField(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var field = current.GetField(name, InstanceFlags);
            if (field is not null) return field;
        }

        return null;
    }

    private static void RequireMethod(Type type, string name, Type returnType, ICollection<string> missing, string displayName, params Type[] parameterTypes)
    {
        if (!HasMethod(type, name, returnType, parameterTypes)) missing.Add(displayName);
    }

    private static bool HasMethod(Type type, string name, Type returnType, params Type[] parameterTypes)
    {
        if (type is null) return false;
        for (var current = type; current is not null; current = current.BaseType)
        {
            var method = current.GetMethods(InstanceFlags).FirstOrDefault(candidate =>
                candidate.Name == name
                && candidate.ReturnType == returnType
                && candidate.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameterTypes));
            if (method is not null) return true;
        }

        return false;
    }
}

internal sealed class IccceSvgCompatibilityStatus
{
    public IccceSvgCompatibilityStatus(bool isSupported, bool isKnown, string hostVersion, string reason, IReadOnlyList<string> missingCapabilities)
    {
        IsSupported = isSupported;
        IsKnown = isKnown;
        HostVersion = hostVersion ?? "未知";
        Reason = reason ?? "未知原因";
        MissingCapabilities = missingCapabilities ?? Array.Empty<string>();
    }

    public bool IsSupported { get; }
    public bool IsKnown { get; }
    public string HostVersion { get; }
    public string Reason { get; }
    public IReadOnlyList<string> MissingCapabilities { get; }

    public JsonObject ToJson()
    {
        var missing = new JsonArray();
        foreach (var capability in MissingCapabilities) missing.Add(capability);

        return new JsonObject
        {
            ["supported"] = IsSupported,
            ["known"] = IsKnown,
            ["hostVersion"] = HostVersion,
            ["reason"] = Reason,
            ["missingCapabilities"] = missing
        };
    }
}
