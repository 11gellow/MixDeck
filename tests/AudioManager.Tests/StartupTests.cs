using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using AudioManager.Core.Interfaces;
using AudioManager.Desktop.Services;
using AudioManager.Windows;
using Microsoft.Win32;

namespace AudioManager.Tests;

public sealed class StartupTests
{
    private const string Exe = @"C:\Program Files\MixDeck\MixDeck.exe";
    private const string Sid = "S-1-5-21-123";

    [Fact]
    public void DefaultMigrationPreservesOffAndMovesEnabledRunOnlyOnce()
    {
        var tasks = new Tasks(); var legacy = new Legacy();
        var startup = new ScheduledLoginStartup(Exe, Sid, tasks, legacy);
        Assert.False(startup.MigrateEnabledLegacyStartup());
        Assert.Null(tasks.Xml);
        legacy.IsEnabled = true;
        Assert.True(startup.MigrateEnabledLegacyStartup());
        Assert.False(legacy.IsEnabled); Assert.True(startup.IsEnabled);
        var definition = tasks.Xml;
        Assert.False(startup.MigrateEnabledLegacyStartup());
        Assert.Equal(definition, tasks.Xml);
    }

    [Fact]
    public void DefaultMigrationFailureKeepsLegacyEnabled()
    {
        var tasks = new Tasks { FailNextRegister = true }; var legacy = new Legacy { IsEnabled = true };
        var startup = new ScheduledLoginStartup(Exe, Sid, tasks, legacy);
        Assert.Throws<IOException>(() => startup.MigrateEnabledLegacyStartup());
        Assert.True(legacy.IsEnabled); Assert.Null(tasks.Xml);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UninstallOnlyRemovesEntriesOwnedByCurrentExecutable(bool ownTask)
    {
        var tasks = new Tasks { Xml = ScheduledLoginStartup.CreateDefinition(ownTask ? Exe : @"C:\other\MixDeck.exe", Sid) };
        var original = tasks.Xml;
        var legacy = new Legacy { IsEnabled = false };
        new ScheduledLoginStartup(Exe, Sid, tasks, legacy).RemoveOwnedEntries();
        Assert.Equal(ownTask ? null : original, tasks.Xml);
        Assert.False(legacy.IsEnabled);
        legacy.IsEnabled = true;
        new ScheduledLoginStartup(Exe, Sid, tasks, legacy).RemoveOwnedEntries();
        Assert.False(legacy.IsEnabled);
    }

    [Fact]
    public void UninstallFailureRestoresOwnedRegistration()
    {
        var tasks = new Tasks { Xml = ScheduledLoginStartup.CreateDefinition(Exe, Sid) };
        var legacy = new Legacy { IsEnabled = true, FailNextWrite = true };
        Assert.Throws<IOException>(() => new ScheduledLoginStartup(Exe, Sid, tasks, legacy).RemoveOwnedEntries());
        Assert.NotNull(tasks.Xml); Assert.True(legacy.IsEnabled);
    }

    [Fact]
    public void MigrationRegistersAndVerifiesBeforeRemovingRunAndDisableRemovesBoth()
    {
        var tasks = new Tasks(); var legacy = new Legacy { IsEnabled = true };
        var sut = new ScheduledLoginStartup(Exe, Sid, tasks, legacy);
        Assert.True(sut.IsEnabled);
        Assert.Null(tasks.Xml); // 只读状态不能偷偷迁移/重新启用。
        legacy.BeforeWrite = enabled => { if (!enabled) Assert.NotNull(tasks.Xml); };
        sut.SetEnabled(true);
        legacy.BeforeWrite = null;
        Assert.False(legacy.IsEnabled);
        Assert.True(sut.IsEnabled);
        Assert.Equal("task", sut.RegistrationKind);
        Assert.DoesNotContain("<Delay>", tasks.Xml!);
        Assert.Contains("<RunLevel>LeastPrivilege</RunLevel>", tasks.Xml!);
        Assert.Contains("<Priority>6</Priority>", tasks.Xml!);
        sut.SetEnabled(false);
        sut.SetEnabled(false);
        Assert.Null(tasks.Xml);
        Assert.False(sut.IsEnabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RegisterFailureOrBadReadbackPreservesOldRun(bool throwRegister)
    {
        var tasks = new Tasks { FailNextRegister = throwRegister, CorruptNextRegister = !throwRegister };
        var legacy = new Legacy { IsEnabled = true };
        var sut = new ScheduledLoginStartup(Exe, Sid, tasks, legacy);
        Assert.Throws<IOException>(() => sut.SetEnabled(true));
        Assert.True(legacy.IsEnabled);
        Assert.Null(tasks.Xml);
    }

    [Fact]
    public void FailedLegacyRemovalRestoresPreviousTaskAndLegacyAndOffFailureRollsBack()
    {
        var tasks = new Tasks { Xml = ScheduledLoginStartup.CreateDefinition(@"C:\old\MixDeck.exe", Sid) };
        var original = tasks.Xml;
        var legacy = new Legacy { IsEnabled = true, FailNextWrite = true };
        var sut = new ScheduledLoginStartup(Exe, Sid, tasks, legacy);
        Assert.Throws<IOException>(() => sut.SetEnabled(true));
        Assert.Equal(original, tasks.Xml);
        Assert.True(legacy.IsEnabled);
        legacy.FailNextWrite = true;
        Assert.Throws<IOException>(() => sut.SetEnabled(false));
        Assert.Equal(original, tasks.Xml);
        Assert.True(legacy.IsEnabled);
    }

    [Theory]
    [InlineData("<Enabled>true</Enabled>", "<Enabled>false</Enabled>")]
    [InlineData("<RunLevel>LeastPrivilege</RunLevel>", "<RunLevel>HighestAvailable</RunLevel>")]
    [InlineData("<Arguments>--tray</Arguments>", "<Arguments></Arguments>")]
    [InlineData("<LogonTrigger>", "<LogonTrigger><Delay>PT30S</Delay>")]
    public void ExternalChangesAreNotReportedAsCorrectRegistration(string old, string replacement)
    {
        var expected = ScheduledLoginStartup.CreateDefinition(Exe, Sid);
        var tasks = new Tasks { Xml = expected.Replace(old, replacement) };
        Assert.False(new ScheduledLoginStartup(Exe, Sid, tasks, new Legacy()).IsEnabled);
        Assert.False(ScheduledLoginStartup.Matches("broken xml", expected));
    }

    [Fact]
    public void PathChangesCanBeUpdatedAndLegacyRollbackIsExplicit()
    {
        var tasks = new Tasks { Xml = ScheduledLoginStartup.CreateDefinition(@"C:\old\MixDeck.exe", Sid) };
        var legacy = new Legacy();
        var sut = new ScheduledLoginStartup(Exe, Sid, tasks, legacy);
        Assert.False(sut.IsEnabled);
        sut.SetEnabled(true);
        Assert.True(sut.IsEnabled);
        sut.UseLegacy();
        Assert.Null(tasks.Xml);
        Assert.True(legacy.IsEnabled);
        Assert.Equal("run", sut.RegistrationKind);
    }

    [Fact]
    public void FailedRunRollbackPreservesExactOldPathInsteadOfOnlyEnabledBoolean()
    {
        var path = @"Software\MixDeck.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path);
            const string previous = @"""C:\old path\MixDeck.exe"" --tray";
            key.SetValue("Test", previous);
            var tasks = new Tasks { FailNextDelete = true };
            var legacy = new WindowsLoginStartup(Exe, "Test", path);
            var sut = new ScheduledLoginStartup(Exe, Sid, tasks, legacy);
            Assert.Throws<IOException>(sut.UseLegacy);
            Assert.Equal(previous, key.GetValue("Test"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, false); }
    }

    [Fact]
    public void DuplicateDesktopInstanceSignalsPrimaryWithoutOwningAnotherAudioLifetime()
    {
        var name = "Local\\MixDeck-Tests-" + Guid.NewGuid().ToString("N");
        using var first = new DesktopInstance(name);
        using var signal = new ManualResetEventSlim();
        first.Listen(signal.Set);
        using (var second = new DesktopInstance(name))
        {
            Assert.True(first.IsPrimary);
            Assert.False(second.IsPrimary);
            second.RequestShow();
            Assert.True(signal.Wait(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task SeparateProcessLaunchSignalsExistingInstanceAndExitReleasesOwnership()
    {
        var name = "Local\\MixDeck-ProcessTest-" + Guid.NewGuid().ToString("N");
        Process Start()
        {
            var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardInput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add(typeof(StartupTests).Assembly.Location);
            info.ArgumentList.Add("--desktop-instance-fixture"); info.ArgumentList.Add(name);
            return Process.Start(info)!;
        }
        using var first = Start();
        try
        {
            Assert.Equal("PRIMARY", await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            using var second = Start();
            Assert.Equal("SECONDARY", await second.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, second.ExitCode);
            Assert.Equal("SHOW", await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            await first.StandardInput.WriteLineAsync("exit");
            await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, first.ExitCode);
            using var third = Start();
            try
            {
                Assert.Equal("PRIMARY", await third.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
                await third.StandardInput.WriteLineAsync("exit");
                await third.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, third.ExitCode);
            }
            finally { if (!third.HasExited) third.Kill(true); }
        }
        finally { if (!first.HasExited) first.Kill(true); }
    }

    internal static int RunInstanceFixture(string name)
    {
        using var instance = new DesktopInstance(name);
        if (instance.IsPrimary)
        {
            instance.Listen(() => { Console.WriteLine("SHOW"); Console.Out.Flush(); });
            Console.WriteLine("PRIMARY"); Console.Out.Flush();
            Console.ReadLine();
        }
        else { instance.RequestShow(); Console.WriteLine("SECONDARY"); Console.Out.Flush(); }
        return 0;
    }

    [Fact]
    public void TimingKeepsMilestonesOnceAndDoesNotBlockOnUnwritablePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MixDeck-Startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "startup.jsonl");
            var journal = new StartupJournal(path);
            journal.Mark("app-entry", "tray"); journal.Mark("app-entry"); journal.Mark("listener-ready");
            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            using var line = JsonDocument.Parse(lines[0]);
            Assert.Equal("tray", line.RootElement.GetProperty("Detail").GetString());
            Assert.True(line.RootElement.GetProperty("SinceProcessMs").GetDouble() >= 0);
            var login = WindowsSessionTiming.GetLogonTime();
            if (login is { } at) Assert.True(at <= DateTimeOffset.UtcNow && at.Year > 2000);
            new StartupJournal(directory).Mark("unwritable");
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class Tasks : ILoginTaskStore
    {
        public string? Xml;
        public bool FailNextRegister, CorruptNextRegister, FailNextDelete;
        public string? Read() => Xml;
        public void Register(string xml)
        {
            if (FailNextRegister) { FailNextRegister = false; throw new IOException("denied"); }
            Xml = CorruptNextRegister ? "invalid" : xml;
            CorruptNextRegister = false;
        }
        public void Delete()
        {
            if (FailNextDelete) { FailNextDelete = false; throw new IOException("cannot delete"); }
            Xml = null;
        }
    }
    private sealed class Legacy : ILoginStartup
    {
        public bool IsEnabled { get; set; }
        public bool FailNextWrite;
        public Action<bool>? BeforeWrite;
        public void SetEnabled(bool enabled)
        {
            BeforeWrite?.Invoke(enabled);
            if (FailNextWrite) { FailNextWrite = false; throw new IOException("Run unavailable"); }
            IsEnabled = enabled;
        }
    }
}

public sealed class StartupNativeFactAttribute : FactAttribute
{
    public StartupNativeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MIXDECK_STARTUP_INTEGRATION") != "1") Skip = "独立计划任务注册测试需显式启用";
    }
}

public sealed class StartupNativeTests
{
    [StartupNativeFact]
    [Trait("Category", "StartupIntegration")]
    public void RegisterReadUpdateAndDeleteIsolatedCurrentUserTaskWithoutRunningAudio()
    {
        var logon = WindowsSessionTiming.GetLogonTime();
        Assert.NotNull(logon);
        Assert.True(logon <= Process.GetCurrentProcess().StartTime.ToUniversalTime());
        var store = new LoginTaskStore("MixDeck-Test-" + Guid.NewGuid().ToString("N"));
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var xml = ScheduledLoginStartup.CreateDefinition(Path.Combine(Environment.SystemDirectory, "cmd.exe"), sid);
        try
        {
            Assert.Null(store.Read());
            store.Register(xml);
            Assert.True(ScheduledLoginStartup.Matches(store.Read()!, xml), store.Read());
            var changed = xml.Replace("<Priority>6</Priority>", "<Priority>5</Priority>");
            store.Register(changed);
            Assert.True(ScheduledLoginStartup.Matches(store.Read()!, changed));
        }
        finally { store.Delete(); }
        Assert.Null(store.Read());
    }
}
