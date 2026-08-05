using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;

namespace Ink_Canvas.SecAgent.Plugin;

internal sealed class SettingsBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly string[] SensitiveWords =
    {
        "password", "passwordhash", "passwordsalt", "totp", "secret", "token", "hash", "salt"
    };

    private static readonly object FileGate = new();

    public string RootPath => GetRootPath();
    public string SettingsPath => Path.Combine(RootPath, "Configs", "Settings.json");

    public JsonObject Read(string path, bool includeSensitive)
    {
        if (!File.Exists(SettingsPath)) throw new FileNotFoundException("ICC-CE 设置文件不存在。", SettingsPath);
        var root = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ??
                   throw new InvalidDataException("ICC-CE Settings.json 不是 JSON 对象。");
        var canonicalPath = CanonicalizePath(path);
        JsonNode value = string.IsNullOrWhiteSpace(canonicalPath) ? root : SelectPath(root, canonicalPath);
        if (!includeSensitive) value = Redact(value, canonicalPath);
        return new JsonObject
        {
            ["config_file"] = "Configs/Settings.json",
            ["path"] = canonicalPath,
            ["value"] = value
        };
    }

    public JsonObject ListPaths(string prefix)
    {
        var canonicalPrefix = string.IsNullOrWhiteSpace(prefix) ? "" : CanonicalizePath(prefix);
        var settingsType = FindSettingsType() ?? throw new InvalidOperationException("无法找到 ICC-CE Settings 类型。");
        var items = new List<JsonNode>();
        VisitMembers(settingsType, "", new HashSet<Type>(), items);
        var filtered = items
            .OfType<JsonObject>()
            .Where(x => string.IsNullOrWhiteSpace(canonicalPrefix) ||
                        x["path"]?.GetValue<string>().StartsWith(canonicalPrefix + ".", StringComparison.Ordinal) == true ||
                        string.Equals(x["path"]?.GetValue<string>(), canonicalPrefix, StringComparison.Ordinal))
            .ToArray();
        return new JsonObject
        {
            ["prefix"] = canonicalPrefix,
            ["settings_type"] = settingsType.FullName,
            ["paths"] = new JsonArray(filtered)
        };
    }

    public JsonObject Update(JsonElement arguments)
    {
        lock (FileGate)
        {
            return UpdateCore(arguments);
        }
    }

    private JsonObject UpdateCore(JsonElement arguments)
    {
        if (!File.Exists(SettingsPath)) throw new FileNotFoundException("ICC-CE 设置文件不存在。", SettingsPath);
        var original = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ??
                       throw new InvalidDataException("ICC-CE Settings.json 不是 JSON 对象。");
        var updated = CloneNode(original).AsObject();
        var updates = new List<PendingUpdate>();

        var hasPath = arguments.TryGetProperty("path", out var pathElement) &&
                      pathElement.ValueKind == JsonValueKind.String &&
                      !string.IsNullOrWhiteSpace(pathElement.GetString());
        var hasPatch = arguments.TryGetProperty("patch", out var patchElement) &&
                       patchElement.ValueKind == JsonValueKind.Object;
        if (hasPath == hasPatch) throw new ArgumentException("请提供 path + value，或提供 patch 对象（二选一）。");

        if (hasPath)
        {
            if (!arguments.TryGetProperty("value", out var valueElement))
                throw new ArgumentException("使用 path 时必须提供 value。");
            AddPendingUpdate(updated, pathElement.GetString(), JsonNode.Parse(valueElement.GetRawText()), updates);
        }
        else CollectPatch(updated, JsonNode.Parse(patchElement.GetRawText()).AsObject(), "", updates);

        if (updates.Count == 0) throw new ArgumentException("patch 不能是空对象。");
        var runtimeApplied = TryApplyRuntime(updates, out var runtimeMessage);
        if (!runtimeApplied) WriteJsonAtomically(updated);

        return new JsonObject
        {
            ["written"] = true,
            ["applied_runtime"] = runtimeApplied,
            ["runtime_message"] = runtimeMessage,
            ["config_file"] = "Configs/Settings.json",
            ["updated_paths"] = new JsonArray(updates.Select(x => (JsonNode)x.Path).ToArray()),
            ["backup"] = SettingsPath + ".bak"
        };
    }

    public JsonObject VersionStatus()
    {
        var appAssembly = FindAppAssembly();
        var appType = appAssembly?.GetType("Ink_Canvas.App");
        var version = appAssembly?.GetName().Version?.ToString() ?? "unknown";
        var appVersion = appType?.GetProperty("AppVersion", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)?.ToString();
        return new JsonObject
        {
            ["version"] = appVersion ?? version,
            ["assembly_version"] = version,
            ["process_id"] = Environment.ProcessId,
            ["root_path"] = RootPath,
            ["settings_path"] = SettingsPath,
            ["settings_exists"] = File.Exists(SettingsPath),
            ["server_binding"] = SecAgentController.ServerUrl
        };
    }

    private void CollectPatch(JsonObject updated, JsonObject patchObject, string prefix, List<PendingUpdate> updates)
    {
        foreach (var item in patchObject)
        {
            var requestedPath = string.IsNullOrWhiteSpace(prefix) ? item.Key : prefix + "." + item.Key;
            var resolved = ResolvePath(requestedPath, allowRootObject: true);
            var value = CloneNode(item.Value);
            if (value is JsonObject objectValue && IsStructuredType(resolved.Member.MemberType))
            {
                if (objectValue.Count == 0) AddPendingUpdate(updated, requestedPath, value, updates);
                else CollectPatch(updated, objectValue, requestedPath, updates);
            }
            else
            {
                AddPendingUpdate(updated, requestedPath, value, updates);
            }
        }
    }

    private void AddPendingUpdate(JsonObject updated, string requestedPath, JsonNode value, List<PendingUpdate> updates)
    {
        var resolved = ResolvePath(requestedPath, allowRootObject: false);
        if (IsSensitive(resolved.CanonicalPath))
            throw new ArgumentException($"出于安全原因，拒绝通过 HTTP API 读取或修改敏感设置：{resolved.CanonicalPath}");
        if (!resolved.Member.CanWrite) throw new ArgumentException($"设置不可写：{resolved.CanonicalPath}");

        var converted = ConvertValue(value, resolved.Member.MemberType, resolved.CanonicalPath);
        SetJsonPath(updated, resolved.CanonicalPath, value);
        updates.Add(new PendingUpdate(resolved.CanonicalPath, resolved.Members, value, converted));
    }

    private bool TryApplyRuntime(IReadOnlyList<PendingUpdate> updates, out string message)
    {
        message = "ICC-CE 运行时 Settings 不可用，将仅更新 Settings.json。";
        var settingsType = FindSettingsType();
        var managerType = FindAppAssembly()?.GetType("Ink_Canvas.Windows.SettingsViews.Helpers.SettingsManager");
        var settingsProperty = managerType?.GetProperty("Settings", BindingFlags.Public | BindingFlags.Static);
        var saveMethod = managerType?.GetMethod("SaveSettingsToFile", BindingFlags.Public | BindingFlags.Static);
        var runtimeSettings = settingsProperty?.GetValue(null);
        if (settingsType is null || runtimeSettings is null) return false;

        try
        {
            void Apply()
            {
                foreach (var update in updates) SetRuntimeValue(runtimeSettings, update.Members, update.ConvertedValue);
                saveMethod?.Invoke(null, null);
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.Invoke(Apply);
            else Apply();
            message = "已同步到 ICC-CE 运行时；部分仅在启动时读取的设置可能需要重启 ICC-CE。";
            return saveMethod is not null;
        }
        catch (Exception ex)
        {
            message = "运行时同步失败，已保留文件更新：" + ex.Message;
            return false;
        }
    }

    private static void SetRuntimeValue(object root, IReadOnlyList<SettingMember> members, object value)
    {
        object current = root;
        for (var i = 0; i < members.Count - 1; i++)
        {
            var member = members[i];
            var child = member.GetValue(current);
            if (child is null)
            {
                child = Activator.CreateInstance(member.MemberType);
                member.SetValue(current, child);
            }
            current = child;
        }
        members[^1].SetValue(current, value);
    }

    private object ConvertValue(JsonNode value, Type targetType, string path)
    {
        try
        {
            if (value is null)
            {
                if (!targetType.IsValueType || Nullable.GetUnderlyingType(targetType) is not null) return null;
                throw new InvalidDataException("值不能为 null");
            }
            if (targetType.IsEnum && value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var enumText))
                return Enum.Parse(targetType, enumText, true);
            return JsonSerializer.Deserialize(value.ToJsonString(), targetType, JsonOptions);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"设置 {path} 的值无法解析为 {targetType.Name}：{ex.Message}");
        }
    }

    private ResolvedPath ResolvePath(string requestedPath, bool allowRootObject)
    {
        if (string.IsNullOrWhiteSpace(requestedPath)) throw new ArgumentException("设置路径不能为空。");
        var settingsType = FindSettingsType() ?? throw new InvalidOperationException("无法找到 ICC-CE Settings 类型。");
        var currentType = settingsType;
        var members = new List<SettingMember>();
        var canonical = new List<string>();
        foreach (var segment in requestedPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var member = FindMember(currentType, segment);
            if (member is null) throw new ArgumentException($"ICC-CE 不存在设置路径：{requestedPath}");
            members.Add(member);
            canonical.Add(member.JsonName);
            currentType = member.MemberType;
        }
        var last = members[^1];
        if (!allowRootObject && IsStructuredType(last.MemberType))
            throw new ArgumentException($"请继续指定 {string.Join(".", canonical)} 下的具体字段；对象整体替换可能丢失未列出的设置。");
        return new ResolvedPath(string.Join('.', canonical), members, last);
    }

    private string CanonicalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        return ResolvePath(path, allowRootObject: true).CanonicalPath;
    }

    private static JsonNode SelectPath(JsonNode root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current switch
            {
                JsonObject obj when obj[segment] is { } child => child,
                _ => throw new KeyNotFoundException($"设置路径不存在：{path}")
            };
        }
        return CloneNode(current);
    }

    private static void SetJsonPath(JsonObject root, string path, JsonNode value)
    {
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        JsonObject current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (current[parts[i]] is not JsonObject child) throw new KeyNotFoundException($"设置路径不存在：{path}");
            current = child;
        }
        current[parts[^1]] = CloneNode(value);
    }

    private static JsonNode Redact(JsonNode value, string path)
    {
        if (value is JsonObject obj)
        {
            var copy = new JsonObject();
            foreach (var item in obj)
            {
                var childPath = string.IsNullOrWhiteSpace(path) ? item.Key : path + "." + item.Key;
                copy[item.Key] = IsSensitive(childPath) ? JsonValue.Create("[REDACTED]") : Redact(item.Value, childPath);
            }
            return copy;
        }
        if (value is JsonArray array)
        {
            var copy = new JsonArray();
            foreach (var item in array) copy.Add(Redact(item, path));
            return copy;
        }
        return CloneNode(value);
    }

    private static JsonNode CloneNode(JsonNode node) => node is null ? null : JsonNode.Parse(node.ToJsonString());

    private void WriteJsonAtomically(JsonObject node)
    {
        var serialized = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var backupPath = SettingsPath + ".bak";
        Exception lastError = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var tempPath = SettingsPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(SettingsPath, backupPath, true);
                File.WriteAllText(tempPath, serialized);
                File.Move(tempPath, SettingsPath, true);
                return;
            }
            catch (IOException ex)
            {
                lastError = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                lastError = ex;
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }

            if (attempt < 7) Thread.Sleep(150 * (attempt + 1));
        }

        throw new IOException($"ICC-CE Settings.json 当前被占用，重试保存仍失败：{lastError?.Message}", lastError);
    }

    private static bool IsSensitive(string path)
    {
        var normalized = path.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        return SensitiveWords.Any(normalized.Contains);
    }

    private static bool IsStructuredType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type != typeof(string) && !type.IsPrimitive && !type.IsEnum &&
               type != typeof(decimal) && type != typeof(DateTime) &&
               !typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
    }

    private static void VisitMembers(Type type, string prefix, HashSet<Type> ancestors, List<JsonNode> items)
    {
        if (!ancestors.Add(type)) return;
        foreach (var member in GetMembers(type))
        {
            var path = string.IsNullOrWhiteSpace(prefix) ? member.JsonName : prefix + "." + member.JsonName;
            if (IsSensitive(path)) continue;
            if (IsStructuredType(member.MemberType))
            {
                VisitMembers(member.MemberType, path, ancestors, items);
            }
            else
            {
                items.Add(new JsonObject
                {
                    ["path"] = path,
                    ["type"] = Nullable.GetUnderlyingType(member.MemberType)?.FullName ?? member.MemberType.FullName,
                    ["writable"] = member.CanWrite
                });
            }
        }
        ancestors.Remove(type);
    }

    private static IEnumerable<SettingMember> GetMembers(Type type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.GetIndexParameters().Length == 0)
            .Select(x => new SettingMember(x));
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => !x.IsInitOnly)
            .Select(x => new SettingMember(x));
        return properties.Concat(fields).Where(x => !x.IsIgnored);
    }

    private static SettingMember FindMember(Type type, string name) =>
        GetMembers(type).FirstOrDefault(x => string.Equals(x.JsonName, name, StringComparison.OrdinalIgnoreCase) ||
                                             string.Equals(x.Info.Name, name, StringComparison.OrdinalIgnoreCase));

    private static Type FindSettingsType() => FindAppAssembly()?.GetType("Ink_Canvas.Settings");

    private static Assembly FindAppAssembly() =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetType("Ink_Canvas.Settings") is not null);

    private static string GetRootPath()
    {
        var appType = FindAppAssembly()?.GetType("Ink_Canvas.App");
        var field = appType?.GetField("RootPath", BindingFlags.Public | BindingFlags.Static);
        return field?.GetValue(null)?.ToString() ?? AppContext.BaseDirectory;
    }

    private sealed class SettingMember
    {
        public SettingMember(MemberInfo info)
        {
            Info = info;
            MemberType = info is PropertyInfo property ? property.PropertyType : ((FieldInfo)info).FieldType;
            JsonName = ReadJsonName(info) ?? ToCamelCase(info.Name);
            IsIgnored = HasAttribute(info, "Newtonsoft.Json.JsonIgnoreAttribute");
            CanWrite = info is PropertyInfo propertyInfo ? propertyInfo.CanWrite : !((FieldInfo)info).IsInitOnly;
        }

        public MemberInfo Info { get; }
        public Type MemberType { get; }
        public string JsonName { get; }
        public bool IsIgnored { get; }
        public bool CanWrite { get; }

        public object GetValue(object instance) => Info is PropertyInfo property ? property.GetValue(instance) : ((FieldInfo)Info).GetValue(instance);
        public void SetValue(object instance, object value)
        {
            if (Info is PropertyInfo property) property.SetValue(instance, value);
            else ((FieldInfo)Info).SetValue(instance, value);
        }

        private static string ReadJsonName(MemberInfo info)
        {
            var attribute = info.GetCustomAttributes(true).FirstOrDefault(x => x.GetType().FullName == "Newtonsoft.Json.JsonPropertyAttribute");
            return attribute?.GetType().GetProperty("PropertyName")?.GetValue(attribute)?.ToString();
        }

        private static bool HasAttribute(MemberInfo info, string fullName) => info.GetCustomAttributes(true).Any(x => x.GetType().FullName == fullName);
        private static string ToCamelCase(string name) => string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    private sealed record ResolvedPath(string CanonicalPath, IReadOnlyList<SettingMember> Members, SettingMember Member);
    private sealed record PendingUpdate(string Path, IReadOnlyList<SettingMember> Members, JsonNode Value, object ConvertedValue);
}
