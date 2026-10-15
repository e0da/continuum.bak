using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class Program
{
    sealed class Node(string name, int start)
    {
        public string Name = name;
        public int Start = start, End;
        public readonly List<Node> Children = [];
        public readonly List<Value> Values = [];
        public Value One(string key) => Values.Single(v => v.Key == key);
        public string Text(string key) => One(key).Text;
    }
    sealed record Value(string Key, string Text, int Line);
    sealed record Edit(int Start, int End, string Text);
    sealed record Config(string Path, string Sha256, string Part, double Mass, double Bottom, double Top);
    const double Tolerance = 0.0001;
    static readonly int[] Docks = [9, 110, 113, 116];
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static string Absolute(string value) => Path.IsPathRooted(value) ? Path.GetFullPath(value) : throw new InvalidDataException("Absolute path required.");
    static double Number(string value)
    {
        double result = double.Parse(value, Invariant);
        return double.IsFinite(result) ? result : throw new InvalidDataException("Nonfinite scalar.");
    }
    static string[] Lines(string text) => Regex.Matches(text, @"[^\n]*\n|[^\n]+$").Select(m => m.Value).ToArray();
    static List<Node> Parse(string[] lines)
    {
        var roots = new List<Node>(); var stack = new Stack<Node>(); string? pending = null; int pendingLine = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Split("//", 2, StringSplitOptions.None)[0].Trim().TrimStart('\uFEFF');
            if (line.Length == 0) continue;
            if (line == "{")
            {
                if (pending == null) throw new InvalidDataException("Opening brace without node.");
                var node = new Node(pending, pendingLine); if (stack.Count == 0) roots.Add(node); else stack.Peek().Children.Add(node);
                stack.Push(node); pending = null;
            }
            else if (line == "}") { if (pending != null || stack.Count == 0) throw new InvalidDataException("Unbalanced node."); stack.Pop().End = i; }
            else if (line.Contains('='))
            {
                if (pending != null || stack.Count == 0) throw new InvalidDataException("Value outside node.");
                int equal = line.IndexOf('='); stack.Peek().Values.Add(new Value(line[..equal].Trim(), line[(equal + 1)..].Trim(), i));
            }
            else { if (pending != null) throw new InvalidDataException("Consecutive node labels."); pending = line; pendingLine = i; }
        }
        if (stack.Count != 0 || pending != null) throw new InvalidDataException("Incomplete nodes."); return roots;
    }
    static IEnumerable<Node> Descendants(Node node)
    {
        yield return node; foreach (Node child in node.Children) foreach (Node nested in Descendants(child)) yield return nested;
    }
    static Vector3 Vector(string value)
    {
        double[] a = value.Split(',').Select(Number).ToArray(); if (a.Length != 3) throw new InvalidDataException("Vector layout.");
        var v = new Vector3((float)a[0], (float)a[1], (float)a[2]); if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) throw new InvalidDataException("Vector overflow."); return v;
    }
    static Quaternion Rotation(string value)
    {
        double[] a = value.Split(',').Select(Number).ToArray(); if (a.Length != 4) throw new InvalidDataException("Quaternion layout.");
        var q = new Quaternion((float)a[0], (float)a[1], (float)a[2], (float)a[3]);
        if (!float.IsFinite(q.LengthSquared()) || Math.Abs(q.LengthSquared() - 1) > 0.0001) throw new InvalidDataException("Nonunit quaternion."); return q;
    }
    static string Csv(Vector3 v) => string.Join(',', v.X.ToString("R", Invariant), v.Y.ToString("R", Invariant), v.Z.ToString("R", Invariant));
    static string Csv(Quaternion q) => string.Join(',', q.X.ToString("R", Invariant), q.Y.ToString("R", Invariant), q.Z.ToString("R", Invariant), q.W.ToString("R", Invariant));
    static string Replacement(string original, string value)
    {
        int equal = original.IndexOf('='); if (equal < 0 || value.Contains('\n') || value.Contains('\r')) throw new InvalidDataException("Value replacement layout.");
        string newline = original.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : original.EndsWith('\n') ? "\n" : "";
        int comment = original.IndexOf("//", equal, StringComparison.Ordinal);
        string suffix = comment >= 0 ? "\t" + original[comment..].TrimEnd('\r', '\n') : "";
        return original[..(equal + 1)] + " " + value + suffix + newline;
    }
    static Config ReadConfig(string path, string expected)
    {
        string sha = Hash(path); Node node = Parse(Lines(File.ReadAllText(path))).Single(n => n.Name == "PART");
        if (node.Text("name") != expected || Number(node.Text("rescaleFactor")) != 1 || node.Values.Any(v => v.Key == "PhysicsSignificance" && Number(v.Text) != 0)) throw new InvalidDataException("Stock physical part identity/scale changed.");
        double NodeY(string key, double direction)
        {
            double[] a = node.Text(key).Split(',').Select(Number).ToArray();
            if (a.Length != 7 || a[0] != 0 || a[2] != 0 || a[3] != 0 || a[4] != direction || a[5] != 0) throw new InvalidDataException("Stock stack node axis/layout changed."); return a[1];
        }
        return new Config(path, sha, expected, Number(node.Text("mass")), NodeY("node_stack_bottom", -1), NodeY("node_stack_top", 1));
    }
    static int Main(string[] args)
    {
        try { Generate(args); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 2; }
    }
    static void Generate(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (args.Length % 2 != 0) throw new ArgumentException("Expected named option/value pairs.");
        for (int i = 0; i < args.Length; i += 2) if (!options.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Duplicate option.");
        string[] allowed = ["--source", "--expected-sha", "--game-data", "--output", "--report", "--title", "--parts"];
        if (options.Keys.Any(k => !allowed.Contains(k))) throw new ArgumentException("Unknown option.");
        string source = Absolute(options["--source"]), gameData = Absolute(options["--game-data"]), output = Absolute(options["--output"]), reportPath = Absolute(options["--report"]);
        string requestedParts = options.GetValueOrDefault("--parts", "256");
        int targetParts = requestedParts == "256" ? 256 : requestedParts == "512" ? 512 : throw new ArgumentException("--parts must be 256 or 512.");
        int addedParts = targetParts - 196, perArm = addedParts / Docks.Length;
        if (addedParts % Docks.Length != 0) throw new InvalidDataException("Unequal boom recipe.");
        string title = options.GetValueOrDefault("--title", "Continuum Capacity " + targetParts);
        if (title.Contains('\r') || title.Contains('\n') || title.Contains("//", StringComparison.Ordinal) || title.Length == 0) throw new ArgumentException("Single-line title required.");
        if (output == source || output == reportPath || File.Exists(output) || Directory.Exists(output) || File.Exists(reportPath) || Directory.Exists(reportPath)) throw new InvalidOperationException("New distinct output and report paths required.");
        byte[] originalBytes = File.ReadAllBytes(source); string sourceSha = HashBytes(originalBytes);
        if (sourceSha != options["--expected-sha"].ToLowerInvariant()) throw new InvalidDataException("Source SHA differs.");
        string original = new UTF8Encoding(false, true).GetString(originalBytes); string[] lines = Lines(original); Node game = Parse(lines).Single(n => n.Name == "GAME");
        Node[] vessels = Descendants(game).Where(n => n.Name == "VESSEL").ToArray(); Node vessel = vessels.Single(n => n.Children.Count(c => c.Name == "PART") == 196);
        Node[] parts = vessel.Children.Where(n => n.Name == "PART").ToArray(); Node donor = parts[47];
        if (donor.Text("name") != "trussPiece3x" || donor.Children.Any(c => c.Name is "MODULE" or "RESOURCE") || donor.Values.Any(v => v.Key == "crew")) throw new InvalidDataException("Expected plain structural donor47.");
        Config dock = ReadConfig(Path.Combine(gameData, "Squad", "Parts", "Utility", "dockingPort", "dockingPort.cfg"), "dockingPort2");
        Config girder = ReadConfig(Path.Combine(gameData, "Squad", "Parts", "Structural", "trussGirderL", "trussGirderL.cfg"), "trussPiece1x");
        double spacing = girder.Top - girder.Bottom;
        if (Math.Abs(spacing - 1.1185456) > 1e-9 || Math.Abs(girder.Mass - 0.125) > 1e-9 || Math.Abs(dock.Top - 0.2828832) > 1e-9) throw new InvalidDataException("Pinned stock recipe scalar differs.");
        var used = Descendants(game).Where(n => n.Name == "PART").Select(n => uint.Parse(n.Text("uid"), Invariant)).ToHashSet();
        int originalPartCount = Descendants(game).Count(n => n.Name == "PART"); if (used.Count != originalPartCount) throw new InvalidDataException("Original full-save UIDs not unique.");
        uint nextUid = 1; var edits = new List<Edit>(); var clones = new StringBuilder(); var additions = new List<object>(); var recipes = new List<object>();
        Value titleValue = game.One("Title"); edits.Add(new Edit(titleValue.Line, titleValue.Line + 1, Replacement(lines[titleValue.Line], title)));
        for (int arm = 0; arm < Docks.Length; arm++)
        {
            int parentIndex = Docks[arm]; Node parent = parts[parentIndex]; if (parent.Text("name") != "dockingPort2") throw new InvalidDataException("Expected docking donor at selected index.");
            Node docking = parent.Children.Single(n => n.Name == "MODULE" && n.Text("name") == "ModuleDockingNode");
            if (docking.Text("state") != "Ready" || uint.Parse(docking.Text("dockUId"), Invariant) != 0) throw new InvalidDataException("Dock retains linked state/ownership; no unlinking permitted.");
            Value top = parent.Values.Single(v => v.Key == "attN" && v.Text.Split(',')[0].Trim() == "top"); if (top.Text.Split(',')[1].Trim() != "-1") throw new InvalidDataException("Dock top already occupied.");
            int first = 196 + arm * perArm; edits.Add(new Edit(top.Line, top.Line + 1, Replacement(lines[top.Line], "top, " + first)));
            Vector3 dockPosition = Vector(parent.Text("position")); Quaternion rotation = Rotation(parent.Text("rotation"));
            recipes.Add(new { dockIndex = parentIndex, firstIndex = first, count = perArm, dockPosition = parent.Text("position"), rotation = parent.Text("rotation") });
            for (int j = 0; j < perArm; j++)
            {
                while (used.Contains(nextUid)) { if (nextUid == uint.MaxValue) throw new InvalidOperationException("UID space exhausted."); nextUid++; }
                uint uid = nextUid; used.Add(uid); int index = first + j, previous = j == 0 ? parentIndex : index - 1;
                Vector3 position = dockPosition + Vector3.Transform(Vector3.UnitY * (float)(dock.Top - girder.Bottom + j * spacing), rotation);
                var changes = new Dictionary<string, string> { ["name"] = "trussPiece1x", ["uid"] = uid.ToString(Invariant), ["parent"] = previous.ToString(Invariant), ["position"] = Csv(position), ["rotation"] = Csv(rotation), ["mass"] = girder.Mass.ToString("R", Invariant), ["rTrf"] = "trussPiece1x", ["srfN"] = "srfAttach, -1", ["sepI"] = "-1", ["autostrutMode"] = "Off", ["rigidAttachment"] = "False" };
                var byLine = donor.Values.ToDictionary(v => v.Line); int topCount = 0, bottomCount = 0;
                for (int line = donor.Start; line <= donor.End; line++)
                {
                    if (!byLine.TryGetValue(line, out Value? value)) { clones.Append(lines[line]); continue; }
                    if (value.Key == "attN")
                    {
                        string node = value.Text.Split(',')[0].Trim(); if (node == "bottom") { bottomCount++; clones.Append(Replacement(lines[line], "bottom, " + previous)); }
                        else if (node == "top") { topCount++; clones.Append(Replacement(lines[line], "top, " + (j == perArm - 1 ? -1 : index + 1))); }
                        else throw new InvalidDataException("Unexpected donor attach node.");
                    }
                    else clones.Append(changes.TryGetValue(value.Key, out string? changed) ? Replacement(lines[line], changed) : lines[line]);
                }
                if (topCount != 1 || bottomCount != 1) throw new InvalidDataException("Donor stack nodes missing/duplicated.");
                additions.Add(new { index, uid, parent = previous, position = Csv(position), rotation = Csv(rotation) });
            }
        }
        edits.Add(new Edit(parts[^1].End + 1, parts[^1].End + 1, clones.ToString()));
        var result = new StringBuilder(); int cursor = 0;
        foreach (Edit edit in edits.OrderBy(e => e.Start)) { if (edit.Start < cursor) throw new InvalidDataException("Overlapping edits."); for (; cursor < edit.Start; cursor++) result.Append(lines[cursor]); result.Append(edit.Text); cursor = edit.End; }
        for (; cursor < lines.Length; cursor++) result.Append(lines[cursor]);
        string generated = result.ToString(); Node outputGame = Parse(Lines(generated)).Single(n => n.Name == "GAME"); Node outputVessel = Descendants(outputGame).Single(n => n.Name == "VESSEL" && n.Text("pid") == vessel.Text("pid"));
        Node[] outputParts = outputVessel.Children.Where(n => n.Name == "PART").ToArray(); if (outputParts.Length != targetParts) throw new InvalidDataException("Output station count differs.");
        uint[] outputIds = Descendants(outputGame).Where(n => n.Name == "PART").Select(n => uint.Parse(n.Text("uid"), Invariant)).ToArray();
        if (outputIds.Length != originalPartCount + addedParts || outputIds.Distinct().Count() != outputIds.Length) throw new InvalidDataException("Output full-save UID/count proof failed.");
        string[] outputLines = Lines(generated);
        for (int i = 0; i < 196; i++)
        {
            string[] expected = lines[parts[i].Start..(parts[i].End + 1)].ToArray();
            int arm = Array.IndexOf(Docks, i);
            if (arm >= 0)
            {
                Value top = parts[i].Values.Single(v => v.Key == "attN" && v.Text.Split(',')[0].Trim() == "top");
                expected[top.Line - parts[i].Start] = Replacement(lines[top.Line], "top, " + (196 + arm * perArm));
            }
            if (string.Concat(expected) != string.Concat(outputLines[outputParts[i].Start..(outputParts[i].End + 1)])) throw new InvalidDataException("Original station part block drifted.");
        }
        Node[] outputVessels = Descendants(outputGame).Where(n => n.Name == "VESSEL").ToArray();
        foreach (Node other in vessels.Where(n => n != vessel))
        {
            Node actual = outputVessels.Single(n => n.Text("pid") == other.Text("pid"));
            if (string.Concat(lines[other.Start..(other.End + 1)]) != string.Concat(outputLines[actual.Start..(actual.End + 1)])) throw new InvalidDataException("Other vessel block drifted.");
        }
        double maxNodeError = 0;
        for (int i = 196; i < targetParts; i++)
        {
            Node child = outputParts[i]; int parentIndex = int.Parse(child.Text("parent"), Invariant); Node parent = outputParts[parentIndex];
            if (parentIndex != ((i - 196) % perArm == 0 ? Docks[(i - 196) / perArm] : i - 1)) throw new InvalidDataException("Added parent recipe mismatch.");
            if (child.Text("name") != girder.Part || Number(child.Text("mass")) != girder.Mass || child.Text("autostrutMode") != "Off" || child.Text("rigidAttachment") != "False") throw new InvalidDataException("Generated physical part recipe mismatch.");
            int expectedTop = (i - 196) % perArm == perArm - 1 ? -1 : i + 1;
            if (!child.Values.Any(v => v.Key == "attN" && v.Text == "top, " + expectedTop)) throw new InvalidDataException("Generated terminal/forward link mismatch.");
            if (!child.Values.Any(v => v.Key == "attN" && v.Text == "bottom, " + parentIndex) || !parent.Values.Any(v => v.Key == "attN" && v.Text == "top, " + i)) throw new InvalidDataException("Reciprocal stack reference mismatch.");
            double parentTop = parentIndex < 196 ? dock.Top : girder.Top;
            Vector3 a = Vector(parent.Text("position")) + Vector3.Transform(Vector3.UnitY * (float)parentTop, Rotation(parent.Text("rotation")));
            Vector3 b = Vector(child.Text("position")) + Vector3.Transform(Vector3.UnitY * (float)girder.Bottom, Rotation(child.Text("rotation")));
            maxNodeError = Math.Max(maxNodeError, Vector3.Distance(a, b)); if (maxNodeError > Tolerance) throw new InvalidDataException("Stack node alignment exceeds tolerance.");
        }
        if (Hash(source) != sourceSha || Hash(dock.Path) != dock.Sha256 || Hash(girder.Path) != girder.Sha256) throw new InvalidDataException("Inputs changed during preparation.");
        byte[] outputBytes = Encoding.UTF8.GetBytes(generated);
        var receipt = new { schema = "continuum-flight-fixture/v1", status = "generated-node-alignment-verified", source, sourceSha256 = sourceSha, output, outputSha256 = HashBytes(outputBytes), title, vesselId = vessel.Text("pid"), originalStationParts = 196, stationParts = targetParts, addedParts, partsPerBoom = perArm, originalSaveParts = originalPartCount, outputSaveParts = outputIds.Length, preservedOriginalStationPartBlocks = 192, changedOnlyTopReferencePartBlocks = 4, preservedOtherVesselBlocks = vessels.Length - 1, addedMassTonnes = addedParts * girder.Mass, spacing, nodeToleranceMeters = Tolerance, maxNodeErrorMeters = maxNodeError, dock, girder, recipes, additions, scope = "Ordered source blocks preserved except GAME.Title and four free dock top references; adds stock physical girders. Node alignment is not a collider/self-contact, load, rigidbody, joint, behavior or warp qualification." };
        string receiptJson = JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(output)!); Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write)) file.Write(outputBytes);
        using (var file = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write)) { byte[] bytes = Encoding.UTF8.GetBytes(receiptJson); file.Write(bytes); }
        Console.WriteLine(reportPath);
    }
}
