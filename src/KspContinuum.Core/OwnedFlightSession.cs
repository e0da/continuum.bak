using System;

namespace KspContinuum
{
    [Serializable] public sealed class OwnedFlightSessionData
    {
        public string schema="continuum-owned-flight-session/v1", contextSha256, mode="local";
        public OwnedFlightCapture context;
        public double[] origin;
        public double runtimeEpoch;
    }

    public sealed class OwnedFlightSession : IOwnedRuntimeEnvironmentField, IOwnedRuntimeStepGuard
    {
        public readonly OwnedRuntimeWorld Runtime;
        public readonly OwnedFlightWorld Context;
        public readonly double RuntimeEpoch;
        public readonly string FrameId;
        Vec origin;
        long frameRevision,extentTopology=-1,extentDefinition=-1;
        double[] extents;
        Vec[] sampledCenters,sampledVelocities;
        RigidAssemblyProperties[] sampledProperties;
        OwnedRuntimeStamp[] sampledStamps;
        bool[] sampled;
        public Vec Origin { get { return origin; } }
        public string Mode { get; private set; }
        public double UniversalTime { get { CheckFrame(Runtime.Stamp);return Context.Epoch+(Runtime.Stamp.Time-RuntimeEpoch); } }

        public OwnedFlightSession(OwnedRuntimeWorld runtime, OwnedFlightWorld context, Vec origin, double runtimeEpoch)
        {
            if(runtime==null||context==null)throw new ArgumentNullException("runtime/context");
            OwnedFlightWorld.Require(origin);AssemblyModel.Finite(runtimeEpoch);
            Runtime=runtime;Context=context;this.origin=origin;RuntimeEpoch=runtimeEpoch;FrameId=context.Frame+"/local";
            OwnedFlightWorld.Need(runtime.Stamp.FrameId==FrameId && runtime.Stamp.Time>=runtimeEpoch,"Owned flight runtime frame/clock differs.");
            frameRevision=runtime.Stamp.FrameRevision;Mode="local";EnsureExtents(runtime.Stamp);
        }

        public static OwnedFlightSession SeedCaptured(OwnedRuntimeWorld runtime, OwnedFlightWorld context, Vec origin)
        {
            if(runtime==null||context==null)throw new ArgumentNullException("runtime/context");OwnedFlightWorld.Require(origin);
            var expected=runtime.Stamp;OwnedRuntimeMotion motion;
            using(var read=runtime.AcquireRead())
            {
                OwnedFlightWorld.Need(read.Stamp.Matches(expected)&&read.RegionCount==1,"Captured placement requires one current rigid region.");
                var region=read.GetRegion(0);motion=region.Motion;
                Rotation change=context.CapturedRootRotation*motion.Orientation.Inverse;
                motion.RootPosition=context.CapturedRootPosition+origin*-1;motion.Orientation=context.CapturedRootRotation;
                motion.Velocity=context.CapturedCenterVelocity;motion.AngularMomentum=change.Rotate(motion.AngularMomentum);
                OwnedFlightWorld.Need(OwnedFlightWorld.Norm(context.CapturedRootPosition+motion.Orientation.Rotate(region.Properties.CenterOfMass))>context.Radius,
                    "Captured placement is at or below the central-body sphere.");
            }
            runtime.SetRegionMotions(expected,new[]{motion},expected.Time,context.Frame+"/local");
            return new OwnedFlightSession(runtime,context,origin,expected.Time);
        }

