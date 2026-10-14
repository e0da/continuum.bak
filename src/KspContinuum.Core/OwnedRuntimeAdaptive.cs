using System;
using System.Diagnostics;

namespace KspContinuum
{
    // Every future-relevant schedule value must be in the bank. Trials may repeat this schedule.
    public interface IOwnedRuntimeTrialSafeSchedule : IOwnedRuntimeFieldSchedule { }
    public enum OwnedRuntimeMechanicalMethod { StepDoubling=0, EmbeddedMidpoint=1 }
    public sealed class OwnedRuntimeMechanicalPolicy
    {
        public readonly bool Adaptive;
        public readonly OwnedRuntimeMechanicalMethod Method;
        public readonly double MinimumInterval,MaximumInterval,PositionTolerance,VelocityTolerance,AngleTolerance,ScaledStateTolerance,InitialInterval;
        public OwnedRuntimeMechanicalPolicy(bool adaptive=false,double minimumInterval=.0001,double maximumInterval=.1,
            double positionTolerance=1e-4,double velocityTolerance=1e-4,double angleTolerance=1e-6,double scaledStateTolerance=1e-6,double initialInterval=.02,OwnedRuntimeMechanicalMethod method=OwnedRuntimeMechanicalMethod.StepDoubling)
        {
            AssemblyModel.Positive(minimumInterval);AssemblyModel.Positive(maximumInterval);AssemblyModel.Positive(positionTolerance);AssemblyModel.Positive(velocityTolerance);AssemblyModel.Positive(angleTolerance);AssemblyModel.Positive(scaledStateTolerance);AssemblyModel.Positive(initialInterval);
            if(maximumInterval>.1||minimumInterval>maximumInterval||initialInterval<minimumInterval||initialInterval>maximumInterval)
                throw new ArgumentException("Mechanical interval bounds differ.");
            if(method!=OwnedRuntimeMechanicalMethod.StepDoubling&&method!=OwnedRuntimeMechanicalMethod.EmbeddedMidpoint)throw new ArgumentOutOfRangeException("method");
            Method=method;Adaptive=adaptive;MinimumInterval=minimumInterval;MaximumInterval=maximumInterval;PositionTolerance=positionTolerance;VelocityTolerance=velocityTolerance;AngleTolerance=angleTolerance;ScaledStateTolerance=scaledStateTolerance;InitialInterval=initialInterval;
        }
    }
    [Serializable] public sealed class OwnedRuntimeMechanicalState
    {
        public string schema="continuum-owned-runtime-mechanical-state/v1";
        public bool adaptive;
        public int method;
        public double minimumInterval=.0001,maximumInterval=.1,positionTolerance=1e-4,velocityTolerance=1e-4,angleTolerance=1e-6,scaledStateTolerance=1e-6,initialInterval=.02,nextInterval=.02,lastAcceptedInterval,lastError;
        public long accepted,rejected,trialExecutions,policyRevision,roundoffCoalescedIntervals;
        public OwnedRuntimeMechanicalPolicy Policy {get{return new OwnedRuntimeMechanicalPolicy(adaptive,minimumInterval,maximumInterval,positionTolerance,velocityTolerance,angleTolerance,scaledStateTolerance,initialInterval,(OwnedRuntimeMechanicalMethod)method);}}
        internal void Apply(OwnedRuntimeMechanicalPolicy p)
        {method=(int)p.Method;adaptive=p.Adaptive;minimumInterval=p.MinimumInterval;maximumInterval=p.MaximumInterval;positionTolerance=p.PositionTolerance;velocityTolerance=p.VelocityTolerance;angleTolerance=p.AngleTolerance;scaledStateTolerance=p.ScaledStateTolerance;initialInterval=p.InitialInterval;nextInterval=p.InitialInterval;lastError=0;lastAcceptedInterval=0;policyRevision=checked(policyRevision+1);}
        internal void CopyFrom(OwnedRuntimeMechanicalState s)
        {schema=s.schema;method=s.method;adaptive=s.adaptive;minimumInterval=s.minimumInterval;maximumInterval=s.maximumInterval;positionTolerance=s.positionTolerance;velocityTolerance=s.velocityTolerance;angleTolerance=s.angleTolerance;scaledStateTolerance=s.scaledStateTolerance;initialInterval=s.initialInterval;nextInterval=s.nextInterval;lastAcceptedInterval=s.lastAcceptedInterval;lastError=s.lastError;accepted=s.accepted;rejected=s.rejected;trialExecutions=s.trialExecutions;policyRevision=s.policyRevision;roundoffCoalescedIntervals=s.roundoffCoalescedIntervals;}
        public OwnedRuntimeMechanicalState Clone(){Validate();var s=new OwnedRuntimeMechanicalState();s.CopyFrom(this);return s;}
        internal void Validate()
        {
            if(schema!="continuum-owned-runtime-mechanical-state/v1"||method<0||method>1||accepted<0||rejected<0||trialExecutions<0||policyRevision<0||roundoffCoalescedIntervals<0||roundoffCoalescedIntervals>accepted)throw new ArgumentException("Mechanical continuation metadata differs.");
            AssemblyModel.Positive(minimumInterval);AssemblyModel.Positive(maximumInterval);AssemblyModel.Positive(positionTolerance);AssemblyModel.Positive(velocityTolerance);AssemblyModel.Positive(angleTolerance);AssemblyModel.Positive(scaledStateTolerance);AssemblyModel.Positive(initialInterval);
            AssemblyModel.Finite(nextInterval);AssemblyModel.Finite(lastAcceptedInterval);AssemblyModel.Finite(lastError);
            if(maximumInterval>.1||minimumInterval>maximumInterval||initialInterval<minimumInterval||initialInterval>maximumInterval||nextInterval<minimumInterval||nextInterval>maximumInterval||lastAcceptedInterval<0||lastAcceptedInterval>maximumInterval||lastError<0)throw new ArgumentException("Mechanical controller state differs.");
        }
    }
    [Serializable] public struct OwnedRuntimeAdaptiveObservation
    {public long Trials,Rejected,Accepted,WorkTicks,ClockFrequency,RoundoffCoalescedIntervals,ActuatorBoundaryClockCeilings,EmbeddedAttempts,EmbeddedForceSamples;public double LastError,LastInterval,LastBoundaryClockCeilingSeconds;public string LastFailure;}
    public sealed class OwnedRuntimeMechanicalAccuracyException : InvalidOperationException
    {
        public readonly double Interval,NormalizedError;
        public OwnedRuntimeMechanicalAccuracyException(double interval,double error):base("Mechanical estimate exceeds frozen budgets at the minimum interval: dt="+interval+", normalizedError="+error)
        {Interval=interval;NormalizedError=error;}
    }
    [Serializable] public struct OwnedRuntimeActuatorBoundary
    {
        public OwnedRuntimeDeadlineStrategy Strategy;
        public string Certification,FallbackReason;
        public int ProbeCount;
        public OwnedRuntimeControls Controls;
        public double MaximumHorizonSeconds;
        public bool PredictionAvailable;
        public double PredictedDelta,PredictedLowerDelta,PredictedUpperDelta;
        public string PredictedKind;
        public int PredictedActuator,PredictedResource;
        public bool Supported,Reached;
        public double DeltaSeconds;
        public int Actuator,Resource;
        public string Kind,Reason;
        public OwnedRuntimeStamp SourceStamp;
        public long ConfigurationRevision;
    }

