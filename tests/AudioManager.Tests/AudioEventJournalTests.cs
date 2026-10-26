using System.IO;
using System.Text.Json;
using AudioManager.Core.Events;
using AudioManager.Core.Models;
using AudioManager.Desktop.Services;

namespace AudioManager.Tests;

public sealed class AudioEventJournalTests
{
    [Fact]
    public async Task RecordsBeforeAndAfterTargetsAndExactContextWithoutWritingAudioOrConfiguration()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = scenario.Raw.AddSession("player", .1f);
        await scenario.StartAsync();
        var journal = new AudioEventJournal(Path.Combine(scenario.Directory, "diagnostics", "events.jsonl"));
        journal.Record("initial", scenario.State(), null, scenario.Service.Current);
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(1, false), State = AudioSessionState.Active };
        var state = scenario.State();
        var context = Guid.NewGuid();
        var change = new AudioStateChanged(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.External, state, "device-a", session.Key, context);
        var persisted = await File.ReadAllTextAsync(scenario.Store.FilePath);
        var writes = scenario.Raw.Writes.Count;
        journal.Record("received", state, change, scenario.Service.Current);
        Assert.Equal(persisted, await File.ReadAllTextAsync(scenario.Store.FilePath));
        Assert.Equal(writes, scenario.Raw.Writes.Count);
        await scenario.Service.HandleStateAsync(state, change);
        journal.Record("processed", state, change, scenario.Service.Current);
        var lines = await File.ReadAllLinesAsync(journal.FilePath);
        Assert.Equal(3, lines.Length);
        using var received = JsonDocument.Parse(lines[1]);
        using var processed = JsonDocument.Parse(lines[2]);
        var receivedData = received.RootElement.GetProperty("Data");
        Assert.Equal(context, receivedData.GetProperty("Change").GetProperty("EventContext").GetGuid());
        Assert.Equal("External", receivedData.GetProperty("Change").GetProperty("Origin").GetString());
        static float ApplicationTarget(JsonElement data) => data.GetProperty("Devices")[0].GetProperty("Targets")
            .EnumerateArray().Single(rule => rule.GetProperty("Target").GetProperty("Target").GetProperty("Application").ValueKind != JsonValueKind.Null)
            .GetProperty("Target").GetProperty("Volume").GetProperty("Level").GetSingle();
        Assert.Equal(.1f, ApplicationTarget(receivedData));
        Assert.Equal(1f, ApplicationTarget(processed.RootElement.GetProperty("Data")));
        Assert.Null(journal.LastError);
    }

    [Fact]
    public async Task RotatesAndKeepsRestoreReadbackAndSurvivesUnavailableDirectory()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var path = Path.Combine(scenario.Directory, "events.jsonl");
        await File.WriteAllTextAsync(path, new string('x', 1024 * 1024));
        var journal = new AudioEventJournal(path);
        journal.RecordRestore("device-a", null, new(.1f, true));
        Assert.Equal(1024 * 1024, new FileInfo(path + ".previous").Length);
        using var line = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(.1f, line.RootElement.GetProperty("Data").GetProperty("Actual").GetProperty("Level").GetSingle());
        var unavailable = new AudioEventJournal(Path.Combine(path, "invalid.jsonl"));
        unavailable.RecordRestore("device-a", null, new(.1f, true));
        Assert.NotNull(unavailable.LastError);
    }
}
