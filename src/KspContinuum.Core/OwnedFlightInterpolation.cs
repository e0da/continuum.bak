using System;

namespace KspContinuum
{
    public struct OwnedFlightDisplayPose
    {
        public long Id;
        public Vec CenterPositionBodyMeters,LocalCenterOfMass,LinearVelocity;
        public Rotation Orientation;
        public Vec RootPositionBodyMeters {get{return CenterPositionBodyMeters+Orientation.Rotate(LocalCenterOfMass)*-1;}}
    }
    public struct OwnedFlightInterpolationSource
    {
        public OwnedRuntimeStamp Stamp;
        public double UniversalTime,Warp;
        public long CodeGeneration,ConfigurationRevision;
        public string ContextSha256;
        public bool Paused;
    }
    public sealed class OwnedFlightDisplayFrame
    {
        internal readonly OwnedFlightDisplayPose[] poses;
        internal int count;
        internal OwnedFlightDisplayFrame(int capacity){poses=new OwnedFlightDisplayPose[capacity];}
        public int RegionCount {get{return count;}}
        public OwnedFlightDisplayPose GetRegion(int slot){if(slot<0||slot>=count)throw new ArgumentOutOfRangeException("slot");return poses[slot];}
        public OwnedRuntimeStamp PhysicalStamp {get;internal set;}
        public OwnedFlightWorld Context {get;internal set;}
        public string ContextSha256 {get;internal set;}
        public string Mode {get;internal set;}
        public double UniversalTime {get;internal set;}
        public double PhysicalUniversalTime {get;internal set;}
        public double Alpha {get;internal set;}
        public double DelaySeconds {get;internal set;}
        public long Revision {get;internal set;}
        public long AcceptedFrames {get;internal set;}
        public long Resets {get;internal set;}
        public string ResetReason {get;internal set;}
        public bool Enabled {get;internal set;}
        public bool Interpolated {get;internal set;}
    }