    internal sealed class OwnedRuntimeAdaptive
    {
        readonly OwnedRuntimeWorld source,coarse,fine;
        readonly OwnedRuntimeSystems coarseSystems,fineSystems;
        readonly OwnedRuntimeHost coarseHost,fineHost;
        readonly OwnedRuntimeDefinition definition;
        readonly OwnedRuntimeTopology topology;
        readonly long configuration,code;
        OwnedFlightSession sourceSession,coarseSession,fineSession;
        internal OwnedRuntimeAdaptive(OwnedRuntimeHost host)
        {
            source=host.World;definition=source.Definition;topology=source.Topology;configuration=host.Systems.ConfigurationRevision;code=host.CodeGeneration;
            coarse=source.CreateTrialWorld();fine=source.CreateTrialWorld();coarseSystems=host.Systems.CreateTrialSystems(coarse);fineSystems=host.Systems.CreateTrialSystems(fine);
            coarseHost=host.CreateTrialHost(coarse,coarseSystems);fineHost=host.CreateTrialHost(fine,fineSystems);
        }
        internal bool Matches(OwnedRuntimeHost host)
        {return ReferenceEquals(source,host.World)&&ReferenceEquals(definition,source.Definition)&&ReferenceEquals(topology,source.Topology)&&configuration==host.Systems.ConfigurationRevision&&code==host.CodeGeneration;}
        internal OwnedRuntimeStamp Advance(OwnedRuntimeHost host,double upper,OwnedRuntimeInput input,OwnedFlightSession session,OwnedRuntimeControls controls,ref OwnedRuntimeAdaptiveObservation observation)
        {
            var expected=source.Stamp;var state=source.MechanicalInternal;state.Validate();AssemblyModel.Positive(upper);
            if(!ReferenceEquals(sourceSession,session))
            {
                sourceSession=session;coarseSession=session.CreateTrialSession(coarse);fineSession=session.CreateTrialSession(fine);
            }
            double interval=Math.Min(upper,Math.Min(state.maximumInterval,state.nextInterval));long rejected=0,trials=0;
            try
            {
                for(int attempt=0;attempt<32;attempt++)
                {
                    double target=expected.Time+interval;AssemblyModel.Finite(target);
                    if(!(target>expected.Time))throw new ArgumentException("Trial interval does not advance representable physical time.");
                    double middle=expected.Time+(target-expected.Time)*.5;
                    bool refined=middle>expected.Time&&middle<target;
                    if(!refined&&NextTime(expected.Time)!=target)throw new ArgumentException("Trial clock has no admitted interior endpoint.");
                    source.ResetTrial(coarse);source.ResetTrial(fine);coarseSession.SyncTrial(session);fineSession.SyncTrial(session);coarseSystems.ResetTrialCounters();fineSystems.ResetTrialCounters();
                    trials++;observation.Trials++;coarseSession.AdvanceTrial(coarseHost,interval,input,controls);
                    OwnedRuntimeWorld selected=coarse;OwnedRuntimeSystems selectedSystems=coarseSystems;double error;
                    if(refined)
                    {
                        double first=middle-expected.Time;
                        trials++;observation.Trials++;fineSession.AdvanceTrial(fineHost,first,input,controls);
                        double reached=fine.Stamp.Time,remaining=target-reached;
                        if(reached!=middle||!(remaining>0)||reached+remaining!=target)throw new ArgumentException("Fine trial clock differs from its planned endpoints.");
                        trials++;observation.Trials++;fineSession.AdvanceTrial(fineHost,remaining,input,controls);
                        selected=fine;selectedSystems=fineSystems;error=Estimate(coarse.CurrentForTrial,fine.CurrentForTrial,state);
                    }
                    else
                    {
                        // An adjacent clock endpoint cannot contain two committed halves. Bound its entire change instead.
                        error=Estimate(source.CurrentForTrial,coarse.CurrentForTrial,state);
                    }
                    observation.LastError=error;
                    if(error<=1)
                    {
                        double factor=error==0?2:Math.Max(.2,Math.Min(2,.9*Math.Pow(error,-1d/3)));
                        double next=refined?Math.Max(state.minimumInterval,Math.Min(state.maximumInterval,interval*factor)):state.nextInterval;
                        if(!refined)selected.MechanicalInternal.roundoffCoalescedIntervals=checked(state.roundoffCoalescedIntervals+1);
                        var stamp=source.PublishAdaptive(expected,selected,interval,input,error,next,rejected,trials);
                        host.Systems.AccumulateTrialCounters(selectedSystems);observation.Accepted++;if(!refined)observation.RoundoffCoalescedIntervals++;observation.LastInterval=interval;observation.LastFailure=null;return stamp;
                    }
                    rejected++;observation.Rejected++;
                    if(!refined||interval<=state.minimumInterval)throw new OwnedRuntimeMechanicalAccuracyException(interval,error);
                    double reduction=Math.Max(.2,Math.Min(.8,.9*Math.Pow(error,-1d/3)));
                    interval=Math.Max(state.minimumInterval,interval*reduction);
                }
                throw new InvalidOperationException("Mechanical refinement count exceeded its bounded admission.");
            }
            catch(Exception error){observation.LastFailure=error.ToString();throw;}
        }
        internal static double NextTime(double value)
        {return value==0?double.Epsilon:BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(value)+(value>0?1:-1));}
        static double Norm(Vec v){double scale=Math.Max(Math.Abs(v.X),Math.Max(Math.Abs(v.Y),Math.Abs(v.Z)));if(scale==0)return 0;double x=v.X/scale,y=v.Y/scale,z=v.Z/scale;double n=scale*Math.Sqrt(x*x+y*y+z*z);AssemblyModel.Finite(n);return n;}
        static double Scalar(double a,double b,double tolerance){return Math.Abs(a-b)/(tolerance*Math.Max(1,Math.Max(Math.Abs(a),Math.Abs(b))));}
        static double Estimate(OwnedRuntimeBank a,OwnedRuntimeBank b,OwnedRuntimeMechanicalState p)
        {
            double error=0;
            for(int r=0;r<a.RegionMotion.Length;r++)
            {
                var x=a.RegionMotion[r];var y=b.RegionMotion[r];Vec ca=x.RootPosition+x.Orientation.Rotate(a.Properties[r].CenterOfMass),cb=y.RootPosition+y.Orientation.Rotate(b.Properties[r].CenterOfMass);
                error=Math.Max(error,Norm(ca+cb*-1)/p.positionTolerance);error=Math.Max(error,Norm(x.Velocity+y.Velocity*-1)/p.velocityTolerance);
                double sign=Rotation.Dot(x.Orientation,y.Orientation)<0?-1:1;
                double dx=x.Orientation.X-sign*y.Orientation.X,dy=x.Orientation.Y-sign*y.Orientation.Y,dz=x.Orientation.Z-sign*y.Orientation.Z,dw=x.Orientation.W-sign*y.Orientation.W;
                double angle=4*Math.Asin(Math.Min(1,.5*Math.Sqrt(dx*dx+dy*dy+dz*dz+dw*dw)));error=Math.Max(error,angle/p.angleTolerance);
                error=Math.Max(error,Norm(x.AngularMomentum+y.AngularMomentum*-1)/(p.scaledStateTolerance*Math.Max(1,Math.Max(Norm(x.AngularMomentum),Norm(y.AngularMomentum)))));
            }
            for(int i=0;i<a.ResourceQuantity.Length;i++)error=Math.Max(error,Scalar(a.ResourceQuantity[i],b.ResourceQuantity[i],p.scaledStateTolerance));
            for(int i=0;i<a.ProviderState.Length;i++)error=Math.Max(error,Scalar(a.ProviderState[i],b.ProviderState[i],p.scaledStateTolerance));
            AssemblyModel.Finite(error);return error;
        }
    }
}