        public Vec BodyPosition(int region)
        {using(var read=Runtime.AcquireRead()){CheckFrame(read.Stamp);return origin+read.GetRegion(region).Motion.RootPosition;}}
        public Vec CenterPosition(int region)
        {using(var read=Runtime.AcquireRead()){CheckFrame(read.Stamp);var state=read.GetRegion(region);return Center(state.Motion,state.Properties);}}
        public int FindRegion(long id)
        {using(var read=Runtime.AcquireRead())for(int i=0;i<read.RegionCount;i++)if(read.GetRegion(i).Id==id)return i;throw new ArgumentException("Owned flight region is absent.");}
        Vec Center(OwnedRuntimeMotion motion,RigidAssemblyProperties properties)
        {return origin+motion.RootPosition+motion.Orientation.Rotate(properties.CenterOfMass);}
        public OwnedRuntimeEnvironment EvaluateRegion(int region)
        {
            using(var read=Runtime.AcquireRead())
            {
                var sample=CaptureReadDescriptor(read).Sample(read,region);
                return new OwnedRuntimeEnvironment(sample.SourceId,sample.Revision,sample.Epoch,sample.Density,sample.Temperature,
                    sample.WindVelocity,sample.Gravity,sample.ConvectiveCoefficient,sample.RadiationTemperature);
            }
        }
        public OwnedFlightReadDescriptor CaptureReadDescriptor(OwnedRuntimeReadLease read)
        {
            if(read==null)throw new ArgumentNullException("read");
            if(!ReferenceEquals(read.Definition,Runtime.Definition))throw new InvalidOperationException("Read descriptor requires its current session definition.");
            return CaptureReadDescriptor(read.Stamp);
        }
        internal OwnedFlightReadDescriptor CaptureReadDescriptor(OwnedRuntimeStamp stamp)
        {
            CheckFrame(stamp);if(!stamp.Matches(Runtime.Stamp))throw new InvalidOperationException("Read descriptor requires its current session bank.");
            return new OwnedFlightReadDescriptor(stamp,origin,RuntimeEpoch,Mode,Context);
        }
        public OwnedRuntimeEnvironmentSample Sample(OwnedRuntimeStamp stamp,int region,OwnedRuntimeMotion motion,RigidAssemblyProperties properties)
        {
            CheckFrame(stamp);EnsureExtents(stamp);OwnedFlightWorld.Need(region>=0&&region<extents.Length&&properties!=null,"Owned flight region sample is absent.");
            Vec center=Center(motion,properties);OwnedFlightWorld.Need(OwnedFlightWorld.Norm(center)>Context.Radius+GuardExtent(region,properties),"Owned flight reached its finite-size surface guard.");
            var environment=Context.EvaluateSample(center,Context.Epoch+(stamp.Time-RuntimeEpoch),stamp.Time);
            sampledCenters[region]=center;sampledVelocities[region]=motion.Velocity;sampledProperties[region]=properties;
            sampledStamps[region]=stamp;sampled[region]=true;return environment;
        }
        public void ValidateStep(OwnedRuntimeStep step)
        {
            Runtime.CheckStep(step);CheckFrame(step.ExpectedStamp);EnsureExtents(step.ExpectedStamp);
            try { using(var read=Runtime.AcquireRead())
            {
                OwnedFlightWorld.Need(read.Stamp.Matches(step.ExpectedStamp),"Owned flight step guard is stale.");
                for(int r=0;r<read.RegionCount;r++)
                {
                    var after=step.GetRegion(r);
                    OwnedFlightWorld.Need(sampled[r]&&sampledStamps[r].Matches(step.ExpectedStamp)&&ReferenceEquals(sampledProperties[r],after.Properties),
                        "Owned flight guard requires its current post-mass-refresh sample and unchanged integration properties.");
                    Vec start=sampledCenters[r],end=Center(after.Motion,after.Properties);
                    Vec delta=end+start*-1;double square=OwnedFlightWorld.Dot(delta,delta);AssemblyModel.Finite(square);
                    double u=square==0?0:Math.Max(0,Math.Min(1,-OwnedFlightWorld.Dot(start,delta)/square));
                    double minimum=OwnedFlightWorld.Norm(start+delta*u);
                    // The constant-force COM step deviates from its chord by at most |delta-v|*dt/8.
                    double curveMargin=OwnedFlightWorld.Norm(after.Motion.Velocity+sampledVelocities[r]*-1)*step.DeltaTime*.125;
                    double extent=GuardExtent(r,after.Properties);
                    OwnedFlightWorld.Need(minimum-curveMargin>Context.Radius+extent,"Owned flight local interval reaches the spherical surface guard.");
                }
            }} finally {ClearSamples();}
        }
        public OwnedRuntimeStamp AdvanceLocal(OwnedRuntimeHost host,double delta,OwnedRuntimeControls controls)
        {
            if(host==null||!ReferenceEquals(host.World,Runtime))throw new ArgumentException("Owned flight host differs.");controls.Validate();
            var input=new OwnedRuntimeInput(controls.Throttle,controls.Attitude.X,controls.Attitude.Y,controls.Attitude.Z,controls.Translation.X,controls.Translation.Y,controls.Translation.Z);
            ClearSamples();try {var stamp=host.Advance(delta,input,this,controls);Mode="local";return stamp;}finally{ClearSamples();}
        }

