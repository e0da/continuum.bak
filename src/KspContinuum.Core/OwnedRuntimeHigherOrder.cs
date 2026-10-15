using System;
using System.Diagnostics;

namespace KspContinuum
{
    internal sealed class OwnedRuntimeHigherOrder
    {
        struct QuaternionValue {internal double X,Y,Z,W;}
        struct Rate {internal Vec Position,Velocity,Momentum;internal double X,Y,Z,W;}
        readonly OwnedRuntimeWorld world;
        readonly OwnedRuntimeSystems systems;
        readonly OwnedRuntimeDefinition definition;
        readonly OwnedRuntimeTopology topology;
        readonly long configuration,code;
        readonly OwnedRuntimeEmbedded fallback;
        readonly OwnedRuntimeMotion[] source,half,threeQuarter,high,low;
        readonly RigidAssemblyProperties[] properties;
        readonly Rate[] k1,k2,k3,k4;
        readonly QuaternionValue[] sourceQ,halfQ,threeQuarterQ,highQ,lowQ;
        readonly Vec[] forces,torques;
        readonly OwnedRuntimeEnvironmentSample[] environment,halfEnvironment;
        readonly double[] heat,heatIntegral;
        sealed class Field : IOwnedRuntimeEnvironmentField
        {
            internal OwnedFlightSession Session;internal OwnedRuntimeStamp Source;
            public OwnedRuntimeEnvironmentSample Sample(OwnedRuntimeStamp stage,int region,OwnedRuntimeMotion motion,RigidAssemblyProperties property)
            {try{return Session.SampleNumerical(Source,stage.Time,region,motion,property);}catch(ArgumentException error){throw new UnsupportedStageException(error);}}
        }
        sealed class UnsupportedStageException : Exception
        {internal UnsupportedStageException(Exception inner):base("Higher-order stage is outside the admitted session domain.",inner){}}
        readonly Field field=new Field();
        internal OwnedRuntimeHigherOrder(OwnedRuntimeHost host)
        {
            world=host.World;systems=host.Systems;definition=world.Definition;topology=world.Topology;configuration=systems.ConfigurationRevision;code=host.CodeGeneration;fallback=new OwnedRuntimeEmbedded(host);
            int count=topology.RegionCount;source=new OwnedRuntimeMotion[count];half=new OwnedRuntimeMotion[count];threeQuarter=new OwnedRuntimeMotion[count];high=new OwnedRuntimeMotion[count];low=new OwnedRuntimeMotion[count];properties=new RigidAssemblyProperties[count];
            sourceQ=new QuaternionValue[count];halfQ=new QuaternionValue[count];threeQuarterQ=new QuaternionValue[count];highQ=new QuaternionValue[count];lowQ=new QuaternionValue[count];k1=new Rate[count];k2=new Rate[count];k3=new Rate[count];k4=new Rate[count];forces=new Vec[count];torques=new Vec[count];environment=new OwnedRuntimeEnvironmentSample[count];halfEnvironment=new OwnedRuntimeEnvironmentSample[count];heat=new double[definition.PartCount];heatIntegral=new double[definition.PartCount];
        }
        internal bool Matches(OwnedRuntimeHost host)
        {return ReferenceEquals(world,host.World)&&ReferenceEquals(definition,world.Definition)&&ReferenceEquals(topology,world.Topology)&&configuration==systems.ConfigurationRevision&&code==host.CodeGeneration;}
        internal OwnedRuntimeStamp Advance(OwnedRuntimeHost host,double maximum,OwnedRuntimeInput input,OwnedFlightSession session,OwnedRuntimeControls controls,ref OwnedRuntimeAdaptiveObservation observation)
        {
            var expected=world.Stamp;var policy=world.MechanicalInternal;policy.Validate();field.Session=session;field.Source=expected;
            double interval=Math.Min(maximum,Math.Min(policy.maximumInterval,policy.nextInterval));long rejected=0,attempts=0;string unsupported=null;
            try
            {
                unsupported=Preflight(expected,controls,interval);
                if(unsupported!=null)
                {
                    observation.HigherOrderFallbacks++;observation.LastHigherOrderFallback=unsupported;
                    return fallback.Advance(host,maximum,input,session,controls,ref observation);
                }
                for(int iteration=0;iteration<32;iteration++)
                {
                    double end=expected.Time+interval,mid=expected.Time+interval*.5,third=expected.Time+interval*.75;AssemblyModel.Finite(end);
                    if(!(end>expected.Time))throw new ArgumentException("Higher-order interval does not advance the clock.");
                    if(!(mid>expected.Time&&third>mid&&end>third)){unsupported="clock-has-no-four-distinct-stages";break;}
                    var step=world.BeginHostedStep(expected,interval,input);attempts++;observation.Trials++;observation.HigherOrderAttempts++;
                    try
                    {
                        Array.Copy(step.Bank.RegionMotion,source,source.Length);Array.Copy(step.Bank.Properties,properties,properties.Length);
                        systems.BeginMechanical(step,controls);
                        unsupported=Unsupported(step);
                        if(unsupported!=null)break;
                        for(int r=0;r<source.Length;r++){var q=source[r].Orientation;sourceQ[r]=new QuaternionValue{X=q.X,Y=q.Y,Z=q.Z,W=q.W};}
                        Array.Clear(heatIntegral,0,heatIntegral.Length);
                        Sample(step,expected,source,sourceQ,k1,2d/9,ref observation);
                        long begin=systems.WorkObservation?Stopwatch.GetTimestamp():0;
                        Compose(half,halfQ,interval,.5,k1,0,k2,0,k3,0,k4);
                        if(systems.WorkObservation)systems.RecordMechanicalIntegrationTicks(Stopwatch.GetTimestamp()-begin);
                        Sample(step,At(expected,mid),half,halfQ,k2,1d/3,ref observation);Array.Copy(environment,halfEnvironment,environment.Length);
                        begin=systems.WorkObservation?Stopwatch.GetTimestamp():0;Compose(threeQuarter,threeQuarterQ,interval,0,k1,.75,k2,0,k3,0,k4);
                        if(systems.WorkObservation)systems.RecordMechanicalIntegrationTicks(Stopwatch.GetTimestamp()-begin);
                        Sample(step,At(expected,third),threeQuarter,threeQuarterQ,k3,4d/9,ref observation);
                        begin=systems.WorkObservation?Stopwatch.GetTimestamp():0;Compose(high,highQ,interval,2d/9,k1,1d/3,k2,4d/9,k3,0,k4);
                        if(systems.WorkObservation)systems.RecordMechanicalIntegrationTicks(Stopwatch.GetTimestamp()-begin);
                        Sample(step,At(expected,end),high,highQ,k4,0,ref observation);
                        begin=systems.WorkObservation?Stopwatch.GetTimestamp():0;Compose(low,lowQ,interval,7d/24,k1,.25,k2,1d/3,k3,.125,k4);
                        double error=OwnedRuntimeEmbedded.Estimate(low,properties,high,properties,policy,ref observation);observation.LastError=error;
                        if(systems.WorkObservation)systems.RecordMechanicalIntegrationTicks(Stopwatch.GetTimestamp()-begin);
                        if(error<=1)
                        {
                            session.ValidateNumericalInterval(expected,interval,source,properties,half,properties,high,properties);
                            systems.CompleteMechanical(step,high,halfEnvironment,heatIntegral);
                            double factor=error==0?2:Math.Max(.2,Math.Min(2,.9*Math.Pow(error,-1d/3)));
                            var state=step.Bank.Mechanical;state.accepted=checked(policy.accepted+1);state.rejected=checked(policy.rejected+rejected);state.trialExecutions=checked(policy.trialExecutions+attempts);
                            state.lastAcceptedInterval=interval;state.lastError=error;state.nextInterval=Math.Max(policy.minimumInterval,Math.Min(policy.maximumInterval,interval*factor));
                            var stamp=world.CommitHostedStep(step);systems.AcknowledgeMechanical(step);observation.Accepted++;observation.LastInterval=interval;observation.LastFailure=null;observation.LastHigherOrderFallback=null;return stamp;
                        }
                        rejected++;observation.Rejected++;
                        if(interval<=policy.minimumInterval)throw new OwnedRuntimeMechanicalAccuracyException(interval,error);
                        interval=Math.Max(policy.minimumInterval,interval*Math.Max(.2,Math.Min(.8,.9*Math.Pow(error,-1d/3))));
                    }
                    catch(UnsupportedStageException error){unsupported="session-stage-domain: "+error.InnerException.Message;break;}
                    finally{try{systems.AbortMechanical(step);}finally{world.EndHostedStep(step);}}
                }
                if(unsupported==null)throw new InvalidOperationException("Higher-order refinement exceeded its bounded attempts.");
                observation.HigherOrderFallbacks++;observation.LastHigherOrderFallback=unsupported;
                return fallback.Advance(host,Math.Min(maximum,interval),input,session,controls,ref observation,attempts,rejected);
            }
            catch(Exception error){observation.LastFailure=error.ToString();throw;}
        }
        string Preflight(OwnedRuntimeStamp expected,OwnedRuntimeControls controls,double interval)
        {
            controls.Validate();var bank=world.CurrentForTrial;
            if(!bank.Stamp.Matches(expected)||!ReferenceEquals(bank.Definition,definition)||!ReferenceEquals(bank.Topology,topology)||systems.ConfigurationRevision!=configuration)throw new InvalidOperationException("Higher-order preflight source/configuration differs.");
            Vec attitude=systems.ControlOrientation.Rotate(controls.Attitude),translation=systems.ControlOrientation.Rotate(controls.Translation);
            for(int i=0;i<systems.ActuatorCount;i++)
            {
                var actuator=systems.GetActuator(i);int slot=systems.WheelProviderSlot(i);
                if(slot>=0&&bank.ProviderState[slot]!=0)return "preflight-existing-internal-wheel-momentum";
                if(!systems.IsEnabled(i))continue;
                bool allowed=actuator.Kind==OwnedRuntimeActuatorKind.Engine?controls.EnginesEnabled:actuator.Kind==OwnedRuntimeActuatorKind.Rcs?controls.RcsEnabled:controls.WheelsEnabled;if(!allowed)continue;
                var part=definition.GetPart(actuator.Part);int body=world.BodyOfPart(actuator.Part);Vec axis=(bank.Members[body].LocalRotation*part.LocalRotation).Rotate(actuator.LocalAxis);
                double request=actuator.Kind==OwnedRuntimeActuatorKind.Engine?controls.Throttle:actuator.Kind==OwnedRuntimeActuatorKind.Rcs?Math.Max(0,OwnedRuntimeDynamics.Dot(axis,translation)):OwnedRuntimeDynamics.Dot(axis,attitude);request=Math.Max(-1,Math.Min(1,request));
                if(request==0)continue;
                if(actuator.Kind==OwnedRuntimeActuatorKind.ReactionWheel)return "preflight-possible-internal-wheel-change";
                if(actuator.FuelCount==0)continue;
                if(actuator.FuelResource>=0)
                {
                    var resource=definition.GetResource(actuator.FuelResource);double quantity=bank.ResourceQuantity[actuator.FuelResource];
                    double mass=actuator.MaximumForceOrTorque*Math.Abs(request)*interval/(actuator.SpecificImpulseSeconds*9.80665);
                    if(resource.MassPerUnit>0&&quantity>0&&quantity-mass/resource.MassPerUnit==quantity)continue;
                }
                return "preflight-possible-resource-mass-change";
            }
            return null;
        }
        string Unsupported(OwnedRuntimeStep step)
        {
            for(int r=0;r<properties.Length;r++)if(!ReferenceEquals(properties[r],step.Bank.Properties[r]))return "scalar-changed-mass-com-or-inertia";
            for(int a=0;a<systems.ActuatorCount;a++)
            {
                int slot=systems.WheelProviderSlot(a);if(slot<0)continue;
                if(step.Bank.ProviderState[slot]!=0||systems.GetMechanicalProviderState(step,slot)!=0)return "nonzero-internal-wheel-momentum";
            }
            return null;
        }
        void Sample(OwnedRuntimeStep step,OwnedRuntimeStamp stamp,OwnedRuntimeMotion[] motion,QuaternionValue[] raw,Rate[] rate,double heatWeight,ref OwnedRuntimeAdaptiveObservation observation)
        {
            systems.SampleMechanical(step,field,stamp,motion,properties,forces,torques,environment,heat);observation.HigherOrderForceSamples++;
            long begin=systems.WorkObservation?Stopwatch.GetTimestamp():0;
            for(int r=0;r<rate.Length;r++)
            {
                var m=motion[r];Vec omega=OwnedRuntimeDynamics.AngularVelocity(properties[r],m.Orientation,m.AngularMomentum);var q=raw[r];
                rate[r]=new Rate{Position=m.Velocity,Velocity=forces[r]*(1/properties[r].Mass)+environment[r].Gravity,Momentum=torques[r],
                    X=.5*(omega.X*q.W+omega.Y*q.Z-omega.Z*q.Y),Y=.5*(-omega.X*q.Z+omega.Y*q.W+omega.Z*q.X),Z=.5*(omega.X*q.Y-omega.Y*q.X+omega.Z*q.W),W=-.5*(omega.X*q.X+omega.Y*q.Y+omega.Z*q.Z)};
            }
            if(heatWeight!=0)for(int p=0;p<heatIntegral.Length;p++)heatIntegral[p]+=heat[p]*heatWeight;
            if(systems.WorkObservation)systems.RecordMechanicalIntegrationTicks(Stopwatch.GetTimestamp()-begin);
        }
        void Compose(OwnedRuntimeMotion[] target,QuaternionValue[] raw,double interval,double w1,Rate[] a,double w2,Rate[] b,double w3,Rate[] c,double w4,Rate[] d)
        {
            for(int r=0;r<target.Length;r++)
            {
                var original=source[r];var q=sourceQ[r];
                raw[r]=new QuaternionValue{X=q.X+interval*(w1*a[r].X+w2*b[r].X+w3*c[r].X+w4*d[r].X),Y=q.Y+interval*(w1*a[r].Y+w2*b[r].Y+w3*c[r].Y+w4*d[r].Y),Z=q.Z+interval*(w1*a[r].Z+w2*b[r].Z+w3*c[r].Z+w4*d[r].Z),W=q.W+interval*(w1*a[r].W+w2*b[r].W+w3*c[r].W+w4*d[r].W)};var orientation=new Rotation(raw[r].X,raw[r].Y,raw[r].Z,raw[r].W);
                Vec center=original.RootPosition+original.Orientation.Rotate(properties[r].CenterOfMass);
                target[r]=new OwnedRuntimeMotion{Orientation=orientation,RootPosition=center+(a[r].Position*w1+b[r].Position*w2+c[r].Position*w3+d[r].Position*w4)*interval+orientation.Rotate(properties[r].CenterOfMass)*-1,
                    Velocity=original.Velocity+(a[r].Velocity*w1+b[r].Velocity*w2+c[r].Velocity*w3+d[r].Velocity*w4)*interval,
                    AngularMomentum=original.AngularMomentum+(a[r].Momentum*w1+b[r].Momentum*w2+c[r].Momentum*w3+d[r].Momentum*w4)*interval};
                OwnedRuntimeWorld.ValidateMotion(target[r]);
            }
        }
        static OwnedRuntimeStamp At(OwnedRuntimeStamp source,double time)
        {return new OwnedRuntimeStamp(source.Epoch,source.StateRevision,source.TopologyRevision,source.DefinitionRevision,source.FrameRevision,time,source.FrameId);}
    }
}
