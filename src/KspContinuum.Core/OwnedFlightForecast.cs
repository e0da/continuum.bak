using System;
using System.Collections.Generic;

namespace KspContinuum
{
    public enum OwnedFlightForecastKind { SurfaceApproach, AtmosphereBoundary, TableKnot }
    public enum OwnedFlightCrossingDirection { Inbound, Outbound, Tangent }
    public struct OwnedFlightCrossing
    {
        public readonly double DeltaSeconds;
        public readonly OwnedFlightCrossingDirection Direction;
        public readonly bool InitiallyOnBoundary;
        internal OwnedFlightCrossing(double delta,OwnedFlightCrossingDirection direction,bool current)
        {DeltaSeconds=delta;Direction=direction;InitiallyOnBoundary=current;}
    }
    public struct OwnedFlightForecastRow
    {
        public readonly long RegionId;
        public readonly OwnedFlightForecastKind Kind;
        public readonly double RadiusMeters,DeltaSeconds;
        public readonly OwnedFlightCrossingDirection Direction;
        public readonly bool InitiallyOnBoundary;
        internal OwnedFlightForecastRow(long id,OwnedFlightForecastKind kind,double radius,OwnedFlightCrossing crossing)
        {RegionId=id;Kind=kind;RadiusMeters=radius;DeltaSeconds=crossing.DeltaSeconds;Direction=crossing.Direction;InitiallyOnBoundary=crossing.InitiallyOnBoundary;}
    }
    public sealed class OwnedFlightForecastSnapshot
    {
        readonly OwnedFlightForecastRow[] rows;
        readonly OwnedRuntimeWorld runtime;
        readonly object bankIdentity;
        public readonly OwnedRuntimeStamp SourceStamp;
        public readonly string ContextSha256,FrameId,Model="constant-velocity/advisory";
        public readonly Vec Origin;
        public readonly double UniversalTime,HorizonSeconds;
        public readonly bool SurfaceGuardAlreadyReached;
        public int Count {get{return rows.Length;}}
        public bool HasPositiveCrossing {get{return rows.Length>0;}}
        public double FirstPositiveDelta {get{return rows.Length==0?HorizonSeconds:rows[0].DeltaSeconds;}}
        public OwnedFlightForecastRow GetRow(int index){return rows[index];}
        internal OwnedFlightForecastSnapshot(OwnedRuntimeWorld runtime,OwnedFlightReadDescriptor descriptor,double horizon,List<OwnedFlightForecastRow> values,bool reached,object bankIdentity)
        {this.runtime=runtime;this.bankIdentity=bankIdentity;SourceStamp=descriptor.Stamp;ContextSha256=descriptor.Context.SourceSha256;FrameId=descriptor.FrameId;Origin=descriptor.Origin;UniversalTime=descriptor.UniversalTime;HorizonSeconds=horizon;rows=values.ToArray();SurfaceGuardAlreadyReached=reached;}
        public void RequireCurrent(OwnedFlightSession session)
        {
            if(session==null||!ReferenceEquals(runtime,session.Runtime)||!SourceStamp.Matches(session.Runtime.Stamp)||ContextSha256!=session.Context.SourceSha256||FrameId!=session.FrameId||Origin.X!=session.Origin.X||Origin.Y!=session.Origin.Y||Origin.Z!=session.Origin.Z)
                throw new InvalidOperationException("Forecast source is stale or foreign.");
        }
        public void RequireMatches(OwnedFlightReadDescriptor descriptor,OwnedRuntimeReadLease read)
        {
            if(read==null||!ReferenceEquals(bankIdentity,read.SourceIdentity)||!SourceStamp.Matches(read.Stamp)||!SourceStamp.Matches(descriptor.Stamp)||descriptor.Context==null||ContextSha256!=descriptor.Context.SourceSha256||FrameId!=descriptor.FrameId||UniversalTime!=descriptor.UniversalTime||Origin.X!=descriptor.Origin.X||Origin.Y!=descriptor.Origin.Y||Origin.Z!=descriptor.Origin.Z)
                throw new InvalidOperationException("Forecast and retained descriptor differ.");
        }
    }
    public static class OwnedFlightForecast
    {
        public static bool TryFirst(OwnedFlightSession session,OwnedRuntimeReadLease read,double horizon,out OwnedFlightForecastRow first)
        {
            if(session==null)throw new ArgumentNullException("session");AssemblyModel.Positive(horizon);if(horizon>7200)throw new ArgumentOutOfRangeException("horizon");
            var descriptor=session.CaptureReadDescriptor(read);var context=descriptor.Context;first=default(OwnedFlightForecastRow);bool found=false;
            for(int region=0;region<read.RegionCount;region++)
            {
                var state=read.GetRegion(region);Vec position=descriptor.CenterPosition(read,region),velocity=state.Motion.Velocity;
                double extent=session.GuardExtentForRead(read,region);
                First(ref first,ref found,state.Id,OwnedFlightForecastKind.SurfaceApproach,position,velocity,context.Radius+extent,horizon,true);
                if(context.AtmosphereDepth>0)First(ref first,ref found,state.Id,OwnedFlightForecastKind.AtmosphereBoundary,position,velocity,context.Radius+context.AtmosphereDepth,horizon,false);
                Vec end=position+velocity*horizon;OwnedRuntimeDynamics.Require(end);
                double speedSquared=OwnedFlightWorld.Dot(velocity,velocity);AssemblyModel.Finite(speedSquared);
                double closest=speedSquared==0?0:Math.Max(0,Math.Min(horizon,-OwnedFlightWorld.Dot(position,velocity)/speedSquared));
                double startRadius=OwnedFlightWorld.Norm(position),endRadius=OwnedFlightWorld.Norm(end);
                double roundingMargin=16*2.2204460492503131e-16*Math.Max(context.Radius,Math.Max(startRadius,endRadius));
                double low=OwnedFlightWorld.Norm(position+velocity*closest)-context.Radius-roundingMargin,high=Math.Max(startRadius,endRadius)-context.Radius+roundingMargin;
                int left=1,right=context.AtmosphereSampleCount-1;
                while(left<right){int middle=left+(right-left)/2;if(context.GetAtmosphereAltitude(middle)<low)left=middle+1;else right=middle;}
                for(int knot=left;knot<context.AtmosphereSampleCount-1&&context.GetAtmosphereAltitude(knot)<=high;knot++)First(ref first,ref found,state.Id,OwnedFlightForecastKind.TableKnot,position,velocity,context.Radius+context.GetAtmosphereAltitude(knot),horizon,false);
            }
            return found;
        }
        static void First(ref OwnedFlightForecastRow first,ref bool found,long id,OwnedFlightForecastKind kind,Vec position,Vec velocity,double radius,double horizon,bool inwardOnly)
        {
            OwnedFlightCrossing crossing;if(!TryFirstPositiveCrossing(position,velocity,radius,horizon,out crossing)||(inwardOnly&&crossing.Direction==OwnedFlightCrossingDirection.Outbound))return;
            if(!found||crossing.DeltaSeconds<first.DeltaSeconds||(crossing.DeltaSeconds==first.DeltaSeconds&&(id<first.RegionId||(id==first.RegionId&&(int)kind<(int)first.Kind))))
            {first=new OwnedFlightForecastRow(id,kind,radius,crossing);found=true;}
        }
        public static OwnedFlightForecastSnapshot Capture(OwnedFlightSession session,OwnedRuntimeStamp expected,double horizonSeconds)
        {
            if(session==null)throw new ArgumentNullException("session");
            using(var read=session.Runtime.AcquireRead())
            {
                if(!read.Stamp.Matches(expected))throw new InvalidOperationException("Forecast source is stale.");
                return CaptureCore(session.Runtime,session.CaptureReadDescriptor(read),read,horizonSeconds);
            }
        }
        public static OwnedFlightForecastSnapshot Capture(OwnedFlightReadDescriptor descriptor,OwnedRuntimeReadLease read,double horizonSeconds)
        {return CaptureCore(null,descriptor,read,horizonSeconds);}
        static OwnedFlightForecastSnapshot CaptureCore(OwnedRuntimeWorld runtime,OwnedFlightReadDescriptor descriptor,OwnedRuntimeReadLease read,double horizon)
        {
            AssemblyModel.Positive(horizon);if(horizon>7200)throw new ArgumentOutOfRangeException("horizon");
            if(read==null||descriptor.Context==null||!read.Stamp.Matches(descriptor.Stamp))throw new InvalidOperationException("Forecast descriptor/read differ.");
            var context=descriptor.Context;var table=context.Capture().altitudeMeters;
            var rows=new List<OwnedFlightForecastRow>();bool reached=false;
            for(int region=0;region<read.RegionCount;region++)
            {
                var state=read.GetRegion(region);Vec position=descriptor.CenterPosition(read,region),velocity=state.Motion.Velocity;
                double extent=context.VesselBoundingRadius;
                foreach(int body in read.Topology.RegionBodies[region])extent=Math.Max(extent,OwnedFlightWorld.Norm(read.GetBody(body).LocalPosition)+context.VesselBoundingRadius);
                extent+=OwnedFlightWorld.Norm(state.Properties.CenterOfMass);AssemblyModel.Finite(extent);
                double surface=context.Radius+extent;AssemblyModel.Finite(surface);
                reached|=OwnedFlightWorld.Norm(position)<=surface;
                Add(rows,state.Id,OwnedFlightForecastKind.SurfaceApproach,position,velocity,surface,horizon,true);
                if(context.AtmosphereDepth>0)Add(rows,state.Id,OwnedFlightForecastKind.AtmosphereBoundary,position,velocity,context.Radius+context.AtmosphereDepth,horizon,false);
                for(int knot=1;knot<table.Length-1;knot++)Add(rows,state.Id,OwnedFlightForecastKind.TableKnot,position,velocity,context.Radius+table[knot],horizon,false);
            }
            rows.Sort((a,b)=>{int c=a.DeltaSeconds.CompareTo(b.DeltaSeconds);if(c!=0)return c;c=a.RegionId.CompareTo(b.RegionId);return c!=0?c:a.Kind.CompareTo(b.Kind);});
            return new OwnedFlightForecastSnapshot(runtime,descriptor,horizon,rows,reached,read.SourceIdentity);
        }
        static void Add(List<OwnedFlightForecastRow> rows,long id,OwnedFlightForecastKind kind,Vec position,Vec velocity,double radius,double horizon,bool inwardOnly)
        {
            OwnedFlightCrossing crossing;
            if(TryFirstPositiveCrossing(position,velocity,radius,horizon,out crossing)&&(!inwardOnly||crossing.Direction!=OwnedFlightCrossingDirection.Outbound))rows.Add(new OwnedFlightForecastRow(id,kind,radius,crossing));
        }
        public static bool TryFirstPositiveCrossing(Vec position,Vec velocity,double radius,double horizon,out OwnedFlightCrossing crossing)
        {
            OwnedRuntimeDynamics.Require(position);OwnedRuntimeDynamics.Require(velocity);AssemblyModel.Positive(radius);AssemblyModel.Positive(horizon);crossing=default(OwnedFlightCrossing);
            double length=OwnedFlightWorld.Norm(position),speed=OwnedFlightWorld.Norm(velocity);if(speed==0)return false;
            double scale=Math.Max(length,radius),timeScale=scale/speed;AssemblyModel.Positive(timeScale);
            Vec p=position*(1/scale),v=velocity*(1/speed);double rr=radius/scale;
            double a=OwnedFlightWorld.Dot(v,v),b=OwnedFlightWorld.Dot(p,v),c=((length-radius)/scale)*(length/scale+radius/scale);
            Vec perpendicular=Vec.Cross(p,v);double discriminant=a*rr*rr-OwnedFlightWorld.Dot(perpendicular,perpendicular);AssemblyModel.Finite(discriminant);if(discriminant<0)return false;
            double root=Math.Sqrt(discriminant),q=-b-(b>=0?root:-root);
            double t0=q==0?0:q/a,t1=q==0?0:c/q;if(t0>t1){double swap=t0;t0=t1;t1=swap;}
            double t=t0>0?t0:t1>0?t1:0;if(t==0)return false;
            double delta=t*timeScale;if(double.IsNaN(delta)||double.IsInfinity(delta)||delta<=0||delta>horizon)return false;
            double derivative=b+a*t;var direction=discriminant==0?OwnedFlightCrossingDirection.Tangent:derivative<0?OwnedFlightCrossingDirection.Inbound:OwnedFlightCrossingDirection.Outbound;
            crossing=new OwnedFlightCrossing(delta,direction,length==radius);return true;
        }
    }
}
