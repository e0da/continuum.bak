using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using KspContinuum;

static partial class Program
{
    sealed class ExecutionSnapshotPin
    {
        public string path="",sha256="",hostSourceSha256="",hostImplementation="",normalizedSha256="";
        public long bytes,codeGeneration,systemsRevision;
        public int binaryVersion,executionStrategy,normalizedBytes;
    }
    sealed class ExecutionSnapshotComparison
    {
        public string file="",status="failed",failure="";
        public ExecutionSnapshotPin? oldPacket,newPacket;
        public bool completeNormalizedBinaryEqual;
        public int firstDifferentNormalizedByte=-1;
    }
    static Dictionary<string,string> ExecutionSnapshotFiles(string directory)
    {
        Need(Path.IsPathFullyQualified(directory),"Absolute checkpoint directory required.");directory=Path.GetFullPath(directory);NoLinks(directory);Need(Directory.Exists(directory),"Checkpoint directory absent.");
        var files=Directory.EnumerateFiles(directory).Where(path=>string.Equals(Path.GetExtension(path),".ofcb",StringComparison.OrdinalIgnoreCase)).OrderBy(path=>Path.GetFileName(path),StringComparer.Ordinal).ToArray();
        Need(files.Length>0&&files.Length<=1024,"Checkpoint directory must contain 1–1024 top-level .ofcb packets.");
        var result=new Dictionary<string,string>(StringComparer.Ordinal);foreach(string path in files){NoLinks(path);Need(result.TryAdd(Path.GetFileName(path),path),"Repeated checkpoint name.");}return result;
    }
    static byte[] NormalizeExecutionSnapshot(string path,ExecutionSnapshotPin pin)
    {
        NoLinks(path);byte[] bytes;
        using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            long length=file.Length;Need(length>=OwnedRuntimeCheckpointBinary.HeaderBytes+1&&length<=OwnedRuntimeCheckpointBinary.HeaderBytes+OwnedRuntimeCheckpointBinary.MaximumPayloadBytes,"Checkpoint size outside binary bounds.");
            bytes=new byte[(int)length];file.ReadExactly(bytes);Need(file.ReadByte()==-1&&file.Length==length,"Checkpoint changed during read.");
        }
        pin.path=Path.GetFullPath(path);pin.bytes=bytes.Length;pin.sha256=Hash(bytes);pin.binaryVersion=BitConverter.ToInt32(bytes,4);
        var packet=OwnedRuntimeCheckpointBinary.DecodeFlight(bytes);
        var host=packet.host??throw new InvalidDataException("Checkpoint host absent.");
        var state=host.state??throw new InvalidDataException("Checkpoint state absent.");
        Need(host.implementation=="retained-bulk-systems/v1","Source-hash exclusion is admitted only for the changed builtin executable treatment.");
        string sourceHash=host.sourceSha256??throw new InvalidDataException("Original builtin executable hash absent.");
        Need(sourceHash.Length==64&&sourceHash.All(c=>c>='0'&&c<='9'||c>='a'&&c<='f'),"Original builtin executable hash invalid.");
        pin.hostSourceSha256=sourceHash;pin.hostImplementation=host.implementation;pin.codeGeneration=host.codeGeneration;pin.executionStrategy=state.executionStrategy;pin.systemsRevision=state.systemsRevision;
        // These three fields are the declared execution treatment, not normalized physical state.
        host.sourceSha256="";state.executionStrategy=0;state.systemsRevision=0;
        byte[] normalized=OwnedRuntimeCheckpointBinary.EncodeFlight(packet);pin.normalizedSha256=Hash(normalized);pin.normalizedBytes=normalized.Length;return normalized;
    }
    static int CompareExecutionSnapshots(string[] args)
    {
        string? receiptPath=null,failure=null;bool created=false;FileStream? output=null;
        var comparisons=new List<ExecutionSnapshotComparison>();int matched=0;
        try
        {
            Need(args.Length==4,"--compare-execution OLD_ABSOLUTE_DIR NEW_ABSOLUTE_DIR FRESH_ABSOLUTE_RECEIPT");
            Need(Path.IsPathFullyQualified(args[1])&&Path.IsPathFullyQualified(args[2])&&Path.IsPathFullyQualified(args[3]),"Absolute comparison paths required.");
            string oldDirectory=Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1])),newDirectory=Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[2]));receiptPath=Path.GetFullPath(args[3]);
            NoLinks(oldDirectory);NoLinks(newDirectory);NoLinks(receiptPath);Need(oldDirectory!=newDirectory,"Comparison requires two distinct retained directories.");
            Need(receiptPath!=oldDirectory&&receiptPath!=newDirectory&&!receiptPath.StartsWith(oldDirectory+Path.DirectorySeparatorChar,StringComparison.Ordinal)&&!receiptPath.StartsWith(newDirectory+Path.DirectorySeparatorChar,StringComparison.Ordinal),"Receipt must be outside both source directories.");
            Need(!File.Exists(receiptPath)&&!Directory.Exists(receiptPath)&&Directory.Exists(Path.GetDirectoryName(receiptPath)),"Fresh receipt with existing parent required.");
            output=new FileStream(receiptPath,FileMode.CreateNew,FileAccess.Write,FileShare.None);created=true;
            var oldFiles=ExecutionSnapshotFiles(oldDirectory);var newFiles=ExecutionSnapshotFiles(newDirectory);
            Need(oldFiles.Keys.OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(newFiles.Keys.OrderBy(x=>x,StringComparer.Ordinal)),"Checkpoint filename coverage differs; no unmatched packet is omitted.");
            foreach(string name in oldFiles.Keys.OrderBy(x=>x,StringComparer.Ordinal))
            {
                var oldPin=new ExecutionSnapshotPin();var newPin=new ExecutionSnapshotPin();
                var row=new ExecutionSnapshotComparison{file=name,oldPacket=oldPin,newPacket=newPin};comparisons.Add(row);
                byte[]? a=null,b=null;
                try{a=NormalizeExecutionSnapshot(oldFiles[name],oldPin);}
                catch(Exception error){row.failure="Old packet: "+error;}
                try{b=NormalizeExecutionSnapshot(newFiles[name],newPin);}
                catch(Exception error){row.failure+=(row.failure.Length>0?"\n":"")+"New packet: "+error;}
                if(a==null||b==null)continue;
                try
                {
                    row.completeNormalizedBinaryEqual=a.AsSpan().SequenceEqual(b);
                    if(!row.completeNormalizedBinaryEqual)
                    {int count=Math.Min(a.Length,b.Length);int offset=0;while(offset<count&&a[offset]==b[offset])offset++;row.firstDifferentNormalizedByte=offset;throw new InvalidDataException("Complete normalized packets differ at byte "+offset+" (lengths "+a.Length+"/"+b.Length+").");}
                    row.status="passed";matched++;
                }
                catch(Exception error){row.failure=error.ToString();}
            }
            foreach(var row in comparisons)
            {
                foreach(var pin in new[]{row.oldPacket,row.newPacket})if(pin!=null&&pin.sha256.Length>0)
                {NoLinks(pin.path);Need(new FileInfo(pin.path).Length==pin.bytes&&HashFile(pin.path)==pin.sha256,"Original checkpoint changed during comparison.");}
            }
            Need(matched==oldFiles.Count,"One or more complete execution packets differ or could not decode.");
        }
        catch(Exception error){failure=error.ToString();}
        finally
        {
            if(created&&output!=null)
            {
                using(output)JsonSerializer.Serialize(output,new{schema="continuum-offline-execution-comparison/v1",status=failure==null?"passed":"failed",failure,
                    oldDirectory=args[1],newDirectory=args[2],matchedPackets=matched,comparedPackets=comparisons.Count,comparisons,
                    excludedFields=new[]{"host.sourceSha256","host.state.executionStrategy","host.state.systemsRevision"},
                    processId=Environment.ProcessId,framework=RuntimeInformation.FrameworkDescription,os=RuntimeInformation.OSDescription,architecture=RuntimeInformation.ProcessArchitecture.ToString(),
                    toolSha256=HashFile(Assembly.GetExecutingAssembly().Location),coreSha256=HashFile(typeof(OwnedRuntimeCheckpointBinary).Assembly.Location),normalizingBinaryVersion=OwnedRuntimeCheckpointBinary.Version,
                    scope="Read-only retained-packet comparison. Independent bounded binary decodes; all fields except the three declared execution-treatment fields are re-encoded and compared byte-for-byte, including bank/history/mechanical/resource/provider/thermal/flight/pilot/control and any execution/event queues. No simulation, host restoration, source mutation, precision tolerance or performance measurement."},Json);
            }
        }
        if(failure!=null){Console.Error.WriteLine(failure);return 1;}
        Console.WriteLine("PASS "+matched+" complete normalized execution packets; receipt "+receiptPath);return 0;
    }
}
