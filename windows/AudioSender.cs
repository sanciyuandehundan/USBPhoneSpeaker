using System.Buffers.Binary;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace USBPhoneSpeaker;

internal sealed class FastLoopback(MMDevice device) : WasapiCapture(device, true, 10)
{
    protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
        base.GetAudioClientStreamFlags() | AudioClientStreamFlags.Loopback;
}

internal static class AudioSender
{
    internal static byte[] Header(int rate, int channels)
    {
        var header = new byte[16];
        "USPK"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), rate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), channels);
        return header;
    }

    public static async Task Run(Phone phone, string endpointId, Action<string> status, CancellationToken ct)
    {
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var client = await Usbmux.Connect(phone.Id, connectTimeout.Token);
        using var cancelSocket = ct.Register(client.Dispose);
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(endpointId);
        using var capture = new FastLoopback(device);
        var format = capture.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.IeeeFloat || format.BitsPerSample != 32 || format.Channels > 8)
            throw new NotSupportedException("当前声卡不是受支持的 32 位浮点混音格式。请在 Windows 声音设置中切换为普通立体声输出。");
        var stream = client.GetStream();
        await stream.WriteAsync(Header(format.SampleRate, format.Channels), ct);
        var ready = new byte[1];
        await stream.ReadExactlyAsync(ready, connectTimeout.Token);
        if (ready[0] != 1) throw new IOException("iPhone 没有准备好音频播放。");

        // Keep at most two capture callbacks; a slow USB connection must not accumulate seconds of audio.
        var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
        });
        capture.DataAvailable += (_, e) => queue.Writer.TryWrite(e.Buffer.AsSpan(0, e.BytesRecorded).ToArray());
        capture.RecordingStopped += (_, e) => queue.Writer.TryComplete(e.Exception);
        capture.StartRecording();
        status($"正在通过 USB 发送 · {format.SampleRate} Hz · {format.Channels} 声道\n采集请求：10 ms；手机缓冲请看 iPhone。总延迟需实测。");
        try
        {
            await foreach (var data in queue.Reader.ReadAllAsync(ct))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromMilliseconds(150));
                await stream.WriteAsync(data, deadline.Token);
            }
        }
        finally
        {
            capture.StopRecording();
        }
    }
}
