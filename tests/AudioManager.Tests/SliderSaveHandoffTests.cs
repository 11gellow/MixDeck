using AudioManager.Core.Configuration;
using AudioManager.Core.Interfaces;
using AudioManager.Core.Services;
using AudioManager.Desktop.ViewModels;

namespace AudioManager.Tests;

public sealed class SliderSaveHandoffTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DragStartedWhilePreviousSaveIsPendingKeepsItsOwnEditAndSavesFinalActual(bool application, bool otherEdit)
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        scenario.Raw.AddSession("slider", .4f);
        var store = new PausingStore(scenario.Store);
        using var service = new AudioConfigurationService(store, scenario.Guard);
        using var configuration = new ConfigurationViewModel(service, new ConfigurationViewModelTests.Monitor(scenario),
            new ConfigurationViewModelTests.FakeStartup(), new ConfigurationViewModelTests.FakeDialogs(), new());
        using var main = new MainViewModel(scenario.Guard, new ConfigurationViewModelTests.Monitor(scenario), new(), new(), TimeProvider.System, configuration);
        await main.InitializeAsync();
        var row = application ? main.Sessions.Single() : null;
        void SetVolume(double value) { if (row is null) main.MasterVolume = value; else row.Volume = value; }
        if (otherEdit) { main.BeginVolumeEdit(); main.MasterVolume = 30; }
        main.BeginVolumeEdit(row);
        SetVolume(20);
        store.PauseNext = true;
        var firstFinish = main.FinishVolumeEditsAsync();
        try
        {
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            main.BeginVolumeEdit(row);
            SetVolume(80);
            var secondFinish = main.FinishVolumeEditsAsync();
            store.Release.TrySetResult();
            await Task.WhenAll(firstFinish, secondFinish).WaitAsync(TimeSpan.FromSeconds(3));
            var actual = application ? scenario.Raw.Sessions.Values.Single().Volume.Level : scenario.Raw.Master.Level;
            Assert.Equal(.8f, actual);
            var target = service.ProfileForDevice("device-a")!.Rules.Single(rule => (rule.Target.Target.Application is not null) == application);
            Assert.Equal(actual, target.Target.Volume.Level);
            var persisted = await scenario.Store.LoadAsync();
            Assert.Equal(actual, persisted.Configuration.Profiles.Single().Rules.Single(rule => rule.Id == target.Id).Target.Volume.Level);
        }
        finally { store.Release.TrySetResult(); await firstFinish; }
    }

    private sealed class PausingStore(IConfigurationStore inner) : IConfigurationStore
    {
        public bool PauseNext { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string FilePath => inner.FilePath;
        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken = default) => inner.LoadAsync(cancellationToken);
        public async Task SaveAsync(AudioConfiguration configuration, CancellationToken cancellationToken = default)
        {
            if (PauseNext)
            {
                PauseNext = false;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            await inner.SaveAsync(configuration, cancellationToken);
        }
        public Task ReplaceAsync(AudioConfiguration configuration, CancellationToken cancellationToken = default) => inner.ReplaceAsync(configuration, cancellationToken);
        public Task<AudioConfiguration> ImportAsync(string path, CancellationToken cancellationToken = default) => inner.ImportAsync(path, cancellationToken);
        public Task ExportAsync(string path, AudioConfiguration configuration, CancellationToken cancellationToken = default) => inner.ExportAsync(path, configuration, cancellationToken);
    }
}