        public OwnedRuntimeStamp Rebase(OwnedRuntimeStamp expected,Vec newOrigin)
        {
            OwnedFlightWorld.Require(newOrigin);CheckFrame(expected);
            var next=Runtime.Rebase(expected,origin+newOrigin*-1,FrameId);
            origin=newOrigin;frameRevision=next.FrameRevision;return next;
        }
        public OwnedRuntimeStamp Place(OwnedRuntimeStamp expected,Vec centerPosition,Vec centerVelocity)
        {
            OwnedFlightWorld.Require(centerPosition);OwnedFlightWorld.Require(centerVelocity);CheckFrame(expected);EnsureExtents(expected);
            OwnedRuntimeMotion motion;
            using(var read=Runtime.AcquireRead())
            {
                OwnedFlightWorld.Need(read.Stamp.Matches(expected)&&read.RegionCount==1,"Placement requires one current rigid region.");
                var region=read.GetRegion(0);OwnedFlightWorld.Need(OwnedFlightWorld.Norm(centerPosition)>Context.Radius+GuardExtent(0,region.Properties),"Placement intersects the spherical surface guard.");
                motion=region.Motion;motion.RootPosition=centerPosition+origin*-1+motion.Orientation.Rotate(region.Properties.CenterOfMass)*-1;motion.Velocity=centerVelocity;
            }
            var stamp=Runtime.SetRegionMotions(expected,new[]{motion},expected.Time,FrameId);Mode="local";return stamp;
        }
        public OwnedRuntimeStamp PlaceCircular(OwnedRuntimeStamp expected,double altitude,Vec radial,Vec planeNormal)
        {
            AssemblyModel.Positive(altitude);double radius=Context.Radius+altitude;AssemblyModel.Finite(radius);
            double n=OwnedFlightWorld.Norm(radial);OwnedFlightWorld.Need(n>0,"Circular placement radial direction is zero.");radial=radial*(1/n);
            Vec tangent=Vec.Cross(planeNormal,radial);n=OwnedFlightWorld.Norm(tangent);OwnedFlightWorld.Need(n>1e-12,"Circular placement plane is degenerate.");tangent=tangent*(1/n);
            return Place(expected,radial*radius,tangent*Math.Sqrt(Context.Mu/radius));
        }