    // Main-thread observation state only. Copies contain no world/bank owner or mutation path.
    public sealed class OwnedFlightInterpolation
    {
        sealed class History
        {
            internal readonly OwnedFlightDisplayPose[] Poses;
            internal int Count;
            internal OwnedFlightInterpolationSource Source;
            internal double Wall;
            internal History(int capacity){Poses=new OwnedFlightDisplayPose[capacity];}
        }
        readonly int capacity;
        readonly double maximumGap;
        readonly OwnedFlightDisplayFrame display;
        History older,latest,staging;
        bool initialized,enabled=true,resetRequested;
        double delay,lastWall=double.NegativeInfinity;
        string requestedReason;
        OwnedFlightWorld context;
        string mode;
        public OwnedFlightInterpolation(int maximumRegions,double delaySeconds=.04,double maximumSampleGapSeconds=.25)
        {
            if(maximumRegions<1||maximumRegions>65536)throw new ArgumentOutOfRangeException("maximumRegions");
            Finite(maximumSampleGapSeconds);if(maximumSampleGapSeconds<=0||maximumSampleGapSeconds>10)throw new ArgumentOutOfRangeException("maximumSampleGapSeconds");
            capacity=maximumRegions;maximumGap=maximumSampleGapSeconds;older=new History(capacity);latest=new History(capacity);staging=new History(capacity);display=new OwnedFlightDisplayFrame(capacity);
            Configure(true,delaySeconds);
        }
        public void Configure(bool active,double delaySeconds)
        {
            Finite(delaySeconds);if(delaySeconds<0||delaySeconds>maximumGap)throw new ArgumentOutOfRangeException("delaySeconds");
            if(active!=enabled||delaySeconds!=delay){enabled=active;delay=delaySeconds;Reset("operator interpolation policy");}
        }
        public void Reset(string reason)
        {if(string.IsNullOrEmpty(reason)||reason.Length>256)throw new ArgumentException("Reset reason is required.");resetRequested=true;requestedReason=reason;}
        public void Accept(OwnedFlightPresentationLease frame,double wallSeconds)
        {
            if(frame==null||!frame.Read.Stamp.Matches(frame.Progress.Stamp))throw new ArgumentException("Interpolation publication differs.");
            var read=frame.Read;var source=new OwnedFlightInterpolationSource{Stamp=read.Stamp,UniversalTime=frame.UniversalTime,Warp=frame.Progress.Warp,
                CodeGeneration=frame.Progress.CodeGeneration,ConfigurationRevision=frame.Progress.ConfigurationRevision,ContextSha256=frame.Context.SourceSha256,Paused=frame.Progress.Paused};
            ValidateSource(source,wallSeconds,read.RegionCount);
            for(int r=0;r<read.RegionCount;r++)
            {var s=read.GetRegion(r);staging.Poses[r]=new OwnedFlightDisplayPose{Id=s.Id,CenterPositionBodyMeters=frame.Descriptor.CenterPosition(read,r),LocalCenterOfMass=s.Properties.CenterOfMass,LinearVelocity=s.Motion.Velocity,Orientation=s.Motion.Orientation};ValidatePose(staging.Poses[r]);}
            CommitAccepted(source,wallSeconds,read.RegionCount);context=frame.Context;mode=frame.Mode;
        }
        // Portable analytic input, also useful to independently check the copied view contract.
        public void Accept(OwnedFlightInterpolationSource source,OwnedFlightDisplayPose[] poses,double wallSeconds)
        {
            if(poses==null)throw new ArgumentNullException("poses");ValidateSource(source,wallSeconds,poses.Length);
            for(int i=0;i<poses.Length;i++){ValidatePose(poses[i]);for(int j=0;j<i;j++)if(poses[j].Id==poses[i].Id)throw new ArgumentException("Duplicate region identity.");staging.Poses[i]=poses[i];}
            CommitAccepted(source,wallSeconds,poses.Length);context=null;mode="portable pose observation";
        }
        void ValidateSource(OwnedFlightInterpolationSource source,double wall,int count)
        {
            Finite(wall);Finite(source.UniversalTime);Finite(source.Warp);
            if(wall<lastWall||source.Warp<=0||source.Warp>1000000||count<1||count>capacity||source.CodeGeneration<0||source.ConfigurationRevision<0||string.IsNullOrEmpty(source.ContextSha256)||source.ContextSha256.Length>128)
                throw new ArgumentException("Interpolation clock/source/storage domain differs.");
            if(source.Stamp.Epoch<0||source.Stamp.StateRevision<0||source.Stamp.TopologyRevision<0||source.Stamp.DefinitionRevision<0||source.Stamp.FrameRevision<0||string.IsNullOrEmpty(source.Stamp.FrameId))throw new ArgumentException("Interpolation physical stamp is invalid.");
        }
        static void ValidatePose(OwnedFlightDisplayPose p)
        {if(p.Id<0)throw new ArgumentException("Negative region identity.");RigidAssemblyProperties.RequireVector(p.CenterPositionBodyMeters);RigidAssemblyProperties.RequireVector(p.LocalCenterOfMass);RigidAssemblyProperties.RequireVector(p.LinearVelocity);Rotation.FromUnitComponents(p.Orientation.X,p.Orientation.Y,p.Orientation.Z,p.Orientation.W);}
        bool Compatible(OwnedFlightInterpolationSource a,OwnedFlightInterpolationSource b)
        {
            return a.Stamp.Epoch==b.Stamp.Epoch&&a.Stamp.TopologyRevision==b.Stamp.TopologyRevision&&a.Stamp.DefinitionRevision==b.Stamp.DefinitionRevision&&
                a.Stamp.FrameRevision==b.Stamp.FrameRevision&&a.Stamp.FrameId==b.Stamp.FrameId&&a.CodeGeneration==b.CodeGeneration&&a.ConfigurationRevision==b.ConfigurationRevision&&a.ContextSha256==b.ContextSha256;
        }
        void CommitAccepted(OwnedFlightInterpolationSource source,double wall,int count)
        {
            bool same=initialized&&source.Stamp.Matches(latest.Source.Stamp)&&Compatible(source,latest.Source)&&source.UniversalTime==latest.Source.UniversalTime;
            if(same)
            {if(count!=latest.Count)throw new ArgumentException("Unversioned region shape mutation.");for(int i=0;i<count;i++)if(!SamePose(staging.Poses[i],latest.Poses[i]))throw new ArgumentException("Unversioned physical pose mutation.");}
            if(same&&!resetRequested&&source.Paused==latest.Source.Paused&&source.Warp==latest.Source.Warp){lastWall=wall;latest.Source=source;return;}
            string reset=!initialized?"first publication":resetRequested?requestedReason:!Compatible(source,latest.Source)?"physical/source discontinuity":
                count!=latest.Count?"region shape changed":source.Paused||source.Paused!=latest.Source.Paused?"pause transition":source.Warp!=latest.Source.Warp?"warp policy transition":
                source.UniversalTime<=latest.Source.UniversalTime?"nonadvancing physical mutation":source.UniversalTime-latest.Source.UniversalTime>maximumGap?"publication gap":null;
            if(reset==null)for(int i=0;i<count;i++)if(staging.Poses[i].Id!=latest.Poses[i].Id){reset="region identity changed";break;}
            staging.Count=count;staging.Source=source;staging.Wall=wall;
            var previous=older;older=latest;latest=staging;staging=previous;
            if(reset!=null)
            {Array.Copy(latest.Poses,older.Poses,count);older.Count=count;older.Source=source;older.Wall=wall;display.Resets=checked(display.Resets+1);display.ResetReason=reset;display.UniversalTime=source.UniversalTime;}
            initialized=true;resetRequested=false;lastWall=wall;display.AcceptedFrames=checked(display.AcceptedFrames+1);
        }
        public OwnedFlightDisplayFrame Sample(double wallSeconds)
        {
            Finite(wallSeconds);if(!initialized)throw new InvalidOperationException("No physical publication accepted.");if(wallSeconds<lastWall)throw new ArgumentException("Display wall clock regressed.");
            double newest=latest.Source.UniversalTime,oldest=older.Source.UniversalTime;
            double target=newest;if(enabled&&!latest.Source.Paused&&newest>oldest)
            {
                double advance=(wallSeconds-latest.Wall)*latest.Source.Warp;Finite(advance);target=newest-delay+advance;Finite(target);
                target=Math.Min(newest,Math.Max(oldest,Math.Max(display.UniversalTime,target)));
            }
            double alpha=newest==oldest?1:Math.Max(0,Math.Min(1,(target-oldest)/(newest-oldest)));
            bool changed=display.Revision==0||display.UniversalTime!=target||!display.PhysicalStamp.Matches(latest.Source.Stamp)||display.Enabled!=enabled;
            for(int i=0;i<latest.Count;i++)
            {
                var a=older.Poses[i];var b=latest.Poses[i];display.poses[i]=alpha==1?b:alpha==0?a:new OwnedFlightDisplayPose{Id=b.Id,
                    CenterPositionBodyMeters=a.CenterPositionBodyMeters*(1-alpha)+b.CenterPositionBodyMeters*alpha,LocalCenterOfMass=a.LocalCenterOfMass*(1-alpha)+b.LocalCenterOfMass*alpha,LinearVelocity=a.LinearVelocity*(1-alpha)+b.LinearVelocity*alpha,Orientation=InterpolateRotation(a.Orientation,b.Orientation,alpha)};
            }
            lastWall=wallSeconds;display.count=latest.Count;display.UniversalTime=target;display.PhysicalUniversalTime=newest;display.PhysicalStamp=latest.Source.Stamp;display.Context=context;display.ContextSha256=latest.Source.ContextSha256;display.Mode=mode;
            display.Alpha=alpha;display.DelaySeconds=newest-target;display.Enabled=enabled;display.Interpolated=alpha<1&&newest>oldest;if(changed)display.Revision=checked(display.Revision+1);return display;
        }
        public static Rotation InterpolateRotation(Rotation a,Rotation b,double fraction)
        {
            Rotation.FromUnitComponents(a.X,a.Y,a.Z,a.W);Rotation.FromUnitComponents(b.X,b.Y,b.Z,b.W);Finite(fraction);if(fraction<0||fraction>1)throw new ArgumentOutOfRangeException("fraction");if(fraction==0)return a;if(fraction==1)return b;
            double dot=Rotation.Dot(a,b),sign=dot<0?-1:1;dot=Math.Min(1,Math.Abs(dot));double wa=1-fraction,wb=fraction;
            if(dot<.9995){double angle=Math.Acos(dot),s=Math.Sin(angle);wa=Math.Sin((1-fraction)*angle)/s;wb=Math.Sin(fraction*angle)/s;}
            return new Rotation(a.X*wa+b.X*wb*sign,a.Y*wa+b.Y*wb*sign,a.Z*wa+b.Z*wb*sign,a.W*wa+b.W*wb*sign);
        }
        static bool SamePose(OwnedFlightDisplayPose a,OwnedFlightDisplayPose b)
        {return a.Id==b.Id&&a.CenterPositionBodyMeters.X==b.CenterPositionBodyMeters.X&&a.CenterPositionBodyMeters.Y==b.CenterPositionBodyMeters.Y&&a.CenterPositionBodyMeters.Z==b.CenterPositionBodyMeters.Z&&a.LocalCenterOfMass.X==b.LocalCenterOfMass.X&&a.LocalCenterOfMass.Y==b.LocalCenterOfMass.Y&&a.LocalCenterOfMass.Z==b.LocalCenterOfMass.Z&&a.LinearVelocity.X==b.LinearVelocity.X&&a.LinearVelocity.Y==b.LinearVelocity.Y&&a.LinearVelocity.Z==b.LinearVelocity.Z&&a.Orientation.X==b.Orientation.X&&a.Orientation.Y==b.Orientation.Y&&a.Orientation.Z==b.Orientation.Z&&a.Orientation.W==b.Orientation.W;}
        static void Finite(double value){AssemblyModel.Finite(value);}
    }
}
