using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using Claunia.PropertyList;

namespace USBPhoneSpeaker;

internal static class FileTransfer
{
    static XElement Dict(params object[] values) => new("dict", values);
    static object[] Field(string name, string value) => [new XElement("key", name), new XElement("string", value)];
    static XElement? Optional(XElement dict, string name) => dict.Elements("key")
        .FirstOrDefault(k => k.Value == name)?.ElementsAfterSelf().First();

    static async Task<XElement> Plist(Stream stream, XElement request, CancellationToken ct)
    {
        var data = Encoding.UTF8.GetBytes(new XElement("plist", new XAttribute("version", "1.0"), request).ToString());
        var size = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(size, data.Length);
        await stream.WriteAsync(size, ct);
        await stream.WriteAsync(data, ct);
        await stream.ReadExactlyAsync(size, ct);
        int length = BinaryPrimitives.ReadInt32BigEndian(size);
        if (length < 1 || length > 16 * 1024 * 1024) throw new IOException("手机返回了无效的服务数据。");
        var response = new byte[length];
        await stream.ReadExactlyAsync(response, ct);
        var result = XDocument.Parse(PropertyListParser.Parse(response).ToXmlPropertyList()).Root!.Element("dict")!;
        if (Optional(result, "Error") is { } error) throw new IOException($"手机拒绝了文件服务请求：{error.Value}。请解锁手机，并确认安装了 0.2.1 或更新版 USB 喇叭。");
        return result;
    }