        public bool PrepareCoast(OwnedRuntimeStamp expected,double targetUniversalTime,OwnedRuntimeControls controls,bool passiveStateFrozen,
            double nextEventUniversalTime,double guardLeadSeconds,out OwnedRuntimeMotion[] motions,out double targetRuntimeTime,out string reason)
        {
            motions=null;targetRuntimeTime=0;reason=null;
            try
            {
                controls.Validate();CheckFrame(expected);EnsureExtents(expected);AssemblyModel.Finite(targetUniversalTime);OwnedFlightWorld.Nonnegative(guardLeadSeconds);
                OwnedFlightWorld.Need(Runtime.Stamp.Matches(expected),"Coast request is stale.");
                double startUniversal=Context.Epoch+(expected.Time-RuntimeEpoch),delta=targetUniversalTime-startUniversal;AssemblyModel.Positive(delta);
                targetRuntimeTime=expected.Time+delta;AssemblyModel.Finite(targetRuntimeTime);OwnedFlightWorld.Need(targetRuntimeTime>expected.Time,"Coast target does not advance representable runtime time.");
                OwnedFlightWorld.Need(passiveStateFrozen,"Coast requires an explicit passive resource/thermal freeze policy.");
                OwnedFlightWorld.Need(controls.Throttle==0&&Zero(controls.Attitude)&&Zero(controls.Translation),"Powered/attitude/RCS input requires local stepping.");
                OwnedFlightWorld.Need(!double.IsNaN(nextEventUniversalTime)&&nextEventUniversalTime!=double.NegativeInfinity,"Coast event time is invalid.");
                OwnedFlightWorld.Need(targetUniversalTime<nextEventUniversalTime-guardLeadSeconds,"Coast target reaches a pending event guard.");
                using(var read=Runtime.AcquireRead())
                {
                    OwnedFlightWorld.Need(read.Stamp.Matches(expected)&&read.RegionCount<=SimulationBatch.MaxBodies,"Coast revision/region bound differs.");
                    OwnedFlightWorld.Need(read.ThermalPendingDuration==0,"Pending thermal evolution requires local work before passive coast.");
                    for(int i=0;i<read.Definition.ProviderStateCount;i++)OwnedFlightWorld.Need(read.GetProviderState(i)==0,"Nonzero retained provider state requires local stepping.");
                    var seeds=new CoastingBody[read.RegionCount];
                    for(int r=0;r<seeds.Length;r++)
                    {
                        var state=read.GetRegion(r);OwnedFlightWorld.Need(Zero(state.Motion.AngularMomentum),"Torque-free rotating attitude requires local stepping.");
                        Vec position=Center(state.Motion,state.Properties);double radius=OwnedFlightWorld.Norm(position),guardRadius=Context.Radius+Context.AtmosphereDepth+GuardExtent(r,state.Properties);
                        OwnedFlightWorld.Need(radius>guardRadius,"Coast requires vacuum outside the finite-size atmosphere guard.");
                        seeds[r]=new CoastingBody(r,position,state.Motion.Velocity);
                    }
                    var engine=new CoastingEngine(startUniversal,Context.Mu,seeds,Math.Max(1,seeds.Length));
                    for(int r=0;r<seeds.Length;r++)
                    {
                        double guardRadius=Context.Radius+Context.AtmosphereDepth+GuardExtent(r,read.GetRegion(r).Properties),radius=OwnedFlightWorld.Norm(seeds[r].Position);
                        Vec position=seeds[r].Position,velocity=seeds[r].Velocity;double speedSquare=OwnedFlightWorld.Dot(velocity,velocity),rv=OwnedFlightWorld.Dot(position,velocity);
                        Vec eccentric=(position*(speedSquare-Context.Mu/radius)+velocity*(-rv))*(1/Context.Mu);
                        double e=OwnedFlightWorld.Norm(eccentric),h2=OwnedFlightWorld.Dot(Vec.Cross(position,velocity),Vec.Cross(position,velocity));AssemblyModel.Finite(h2);
                        double periapsis=(h2/Context.Mu)/(1+e);AssemblyModel.Finite(periapsis);
                        if(periapsis<=guardRadius)
                        {
                            OwnedFlightWorld.Need(periapsis<guardRadius,"Tangent atmosphere/surface coast requires local event stepping.");
                            double energy=speedSquare*.5-Context.Mu/radius;AssemblyModel.Finite(energy);
                            OwnedFlightWorld.Need(energy<0&&e>=1e-8&&e<1,"Unsafe radial/hyperbolic/tangent coast requires local event stepping.");
                            double major=-Context.Mu/(2*energy),meanMotion=Math.Sqrt(Context.Mu/(major*major*major));AssemblyModel.Positive(meanMotion);
                            double period=2*Math.PI/meanMotion,endSearch=startUniversal+period;AssemblyModel.Finite(endSearch);
                            OwnedFlightWorld.Need(endSearch>startUniversal,"Entry event period does not advance representable time.");
                            var eventEngine=new CoastingEngine(startUniversal,Context.Mu,new[]{seeds[r]},1);
                            var crossing=CoastingEventScheduler.FindFirst(eventEngine,new RadiusCrossingSearch(startUniversal,endSearch,
                                guardRadius,RadiusCrossingDirection.Inward));
                            OwnedFlightWorld.Need(crossing!=null,"The guaranteed inward entry event could not be resolved; use local stepping.");
                            OwnedFlightWorld.Need(targetUniversalTime<crossing.TimeSeconds-guardLeadSeconds,"Coast interval reaches the atmosphere/surface entry guard.");
                        }
                    }
                    var candidate=new OwnedRuntimeMotion[seeds.Length];
                    for(int r=0;r<candidate.Length;r++)
                    {
                        var state=read.GetRegion(r);var final=engine.SampleBodyAt(r,targetUniversalTime);
                        OwnedFlightWorld.Need(OwnedFlightWorld.Norm(final.Position)>Context.Radius+Context.AtmosphereDepth+GuardExtent(r,state.Properties),"Coast endpoint reaches atmosphere/surface guard.");
                        candidate[r]=state.Motion;candidate[r].RootPosition=final.Position+origin*-1+candidate[r].Orientation.Rotate(state.Properties.CenterOfMass)*-1;candidate[r].Velocity=final.Velocity;
                    }
                    motions=candidate;return true;
                }
            }
            catch(ArgumentException error){reason=error.Message;motions=null;targetRuntimeTime=0;return false;}
            catch(InvalidOperationException error){reason=error.Message;motions=null;targetRuntimeTime=0;return false;}
        }
        public bool TryCoast(OwnedRuntimeStamp expected,double targetUniversalTime,OwnedRuntimeControls controls,bool passiveStateFrozen,
            double nextEventUniversalTime,double guardLeadSeconds,out string reason)
        {
            OwnedRuntimeMotion[] motions;double time;
            if(!PrepareCoast(expected,targetUniversalTime,controls,passiveStateFrozen,nextEventUniversalTime,guardLeadSeconds,out motions,out time,out reason))return false;
            try{PublishCoast(expected,motions,time);return true;}
            catch(ArgumentException error){reason=error.Message;return false;}
            catch(InvalidOperationException error){reason=error.Message;return false;}
        }
        internal void PublishCoast(OwnedRuntimeStamp expected,OwnedRuntimeMotion[] motions,double time)
        {CheckFrame(expected);Runtime.SetRegionMotions(expected,motions,time,FrameId);Mode="kepler";}
        public OwnedFlightSessionData Capture()
        {CheckFrame(Runtime.Stamp);return new OwnedFlightSessionData {context=Context.Capture(),contextSha256=Context.SourceSha256,origin=new[]{origin.X,origin.Y,origin.Z},runtimeEpoch=RuntimeEpoch,mode=Mode};}
        public static OwnedFlightSession Restore(OwnedRuntimeWorld runtime,OwnedFlightSessionData data,string nativeSha,string partSpecsSha)
        {
            OwnedFlightWorld.Need(data!=null&&data.schema=="continuum-owned-flight-session/v1"&&(data.mode=="local"||data.mode=="kepler"),"Owned flight session schema/mode differs.");
            var result=new OwnedFlightSession(runtime,OwnedFlightWorld.Admit(data.context,nativeSha,partSpecsSha,data.contextSha256),OwnedFlightWorld.Vector(data.origin),data.runtimeEpoch);
            result.Mode=data.mode;return result;
        }
        void CheckFrame(OwnedRuntimeStamp stamp)
        {OwnedFlightWorld.Need(stamp.FrameId==FrameId&&stamp.FrameRevision==frameRevision&&stamp.Time>=RuntimeEpoch,"Owned flight origin/frame clock changed outside its session.");}
        void EnsureExtents(OwnedRuntimeStamp stamp)
        {
            if(extents!=null&&extentTopology==stamp.TopologyRevision&&extentDefinition==stamp.DefinitionRevision)return;
            using(var read=Runtime.AcquireRead())
            {
                OwnedFlightWorld.Need(read.Stamp.Matches(stamp),"Owned flight topology extent admission is stale.");var next=new double[read.RegionCount];
                for(int r=0;r<next.Length;r++)
                {
                    double rootExtent=Context.VesselBoundingRadius;
                    foreach(int b in read.Topology.RegionBodies[r])rootExtent=Math.Max(rootExtent,OwnedFlightWorld.Norm(read.GetBody(b).LocalPosition)+Context.VesselBoundingRadius);
                    next[r]=rootExtent;AssemblyModel.Finite(next[r]);
                }
                extents=next;sampledCenters=new Vec[next.Length];sampledVelocities=new Vec[next.Length];sampledProperties=new RigidAssemblyProperties[next.Length];
                sampledStamps=new OwnedRuntimeStamp[next.Length];sampled=new bool[next.Length];extentTopology=stamp.TopologyRevision;extentDefinition=stamp.DefinitionRevision;
            }
        }
        double GuardExtent(int region,RigidAssemblyProperties properties)
        {double result=extents[region]+OwnedFlightWorld.Norm(properties.CenterOfMass);AssemblyModel.Finite(result);return result;}
        void ClearSamples(){if(sampled!=null)Array.Clear(sampled,0,sampled.Length);}
        static bool Zero(Vec vector){return vector.X==0&&vector.Y==0&&vector.Z==0;}
    }
}
