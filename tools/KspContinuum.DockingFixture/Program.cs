using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class Program
{
    sealed class Node(string name, int start)
    {
        public readonly string Name = name;
        public readonly int Start = start;
        public int End;
        public readonly List<Node> Children = [];
        public readonly List<Value> Values = [];
        public string Text(string key)
        {
            Value[] matches = Values.Where(v => v.Key == key).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Expected one " + Name + " field " + key + ", found " + matches.Length + ".");
            return matches[0].Text;
        }
    }
    sealed record Value(string Key, string Text, int Line);
    sealed record Edit(int Start, int End, string Text);
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    static readonly UTF8Encoding Utf8 = new(false, true);
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static string[] Lines(string text) => text.Split('\n');
    static Node Parse(string[] lines)
    {
        var stack = new Stack<Node>(); Node? root = null; string? pending = null; int pendingLine = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Split("//", 2, StringSplitOptions.None)[0].Trim().TrimStart('\uFEFF');
            if (line.Length == 0) continue;
            if (line == "{")
            {
                if (pending == null) throw new InvalidDataException("Unlabeled brace.");
                var node = new Node(pending, pendingLine);
                if (stack.Count != 0) stack.Peek().Children.Add(node); else if (root == null) root = node; else throw new InvalidDataException("Multiple roots.");
                stack.Push(node); pending = null;
            }
            else if (line == "}") { if (pending != null || stack.Count == 0) throw new InvalidDataException("Unbalanced brace."); stack.Pop().End = i; }
            else if (line.Contains('='))
            {
                if (pending != null || stack.Count == 0) throw new InvalidDataException("Value outside node.");
                int at = line.IndexOf('='); stack.Peek().Values.Add(new Value(line[..at].Trim(), line[(at + 1)..].Trim(), i));
            }
            else { if (pending != null) throw new InvalidDataException("Consecutive labels."); pending = line; pendingLine = i; }
        }
        if (root == null || pending != null || stack.Count != 0) throw new InvalidDataException("Incomplete document."); return root;
    }
    static IEnumerable<Node> All(Node node) { yield return node; foreach (Node child in node.Children) foreach (Node next in All(child)) yield return next; }
    static string Block(string[] lines, Node node) => string.Join('\n', lines[node.Start..(node.End + 1)]);
    static string Apply(string[] lines, IEnumerable<Edit> edits)
    {
        Edit[] ordered = edits.OrderBy(e => e.Start).ToArray(); var output = new StringBuilder(); int cursor = 0;
        foreach (Edit edit in ordered)
        {
            if (edit.Start < cursor || edit.End < edit.Start) throw new InvalidDataException("Overlapping edits.");
            for (; cursor < edit.Start; cursor++) output.AppendLine(lines[cursor]);
            if (edit.Text.Length != 0) output.AppendLine(edit.Text);
            cursor = edit.End + 1;
        }
        for (; cursor < lines.Length; cursor++) output.AppendLine(lines[cursor]); return output.ToString();
    }
    static Edit Change(Value value, string text) => new(value.Line, value.Line, "\t" + value.Key + " = " + text);
    static double Number(string text) { double value = double.Parse(text, Invariant); if (!double.IsFinite(value)) throw new InvalidDataException("Nonfinite number."); return value; }
    static string N(double value) => value.ToString("R", Invariant);
    static void NoLinks(string path)
    {
        string? current = path;
        while (current != null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Symlink/reparse path rejected.");
            current = Path.GetDirectoryName(current);
        }
    }
    static string Absolute(string path) { if (!Path.IsPathRooted(path)) throw new ArgumentException("Absolute paths required."); string result = Path.GetFullPath(path); NoLinks(result); return result; }
    static (Node node, string sha) Config(string path, string name)
    {
        byte[] bytes = File.ReadAllBytes(path); Node node = Parse(Lines(Utf8.GetString(bytes)));
        if (node.Name != "PART" || node.Text("name") != name) throw new InvalidDataException("Unexpected stock part configuration."); return (node, Hash(bytes));
    }
    static string Part(string original, uint uid, bool port, double rootTop)
    {
        string[] lines = Lines(original); Node node = Parse(lines); var edits = new List<Edit>();
        var replacements = new Dictionary<string, string> { ["uid"] = uid.ToString(Invariant), ["parent"] = "0", ["position"] = port ? "0," + N(rootTop) + ",0" : "0,0,0", ["rotation"] = "0,0,0,1", ["attached"] = port ? "True" : "False", ["autostrutMode"] = "Off", ["rigidAttachment"] = "False", ["srfN"] = ", -1" };
        foreach (Value value in node.Values)
        {
            if (value.Key == "crew" || value.Key == "sym" || value.Key == "attN") edits.Add(new Edit(value.Line, value.Line, ""));
            else if (replacements.TryGetValue(value.Key, out string? replacement)) edits.Add(Change(value, replacement));
        }
        foreach (Node module in node.Children.Where(c => c.Name == "MODULE"))
        {
            string name = module.Text("name");
            foreach (Value value in module.Values)
            {
                if (name == "ModuleDockingNode" && value.Key == "state") edits.Add(Change(value, "Ready"));
                if (name == "ModuleDockingNode" && value.Key == "dockUId") edits.Add(Change(value, "0"));
                if (name == "ModuleDockingNode" && value.Key == "dockNodeIdx") edits.Add(Change(value, "0"));
                if (name == "ModuleReactionWheel" && value.Key == "WheelState") edits.Add(Change(value, "Disabled"));
            }
        }
        string links = port ? "\tattN = bottom, 0\n\tattN = top, -1" : "\tattN = bottom, -1\n\tattN = top, 1";
        edits.Add(new Edit(node.End, node.End, links + "\n}")); return Apply(lines, edits);
    }
    static string Vessel(string original, string rootPart, string portPart, string id, uint rootUid, double epoch, double mean)
    {
        string[] lines = Lines(original); Node node = Parse(lines); var edits = new List<Edit>();
        var values = new Dictionary<string, string> { ["pid"] = id, ["name"] = "Continuum Dock " + (mean == 0 ? "A" : "B"), ["type"] = "Ship", ["sit"] = "ORBITING", ["landed"] = "False", ["splashed"] = "False", ["root"] = "0", ["ref"] = rootUid.ToString(Invariant), ["rot"] = "0,0,0,1", ["CoM"] = "0,0,0", ["lat"] = "0", ["lon"] = N(mean * 180 / Math.PI), ["alt"] = "120000", ["lastUT"] = N(epoch), ["ctrl"] = "False", ["stg"] = "0" };
        foreach (Value value in node.Values) if (values.TryGetValue(value.Key, out string? replacement)) edits.Add(Change(value, replacement));
        bool first = true;
        foreach (Node child in node.Children)
        {
            if (child.Name == "PART") { edits.Add(new Edit(child.Start, child.End, first ? rootPart + portPart : "")); first = false; }
            else if (child.Name == "ORBIT") edits.Add(new Edit(child.Start, child.End, "ORBIT\n{\nSMA = 720000\nECC = 0\nINC = 0\nLPE = 0\nLAN = 0\nMNA = " + N(mean) + "\nEPH = " + N(epoch) + "\nREF = 1\n}"));
            else if (child.Name == "ACTIONGROUPS") foreach (Value value in child.Values) edits.Add(Change(value, "False, " + N(epoch)));
            else if (child.Name == "CTRLSTATE") foreach (Value value in child.Values) edits.Add(Change(value, "0"));
            else if (child.Name == "FLIGHTPLAN") edits.Add(new Edit(child.Start, child.End, "FLIGHTPLAN\n{\n}"));
        }
        return Apply(lines, edits);
    }
    static string[] Resources(Node part) => part.Children.Where(c => c.Name == "RESOURCE").Select(c => string.Join('|', c.Values.Select(v => v.Key + "=" + v.Text))).ToArray();
    static int Main(string[] args)
    {
        try { Generate(args); return 0; } catch (Exception error) { Console.Error.WriteLine(error.ToString()); return 2; }
    }
    static void Generate(string[] args)
    {
        string[] allowed = ["--source", "--expected-sha", "--game-data", "--output", "--report"];
        if (args.Length != 10) throw new ArgumentException("Five named option/value pairs required."); var options = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i += 2) if (!allowed.Contains(args[i]) || !options.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Unknown/duplicate option.");
        string source = Absolute(options["--source"]), data = Absolute(options["--game-data"]), output = Absolute(options["--output"]), report = Absolute(options["--report"]);
        if (output == source || report == source || output == report || File.Exists(output) || Directory.Exists(output) || File.Exists(report) || Directory.Exists(report)) throw new IOException("New distinct output/report required.");
        if (new FileInfo(source).Length > 16 * 1024 * 1024) throw new InvalidDataException("Source exceeds 16 MiB seed bound.");
        byte[] bytes = File.ReadAllBytes(source); string sha = Hash(bytes); if (sha != options["--expected-sha"].ToLowerInvariant()) throw new InvalidDataException("Source hash mismatch.");
        string[] lines = Lines(Utf8.GetString(bytes)); Node game = Parse(lines), flight = game.Children.Single(c => c.Name == "FLIGHTSTATE"); Node[] oldVessels = flight.Children.Where(c => c.Name == "VESSEL").ToArray();
        int activeIndex = int.Parse(flight.Text("activeVessel"), Invariant);
        if (activeIndex < 0 || activeIndex >= oldVessels.Length) throw new InvalidDataException("Source active vessel index is invalid.");
        Node donor = oldVessels[activeIndex];
        Node root = donor.Children.First(c => c.Name == "PART");
        if (root.Text("name") != "mk2LanderCabin" || donor.Text("root") != "0") throw new InvalidDataException("Source active root must be the stock Mk2 lander cabin.");
        Node port = donor.Children.First(c => c.Name == "PART" && c.Text("name") == "dockingPort2" && c.Children.Any(m => m.Name == "MODULE" && m.Text("name") == "ModuleDockingNode" && m.Text("state") == "Ready" && m.Text("dockUId") == "0"));
        string rootConfig = Absolute(Path.Combine(data, "Squad/Parts/Command/mk2LanderCan/mk2LanderCan.cfg")), portConfig = Absolute(Path.Combine(data, "Squad/Parts/Utility/dockingPort/dockingPort.cfg"));
        var rc = Config(rootConfig, "mk2LanderCabin"); var pc = Config(portConfig, "dockingPort2");
        double[] top = rc.node.Text("node_stack_top").Split(',').Select(Number).ToArray(), bottom = pc.node.Text("node_stack_bottom").Split(',').Select(Number).ToArray();
        if (top.Length != 7 || bottom.Length != 7 || top[0] != 0 || top[2] != 0 || top[3] != 0 || top[4] != 1 || top[5] != 0 || bottom[0] != 0 || bottom[1] != 0 || bottom[2] != 0 || bottom[3] != 0 || bottom[4] != -1 || bottom[5] != 0) throw new InvalidDataException("Unsupported stock stack-node layout.");
        double epoch = Number(flight.Text("UT")), delta = 20.0 / 720000; string[] ids = [Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")];
        var used = All(game).Where(n => n.Name == "PART").Select(n => uint.Parse(n.Text("uid"), Invariant)).ToHashSet(); var uids = new uint[4]; uint next = 1;
        for (int i = 0; i < 4; i++) { while (used.Contains(next)) next = checked(next + 1); uids[i] = next; used.Add(next); next = checked(next + 1); }
        var replacementVessels = new string[2];
        for (int i = 0; i < 2; i++) replacementVessels[i] = Vessel(Block(lines, donor), Part(Block(lines, root), uids[2 * i], false, top[1]), Part(Block(lines, port), uids[2 * i + 1], true, top[1]), ids[i], uids[2 * i], epoch, i * delta);
        var edits = oldVessels.Select((v, i) => new Edit(v.Start, v.End, i == 0 ? string.Join('\n', replacementVessels) : "")).ToList();
        edits.Add(Change(flight.Values.Single(v => v.Key == "activeVessel"), "0"));
        edits.Add(Change(game.Values.Single(v => v.Key == "Title"), "Continuum Docking Fixture"));
        foreach (Node node in All(game).Where(n => n.Name == "FLIGHT")) foreach (Value value in node.Values.Where(v => v.Key is "CanQuickSave" or "CanQuickLoad" or "CanLeaveToMainMenu")) edits.Add(Change(value, "True"));
        string generated = Apply(lines, edits); Node result = Parse(Lines(generated)); Node[] vessels = result.Children.Single(c => c.Name == "FLIGHTSTATE").Children.Where(c => c.Name == "VESSEL").ToArray();
        if (vessels.Length != 2 || vessels.Select(v => v.Text("pid")).Distinct().Count() != 2) throw new InvalidDataException("Vessel identities failed.");
        var checks = new List<object>();
        for (int i = 0; i < 2; i++)
        {
            Node[] parts = vessels[i].Children.Where(c => c.Name == "PART").ToArray();
            if (parts.Length != 2 || parts[0].Text("parent") != "0" || parts[1].Text("parent") != "0" || vessels[i].Text("ref") != parts[0].Text("uid") ||
                !parts[0].Values.Any(v => v.Key == "attN" && v.Text == "top, 1") || !parts[1].Values.Any(v => v.Key == "attN" && v.Text == "bottom, 0") ||
                parts.Any(p => p.Values.Any(v => v.Key is "crew" or "sym")) || !Resources(parts[0]).SequenceEqual(Resources(root)) || !Resources(parts[1]).SequenceEqual(Resources(port))) throw new InvalidDataException("Part links/resource/crew proof failed.");
            Node docking = parts[1].Children.Single(c => c.Name == "MODULE" && c.Text("name") == "ModuleDockingNode");
            if (docking.Text("state") != "Ready" || docking.Text("dockUId") != "0" || docking.Text("dockNodeIdx") != "0" ||
                parts[0].Children.Single(c => c.Name == "MODULE" && c.Text("name") == "ModuleReactionWheel").Text("WheelState") != "Disabled" ||
                vessels[i].Children.Single(c => c.Name == "ACTIONGROUPS").Values.Any(v => !v.Text.StartsWith("False, ", StringComparison.Ordinal)) ||
                vessels[i].Children.Single(c => c.Name == "CTRLSTATE").Values.Any(v => v.Text != "0") || vessels[i].Text("pid") != ids[i] ||
                parts[0].Text("uid") != uids[2 * i].ToString(Invariant) || parts[1].Text("uid") != uids[2 * i + 1].ToString(Invariant)) throw new InvalidDataException("Dock/control/identity readback proof failed.");
            checks.Add(new { vesselId = ids[i], rootUid = uids[2 * i], portUid = uids[2 * i + 1], parts = 2, crew = 0, meanAnomalyRadians = i * delta, resourcesPreserved = true });
        }
        if (All(result).Where(n => n.Name == "PART").Select(n => n.Text("uid")).Distinct().Count() != 4) throw new InvalidDataException("Part UID uniqueness failed.");
        foreach (Node child in game.Children.Where(c => c.Name != "FLIGHTSTATE" && c.Name != "PARAMETERS"))
            if (Block(lines, child) != Block(Lines(generated), result.Children.Single(c => c.Name == child.Name && c.Values.Select(v => v.Key + v.Text).SequenceEqual(child.Values.Select(v => v.Key + v.Text))))) throw new InvalidDataException("Unrelated game child changed.");
        if (Hash(File.ReadAllBytes(source)) != sha) throw new InvalidDataException("Source changed during preparation.");
        byte[] outputBytes = Utf8.GetBytes(generated); var receipt = new { schema = "continuum-docking-fixture/v1", status = "generated-offline-structure-verified", source, sourceSha256 = sha, output, outputSha256 = Hash(outputBytes),
            rootConfig, rootConfigSha256 = rc.sha, portConfig, portConfigSha256 = pc.sha, epoch, bodyIndex = 1, radiusMeters = 720000, altitudeMeters = 120000,
            meanAnomalySeparationRadians = delta, estimatedRootSeparationMeters = 2 * 720000 * Math.Sin(delta / 2), relativeVelocityEstimate = "Different circular phases imply different velocity vectors; actual relative velocity must be prepared/read back by the runtime driver.",
            checks, removedVessels = oldVessels.Length, scope = "Zero-crew two-vessel offline seed; source roster/scenarios and resource blocks preserved. Saved quaternion is surface-relative identity. No actual model-port, daylight, immediate unpacking, zero relative velocity, collision, or docking readiness claim. Runtime driver must admit and prepare actual facing ports/physical velocities before capture." };
        using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(outputBytes);
        if (Hash(File.ReadAllBytes(output)) != receipt.outputSha256) throw new IOException("Output readback hash differs.");
        using (var stream = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.None)) JsonSerializer.Serialize(stream, receipt, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(JsonSerializer.Serialize(new { receipt.schema, receipt.status, output, report, receipt.outputSha256 }));
    }
}
