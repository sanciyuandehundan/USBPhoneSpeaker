using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace USBPhoneSpeaker;

internal record Phone(int Id, string Serial)
{
    public override string ToString() => $"iPhone / iPad · {Serial}";
}

internal static class Usbmux
{
    internal static XElement Value(XElement dict, string key) =>
        dict.Elements("key").First(k => k.Value == key).ElementsAfterSelf().First();

    internal static byte[] Request(string message, params object[] extra)
    {
        var dict = new XElement("dict",
            new XElement("key", "MessageType"), new XElement("string", message),
            new XElement("key", "ClientVersionString"), new XElement("string", "USBPhoneSpeaker-1.0"),
            new XElement("key", "ProgName"), new XElement("string", "USBPhoneSpeaker"),
            new XElement("key", "kLibUSBMuxVersion"), new XElement("integer", 3), extra);
        var xml = Encoding.UTF8.GetBytes(new XElement("plist", new XAttribute("version", "1.0"), dict).ToString());
        var packet = new byte[16 + xml.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet, packet.Length);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), 8);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12), 1);
        xml.CopyTo(packet, 16);
        return packet;
    }

    private static async Task<XElement> Exchange(TcpClient client, byte[] request, CancellationToken ct)
    {
        var stream = client.GetStream();
        await stream.WriteAsync(request, ct);
        var header = new byte[16];
        await stream.ReadExactlyAsync(header, ct);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 16 || length > 1024 * 1024 || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8)) != 8)
            throw new IOException("Apple USB 服务返回了无效数据。");
        var body = new byte[length - 16];
        await stream.ReadExactlyAsync(body, ct);
        return XDocument.Parse(Encoding.UTF8.GetString(body)).Root!.Element("dict")!;
    }

    public static async Task<List<Phone>> List(CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, 27015, ct);
        var response = await Exchange(client, Request("ListDevices"), ct);
        var devices = new List<Phone>();
        foreach (var item in Value(response, "DeviceList").Elements("dict"))
        {
            var props = Value(item, "Properties");
            if (Value(props, "ConnectionType").Value == "USB")
                devices.Add(new Phone(int.Parse(Value(item, "DeviceID").Value), Value(props, "SerialNumber").Value));
        }
        return devices;
    }

    public static async Task<TcpClient> Connect(int deviceId, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true, SendBufferSize = 8192 };
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, 27015, ct);
            ushort networkPort = BinaryPrimitives.ReverseEndianness((ushort)24680);
            var response = await Exchange(client, Request("Connect",
                new XElement("key", "DeviceID"), new XElement("integer", deviceId),
                new XElement("key", "PortNumber"), new XElement("integer", networkPort)), ct);
            int result = int.Parse(Value(response, "Number").Value);
            if (result != 0)
                throw new IOException($"连接 iPhone 失败（Apple USB 错误 {result}）。请先在手机 App 中点击“开始接收”。");
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
