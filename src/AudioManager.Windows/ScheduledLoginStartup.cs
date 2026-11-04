using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;
using AudioManager.Core.Interfaces;

namespace AudioManager.Windows;

/// <summary>当前交互用户的登录任务。旧 Run 在显式启用成功后才移除。</summary>
public sealed class ScheduledLoginStartup : ILoginStartup
{
    private readonly ILoginTaskStore tasks;
    private readonly ILoginStartup legacy;
    private readonly string definition;

    public ScheduledLoginStartup(string executablePath) : this(executablePath,
        WindowsIdentity.GetCurrent().User!.Value, new LoginTaskStore(), new WindowsLoginStartup(executablePath)) { }

    internal ScheduledLoginStartup(string executablePath, string sid, ILoginTaskStore tasks, ILoginStartup legacy)
    {
        _ = WindowsLoginStartup.BuildCommand(executablePath);
        this.tasks = tasks;
        this.legacy = legacy;
        definition = CreateDefinition(executablePath, sid);
    }

    public bool IsEnabled
    {
        get
        {
            // 已有 Run 可在计划任务服务暂不可用时继续工作。
            if (legacy.IsEnabled) return true;
            var xml = tasks.Read();
            return xml is not null && Matches(xml, definition);
        }
    }

    public void SetEnabled(bool enabled)
    {
        var previous = tasks.Read();
        var wasLegacyEnabled = legacy.IsEnabled;
        var rollbackLegacy = legacy is WindowsLoginStartup registry ? registry.CaptureRollback() : () => legacy.SetEnabled(wasLegacyEnabled);
        try
        {
            if (enabled)
            {
                tasks.Register(definition);
                var actual = tasks.Read();
                if (actual is null || !Matches(actual, definition)) throw new IOException("登录计划任务注册后回读不一致。");
                legacy.SetEnabled(false);
            }
            else
            {
                tasks.Delete();
                if (tasks.Read() is not null) throw new IOException("无法确认登录计划任务已删除。");
                legacy.SetEnabled(false);
            }
        }
        catch (Exception operationError)
        {
            try
            {
                if (previous is null) tasks.Delete(); else tasks.Register(previous);
                rollbackLegacy();
            }
            catch (Exception rollbackError) { throw new AggregateException("登录启动修改失败，计划任务回退也失败；请检查实际启动项。", operationError, rollbackError); }
            throw;
        }
    }

    /// <summary>实机 A/B 基线及回退入口；不接触音频配置。</summary>
    public void UseLegacy()
    {
        var previous = tasks.Read();
        var wasEnabled = legacy.IsEnabled;
        var rollbackLegacy = legacy is WindowsLoginStartup registry ? registry.CaptureRollback() : () => legacy.SetEnabled(wasEnabled);
        try
        {
            legacy.SetEnabled(true);
            tasks.Delete();
            if (tasks.Read() is not null) throw new IOException("无法移除登录任务，未完成 Run 回退。");
        }
        catch
        {
            if (previous is not null) tasks.Register(previous);
            rollbackLegacy();
            throw;
        }
    }

    public string RegistrationKind => legacy.IsEnabled ? "run" : tasks.Read() is { } xml && Matches(xml, definition) ? "task" : "disabled-or-mismatched";

