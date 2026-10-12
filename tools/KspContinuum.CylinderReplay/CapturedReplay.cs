using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
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
            results.Add(new {kind,rows=count,reachedPairs=tape.Length,acceptedCylinders=sourceCount,originalOracleTicks,
                exactOriginalTape=true,exactCandidateTape=true,exactPublishedMultipliers=true,exactAcceptedCylinderOrder=true});
        }
        Require(kinds.SetEquals(new[]{"solar","body"}),"Both scheduled kinds must be captured.");
        Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant()==hash,"Recording mutated during replay.");
        using(var stream=new FileStream(output,FileMode.CreateNew)) JsonSerializer.Serialize(stream,new {schema="continuum-cylinder-replay-proof/v1",status="passed",recordingSha256=hash,
            runId=root.GetProperty("runId").GetString(),ownedPid=root.GetProperty("processId").GetInt32(),assemblySha256=StockCylinder.Sha256,clockFrequency=Stopwatch.Frequency,passes=results,
            scope="Captured post-projection ordered fold only. Original assembly methods, reached pairs, Setup and publication matched bit for bit. Sorting, native projection, epoch publication and performance are separate gates; oracle timings include assertions and allocations and are not a speedup estimate."},new JsonSerializerOptions {WriteIndented=true});
        Console.WriteLine("Captured solar/body original operations and contiguous fold match bit for bit.");
    }
}
