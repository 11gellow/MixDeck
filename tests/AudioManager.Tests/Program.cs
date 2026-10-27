using AudioManager.Tests.Integration;

namespace AudioManager.Tests;

internal static class Program
{
    // VSTest 通过 testhost 加载测试。此入口用于独立音频/桌面测试夹具。
    [MTAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--appearance-fixture") return AppearanceTests.RunFixture(args[1]);
        if (args.Length == 2 && args[0] == "--slider-track-fixture") return SliderInteractionTests.RunFixture(args[1]);
        if (args.Length == 1 && args[0] == "--desktop-audio-fixture") return DesktopAudioFixture.Run();
        if (args.Length == 3 && args[0] == "--configuration-restore-probe" && uint.TryParse(args[2], out var pid))
            return ConfigurationRestoreProbe.Run(args[1], pid);
        if (args.Length != 2 || args[0] != "--silent-session") return 2;
        try
        {
            using var session = new SilentAudioSession(args[1]);
            Console.WriteLine("READY");
            Console.Out.Flush();
            while (Console.ReadLine() is { } command)
            {
                if (command == "pause") { session.Pause(); Console.WriteLine("PAUSED"); Console.Out.Flush(); }
                else if (command == "resume") { session.Resume(); Console.WriteLine("RESUMED"); Console.Out.Flush(); }
                else break;
            }
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}