    internal static string CreateDefinition(string executablePath, string sid)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, object value) => new(ns + name, value);
        return new XElement(ns + "Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", new[] { E("Author", "MixDeck"), E("Description", "MixDeck current-user login startup v1") }),
            E("Triggers", E("LogonTrigger", new[] { E("Enabled", "true"), E("UserId", sid) })),
            E("Principals", new XElement(ns + "Principal", new XAttribute("id", "CurrentUser"),
                E("UserId", sid), E("LogonType", "InteractiveToken"), E("RunLevel", "LeastPrivilege"))),
            E("Settings", new[] { E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", "false"),
                E("StopIfGoingOnBatteries", "false"), E("AllowHardTerminate", "false"), E("StartWhenAvailable", "true"),
                E("RunOnlyIfNetworkAvailable", "false"), E("Enabled", "true"), E("ExecutionTimeLimit", "PT0S"), E("Priority", "6") }),
            new XElement(ns + "Actions", new XAttribute("Context", "CurrentUser"), E("Exec", new[] {
                E("Command", executablePath), E("Arguments", "--tray"), E("WorkingDirectory", Path.GetDirectoryName(executablePath)!) })))
            .ToString(SaveOptions.DisableFormatting);
    }

    internal static bool Matches(string actual, string expected)
    {
        try
        {
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var a = XElement.Parse(actual); var b = XElement.Parse(expected);
            string? Value(XElement root, params string[] path)
            {
                XElement? node = root;
                foreach (var part in path) node = node?.Element(ns + part);
                var value = node?.Value;
                // Task Scheduler 回读省略默认值，触发器还会把 SID 规范化为 DOMAIN\user。
                if (path[^1] == "Enabled") return value ?? "true";
                if (path[^1] == "RunLevel") return value ?? "LeastPrivilege";
                if (path[^1] == "RunOnlyIfNetworkAvailable") return value ?? "false";
                if (path[^1] == "UserId" && value is not null && !value.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
                {
                    try { return new NTAccount(value).Translate(typeof(SecurityIdentifier)).Value; }
                    catch (IdentityNotMappedException) { return value; }
                }
                return value;
            }
            string[][] fields = [["RegistrationInfo", "Description"], ["Triggers", "LogonTrigger", "UserId"],
                ["Triggers", "LogonTrigger", "Enabled"], ["Principals", "Principal", "UserId"],
                ["Principals", "Principal", "LogonType"], ["Principals", "Principal", "RunLevel"],
                ["Actions", "Exec", "Command"], ["Actions", "Exec", "Arguments"], ["Actions", "Exec", "WorkingDirectory"],
                ["Settings", "Enabled"], ["Settings", "MultipleInstancesPolicy"], ["Settings", "ExecutionTimeLimit"],
                ["Settings", "Priority"], ["Settings", "DisallowStartIfOnBatteries"], ["Settings", "StopIfGoingOnBatteries"],
                ["Settings", "RunOnlyIfNetworkAvailable"], ["Settings", "StartWhenAvailable"], ["Settings", "AllowHardTerminate"]];
            return a.Element(ns + "Triggers")?.Elements().Count() == 1
                && a.Element(ns + "Actions")?.Elements().Count() == 1
                && a.Element(ns + "Triggers")?.Element(ns + "LogonTrigger")?.Element(ns + "Delay") is null
                && fields.All(path => string.Equals(Value(a, path), Value(b, path), StringComparison.OrdinalIgnoreCase));
        }
        catch (System.Xml.XmlException) { return false; }
    }
}

internal interface ILoginTaskStore
{
    string? Read();
    void Register(string xml);
    void Delete();
}

internal sealed class LoginTaskStore(string? taskName = null) : ILoginTaskStore
{
    private readonly string name = taskName ?? "MixDeck-" + WindowsIdentity.GetCurrent().User!.Value;
    public string? Read() => WithFolder<string?>(folder =>
    {
        object? task = null;
        try
        {
            task = folder.GetTask(name);
            string xml = ((dynamic)task).Xml;
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            if (XElement.Parse(xml).Element(ns + "RegistrationInfo")?.Element(ns + "Description")?.Value != "MixDeck current-user login startup v1")
                throw new IOException("同名计划任务不属于 MixDeck，未覆盖。");
            return xml;
        }
        catch (Exception exception) when (exception.HResult == unchecked((int)0x80070002)) { return null; }
        finally { Release(task); }
    });

    public void Register(string xml) => WithFolder<object?>(folder =>
    {
        _ = Read(); // 拒绝覆盖同名第三方任务。
        object? task = null;
        try { task = folder.RegisterTask(name, xml, 6, null, null, 3, null); return null; }
        finally { Release(task); }
    });

    public void Delete() => WithFolder<object?>(folder =>
    {
        if (Read() is not null) folder.DeleteTask(name, 0);
        return null;
    });

    private static T WithFolder<T>(Func<dynamic, T> action)
    {
        object? service = null, folder = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!);
            ((dynamic)service!).Connect();
            folder = ((dynamic)service).GetFolder("\\");
            return action(folder);
        }
        finally { Release(folder); Release(service); }
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
