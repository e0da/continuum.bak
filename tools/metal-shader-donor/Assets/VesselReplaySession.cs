using System;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using KspContinuum;
using UnityEngine;

public sealed class VesselReplaySession
{
    [Serializable] public sealed class Observation
    {
        public string schema="continuum-vessel-replay/v1",status="paused",recordingPath,recordingSha256,sessionId,vesselId,manifestSha256,sceneSha256,error,forceSummary,momentSummary;
        public int cursor,frames,draws,parts,appliedFrames,skippedPlaybackFrames,dynamicUploads,visibleDraws;
        public long topology,sequence,analysisTicks,applyTicks,dynamicUploadBytes,loadTicks,clockFrequency=Stopwatch.Frequency;
        public double universalTime,altitude,density,speed,rate=1;
        public double[] force,moment;
        public bool playing;
        public string scope="Captured rigid root-local poses and observed force resultant; no integration, uncaptured animation or new force publication";
    }
    [Serializable] sealed class Checkpoint
    {
        public string schema="continuum-vessel-replay-checkpoint/v1",recordingPath,recordingSha256,sceneSha256,manifestSha256,sessionId,vesselId;
        public long topology,sequence; public int cursor; public double universalTime,rate; public bool playing;
    }
    public readonly Observation Status;
    readonly VesselRuntimeRecording recording; readonly WholeCraftRuntime runtime;
    double playClock,playUT;
    static string Absolute(string path) { if(string.IsNullOrEmpty(path)||!Path.IsPathRooted(path)) throw new InvalidDataException("Absolute replay path required."); return Path.GetFullPath(path); }
    static string Hash(string path) { using(var file=File.OpenRead(path)) using(var hash=SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(file)).Replace("-","").ToLowerInvariant(); }
    static double Clock { get { return Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency; } }
    VesselReplaySession(string path,string sceneHash,WholeCraftRuntime whole)
    {
        long start=Stopwatch.GetTimestamp(); if(whole==null) throw new InvalidDataException("Whole-craft consumer required for replay."); path=Absolute(path); if(new FileInfo(path).Length>VesselRuntimeFormat.MaxBytes) throw new InvalidDataException("Recording file bound exceeded."); string digest=Hash(path); recording=VesselRuntimeFormat.Read(path); if(Hash(path)!=digest) throw new InvalidDataException("Recording changed during read."); VesselRuntimeFormat.Bind(recording,whole.ManifestSha256,whole.Manifest); runtime=whole;
        Status=new Observation {recordingPath=path,recordingSha256=digest,sessionId=recording.sessionId,vesselId=recording.vesselId,manifestSha256=recording.manifestSha256,sceneSha256=sceneHash,topology=recording.topology,frames=recording.frames.Length,draws=recording.drawIds.Length,parts=recording.partIds.Length,loadTicks=Stopwatch.GetTimestamp()-start};
    }
    public static VesselReplaySession Open(string path,string sceneHash,WholeCraftRuntime whole)
    { var session=new VesselReplaySession(path,sceneHash,whole); session.Seek(0); return session; }
    void Apply(int index)
    {
        if(index<0||index>=recording.frames.Length) throw new InvalidDataException("Replay cursor outside recording."); var frame=recording.frames[index]; VesselRuntimeFormat.ValidateFrame(frame,recording.drawIds.Length,recording.partIds.Length);
        long start=Stopwatch.GetTimestamp(); var result=VesselRuntimeFormat.Analyze(frame); long analyzed=Stopwatch.GetTimestamp(); runtime.ApplyReplayFrame(frame); long applied=Stopwatch.GetTimestamp();
        Status.cursor=index; Status.sequence=frame.sequence; Status.universalTime=frame.universalTime; Status.altitude=frame.altitude; Status.density=frame.density; Status.speed=frame.speed; Status.force=result.force; Status.moment=result.moment; Status.forceSummary=string.Format(CultureInfo.InvariantCulture,"{0:G5},{1:G5},{2:G5}",result.force[0],result.force[1],result.force[2]); Status.momentSummary=string.Format(CultureInfo.InvariantCulture,"{0:G5},{1:G5},{2:G5}",result.moment[0],result.moment[1],result.moment[2]); Status.analysisTicks+=analyzed-start; Status.applyTicks+=applied-analyzed; Status.appliedFrames++; Status.dynamicUploadBytes=runtime.DynamicUploadBytes; Status.dynamicUploads=runtime.DynamicUploadCount; Status.visibleDraws=runtime.Manifest.draws.Length-runtime.NonvisibleDraws; Status.error=null;
    }
    public void Seek(int index) { Apply(index); Pause(); }
    public void Pause() { Status.playing=false; Status.status="paused"; }
    public void Play(double rate)
    {
        if(double.IsNaN(rate)||double.IsInfinity(rate)||rate<=0||rate>16) throw new InvalidDataException("Replay rate must be finite in (0,16]."); Status.rate=rate; Status.playing=true; Status.status="playing"; playClock=Clock; playUT=Status.universalTime;
    }
    public void Tick()
    {
        if(!Status.playing) return; double target=playUT+(Clock-playClock)*Status.rate; int index=Status.cursor;
        while(index+1<recording.frames.Length&&recording.frames[index+1].universalTime<=target) index++;
        if(index!=Status.cursor) { int skipped=index-Status.cursor-1; Apply(index); Status.skippedPlaybackFrames+=skipped; }
        if(Status.cursor==recording.frames.Length-1) { Status.playing=false; Status.status="ended"; }
    }
    public void Fault(Exception error) { Status.playing=false; Status.status="failed"; Status.error=error.ToString(); }
    public void CheckpointTo(string path)
    {
        path=Absolute(path); if(File.Exists(path)||Directory.Exists(path)||!Directory.Exists(Path.GetDirectoryName(path))) throw new InvalidDataException("New checkpoint in existing private directory required.");
        var checkpoint=new Checkpoint {recordingPath=Status.recordingPath,recordingSha256=Status.recordingSha256,sceneSha256=Status.sceneSha256,manifestSha256=Status.manifestSha256,sessionId=Status.sessionId,vesselId=Status.vesselId,topology=Status.topology,cursor=Status.cursor,sequence=Status.sequence,universalTime=Status.universalTime,rate=Status.rate,playing=Status.playing};
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp"; try { File.WriteAllText(temporary,JsonUtility.ToJson(checkpoint,true)); File.Move(temporary,path); } finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }
    public static VesselReplaySession Resume(string path,string sceneHash,WholeCraftRuntime whole)
    {
        path=Absolute(path); string text; using(var stream=File.OpenRead(path)) { if(stream.Length<=0||stream.Length>65536) throw new InvalidDataException("Replay checkpoint bound exceeded."); byte[] bytes=new byte[(int)stream.Length]; int read=0; while(read<bytes.Length) { int count=stream.Read(bytes,read,bytes.Length-read); if(count==0) throw new InvalidDataException("Replay checkpoint truncated."); read+=count; } if(stream.ReadByte()!=-1) throw new InvalidDataException("Replay checkpoint grew."); text=new System.Text.UTF8Encoding(false,true).GetString(bytes); } var checkpoint=JsonUtility.FromJson<Checkpoint>(text);
        if(checkpoint==null||checkpoint.schema!="continuum-vessel-replay-checkpoint/v1"||checkpoint.sceneSha256!=sceneHash||checkpoint.manifestSha256!=whole.ManifestSha256) throw new InvalidDataException("Replay checkpoint scene binding differs.");
        var next=new VesselReplaySession(checkpoint.recordingPath,sceneHash,whole);
        if(next.Status.recordingSha256!=checkpoint.recordingSha256||next.Status.sessionId!=checkpoint.sessionId||next.Status.vesselId!=checkpoint.vesselId||next.Status.topology!=checkpoint.topology||checkpoint.cursor<0||checkpoint.cursor>=next.recording.frames.Length) throw new InvalidDataException("Replay checkpoint recording binding differs.");
        var frame=next.recording.frames[checkpoint.cursor]; if(frame.sequence!=checkpoint.sequence||frame.universalTime!=checkpoint.universalTime||double.IsNaN(checkpoint.rate)||double.IsInfinity(checkpoint.rate)||checkpoint.rate<=0||checkpoint.rate>16) throw new InvalidDataException("Replay checkpoint cursor or rate differs.");
        next.Seek(checkpoint.cursor); if(checkpoint.playing) next.Play(checkpoint.rate); else next.Status.rate=checkpoint.rate; return next;
    }
}
