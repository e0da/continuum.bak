using System;
using System.Diagnostics;

namespace KspContinuum
{
    // Two wrench samples share one scalar transaction; only the accepted terminal bank publishes.
    internal sealed class OwnedRuntimeEmbedded
    {
        readonly OwnedRuntimeWorld world;
        readonly OwnedRuntimeSystems systems;
        readonly OwnedRuntimeDefinition definition;
        readonly OwnedRuntimeTopology topology;
        readonly long configuration,code;
        readonly OwnedRuntimeMotion[] source,middle,lower,upper;
        readonly RigidAssemblyProperties[] before,midpoint,terminal;
        readonly Vec[] force0,torque0,force1,torque1;
        readonly OwnedRuntimeEnvironmentSample[] environment0,environment1;
        readonly double[] heat0,heat1;
        readonly NumericalField field=new NumericalField();
        sealed class NumericalField : IOwnedRuntimeEnvironmentField
        {
            internal OwnedFlightSession Session;
            internal OwnedRuntimeStamp Source;
            public OwnedRuntimeEnvironmentSample Sample(OwnedRuntimeStamp stage,int region,OwnedRuntimeMotion motion,RigidAssemblyProperties properties)
            {return Session.SampleNumerical(Source,stage.Time,region,motion,properties);}
        }
        internal OwnedRuntimeEmbedded(OwnedRuntimeHost host)
        {
            world=host.World;systems=host.Systems;definition=world.Definition;topology=world.Topology;configuration=systems.ConfigurationRevision;code=host.CodeGeneration;
            int count=topology.RegionCount;source=new OwnedRuntimeMotion[count];middle=new OwnedRuntimeMotion[count];lower=new OwnedRuntimeMotion[count];upper=new OwnedRuntimeMotion[count];
            before=new RigidAssemblyProperties[count];midpoint=new RigidAssemblyProperties[count];terminal=new RigidAssemblyProperties[count];
            force0=new Vec[count];torque0=new Vec[count];force1=new Vec[count];torque1=new Vec[count];environment0=new OwnedRuntimeEnvironmentSample[count];environment1=new OwnedRuntimeEnvironmentSample[count];
            heat0=new double[definition.PartCount];heat1=new double[definition.PartCount];
        }
        internal bool Matches(OwnedRuntimeHost host)
        {return ReferenceEquals(world,host.World)&&ReferenceEquals(definition,world.Definition)&&ReferenceEquals(topology,world.Topology)&&configuration==systems.ConfigurationRevision&&code==host.CodeGeneration;}
        internal OwnedRuntimeStamp Advance(OwnedRuntimeHost host,double maximum,OwnedRuntimeInput input,OwnedFlightSession session,OwnedRuntimeControls controls,ref OwnedRuntimeAdaptiveObservation observation,long predecessorAttempts=0,long predecessorRejected=0)
        {
            var expected=world.Stamp;var policy=world.MechanicalInternal;policy.Validate();
            double interval=Math.Min(maximum,Math.Min(policy.maximumInterval,policy.nextInterval));long rejected=predecessorRejected,attempts=predecessorAttempts;
            field.Session=session;field.Source=expected;
            try
            {
                for(int iteration=0;iteration<32;iteration++)
                {
                    double end=expected.Time+interval;AssemblyModel.Finite(end);
                    if(!(end>expected.Time))throw new ArgumentException("Embedded interval does not advance the physical clock.");
                    double stageTime=expected.Time+interval*.5;bool coalesced=!(stageTime>expected.Time&&stageTime<end);
                    if(coalesced&&OwnedRuntimeAdaptive.NextTime(expected.Time)!=end)throw new ArgumentException("Embedded clock has no admitted midpoint.");
                    var step=world.BeginHostedStep(expected,interval,input);attempts++;observation.Trials++;observation.EmbeddedAttempts++;
                    try
                    {
                        Array.Copy(step.Bank.RegionMotion,source,source.Length);Array.Copy(step.Bank.Properties,before,before.Length);
                        systems.BeginMechanical(step,controls);Array.Copy(step.Bank.Properties,terminal,terminal.Length);
                        systems.SampleMechanical(step,field,expected,source,before,force0,torque0,environment0,heat0);observation.EmbeddedForceSamples++;
                        long integrationBegin=systems.WorkObservation?Stopwatch.GetTimestamp():0;
                        for(int r=0;r<source.Length;r++)
                        {
                            midpoint[r]=HalfProperties(before[r],terminal[r]);
                            Vec oldWheel=systems.OldWheelMomentum[r],newWheel=systems.NewWheelMomentum[r];
                            lower[r]=AdvanceMotion(source[r],before[r],before[r],terminal[r],force0[r],force0[r],torque0[r],environment0[r].Gravity,environment0[r].Gravity,oldWheel,newWheel,interval,false);
                            Vec shiftedTorque=torque0[r]+Vec.Cross(source[r].Orientation.Rotate(before[r].CenterOfMass+midpoint[r].CenterOfMass*-1),force0[r]);
                            middle[r]=AdvanceMotion(source[r],before[r],midpoint[r],midpoint[r],force0[r],force0[r],shiftedTorque,environment0[r].Gravity,environment0[r].Gravity,oldWheel,(oldWheel+newWheel)*.5,interval*.5,false);
                        }
                        if(systems.WorkObservation)systems.RecordMechanicalIntegrationTicks(Stopwatch.GetTimestamp()-integrationBegin);
                        if(!coalesced)
                        {
                            var stage=new OwnedRuntimeStamp(expected.Epoch,expected.StateRevision,expected.TopologyRevision,expected.DefinitionRevision,expected.FrameRevision,stageTime,expected.FrameId);
                            systems.SampleMechanical(step,field,stage,middle,midpoint,force1,torque1,environment1,heat1);observation.EmbeddedForceSamples++;
                            integrationBegin=systems.WorkObservation?Stopwatch.GetTimestamp():0;
                            for(int r=0;r<source.Length;r++)
                                upper[r]=AdvanceMotion(source[r],before[r],midpoint[r],terminal[r],force0[r],force1[r],torque1[r],environment0[r].Gravity,environment1[r].Gravity,systems.OldWheelMomentum[r],systems.NewWheelMomentum[r],interval,true);
                        }
                        else {integrationBegin=systems.WorkObservation?Stopwatch.GetTimestamp():0;Array.Copy(lower,upper,upper.Length);}
                        double error=Estimate(coalesced?source:lower,coalesced?before:terminal,upper,terminal,policy,ref observation);
                        if(coalesced)
                        {
                            var original=world.CurrentForTrial;
                            double scalar=0;
                            for(int i=0;i<original.ResourceQuantity.Length;i++)scalar=Math.Max(scalar,ScalarChange(original.ResourceQuantity[i],step.Bank.ResourceQuantity[i],policy.scaledStateTolerance));
                            for(int i=0;i<original.ProviderState.Length;i++)scalar=Math.Max(scalar,ScalarChange(original.ProviderState[i],systems.GetMechanicalProviderState(step,i),policy.scaledStateTolerance));
                            error=Math.Max(error,scalar);observation.Components(observation.PositionError,observation.VelocityError,observation.AngleError,observation.MomentumError,scalar);
                        }
                        if(systems.WorkObservation)systems.RecordMechanicalIntegrationTicks(Stopwatch.GetTimestamp()-integrationBegin);
                        observation.LastError=error;
                        if(error<=1)
                        {
                            session.ValidateNumericalInterval(expected,interval,source,before,middle,midpoint,upper,terminal);
                            systems.CompleteMechanical(step,upper,coalesced?environment0:environment1,coalesced?heat0:heat1);
                            double factor=error==0?2:Math.Max(.2,Math.Min(2,.9/Math.Sqrt(error)));
                            var state=step.Bank.Mechanical;state.accepted=checked(policy.accepted+1);state.rejected=checked(policy.rejected+rejected);state.trialExecutions=checked(policy.trialExecutions+attempts);
                            state.lastAcceptedInterval=interval;state.lastError=error;state.nextInterval=coalesced?policy.nextInterval:Math.Max(policy.minimumInterval,Math.Min(policy.maximumInterval,interval*factor));
                            state.roundoffCoalescedIntervals=checked(policy.roundoffCoalescedIntervals+(coalesced?1:0));
                            var stamp=world.CommitHostedStep(step);systems.AcknowledgeMechanical(step);observation.Accepted++;if(coalesced)observation.RoundoffCoalescedIntervals++;
                            observation.LastInterval=interval;observation.LastFailure=null;return stamp;
                        }
                        rejected++;observation.Rejected++;
                        if(coalesced||interval<=policy.minimumInterval)throw new OwnedRuntimeMechanicalAccuracyException(interval,error);
                        interval=Math.Max(policy.minimumInterval,interval*Math.Max(.2,Math.Min(.8,.9/Math.Sqrt(error))));
                    }
                    finally{try{systems.AbortMechanical(step);}finally{world.EndHostedStep(step);}}
                }
                throw new InvalidOperationException("Embedded refinement exceeded its bounded attempts.");
            }
            catch(Exception error){observation.LastFailure=error.ToString();throw;}
        }
        static RigidAssemblyProperties HalfProperties(RigidAssemblyProperties a,RigidAssemblyProperties b)
        {
            if(ReferenceEquals(a,b))return a;
            double mass=.5*a.Mass+.5*b.Mass;AssemblyModel.Positive(mass);
            Vec center=a.CenterOfMass+(b.CenterOfMass+a.CenterOfMass*-1)*(.5*b.Mass/mass),da=a.CenterOfMass+center*-1,db=b.CenterOfMass+center*-1;
            var x=a.Inertia;var y=b.Inertia;
            double wa=.5*a.Mass,wb=.5*b.Mass;
            var tensor=new InertiaTensor6Dof(.5*x.XX+.5*y.XX+wa*(da.Y*da.Y+da.Z*da.Z)+wb*(db.Y*db.Y+db.Z*db.Z),
                .5*x.XY+.5*y.XY-wa*da.X*da.Y-wb*db.X*db.Y,.5*x.XZ+.5*y.XZ-wa*da.X*da.Z-wb*db.X*db.Z,
                .5*x.YY+.5*y.YY+wa*(da.X*da.X+da.Z*da.Z)+wb*(db.X*db.X+db.Z*db.Z),
                .5*x.YZ+.5*y.YZ-wa*da.Y*da.Z-wb*db.Y*db.Z,.5*x.ZZ+.5*y.ZZ+wa*(da.X*da.X+da.Y*da.Y)+wb*(db.X*db.X+db.Y*db.Y));
            Vec principal;Rotation rotation;RigidAssemblyProperties.Diagonalize(tensor,out principal,out rotation);
            if(!(principal.X/principal.Z>=RigidAssemblyProjection.MinimumPrincipalMomentRatio))throw new ArgumentException("Midpoint inertia is ill-conditioned.");
            return new RigidAssemblyProperties(mass,center,tensor,principal,rotation);
        }
        static OwnedRuntimeMotion AdvanceMotion(OwnedRuntimeMotion initial,RigidAssemblyProperties initialProperties,RigidAssemblyProperties integrationProperties,RigidAssemblyProperties finalProperties,
            Vec forceStart,Vec forceMiddle,Vec torque,Vec gravityStart,Vec gravityMiddle,Vec oldWheel,Vec newWheel,double interval,bool midpointForce)
        {
            var motion=initial;
            if(!ReferenceEquals(initialProperties,integrationProperties))OwnedRuntimeDynamics.ChangeProperties(ref motion,initialProperties,integrationProperties);
            Vec center=motion.RootPosition+motion.Orientation.Rotate(integrationProperties.CenterOfMass),velocity=motion.Velocity;
            if(midpointForce)OwnedRuntimeDynamics.StepCoupled(ref motion,integrationProperties,new Vec(),torque,oldWheel,newWheel,interval);
            else
            {
                Vec omega=OwnedRuntimeDynamics.AngularVelocity(integrationProperties,motion.Orientation,motion.AngularMomentum),total=motion.AngularMomentum+motion.Orientation.Rotate(oldWheel);
                Vec wheelRate=new Vec((newWheel.X-oldWheel.X)/interval,(newWheel.Y-oldWheel.Y)/interval,(newWheel.Z-oldWheel.Z)/interval);
                Vec alpha=OwnedRuntimeDynamics.AngularVelocity(integrationProperties,motion.Orientation,
                    torque+Vec.Cross(omega,total)*-1+motion.Orientation.Rotate(wheelRate)*-1);
                Vec turn=omega*interval+alpha*(.5*interval*interval);double magnitude=Norm(turn);
                if(magnitude!=0)motion.Orientation=Rotation.AxisAngle(turn,magnitude)*motion.Orientation;
                motion.AngularMomentum=total+torque*interval+motion.Orientation.Rotate(newWheel)*-1;
            }
            double velocityWeight,positionWeight,changeWeight;MassWeights(initialProperties.Mass,finalProperties.Mass,interval,out velocityWeight,out positionWeight,out changeWeight);
            Vec displacement=velocity*interval+forceStart*positionWeight+gravityStart*(.5*interval*interval);
            if(midpointForce)displacement=displacement+(forceMiddle+forceStart*-1)*changeWeight+(gravityMiddle+gravityStart*-1)*(interval*interval/3);
            motion.RootPosition=center+displacement+motion.Orientation.Rotate(integrationProperties.CenterOfMass)*-1;
            motion.Velocity=velocity+(midpointForce?forceMiddle:forceStart)*velocityWeight+(midpointForce?gravityMiddle:gravityStart)*interval;
            if(!ReferenceEquals(integrationProperties,finalProperties))OwnedRuntimeDynamics.ChangeProperties(ref motion,integrationProperties,finalProperties);
            OwnedRuntimeWorld.ValidateMotion(motion);return motion;
        }
        static void MassWeights(double initial,double terminal,double interval,out double velocity,out double position,out double variation)
        {
            AssemblyModel.Positive(initial);AssemblyModel.Positive(terminal);
            if(terminal>initial)throw new InvalidOperationException("Embedded actuation does not admit a mass source.");
            double ratio=(initial-terminal)/initial,a=0,b=0,c=0;
            if(ratio<.125)
            {
                double power=1;
                for(int n=0;n<24;n++){a+=power/(n+1);b+=power/((n+1d)*(n+2));c+=2*power/((n+2d)*(n+3));power*=ratio;}
            }
            else
            {
                double remaining=terminal/initial,logarithm=remaining==0?Math.Log(initial)-Math.Log(terminal):-Math.Log(remaining);
                a=logarithm/ratio;b=(ratio-remaining*logarithm)/(ratio*ratio);
                c=2*(ratio-.5*ratio*ratio-remaining*logarithm)/(ratio*ratio*ratio);
            }
            velocity=interval/initial*a;position=interval*interval/initial*b;variation=interval*interval/initial*c;
            AssemblyModel.Finite(velocity);AssemblyModel.Finite(position);AssemblyModel.Finite(variation);
            if(velocity<0||position<0||variation<0)throw new ArgumentException("Embedded mass quadrature is outside its finite domain.");
        }
        static double Norm(Vec v)
        {double s=Math.Max(Math.Abs(v.X),Math.Max(Math.Abs(v.Y),Math.Abs(v.Z)));if(s==0)return 0;double n=s*Math.Sqrt((v.X/s)*(v.X/s)+(v.Y/s)*(v.Y/s)+(v.Z/s)*(v.Z/s));AssemblyModel.Finite(n);return n;}
        static double ScalarChange(double a,double b,double tolerance)
        {double error=Math.Abs(a-b)/(tolerance*Math.Max(1,Math.Max(Math.Abs(a),Math.Abs(b))));AssemblyModel.Finite(error);return error;}
        internal static double Estimate(OwnedRuntimeMotion[] a,RigidAssemblyProperties[] pa,OwnedRuntimeMotion[] b,RigidAssemblyProperties[] pb,OwnedRuntimeMechanicalState policy,ref OwnedRuntimeAdaptiveObservation observation)
        {
            double position=0,velocity=0,angle=0,momentum=0;
            for(int r=0;r<a.Length;r++)
            {
                Vec ca=a[r].RootPosition+a[r].Orientation.Rotate(pa[r].CenterOfMass),cb=b[r].RootPosition+b[r].Orientation.Rotate(pb[r].CenterOfMass);
                position=Math.Max(position,Norm(ca+cb*-1)/policy.positionTolerance);velocity=Math.Max(velocity,Norm(a[r].Velocity+b[r].Velocity*-1)/policy.velocityTolerance);
                double sign=Rotation.Dot(a[r].Orientation,b[r].Orientation)<0?-1:1,dx=a[r].Orientation.X-sign*b[r].Orientation.X,dy=a[r].Orientation.Y-sign*b[r].Orientation.Y,dz=a[r].Orientation.Z-sign*b[r].Orientation.Z,dw=a[r].Orientation.W-sign*b[r].Orientation.W;
                angle=Math.Max(angle,4*Math.Asin(Math.Min(1,.5*Math.Sqrt(dx*dx+dy*dy+dz*dz+dw*dw)))/policy.angleTolerance);
                momentum=Math.Max(momentum,Norm(a[r].AngularMomentum+b[r].AngularMomentum*-1)/(policy.scaledStateTolerance*Math.Max(1,Math.Max(Norm(a[r].AngularMomentum),Norm(b[r].AngularMomentum)))));
            }
            observation.Components(position,velocity,angle,momentum);double error=Math.Max(Math.Max(position,velocity),Math.Max(angle,momentum));AssemblyModel.Finite(error);return error;
        }
    }
}