    sealed class Connection(TcpClient client) : IDisposable
    {
        public Stream Stream = client.GetStream();
        public async Task Secure(X509Certificate2 host, X509Certificate2 device, CancellationToken ct)
        {
            var ssl = new SslStream(Stream, false, (_, certificate, _, _) =>
                certificate != null && certificate.GetRawCertData().AsSpan().SequenceEqual(device.RawData));
            Stream = ssl;
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "iPhone",
                ClientCertificates = new X509CertificateCollection { host },
                EnabledSslProtocols = SslProtocols.Tls12,
            }, ct);
        }
        public void Dispose() { Stream.Dispose(); client.Dispose(); }
    }

    public static async Task<List<string>> Send(Phone phone, string[] paths, Action<string> progress, CancellationToken ct)
    {
        var pair = (NSDictionary)PropertyListParser.Parse(await Usbmux.PairRecord(phone, ct));
        string PairString(string name) => pair[name].ToString()!;
        string PairPem(string name) => Encoding.UTF8.GetString(((NSData)pair[name]).Bytes);
        using var pem = X509Certificate2.CreateFromPem(PairPem("HostCertificate"), PairPem("HostPrivateKey"));
        // Schannel requires a native key handle; all certificate material comes from the existing USB pairing.
        using var host = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
        using var device = X509Certificate2.CreateFromPem(PairPem("DeviceCertificate"));
        using var lockdown = new Connection(await Usbmux.Connect(phone.Id, ct, 62078));
        var session = await Plist(lockdown.Stream, Dict(Field("Label", "USBPhoneSpeaker"), Field("Request", "StartSession"),
            Field("HostID", PairString("HostID")), Field("SystemBUID", PairString("SystemBUID"))), ct);
        if (Optional(session, "EnableSessionSSL")?.Name.LocalName == "true") await lockdown.Secure(host, device, ct);

        async Task<Connection> Service(string name)
        {
            var info = await Plist(lockdown.Stream, Dict(Field("Label", "USBPhoneSpeaker"), Field("Request", "StartService"), Field("Service", name)), ct);
            var connection = new Connection(await Usbmux.Connect(phone.Id, ct, ushort.Parse(Usbmux.Value(info, "Port").Value)));
            try
            {
                if (Optional(info, "EnableServiceSSL")?.Name.LocalName == "true") await connection.Secure(host, device, ct);
                return connection;
            }
            catch { connection.Dispose(); throw; }
        }

        string bundle;
        using (var installation = await Service("com.apple.mobile.installation_proxy"))
        {
            var options = Dict(Field("ApplicationType", "User"), new XElement("key", "ReturnAttributes"),
                new XElement("array", new XElement("string", "CFBundleIdentifier"), new XElement("string", "UIFileSharingEnabled")));
            var response = await Plist(installation.Stream, Dict(Field("Command", "Lookup"), new XElement("key", "ClientOptions"), options), ct);
            var matches = Usbmux.Value(response, "LookupResult").Elements("dict")
                .Where(app => Usbmux.Value(app, "CFBundleIdentifier").Value.StartsWith("io.github.sanciyuandehundan.USBPhoneSpeaker", StringComparison.Ordinal))
                .Where(app => Optional(app, "UIFileSharingEnabled")?.Name.LocalName == "true").ToArray();
            if (matches.Length != 1) throw new IOException(matches.Length == 0
                ? "没有找到支持文件共享的 USB 喇叭。请安装手机端 0.2.1 或更新版。"
                : "手机安装了多个 USB 喇叭，请只保留你要使用的那一个后再传文件。");
            bundle = Usbmux.Value(matches[0], "CFBundleIdentifier").Value;
        }
        using var files = await Service("com.apple.mobile.house_arrest");
        await Plist(files.Stream, Dict(Field("Command", "VendDocuments"), Field("Identifier", bundle)), ct);
        var afc = new Afc(files.Stream);
        var entries = Encoding.UTF8.GetString(await afc.Call(3, Encoding.UTF8.GetBytes("/Documents\0"), ReadOnlyMemory<byte>.Empty, ct));
        var existing = entries.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sent = new List<string>();
        foreach (string path in paths)
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, useAsync: true);
            string name = AvailableName(Path.GetFileName(path), existing);
            string partial = "/Documents/" + AvailableName(name + ".正在传输", existing);
            var open = new byte[8 + Encoding.UTF8.GetByteCount(partial) + 1];
            BinaryPrimitives.WriteUInt64LittleEndian(open, 3);
            Encoding.UTF8.GetBytes(partial).CopyTo(open, 8);
            byte[] handle = await afc.Call(13, open, ReadOnlyMemory<byte>.Empty, ct);
            if (handle.Length != 8) throw new IOException("手机返回了无效的文件句柄。");
            var buffer = new byte[256 * 1024];
            long total = 0;
            int lastPercent = -1;
            int count;
            while ((count = await input.ReadAsync(buffer, ct)) != 0)
            {
                await afc.Call(16, handle, buffer.AsMemory(0, count), ct);
                total += count;
                int percent = (int)Math.Min(99, total * 100 / input.Length);
                if (percent != lastPercent) { progress($"正在传入 {name} · {percent}%"); lastPercent = percent; }
            }
            await afc.Call(20, handle, ReadOnlyMemory<byte>.Empty, ct);
            await afc.Call(24, Encoding.UTF8.GetBytes(partial + "\0/Documents/" + name + "\0"), ReadOnlyMemory<byte>.Empty, ct);
            existing.Add(name);
            sent.Add(name);
        }
        return sent;
    }

    internal static string AvailableName(string name, HashSet<string> existing)
    {
        string result = name;
        for (int number = 1; existing.Contains(result); number++)
            result = $"{Path.GetFileNameWithoutExtension(name)} ({number}){Path.GetExtension(name)}";
        return result;
    }

    sealed class Afc(Stream stream)
    {
        ulong sequence;
        public async Task<byte[]> Call(ulong operation, byte[] arguments, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            var header = new byte[40];
            "CFA6LPAA"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), (ulong)(40 + arguments.Length + payload.Length));
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), (ulong)(40 + arguments.Length));
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), sequence++);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), operation);
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(arguments, ct);
            if (!payload.IsEmpty) await stream.WriteAsync(payload, ct);
            await stream.ReadExactlyAsync(header, ct);
            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
            if (!header.AsSpan(0, 8).SequenceEqual("CFA6LPAA"u8) || length < 40 || length > 16 * 1024 * 1024)
                throw new IOException("手机返回了无效的文件数据。");
            var response = new byte[(int)length - 40];
            await stream.ReadExactlyAsync(response, ct);
            if (BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32)) == 1)
            {
                if (response.Length != 8) throw new IOException("手机返回了无效的文件状态。");
                ulong error = BinaryPrimitives.ReadUInt64LittleEndian(response);
                if (error != 0) throw new IOException(error == 18 ? "iPhone 储存空间不足。" : $"手机文件操作失败（{error}）。");
            }
            return response;
        }
    }
}
