using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;
using AudioManager.Core.Models;

namespace AudioManager.Core.Configuration;

public static class ConfigurationJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<ApplicationIdentityKind>(allowIntegerValues: false) }
    };

    public static string Serialize(AudioConfiguration configuration)
    {
        Validate(configuration);
        return JsonSerializer.Serialize(configuration, Options);
    }

    public static AudioConfiguration Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        Require(document.RootElement.ValueKind == JsonValueKind.Object, "配置根节点必须是对象。");
        RejectDuplicateProperties(document.RootElement);
        if (document.RootElement.TryGetProperty("version", out var version))
            Require(version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out _), "配置版本必须是整数。");
        if (document.RootElement.TryGetProperty("version", out version) && version.TryGetInt32(out var number)
            && number != 1 && number != AudioConfiguration.CurrentVersion)
            throw new UnsupportedConfigurationVersionException(number);
        var legacy = version.ValueKind == JsonValueKind.Number && version.GetInt32() == 1;
        if (legacy)
        {
            Require(!document.RootElement.TryGetProperty("deviceProfiles", out _), "版本 1 不支持设备方案映射。");
            var node = JsonNode.Parse(json)!.AsObject();
            if (node["settings"] is JsonObject settings)
            {
                if (settings.TryGetPropertyValue("automaticRestoreEnabled", out var enabled))
                    Require(enabled is JsonValue flag && flag.TryGetValue<bool>(out _), "旧自动恢复设置必须是布尔值。");
                settings.Remove("automaticRestoreEnabled");
            }
            node["version"] = AudioConfiguration.CurrentVersion;
            node["deviceProfiles"] = new JsonArray();
            json = node.ToJsonString();
        }
        var value = JsonSerializer.Deserialize<AudioConfiguration>(json, Options)
            ?? throw new InvalidDataException("配置不能为空。");
        Validate(value);
        if (legacy)
        {
            value = value with { DeviceProfiles = value.KnownDevices.Select(device =>
            {
                bool ContainsDevice(AudioProfile profile) => profile.Rules.Any(rule => string.Equals(rule.Target.Target.DeviceId, device.Id, StringComparison.OrdinalIgnoreCase))
                    || profile.Snapshot.Entries.Any(entry => string.Equals(entry.Target.DeviceId, device.Id, StringComparison.OrdinalIgnoreCase));
                var profile = value.Profiles.FirstOrDefault(profile => profile.Id == value.ActiveProfileId && ContainsDevice(profile))
                    ?? value.Profiles.FirstOrDefault(ContainsDevice);
                return profile is null ? null : new DeviceProfileSelection(device.Id, profile.Id);
            }).OfType<DeviceProfileSelection>().ToArray() };
            Validate(value);
        }
        return value;
    }

    public static void Validate(AudioConfiguration configuration)
    {
        if (configuration.Version != AudioConfiguration.CurrentVersion)
            throw new UnsupportedConfigurationVersionException(configuration.Version);
        Require(configuration.Settings is not null && configuration.Profiles is not null
            && configuration.KnownApplications is not null && configuration.KnownDevices is not null
            && configuration.DeviceProfiles is not null, "配置缺少必需集合或设置。");
        var profileIds = new HashSet<Guid>();
        var devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in configuration.KnownDevices!)
        {
            Require(device is not null && !string.IsNullOrWhiteSpace(device.Id) && !string.IsNullOrWhiteSpace(device.Name), "设备信息不完整。");
            Require(devices.Add(device!.Id), "设备 ID 重复。");
        }
        foreach (var app in configuration.KnownApplications!)
        {
            ValidateIdentity(app);
            Require(apps.Add(IdentityKey(app)), "应用身份重复。");
        }
        foreach (var profile in configuration.Profiles!)
        {
            Require(profile is not null && profile.Id != Guid.Empty && !string.IsNullOrWhiteSpace(profile.Name), "方案 ID 或名称无效。");
            Require(profileIds.Add(profile!.Id), "方案 ID 重复。");
            Require(profile.Snapshot is not null && profile.Snapshot.Entries is not null && profile.Rules is not null, "方案缺少快照或规则。");
            var savedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in profile.Snapshot!.Entries!)
            {
                ValidateEntry(entry, devices, apps);
                Require(savedTargets.Add(TargetKey(entry.Target)), "快照包含重复目标。");
            }
            var ruleIds = new HashSet<Guid>();
            var ruleTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in profile.Rules!)
            {
                Require(rule is not null && rule.Id != Guid.Empty && ruleIds.Add(rule.Id), "规则 ID 无效或重复。");
                ValidateEntry(rule!.Target, devices, apps);
                Require(ruleTargets.Add(TargetKey(rule.Target.Target)), "同一方案的规则目标重复。");
            }
        }
        Require(configuration.ActiveProfileId is null || profileIds.Contains(configuration.ActiveProfileId.Value), "当前方案不存在。");
        var selectedDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selection in configuration.DeviceProfiles!)
        {
            Require(selection is not null && devices.Contains(selection.DeviceId) && selectedDevices.Add(selection.DeviceId), "设备方案引用未知或重复设备。");
            var profile = configuration.Profiles.FirstOrDefault(profile => profile.Id == selection!.ProfileId);
            Require(profile is not null && (profile.Rules.Any(rule => string.Equals(rule.Target.Target.DeviceId, selection!.DeviceId, StringComparison.OrdinalIgnoreCase))
                || profile.Snapshot.Entries.Any(entry => string.Equals(entry.Target.DeviceId, selection!.DeviceId, StringComparison.OrdinalIgnoreCase))), "设备方案不存在或不包含该输出设备。");
        }
    }

    public static string IdentityKey(ApplicationIdentity identity) => $"{identity.Kind}:{identity.Value}";
    public static string TargetKey(SavedAudioTarget target) => $"{target.DeviceId}\n{(target.Application is null ? "master" : IdentityKey(target.Application))}";

    public static void ValidateIdentity(ApplicationIdentity? identity)
    {
        Require(identity is not null && !string.IsNullOrWhiteSpace(identity.Value), "应用身份不能为空。");
        if (identity!.Kind == ApplicationIdentityKind.SystemSounds)
            Require(identity.Value == "system-sounds", "系统声音身份无效。");
        else if (identity.Kind == ApplicationIdentityKind.ExecutablePath)
            Require(IsAbsoluteExecutablePath(identity.Value), "应用身份必须是完整 Windows 程序路径。");
        else throw new InvalidDataException("不支持的应用身份类型。");
    }

    public static bool IsAbsoluteExecutablePath(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.IndexOfAny(['\r', '\n', '\0']) < 0
           && ((value.Length > 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '\\')
               || (value.StartsWith(@"\\", StringComparison.Ordinal) && value.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length >= 3));

    private static void ValidateEntry(SavedVolumeEntry? entry, HashSet<string> devices, HashSet<string> apps)
    {
        Require(entry is not null && entry.Target is not null && entry.Volume is not null && !string.IsNullOrWhiteSpace(entry.DisplayName), "音量目标不完整。");
        Require(devices.Contains(entry!.Target.DeviceId), "音量目标引用未知设备。");
        if (entry.Target.Application is { } identity)
        {
            ValidateIdentity(identity);
            Require(apps.Contains(IdentityKey(identity)), "音量目标引用未知应用。");
        }
        try { AudioVolume.ValidateLevel(entry.Volume.Level); }
        catch (ArgumentOutOfRangeException exception) { throw new InvalidDataException("配置音量必须在 0～1 之间。", exception); }
    }

    private static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                Require(names.Add(property.Name), "配置包含重复字段：" + property.Name);
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
}

public sealed class UnsupportedConfigurationVersionException(int version)
    : IOException($"配置版本 {version} 不受支持（当前版本 {AudioConfiguration.CurrentVersion}）；保留原文件，请使用兼容版本或显式导入/恢复默认。");
