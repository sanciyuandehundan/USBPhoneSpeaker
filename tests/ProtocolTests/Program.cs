using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using USBPhoneSpeaker;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

byte[] expected = [85,83,80,75,1,0,0,0,128,187,0,0,2,0,0,0];
Check(AudioSender.Header(48000, 2).SequenceEqual(expected), "PCM header must match the iPhone protocol.");
var packet = Usbmux.Request("Connect",
    new XElement("key", "DeviceID"), new XElement("integer", 42),
    new XElement("key", "PortNumber"), new XElement("integer", BinaryPrimitives.ReverseEndianness((ushort)24680)));
Check(BinaryPrimitives.ReadInt32LittleEndian(packet) == packet.Length, "Wrong usbmux packet length.");
Check(BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4)) == 1, "Wrong usbmux protocol version.");
Check(BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(8)) == 8, "Expected plist packet.");
var dict = XDocument.Parse(Encoding.UTF8.GetString(packet, 16, packet.Length - 16)).Root!.Element("dict")!;
Check(Usbmux.Value(dict, "MessageType").Value == "Connect", "Wrong USB message type.");
Check(Usbmux.Value(dict, "DeviceID").Value == "42", "Wrong device ID.");
Check(Usbmux.Value(dict, "PortNumber").Value == "26720", "USB port must use network byte order.");
Console.WriteLine("Windows protocol tests passed.");
