using System;
using System.IO;
using KspContinuum;
static class Program
{
    static double[] Identity() { return new double[] { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 }; }
    static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } catch (EndOfStreamException) { return; } throw new Exception("Expected rejection."); }
    static int Main(string[] args)
    {
        if(args.Length>0 && args[0]=="--compare") return RecordingComparison.Run(args);
        var frame = new VesselRuntimeFrame { sequence=1, universalTime=123, rootToWorld=Identity(), poses=Identity(), visible=new[]{true}, altitude=60000, density=.001, speed=2000, loads=new double[]{2,0,0,0,3,0, -2,0,0,0,-3,0} };
        var recording = new VesselRuntimeRecording { sessionId="analytic", vesselId="vessel", manifestSha256=new string('a',64), topology=1, drawIds=new[]{"1/0/MeshRenderer/slot/0"}, partIds=new uint[]{1,2}, frames=new[]{frame} };
        var result=VesselRuntimeFormat.Analyze(frame); Check(result.force[0]==0 && result.force[1]==0 && result.moment[2]==12, "Independent analytic force couple differs.");
        frame.loads=new double[]{2,4,0,1,3,0, -2,4,0,0,-3,0}; var before=VesselRuntimeFormat.Analyze(frame); frame.loads[0]-=5;frame.loads[6]-=5; var after=VesselRuntimeFormat.Analyze(frame); Check(after.moment[2]==before.moment[2]-5*before.force[1], "Origin translation law differs.");
        string path=args.Length==1?Path.GetFullPath(args[0]):Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".cvr");
        try {
            VesselRuntimeFormat.Write(path,recording); var decoded=VesselRuntimeFormat.Read(path); Check(decoded.frames[0].loads[3]==1&&decoded.frames[0].poses[15]==1,"Cross-boundary values differ.");
            var manifest=new WholeCraftSceneManifest { vesselId="vessel", draws=new[]{new WholeCraftDraw {id=recording.drawIds[0],partId=1}} }; VesselRuntimeFormat.Bind(decoded,new string('a',64),manifest); Reject(()=>VesselRuntimeFormat.Bind(decoded,new string('b',64),manifest));
            recording.partIds[1]=1;Reject(()=>VesselRuntimeFormat.Validate(recording));recording.partIds[1]=2;
            recording.frames=new[]{frame,frame};Reject(()=>VesselRuntimeFormat.Validate(recording));recording.frames=new[]{frame};
            frame.poses[0]=0;Reject(()=>VesselRuntimeFormat.Validate(recording));frame.poses[0]=1;
            frame.loads[0]=double.NaN;Reject(()=>VesselRuntimeFormat.Analyze(frame));frame.loads[0]=-3;
            string damaged=path+".truncated";byte[] bytes=File.ReadAllBytes(path);File.WriteAllBytes(damaged,new byte[bytes.Length-1]);try{Reject(()=>VesselRuntimeFormat.Read(damaged));}finally{File.Delete(damaged);}
            Console.WriteLine("PASS analytic couple, origin law, binary roundtrip, foreign binding, duplicate identity, stale frame, singular pose, nonfinite load, truncated input."); return 0;
        } finally { if(args.Length==0)File.Delete(path); }
    }
}
