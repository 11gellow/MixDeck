using System.Collections.ObjectModel;
using AudioManager.Core.Models;

namespace AudioManager.Desktop.ViewModels;

/// <param name="Source">变化来源标签（如“外部”“系统”“本软件”），无来源时为 null。</param>
public sealed record OperationEntry(DateTime Time, string Text, bool IsError, string? Source = null, string? CoalesceKey = null)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}

/// <summary>界面操作反馈，仅保存在内存中。</summary>
public sealed class OperationLog : ObservableObject
{
    private const int MaxEntries = 100;
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(2);
    private OperationEntry? latest;

    public ObservableCollection<OperationEntry> Entries { get; } = [];

    public OperationEntry? Latest
    {
        get => latest;
        private set => SetProperty(ref latest, value);
    }

    public void Info(string text, string? source = null) => Add(new(DateTime.Now, text, false, source));

    /// <summary>连续同类事件（如外部拖动产生的多次音量变化）在短时间内合并为一条，只保留最新内容。</summary>
    public void Coalesce(string key, string text, string? source = null)
    {
        var now = DateTime.Now;
        if (Entries.Count > 0 && Entries[0].CoalesceKey == key && now - Entries[0].Time < CoalesceWindow)
        {
            var entry = new OperationEntry(now, text, false, source, key);
            Entries[0] = entry;
            Latest = entry;
            return;
        }
        Add(new(now, text, false, source, key));
    }

    public void Warning(string text, string? source = null) => Add(new(DateTime.Now, text, true, source));

    public void Error(string context, Exception exception) => Add(new(DateTime.Now, $"{context}：{Describe(exception)}", true));

    public static string Describe(Exception exception) => exception switch
    {
        AudioControlException audio => $"{audio.Message}（{DescribeCode(audio.Code)}{FormatHResult(audio.NativeErrorCode)}）",
        _ => exception.Message
    };

    private static string DescribeCode(AudioErrorCode code) => code switch
    {
        AudioErrorCode.DeviceUnavailable => "设备不可用",
        AudioErrorCode.SessionUnavailable => "会话已失效",
        AudioErrorCode.AccessDenied => "拒绝访问",
        _ => "音频 API 调用失败"
    };

    private static string FormatHResult(int? hresult) => hresult is { } value ? $"，0x{value:X8}" : "";

    private void Add(OperationEntry entry)
    {
        Entries.Insert(0, entry);
        while (Entries.Count > MaxEntries) Entries.RemoveAt(Entries.Count - 1);
        Latest = entry;
    }
}
