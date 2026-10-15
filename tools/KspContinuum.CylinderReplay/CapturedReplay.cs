using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Reflection;
using KspContinuum;
using UnityEngine;

static class CapturedReplay
{
    static void Require(bool condition,string detail) { if(!condition) throw new InvalidDataException(detail); }
    static float F(JsonElement row,string key) { float n=row.GetProperty(key).GetSingle(); Require(float.IsFinite(n),"Nonfinite "+key); return n; }
    static long L(JsonElement row,string key) { return row.GetProperty(key).GetInt64(); }
    static double D(JsonElement row,string key) { double n=BitConverter.Int64BitsToDouble(L(row,key)); Require(double.IsFinite(n),"Nonfinite "+key); return n; }
    static bool Same(float a,float b) { return BitConverter.SingleToInt32Bits(a)==BitConverter.SingleToInt32Bits(b); }
    static CylinderOcclusionFold.Row Row(JsonElement row)
    { return new CylinderOcclusionFold.Row { minX=F(row,"minX"),minY=F(row,"minY"),maxX=F(row,"maxX"),maxY=F(row,"maxY"),centerX=F(row,"centerX"),centerY=F(row,"centerY"),extentX=F(row,"extentX"),extentY=F(row,"extentY") }; }
    static void Cylinder(JsonElement tape,OcclusionCylinder c,int row)
    {
        Require(tape.GetProperty("row").GetInt32()==row && Same(F(tape,"centerX"),c.center.x) && Same(F(tape,"centerY"),c.center.y) && Same(F(tape,"centerZ"),c.center.z) &&
            Same(F(tape,"offsetX"),c.offset.x) && Same(F(tape,"offsetY"),c.offset.y) && Same(F(tape,"extentX"),c.extents.x) && Same(F(tape,"extentY"),c.extents.y) &&
            StockCylinder.Same(D(tape,"radiusBits"),c.radius) && StockCylinder.Same(D(tape,"cylNoseDotBits"),c.cylNoseDot),"Original Setup output differs at row"+row);
    }
    sealed class Outcome
    {
        internal double[] multipliers; internal int[] accepted; internal int count;
        internal long scalarCalls,nodeVisits; internal double margin;
    }
    static Outcome StockFold(OcclusionData[] rows)
    {
        int n=rows.Length; var result=new Outcome { multipliers=new double[n],accepted=new int[n],count=1 };
        var sources=new OcclusionCylinder[n]; StockFoldInto(rows,result,sources); return result;
    }
    static void StockFoldInto(OcclusionData[] rows,Outcome result,OcclusionCylinder[] sources)
    {
        int n=rows.Length; result.count=1; result.scalarCalls=0; result.accepted[0]=n-1; result.multipliers[n-1]=1;
        if(sources[0]==null) sources[0]=new OcclusionCylinder(); sources[0].Setup(rows[n-1]);
        for(int target=n-2;target>=0;target--)
        {
            double exposure=1;
            for(int j=0;j<result.count;j++)
            {
                result.scalarCalls++; exposure-=rows[target].GetCylinderOcclusion(sources[j]);
                if(exposure<=.001) { exposure=0; break; }
            }
            result.multipliers[target]=exposure;
            if(exposure>0) { result.accepted[result.count]=target; if(sources[result.count]==null) sources[result.count]=new OcclusionCylinder(); sources[result.count].Setup(rows[target]); result.count++; }
        }
    }
    static Outcome IndexedFold(CylinderOcclusionFold.Row[] rows)
    {
        var result=new Outcome { multipliers=new double[rows.Length],accepted=new int[rows.Length] };
        var index=new CylinderOcclusionIndex(rows,rows.Length);
        result.count=index.Evaluate(result.multipliers,result.accepted);
        result.scalarCalls=index.ScalarCalls; result.nodeVisits=index.NodeVisits; result.margin=index.ConservativeMargin;
        return result;
    }
    static void Match(Outcome result,double[] expected,int[] order,int count)
    {
        Require(result.count==count,"Timed accepted count differs.");
        for(int i=0;i<expected.Length;i++) Require(StockCylinder.Same(result.multipliers[i],expected[i]),"Timed multiplier differs.");
        for(int i=0;i<count;i++) Require(result.accepted[i]==order[i],"Timed accepted order differs.");
    }
    public static void Run(string source,string output)
    {
        Require(Path.IsPathRooted(source)&&Path.IsPathRooted(output)&&!File.Exists(output),"Absolute retained source and new output required.");
        StockCylinder.Admit(); byte[] bytes=File.ReadAllBytes(source); string hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        using var document=JsonDocument.Parse(bytes); JsonElement root=document.RootElement;
        Require(root.GetProperty("schema").GetString()=="continuum-cylinder-occlusion-recording/v1" && root.GetProperty("assemblySha256").GetString()==StockCylinder.Sha256 &&
            root.GetProperty("assemblyMvid").GetString()=="10657063-2fc3-43a7-84fa-d39e75e877bf","Recording identity differs.");
        var results=new List<object>(); var kinds=new HashSet<string>();
        foreach(JsonElement pass in root.GetProperty("passes").EnumerateArray())
        {
            string kind=pass.GetProperty("kind").GetString(); Require(kind is "solar" or "body" && kinds.Add(kind),"Duplicate or unknown pass kind.");
            var raw=pass.GetProperty("rows").EnumerateArray().ToArray(); int count=pass.GetProperty("rowCount").GetInt32();
            Require(count>0&&count<=1024&&raw.Length==count&&L(pass,"endTicks")>L(pass,"startTicks"),"Pass shape or interval differs.");
            var geometry=raw.Select(Row).ToArray(); var original=new OcclusionData[count]; var ids=new HashSet<uint>();
            for(int i=0;i<count;i++)
            {
                Require(ids.Add(raw[i].GetProperty("flightId").GetUInt32()),"Duplicate part identity.");
                original[i]=StockCylinder.Input(geometry[i]); original[i].projectedCenter=new Vector3(F(raw[i],"projectedCenterX"),F(raw[i],"projectedCenterY"),F(raw[i],"projectedCenterZ"));
                original[i].projectedRadius=D(raw[i],"projectedRadiusBits"); original[i].maximumDot=D(raw[i],"maximumDotBits"); original[i].maxWidthDepth=D(raw[i],"maxWidthDepthBits");
                if(i>0) Require(original[i].maximumDot>=original[i-1].maximumDot,"Post-projection order is not ascending.");
            }
            uint[] initial=pass.GetProperty("initialOrder").EnumerateArray().Select(x=>x.GetUInt32()).ToArray();
            Require(initial.Length==count&&initial.Distinct().Count()==count&&initial.All(ids.Contains),"Initial membership differs.");
            JsonElement[] tape=pass.GetProperty("pairs").EnumerateArray().ToArray(), cylinders=pass.GetProperty("cylinders").EnumerateArray().ToArray();
            Require(tape.Length==pass.GetProperty("pairCount").GetInt32()&&cylinders.Length==pass.GetProperty("cylinderCount").GetInt32(),"Tape counts differ.");
            var accepted=new int[count]; var stock=new double[count]; var sources=new OcclusionCylinder[count]; int sourceCount=1,pairIndex=0;
            long before=Stopwatch.GetTimestamp();
            accepted[0]=count-1; stock[count-1]=1; sources[0]=new OcclusionCylinder(); sources[0].Setup(original[count-1]);
            Require(cylinders.Length>0,"First cylinder absent."); Cylinder(cylinders[0],sources[0],count-1);
            for(int target=count-2;target>=0;target--)
            {
                double exposure=1;
                for(int j=0;j<sourceCount;j++)
                {
                    Require(pairIndex<tape.Length,"Original replay reached an unrecorded pair."); JsonElement pair=tape[pairIndex++];
                    double contribution=original[target].GetCylinderOcclusion(sources[j]);
                    Require(pair.GetProperty("row").GetInt32()==target&&pair.GetProperty("cylinderRow").GetInt32()==accepted[j]&&StockCylinder.Same(contribution,D(pair,"resultBits")),"Original reached pair/result differs.");
                    exposure-=contribution; if(exposure<=.001) { exposure=0; break; }
                }
                stock[target]=exposure;
                if(exposure>0)
                {
                    Require(sourceCount<cylinders.Length,"Original replay accepted an unrecorded cylinder.");
                    accepted[sourceCount]=target; sources[sourceCount]=new OcclusionCylinder(); sources[sourceCount].Setup(original[target]);
                    Cylinder(cylinders[sourceCount],sources[sourceCount],target); sourceCount++;
                }
            }
            Require(pairIndex==tape.Length&&sourceCount==cylinders.Length,"Original replay omitted recorded operations.");
            long originalOracleTicks=Stopwatch.GetTimestamp()-before;
            var candidate=new double[count]; var candidateAccepted=new int[count]; int candidatePair=0;
            int candidateCount=CylinderOcclusionFold.Evaluate(geometry,count,candidate,candidateAccepted,(t,s,value)=>
            {
                Require(candidatePair<tape.Length,"Candidate extra pair."); JsonElement pair=tape[candidatePair++];
                Require(pair.GetProperty("row").GetInt32()==t&&pair.GetProperty("cylinderRow").GetInt32()==s&&StockCylinder.Same(value,D(pair,"resultBits")),"Candidate ordered pair/result differs.");
            });
            Require(candidatePair==tape.Length&&candidateCount==sourceCount,"Candidate tape coverage differs.");
            for(int i=0;i<count;i++) Require(StockCylinder.Same(stock[i],D(raw[i],"expectedMultiplierBits"))&&StockCylinder.Same(candidate[i],stock[i]),"Published multiplier differs at row"+i);
            for(int i=0;i<sourceCount;i++) Require(candidateAccepted[i]==accepted[i],"Accepted cylinder order differs.");
            var indexed=new double[count]; var indexedAccepted=new int[count]; int tapeCursor=0,indexedPairs=0,omittedZeros=0;
            var index=new CylinderOcclusionIndex(geometry,count);
            int indexedCount=index.Evaluate(indexed,indexedAccepted,(t,s,value)=>
            {
                while(tapeCursor<tape.Length && (tape[tapeCursor].GetProperty("row").GetInt32()!=t || tape[tapeCursor].GetProperty("cylinderRow").GetInt32()!=s))
                { Require(D(tape[tapeCursor++],"resultBits")==0,"Index omitted a nonzero original pair."); omittedZeros++; }
                Require(tapeCursor<tape.Length && StockCylinder.Same(value,D(tape[tapeCursor++],"resultBits")),"Index changed or added a reached pair."); indexedPairs++;
            });
            while(tapeCursor<tape.Length) { Require(D(tape[tapeCursor++],"resultBits")==0,"Index omitted a trailing nonzero original pair."); omittedZeros++; }
            Require(indexedCount==sourceCount && indexedPairs+omittedZeros==tape.Length,"Index coverage differs.");
            for(int i=0;i<count;i++) Require(StockCylinder.Same(indexed[i],stock[i]),"Indexed multiplier bits differ.");
            for(int i=0;i<sourceCount;i++)
            { Require(indexedAccepted[i]==accepted[i],"Indexed accepted order differs."); var c=new OcclusionCylinder(); c.Setup(original[indexedAccepted[i]]); Cylinder(cylinders[i],c,indexedAccepted[i]); }
            // Prime method compilation separately. Each measured sample still constructs a fresh index and output storage.
            Match(StockFold(original),stock,accepted,sourceCount); Match(IndexedFold(geometry),stock,accepted,sourceCount);
            var residentStock=new Outcome {multipliers=new double[count],accepted=new int[count]}; var residentSources=new OcclusionCylinder[count];
            for(int i=0;i<count;i++) residentSources[i]=new OcclusionCylinder();
            var residentIndex=new CylinderOcclusionIndex(geometry,count); var residentIndexed=new Outcome {multipliers=new double[count],accepted=new int[count]};
            StockFoldInto(original,residentStock,residentSources); Match(residentStock,stock,accepted,sourceCount);
            residentIndex.UpdateSnapshot(geometry); residentIndexed.count=residentIndex.Evaluate(residentIndexed.multipliers,residentIndexed.accepted); Match(residentIndexed,stock,accepted,sourceCount);
            var costs=new List<object>();
            for(int sample=0;sample<2;sample++)
            {
                foreach(bool useIndex in sample==0?new[]{false,true}:new[]{true,false})
                {
                    long allocated=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();
                    Outcome result=useIndex?IndexedFold(geometry):StockFold(original);
                    long ticks=Stopwatch.GetTimestamp()-start,allocation=GC.GetAllocatedBytesForCurrentThread()-allocated;
                    Match(result,stock,accepted,sourceCount);
                    costs.Add(new {sample,route=useIndex?"indexed-cold-fold":"original-assembly-fold",ticks,allocatedBytes=allocation,result.scalarCalls,result.nodeVisits,result.margin});
                }
                foreach(bool useIndex in sample==0?new[]{false,true}:new[]{true,false})
                {
                    long allocated=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();
                    Outcome result=useIndex?residentIndexed:residentStock;
                    if(useIndex)
                    {
                        residentIndex.UpdateSnapshot(geometry); result.count=residentIndex.Evaluate(result.multipliers,result.accepted);
                        result.scalarCalls=residentIndex.ScalarCalls; result.nodeVisits=residentIndex.NodeVisits; result.margin=residentIndex.ConservativeMargin;
                    }
                    else StockFoldInto(original,result,residentSources);
                    long ticks=Stopwatch.GetTimestamp()-start,allocation=GC.GetAllocatedBytesForCurrentThread()-allocated;
                    Match(result,stock,accepted,sourceCount);
                    costs.Add(new {sample,route=useIndex?"indexed-resident-refit-fold":"original-resident-fold",ticks,allocatedBytes=allocation,result.scalarCalls,result.nodeVisits,result.margin});
                }
            }
            results.Add(new {kind,rows=count,reachedPairs=tape.Length,acceptedCylinders=sourceCount,originalOracleTicks,
                exactOriginalTape=true,exactCandidateTape=true,exactPublishedMultipliers=true,exactAcceptedCylinderOrder=true,
                exactIndexedMultipliers=true,exactIndexedCylinderFields=true,retainedIndexedPairs=indexedPairs,omittedOriginalZeroPairs=omittedZeros,
                indexedPairTapeIsOrderedSubsequence=true,indexedNodeVisits=index.NodeVisits,conservativeMargin=index.ConservativeMargin,costs});
        }
        Require(kinds.SetEquals(new[]{"solar","body"}),"Both scheduled kinds must be captured.");
        Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant()==hash,"Recording mutated during replay.");
        using(var stream=new FileStream(output,FileMode.CreateNew)) JsonSerializer.Serialize(stream,new {schema="continuum-cylinder-replay-proof/v1",status="passed",recordingSha256=hash,
            runId=root.GetProperty("runId").GetString(),ownedPid=root.GetProperty("processId").GetInt32(),assemblySha256=StockCylinder.Sha256,clockFrequency=Stopwatch.Frequency,passes=results,
            toolSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Assembly.GetExecutingAssembly().Location))).ToLowerInvariant(),
            coreSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(CylinderOcclusionIndex).Assembly.Location))).ToLowerInvariant(),
            sourceHead=Environment.GetEnvironmentVariable("CONTINUUM_SOURCE_HEAD"),runtime=RuntimeInformation.FrameworkDescription,architecture=RuntimeInformation.ProcessArchitecture.ToString(),os=RuntimeInformation.OSDescription,
            scope="Captured post-projection fold only. Original methods and the contiguous fold match the complete tape; the spatial index retains all nonzero pairs in original order and matches published multiplier/cylinder bits. Two alternating elapsed-cost samples per treatment: cold index includes construction/copy/buffers; resident index includes current-row copy, conservative bound refit, queries, scalar fold and reused outputs. Resident original reuses cylinders/output storage and invokes original scalar/Setup. Both start from already projected rows; parsing, projection, live acquisition/publication and runtime differences are excluded. Priming excludes JIT. Cold original allocates cylinders unlike live stock, so resident comparison is the more relevant resolving observation. These are offline costs, not KSP FPS or confidence evidence; oracle assertion timings are not performance evidence."},new JsonSerializerOptions {WriteIndented=true});
        Console.WriteLine("Original tape, contiguous fold and zero-eliding spatial index match solar/body multiplier and cylinder bits.");
    }
}
