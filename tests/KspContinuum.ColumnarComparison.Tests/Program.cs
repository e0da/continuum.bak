using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

static class Program
{
    const int MaximumImage=4*1024*1024;
    static void Need(bool value,string reason){if(!value)throw new InvalidOperationException(reason);}
    static void Fresh(string path,byte[] bytes)
    {using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);file.Write(bytes);}
    static string Hash(byte[] data)=>Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    static int Run(string directory,string name,params string[] arguments)
    {
        string tool=Path.Combine(AppContext.BaseDirectory,"capacity-lab","KspContinuum.CapacityLab.dll");
        Need(File.Exists(tool),"Built actual CapacityLab consumer is missing.");
        var start=new ProcessStartInfo("dotnet"){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};
        start.ArgumentList.Add(tool);foreach(string argument in arguments)start.ArgumentList.Add(argument);
        using var child=Process.Start(start)??throw new InvalidOperationException("CapacityLab process did not start.");
        var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();
        if(!child.WaitForExit(120000)){child.Kill(entireProcessTree:true);child.WaitForExit();throw new TimeoutException("Bounded tiny CapacityLab consumer exceeded two minutes.");}
        Fresh(Path.Combine(directory,name+".log"),Encoding.UTF8.GetBytes(stdout.GetAwaiter().GetResult()+stderr.GetAwaiter().GetResult()));return child.ExitCode;
    }
    static void CopyFixture(string source,string destination)
    {
        Directory.CreateDirectory(destination);
        foreach(string file in Directory.GetFiles(source))
        {Need(new FileInfo(file).Length<=MaximumImage,"Tiny fixture file exceeds frozen 4 MiB limit.");File.Copy(file,Path.Combine(destination,Path.GetFileName(file)),overwrite:false);}
    }
    static void MutateQuantity(string path)
    {
        Need(new FileInfo(path).Length<=MaximumImage,"Image exceeds test bound.");byte[] bytes=File.ReadAllBytes(path);
        Need(bytes.Length>=144&&Encoding.ASCII.GetString(bytes,0,8)=="ORSCOL01"&&BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8,4))==1,"Unexpected real wire.");
        int metadata=BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(88,4));Need(metadata>=0&&metadata<=bytes.Length-144,"Metadata extent differs.");
        int cursor=92+metadata,footer=bytes.Length-52;bool changed=false,massless=false;
        while(cursor<footer)
        {
            Need(cursor<=footer-57,"Short chunk header.");var header=bytes.AsSpan(cursor,57);
            int id=BinaryPrimitives.ReadInt32LittleEndian(header),count=BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12,4)),width=BinaryPrimitives.ReadInt32LittleEndian(header.Slice(16,4));
            long start=BinaryPrimitives.ReadInt64LittleEndian(header.Slice(4,8));byte encoding=header[20];int payload=BinaryPrimitives.ReadInt32LittleEndian(header.Slice(21,4));
            Need(count>0&&start>=0&&width>=0&&payload>=0&&payload<=footer-cursor-57,"Chunk extent differs.");
            if(id==20&&start<=1&&1-start<count)
            {
                Need(width==41&&encoding==0&&payload==checked(count*width),"Resource definition row layout differs.");
                int densityOffset=checked(cursor+57+(int)(1-start)*width+24);
                massless=BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(densityOffset,8)))==0;
                Need(massless,"Selected quantity row is not massless.");
            }
            if(id==22&&start<=1&&1-start<count)
            {
                Need(!changed&&width==8&&encoding==0&&payload==checked(count*width),"Expected unique raw EC quantity column.");
                int valueOffset=checked(cursor+57+(int)(1-start)*width);long raw=BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(valueOffset,8));double original=BitConverter.Int64BitsToDouble(raw);
                Need(double.IsFinite(original)&&original>.125,"Reached EC row cannot admit frozen mutation.");
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(valueOffset,8),BitConverter.DoubleToInt64Bits(original-.125));
                SHA256.HashData(bytes.AsSpan(cursor+57,payload)).AsSpan().CopyTo(bytes.AsSpan(cursor+25,32));changed=true;
            }
            cursor=checked(cursor+57+payload);
        }
        Need(changed&&massless&&cursor==footer&&BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(footer,4))==-1,"Real massless EC row/footer was not reached.");
        Need(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(footer+4,8))==bytes.Length-32,"Whole envelope count differs.");
        SHA256.HashData(bytes.AsSpan(0,bytes.Length-32)).AsSpan().CopyTo(bytes.AsSpan(bytes.Length-32,32));
        File.WriteAllBytes(path,bytes);
        Need(SHA256.HashData(bytes.AsSpan(0,bytes.Length-32)).AsSpan().SequenceEqual(bytes.AsSpan(bytes.Length-32,32)),"Injected envelope digest is not valid.");
    }
    static int Main(string[] args)
    {
        string root=args.Length==0?Path.Combine(Path.GetTempPath(),"continuum-columnar-falsifier-"+Guid.NewGuid().ToString("N")):args.Length==1?args[0]:throw new ArgumentException("Use optional fresh absolute test-artifact directory.");
        Need(Path.IsPathFullyQualified(root)&&!Directory.Exists(root)&&!File.Exists(root),"Test directory must be fresh and absolute.");Directory.CreateDirectory(root);
        Fresh(Path.Combine(root,"test-scope.json"),JsonSerializer.SerializeToUtf8Bytes(new{schema="continuum-columnar-comparison-falsifier/v1",scope="Tiny generated actual consumer test. injected/receipt.json is deliberately rewritten test data, not a new qualified capacity result.",quantityRow=1,delta=-.125,maximumImageBytes=MaximumImage}));
        string original=Path.Combine(root,"original"),injected=Path.Combine(root,"injected"),positive=Path.Combine(root,"self-comparison"),negative=Path.Combine(root,"changed-comparison");
        Need(Run(root,"fixture","--mode","runtime","--state-format","columnar","--provider","engine","--parts","8","--bodies","4","--regions","1","--steps","1","--continuation-steps","1","--active-fraction",".25","--strategy","compiled","--output",original)==0,"Actual tiny fixture failed.");
        Need(Run(root,"self-comparison","--mode","compare-columnar","--baseline",original,"--candidate",original,"--output",positive)==0,"Actual self comparison failed.");
        CopyFixture(original,injected);string checkpoint=Path.Combine(injected,"checkpoint.ocss");MutateQuantity(checkpoint);
        string receiptPath=Path.Combine(injected,"receipt.json");var receipt=JsonNode.Parse(File.ReadAllBytes(receiptPath))??throw new InvalidDataException("Fixture receipt missing.");
        string oldHash=receipt["CheckpointSha256"]!.GetValue<string>();string changedHash=Hash(File.ReadAllBytes(checkpoint));Need(oldHash!=changedHash,"Mutation did not change image.");receipt["CheckpointSha256"]=changedHash;File.WriteAllBytes(receiptPath,JsonSerializer.SerializeToUtf8Bytes(receipt));
        Need(Run(root,"changed-comparison","--mode","compare-columnar","--baseline",original,"--candidate",injected,"--output",negative)!=0,"Authenticated quantity mutation falsely passed.");
        using var failure=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(negative,"receipt.json")));
        Need(failure.RootElement.GetProperty("status").GetString()=="failed"&&failure.RootElement.GetProperty("failure").GetString()!.Contains("Complete physical state differs at byte",StringComparison.Ordinal),"Mutation failed for another premise instead of physical bytes outside mask.");
        Fresh(Path.Combine(root,"proof.json"),JsonSerializer.SerializeToUtf8Bytes(new{schema="continuum-columnar-comparison-falsifier/v1",status="passed",actualConsumerSelfComparison=true,validChunkAndEnvelopeHashes=true,receiptDigestRebound=true,changedMasslessEcQuantityRejected=true,originalCheckpointSha256=oldHash,injectedCheckpointSha256=changedHash}));
        Console.WriteLine("PASS actual tiny CapacityLab self comparison and authenticated massless EC corruption rejected outside metadata mask: "+root);return 0;
    }
}
