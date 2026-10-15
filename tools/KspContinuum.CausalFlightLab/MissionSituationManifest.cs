using System;
using KspContinuum;

public sealed class MissionControlCue
{
    public string id="";
    public long sequence;
    public double runtimeSeconds;
    public double throttle;
    public double[] attitude=new double[3],translation=new double[3];
    public bool engines,rcs,wheels;
    public OwnedRuntimeControls Controls()=>new(){Throttle=throttle,Attitude=new Vec(attitude[0],attitude[1],attitude[2]),Translation=new Vec(translation[0],translation[1],translation[2]),EnginesEnabled=engines,RcsEnabled=rcs,WheelsEnabled=wheels};
}

public sealed class MissionSituationManifest
{
    public string schema="continuum-mission-situation/v1",id="",title="",placement="",scope="";
    public double durationSeconds,altitudeMeters,airSpeedMetersPerSecond,descentMetersPerSecond,minimumMechanicalIntervalSeconds=.0001;
    public MissionControlCue[] controls=Array.Empty<MissionControlCue>();
    public string[] operations=Array.Empty<string>();
}

public static class MissionSituationCatalog
{
    static MissionControlCue Cue(string id,long sequence,double time,double throttle=0,double pitch=0,double yaw=0,double roll=0,double x=0,double y=0,double z=0,bool engines=false,bool rcs=false,bool wheels=false)
        =>new(){id=id,sequence=sequence,runtimeSeconds=time,throttle=throttle,attitude=new[]{pitch,yaw,roll},translation=new[]{x,y,z},engines=engines,rcs=rcs,wheels=wheels};
    public static MissionSituationManifest[] Create(string selector)
    {
        if(selector!="all"&&selector!="orbital"&&selector!="reentry")throw new ArgumentException("Mission selector must be all, orbital or reentry.");
        var orbit=new MissionSituationManifest{id="orbital",title="Captured craft: ordered burn, reversals, impulse, topology and continuation",placement="authored-circular-orbit",durationSeconds=1.125,altitudeMeters=100000,
            controls=new[]{Cue("burn-start",1,.125,.25,engines:true),Cue("burn-stage",2,.25,.4,engines:true),Cue("burn-cutoff-rcs-wheel",3,.375,pitch:.025,yaw:-.015,roll:.01,x:.02,z:-.01,rcs:true,wheels:true),Cue("rcs-wheel-reversal",4,.5,pitch:-.025,yaw:.015,roll:-.01,x:-.02,z:.01,rcs:true,wheels:true),Cue("controls-off",5,.625)},
            operations=new[]{"quiet pending-heat step","4x queued demand","execution checkpoint with debt and future events","exact restored continuation","same-frontier center impulse then controls","flush, split and rejoin acquired connection","4x and 32x demand without presentation"},
            scope="Actual acquired identities, geometry, providers and central body; authored placement and controls. No staging decoupler parity, contacts, arbitrary providers or original KSP behavior claim."};
        var reentry=new MissionSituationManifest{id="reentry",title="Captured craft: airborne fast descent with rotating atmosphere and wheel reversal",placement="authored-airborne-reentry",durationSeconds=.5,altitudeMeters=25000,airSpeedMetersPerSecond=1800,descentMetersPerSecond=100,minimumMechanicalIntervalSeconds=.000001,
            controls=new[]{Cue("reentry-wheel",1,.125,pitch:.02,yaw:.01,roll:-.01,wheels:true),Cue("reentry-wheel-reversal",2,.25,pitch:-.02,yaw:-.01,roll:.01,wheels:true),Cue("reentry-controls-off",3,.375)},
            operations=new[]{"captured altitude-density-temperature interpolation","corotating wind plus authored air-relative descent","ordered wheel reversals","thermal lag and finite-size spherical surface guard"},
            scope="Airborne spherical central-body/atmosphere model, short physical horizon. No terrain, landing, contact, shock/heat-source error certificate or stock reentry parity."};
        return selector=="orbital"?new[]{orbit}:selector=="reentry"?new[]{reentry}:new[]{orbit,reentry};
    }
}
