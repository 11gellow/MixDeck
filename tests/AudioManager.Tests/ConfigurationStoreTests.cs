using AudioManager.Core.Configuration;
using AudioManager.Core.Services;
using System.IO;
using ApplicationIdentity = AudioManager.Core.Configuration.ApplicationIdentity;

namespace AudioManager.Tests;

public sealed class ConfigurationStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "MixDeck-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(directory, "settings.json");

    internal static AudioConfiguration Sample(float volume = .37f)
    {
        var app = new ApplicationIdentity(ApplicationIdentityKind.ExecutablePath, @"C:\Apps\Player.exe");
        var entry = new SavedVolumeEntry(new("device-a", app), "播放器", new(volume, true));
        var profile = new AudioProfile(Guid.NewGuid(), "工作", new(DateTimeOffset.UtcNow, [entry]), [new(Guid.NewGuid(), true, entry)]);
        return AudioConfiguration.Empty with
        {
            Settings = new() { ShowUnavailableDevices = true },
            KnownApplications = [app], KnownDevices = [new("device-a", "耳机")], Profiles = [profile], ActiveProfileId = profile.Id, DeviceProfiles = [new("device-a", profile.Id)]
        };
    }

    [Fact]
    public async Task FirstRunDoesNotWriteAndRoundTripPreservesProfilesRulesPreferencesAndBackup()
    {
        var store = new JsonConfigurationStore(FilePath);
        Assert.Equal(ConfigurationLoadKind.Missing, (await store.LoadAsync()).Kind);
        Assert.False(File.Exists(FilePath));
        var first = Sample();
        await store.SaveAsync(first);
        var json = await File.ReadAllTextAsync(FilePath);
        Assert.DoesNotContain("processId", json);
        Assert.DoesNotContain("instanceId", json);
        Assert.Equal(ConfigurationJson.Serialize(first), ConfigurationJson.Serialize((await store.LoadAsync()).Configuration));
        var second = Sample(.22f);
        await store.SaveAsync(second);
        Assert.Equal(json, await File.ReadAllTextAsync(store.BackupPath));
        Assert.Equal(ConfigurationJson.Serialize(second), await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public async Task CorruptPrimaryLoadsBackupWithoutWritingThenPreservesGoodBackupOnRepair()
    {
        Directory.CreateDirectory(directory);
        var saved = ConfigurationJson.Serialize(Sample());
        await File.WriteAllTextAsync(FilePath + ".bak", saved);
        await File.WriteAllTextAsync(FilePath, "{broken");
        var store = new JsonConfigurationStore(FilePath);
        var result = await store.LoadAsync();
        Assert.Equal(ConfigurationLoadKind.BackupRecovered, result.Kind);
        Assert.Equal("{broken", await File.ReadAllTextAsync(FilePath));
        await store.SaveAsync(result.Configuration);
        Assert.Equal(saved, await File.ReadAllTextAsync(FilePath + ".bak"));
        Assert.Single(Directory.GetFiles(directory, "*.preserved-*"));
        Assert.Equal(ConfigurationLoadKind.Loaded, (await store.LoadAsync()).Kind);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"version\":999}")]
    [InlineData("{\"version\":1}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"version\":\"1\"}")]
    [InlineData("{\"version\":1,\"version\":999}")]
    public async Task UnreadableMissingFieldsOrFutureVersionBlocksNormalSaveAndExplicitResetPreservesOriginal(string original)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(FilePath, original);
        var store = new JsonConfigurationStore(FilePath);
        var result = await store.LoadAsync();
        Assert.Equal(ConfigurationLoadKind.Blocked, result.Kind);
        Assert.Empty(result.Configuration.DeviceProfiles);
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(AudioConfiguration.Empty));
        Assert.Equal(original, await File.ReadAllTextAsync(FilePath));
        await store.ReplaceAsync(AudioConfiguration.Empty);
        var preserved = Assert.Single(Directory.GetFiles(directory, "*.preserved-*"));
        Assert.Equal(original, await File.ReadAllTextAsync(preserved));
    }

    [Fact]
    public async Task FuturePrimaryDoesNotSilentlyDowngradeToOlderBackup()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(FilePath, "{\"version\":999}");
        await File.WriteAllTextAsync(FilePath + ".bak", ConfigurationJson.Serialize(Sample()));
        Assert.Equal(ConfigurationLoadKind.Blocked, (await new JsonConfigurationStore(FilePath).LoadAsync()).Kind);
    }

    [Fact]
    public async Task StaleWriterAndLockedDestinationCannotDestroyValidConfiguration()
    {
        var store = new JsonConfigurationStore(FilePath);
        await store.LoadAsync();
        await store.SaveAsync(Sample());
        var other = new JsonConfigurationStore(FilePath);
        await other.LoadAsync();
        await store.SaveAsync(Sample(.45f));
        var expected = await File.ReadAllTextAsync(FilePath);
        await Assert.ThrowsAsync<IOException>(() => other.SaveAsync(Sample(.7f)));
        using (var locked = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(Sample(.2f)));
        Assert.Equal(expected, await File.ReadAllTextAsync(FilePath));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task CancelledWriteAndInvalidImportLeaveCurrentConfigUntouchedAndExportDoesNotOverwrite()
    {
        var store = new JsonConfigurationStore(FilePath);
        await store.LoadAsync();
        var saved = Sample();
        await store.SaveAsync(saved);
        var expected = await File.ReadAllTextAsync(FilePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(Sample(), cancellation.Token));
        var export = Path.Combine(directory, "export.json");
        await store.ExportAsync(export, saved);
        Assert.Equal(expected, ConfigurationJson.Serialize(await store.ImportAsync(export)));
        await Assert.ThrowsAsync<IOException>(() => store.ExportAsync(export, Sample()));
        await Assert.ThrowsAsync<IOException>(() => store.ExportAsync(FilePath, saved));
        await File.WriteAllTextAsync(export, "{}");
        await Assert.ThrowsAnyAsync<Exception>(() => store.ImportAsync(export));
        Assert.Equal(expected, await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public void RejectsInvalidVolumeDuplicateTargetsUnknownDevicesAndRuntimeIdentity()
    {
        var sample = Sample();
        var profile = sample.Profiles[0];
        var entry = profile.Snapshot.Entries[0];
        Assert.Throws<InvalidDataException>(() => ConfigurationJson.Serialize(sample with { Profiles = [profile with { Snapshot = profile.Snapshot with { Entries = [entry with { Volume = new(2, false) }] } }] }));
        Assert.Throws<InvalidDataException>(() => ConfigurationJson.Serialize(sample with { Profiles = [profile with { Rules = [profile.Rules[0], profile.Rules[0] with { Id = Guid.NewGuid() }] }] }));
        Assert.Throws<InvalidDataException>(() => ConfigurationJson.Serialize(sample with { KnownDevices = [] }));
        Assert.Throws<InvalidDataException>(() => ConfigurationJson.Serialize(sample with { KnownApplications = [new(ApplicationIdentityKind.ExecutablePath, "PID:123")] }));
        Assert.ThrowsAny<Exception>(() => ConfigurationJson.Deserialize(ConfigurationJson.Serialize(sample).Replace("\"level\": 0.37,", "")));
    }

    [Fact]
    public async Task InterruptedTemporaryFileIsIgnoredAndOversizedPrimaryIsBlocked()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(FilePath + ".abandoned.tmp", "{partial");
        var store = new JsonConfigurationStore(FilePath);
        Assert.Equal(ConfigurationLoadKind.Missing, (await store.LoadAsync()).Kind);
        await File.WriteAllBytesAsync(FilePath, new byte[4 * 1024 * 1024 + 1]);
        Assert.Equal(ConfigurationLoadKind.Blocked, (await store.LoadAsync()).Kind);
        await store.ReplaceAsync(AudioConfiguration.Empty);
        Assert.Equal(ConfigurationLoadKind.Loaded, (await store.LoadAsync()).Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyConfigurationMigratesDeviceSelectionsAndKeepsOriginalAsBackupOnFirstSave(bool oldSwitch)
    {
        var sample = Sample();
        var node = System.Text.Json.Nodes.JsonNode.Parse(ConfigurationJson.Serialize(sample))!.AsObject();
        node["version"] = 1;
        node.Remove("deviceProfiles");
        node["settings"]!["automaticRestoreEnabled"] = oldSwitch;
        var original = node.ToJsonString();
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(FilePath, original);
        var store = new JsonConfigurationStore(FilePath);
        var loaded = await store.LoadAsync();
        Assert.Equal(ConfigurationLoadKind.Loaded, loaded.Kind);
        Assert.Equal(2, loaded.Configuration.Version);
        Assert.Equal(sample.ActiveProfileId, Assert.Single(loaded.Configuration.DeviceProfiles).ProfileId);
        Assert.Equal(original, await File.ReadAllTextAsync(FilePath));
        Assert.Equal(sample.Profiles[0].Snapshot, loaded.Configuration.Profiles[0].Snapshot with { Entries = sample.Profiles[0].Snapshot.Entries });
        await store.SaveAsync(loaded.Configuration);
        Assert.Equal(original, await File.ReadAllTextAsync(FilePath + ".bak"));
        Assert.DoesNotContain("automaticRestoreEnabled", await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public void DeviceSelectionRejectsDuplicateUnknownAndIncompatibleReferences()
    {
        var sample = Sample();
        var selection = sample.DeviceProfiles[0];
        Assert.Throws<InvalidDataException>(() => ConfigurationJson.Serialize(sample with { DeviceProfiles = [selection, selection] }));
        Assert.Throws<InvalidDataException>(() => ConfigurationJson.Serialize(sample with { DeviceProfiles = [selection with { ProfileId = Guid.NewGuid() }] }));
        Assert.Throws<InvalidDataException>(() => ConfigurationJson.Serialize(sample with { KnownDevices = sample.KnownDevices.Append(new("device-b", "扬声器")).ToArray(), DeviceProfiles = [new("device-b", selection.ProfileId)] }));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
