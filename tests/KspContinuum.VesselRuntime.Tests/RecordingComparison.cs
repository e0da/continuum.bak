using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using KspContinuum;

static class RecordingComparison
{
    const double AbsoluteTolerance=1e-6, RelativeTolerance=1e-9;
    static void Require(bool condition,string reason) { if(!condition) throw new InvalidDataException(reason); }
    static string Hash(string path) { using(var stream=File.OpenRead(path)) using(var sha=SHA256.Create()) return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant(); }
    static double maximumAbsoluteError;
    static void Compare(double expected,double actual,string source)
    {
        Require(!double.IsNaN(actual)&&!double.IsInfinity(actual),source+" nonfinite result.");
        double error=Math.Abs(expected-actual);maximumAbsoluteError=Math.Max(maximumAbsoluteError,error);
        Require(error<=Math.Max(AbsoluteTolerance,RelativeTolerance*Math.Max(Math.Abs(expected),Math.Abs(actual))),source+" exceeds frozen tolerance.");
    }
    public static int Run(string[] args)
    {
        if(args.Length!=5) throw new ArgumentException("--compare RECORDING KSP_REPORT NATIVE_REPORT NEW_OUTPUT");
        string recordingPath=Path.GetFullPath(args[1]),capturePath=Path.GetFullPath(args[2]),nativePath=Path.GetFullPath(args[3]),outputPath=Path.GetFullPath(args[4]);
        Require(!File.Exists(outputPath),"Comparison output must be new.");
        string recordingHash=Hash(recordingPath);var recording=VesselRuntimeFormat.Read(recordingPath);
        using(var captureDoc=JsonDocument.Parse(File.ReadAllBytes(capturePath)))
        using(var nativeDoc=JsonDocument.Parse(File.ReadAllBytes(nativePath)))
        {
            var capture=captureDoc.RootElement;var native=nativeDoc.RootElement;
            Require(capture.GetProperty("schema").GetString()=="continuum-vessel-runtime-capture/v1"&&capture.GetProperty("status").GetString()=="complete","KSP capture incomplete.");
            Require(native.GetProperty("schema").GetString()=="continuum-vessel-native-analysis/v1"&&native.GetProperty("status").GetString()=="complete","Native analysis incomplete.");
            Require(capture.GetProperty("recordingSha256").GetString()==recordingHash&&capture.GetProperty("manifestSha256").GetString()==recording.manifestSha256&&capture.GetProperty("vesselId").GetString()==recording.vesselId,"KSP capture binding differs.");
            Require(capture.GetProperty("runId").GetString()==recording.sessionId.Substring(0,recording.sessionId.LastIndexOf('/')),"KSP session binding differs.");
            foreach(var source in new[]{capture,native})
                Require(source.GetProperty("frames").GetInt32()==recording.frames.Length&&source.GetProperty("draws").GetInt32()==recording.drawIds.Length&&source.GetProperty("parts").GetInt32()==recording.partIds.Length,"Consumer dimensions differ.");
            Require(native.GetProperty("bytes").GetInt64()==new FileInfo(recordingPath).Length,"Native byte count differs.");
            var captured=capture.GetProperty("results");var analyzed=native.GetProperty("results");
            Require(captured.GetArrayLength()==recording.frames.Length&&analyzed.GetArrayLength()==recording.frames.Length,"Result count differs.");
            for(int i=0;i<recording.frames.Length;i++)
            {
                var frame=recording.frames[i];var expected=VesselRuntimeFormat.Analyze(frame);
                foreach(var row in new[]{captured[i],analyzed[i]})
                {
                    Require(row.GetProperty("sequence").GetInt64()==frame.sequence&&row.GetProperty("universalTime").GetDouble()==frame.universalTime,"Result frame binding differs.");
                    foreach(string component in new[]{"force","moment"})
                    {
                        var vector=row.GetProperty(component);Require(vector.GetArrayLength()==3,"Result vector shape differs.");
                        for(int axis=0;axis<3;axis++) Compare(component=="force"?expected.force[axis]:expected.moment[axis],vector[axis].GetDouble(),component+" frame "+i+" axis "+axis);
                    }
                }
            }
            Require(Hash(recordingPath)==recordingHash,"Recording changed during comparison.");
            var result=new {schema="continuum-vessel-consumer-comparison/v1",status="passed",recordingPath,recordingSha256=recordingHash,captureReportSha256=Hash(capturePath),nativeReportSha256=Hash(nativePath),recording.sessionId,recording.vesselId,recording.manifestSha256,frames=recording.frames.Length,draws=recording.drawIds.Length,parts=recording.partIds.Length,absoluteTolerance=AbsoluteTolerance,relativeTolerance=RelativeTolerance,maximumAbsoluteError,scope="All recorded frames: in-KSP shared analysis, portable C# decode/analysis and independent Rust decode/analysis; diagnostic observed loads, no force publication equivalence."};
            using(var stream=new FileStream(outputPath,FileMode.CreateNew,FileAccess.Write)) JsonSerializer.Serialize(stream,result,new JsonSerializerOptions{WriteIndented=true});
            Console.WriteLine("PASS all "+recording.frames.Length+" frame resultants across KSP, portable C# and Rust; maximum absolute error "+maximumAbsoluteError.ToString("R"));return 0;
        }
    }
}
