using NAudio.CoreAudioApi;

namespace AudioManager.Tests.Integration;

/// <summary>只供测试的共享模式无声流，以随机 GUID 创建独立会话。</summary>
internal sealed class SilentAudioSession : IDisposable
{
    private readonly MMDeviceEnumerator enumerator;
    private readonly MMDevice device;
    private readonly AudioClient client;
    private bool started;

    internal SilentAudioSession(string deviceId)
    {
        enumerator = new MMDeviceEnumerator();
        try
        {
            device = enumerator.GetDevice(deviceId);
            try
            {
                client = device.CreateAudioClient();
                try
                {
                    client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None,
                        1_000_000, 0, client.MixFormat, Guid.NewGuid());
                    using var renderer = client.AudioRenderClient;
                    renderer.GetBuffer(client.BufferSize);
                    renderer.ReleaseBuffer(client.BufferSize, AudioClientBufferFlags.Silent);
                    client.Start();
                    started = true;
                }
                catch { client.Dispose(); throw; }
            }
            catch { device.Dispose(); throw; }
        }
        catch { enumerator.Dispose(); throw; }
    }

    internal void Pause()
    {
        if (!started) return;
        client.Stop();
        started = false;
    }

    internal void Resume()
    {
        if (started) return;
        client.Start();
        started = true;
    }

    public void Dispose()
    {
        try { if (started) { client.Stop(); started = false; } }
        finally
        {
            try { client.Dispose(); }
            finally { try { device.Dispose(); } finally { enumerator.Dispose(); } }
        }
    }
}
