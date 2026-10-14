using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using KspContinuum;

public static class ShellFixture
{
    [Serializable] public sealed class Row { public uint partId; public int moduleIndex; public string sourceType, node; }
    [Serializable] public sealed class Packet
    {
        public string schema, sessionId, vesselId;
        public double universalTime;
        public Row[] modules;
    }
}

static class Program
{
    static int assertions;
    static void Check(bool value, string reason) { assertions++; if (!value) throw new Exception(reason); }
    static void Exact(ShellFixture.Packet expected, ShellFixture.Packet actual)
    {
        Check(actual.schema == expected.schema && actual.sessionId == expected.sessionId && actual.vesselId == expected.vesselId,
            "Shell identity survived the cold codec");
        Check(BitConverter.DoubleToInt64Bits(actual.universalTime) == BitConverter.DoubleToInt64Bits(expected.universalTime),
            "Nontrivial universal time survived bit for bit");
        Check(actual.modules != null && actual.modules.Length == expected.modules.Length, "Complete module table retained");
        for (int i = 0; i < expected.modules.Length; i++)
        {
            var a = actual.modules[i]; var e = expected.modules[i];
            Check(a.partId == e.partId && a.moduleIndex == e.moduleIndex && a.sourceType == e.sourceType, "Unsigned identity/order retained");
            Check(string.Equals(a.node, e.node, StringComparison.Ordinal), "Full ConfigNode text retains CR/LF, tabs, XML punctuation and Unicode");
        }
    }
    static bool IsXmlFailure(Exception error)
    { for (var current = error; current != null; current = current.InnerException) if (current is XmlException) return true; return false; }
    static void Main()
    {
        var original = new ShellFixture.Packet { schema = "continuum-owned-clock-shell/v1", sessionId = "session", vesselId = "vessel",
            universalTime = 1000000.123456789, modules = new[] {
                new ShellFixture.Row { partId = uint.MaxValue, moduleIndex = 2, sourceType = "ModuleCommand",
                    node = "MODULE\r\n{\n\tname = held-value<&>\rprecision = 0.10000000000000001\nlabel = Ω 🚀\r\n}" },
                new ShellFixture.Row { partId = 7, moduleIndex = 0, sourceType = "ModuleTripLogger", node = "MODULE\n{\n\tline = second\n}" } } };
        byte[] encoded = OwnedClockShellCodec.Encode(original);
        Exact(original, OwnedClockShellCodec.Decode<ShellFixture.Packet>(encoded));
        using (var stream = new MemoryStream(encoded, false))
            Exact(original, (ShellFixture.Packet)new System.Xml.Serialization.XmlSerializer(typeof(ShellFixture.Packet)).Deserialize(stream));

        string xml = Encoding.UTF8.GetString(encoded);
        string declaration = "<!DOCTYPE Packet [<!ENTITY external SYSTEM \"file:///must-not-read-owned-clock-entity\">]>";
        string dtd = xml.Insert(xml.IndexOf("?>", StringComparison.Ordinal) + 2, declaration).Replace("held-value", "&external;");
        bool rejected = false;
        try { OwnedClockShellCodec.Decode<ShellFixture.Packet>(Encoding.UTF8.GetBytes(dtd)); }
        catch (Exception error) { rejected = IsXmlFailure(error); }
        Check(rejected, "DTD/external entity declaration rejected before shell construction");
        rejected = false;
        try { OwnedClockShellCodec.Decode<ShellFixture.Packet>(Encoding.UTF8.GetBytes(xml + "<Packet/>")); }
        catch (Exception error) { rejected = IsXmlFailure(error); }
        Check(rejected, "Trailing second document cannot silently hide behind a valid first packet");

        string directory = Path.Combine(Path.GetTempPath(), "continuum-shell-codec-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "shell.xml"); OwnedClockShellCodec.WriteNew(path, original);
            Exact(original, OwnedClockShellCodec.Read<ShellFixture.Packet>(path));
            byte[] preserved = File.ReadAllBytes(path); bool duplicateRefused = false;
            try { OwnedClockShellCodec.WriteNew(path, new ShellFixture.Packet { schema = "replacement" }); }
            catch (IOException) { duplicateRefused = true; }
            Check(duplicateRefused && preserved.SequenceEqual(File.ReadAllBytes(path)), "CreateNew preserves existing shell evidence");
            string oversized = Path.Combine(directory, "oversized.xml");
            using (var file = File.Create(oversized)) file.SetLength(OwnedClockShellCodec.MaximumBytes + 1L);
            bool boundRefused = false;
            try { OwnedClockShellCodec.Read<ShellFixture.Packet>(oversized); }
            catch (InvalidDataException) { boundRefused = true; }
            Check(boundRefused, "Oversized persisted packet rejects before decoding");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("PASS: " + assertions + " cold owned-clock XML assertions.");
    }
}
