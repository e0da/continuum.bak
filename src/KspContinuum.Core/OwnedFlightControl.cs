using System;

namespace KspContinuum
{
    public struct OwnedFlightGuidance
    {
        public OwnedRuntimeInput Input;
        public double AngleErrorRadians, VelocityErrorMetersPerSecond;
        public int SaturatedAxes, UnavailableAxes;
    }
    public struct OwnedFlightAuthority
    {
        public OwnedRuntimeStamp Stamp;
        public long ConfigurationRevision;
        public Vec LinearAcceleration,AngularAcceleration;
        public Vec PositiveAngularAcceleration,NegativeAngularAcceleration;
        public int RcsJets,Wheels,UnavailableResourceActuators,SaturatedWheels;
        public bool NominalUpperBound;
    }

    public static class OwnedFlightControl
    {
        public static OwnedFlightAuthority ObserveAuthority(OwnedRuntimeWorld world,OwnedRuntimeSystems systems,long regionId)
        {
            if(world==null||systems==null)throw new ArgumentNullException("world/systems");
            using(var read=world.AcquireRead())
            {
                int region=Region(read,regionId);
                return ObserveAuthority(world,systems,regionId,read.GetRegion(region).Motion.Orientation*systems.ControlOrientation);
            }
        }
        public static OwnedFlightAuthority ObserveAuthority(OwnedRuntimeWorld world,OwnedRuntimeSystems systems,long regionId,Rotation controlFrameWorld)
        {
            if(world==null||systems==null)throw new ArgumentNullException("world/systems");
            if(!systems.IsBoundTo(world))throw new ArgumentException("Authority systems/world owner differs.");
            RigidAssemblyProperties.RequireRotation(controlFrameWorld);
            long configuration=systems.ConfigurationRevision;
            using(var read=world.AcquireRead())
            {
                int region=Region(read,regionId);var state=read.GetRegion(region);var definition=read.Definition;
                Vec positive=new Vec(),negative=new Vec(),angularPositive=new Vec(),angularNegative=new Vec();
                var result=new OwnedFlightAuthority {Stamp=read.Stamp,ConfigurationRevision=configuration,NominalUpperBound=true};
                for(int i=0;i<systems.ActuatorCount;i++)
                {
                    var a=systems.GetActuator(i);int body=definition.BodySlot(definition.GetPart(a.Part).BodyId);
                    if(!systems.IsEnabled(i)||read.Topology.RegionOfBody(body)!=region||a.Kind==OwnedRuntimeActuatorKind.Engine)continue;
                    if(!Available(read,systems,a,region)){result.UnavailableResourceActuators++;continue;}
                    Rotation local=read.GetBody(body).LocalRotation*definition.GetPart(a.Part).LocalRotation;
                    Vec axis=state.Motion.Orientation.Rotate(local.Rotate(a.LocalAxis));
                    Vec controlAxis=controlFrameWorld.Inverse.Rotate(axis);
                    if(a.Kind==OwnedRuntimeActuatorKind.Rcs)
                    {
                        double scale=a.MaximumForceOrTorque/state.Properties.Mass;result.RcsJets++;
                        positive=positive+new Vec(controlAxis.X>0?scale*controlAxis.X*controlAxis.X:0,
                            controlAxis.Y>0?scale*controlAxis.Y*controlAxis.Y:0,controlAxis.Z>0?scale*controlAxis.Z*controlAxis.Z:0);
                        negative=negative+new Vec(controlAxis.X<0?scale*controlAxis.X*controlAxis.X:0,
                            controlAxis.Y<0?scale*controlAxis.Y*controlAxis.Y:0,controlAxis.Z<0?scale*controlAxis.Z*controlAxis.Z:0);
                    }
                    else
                    {
                        double momentum=read.GetProviderState(systems.WheelProviderSlot(i));
                        if(Math.Abs(momentum)>a.WheelMomentumCapacity)throw new InvalidOperationException("Wheel momentum exceeds its admitted capacity.");
                        if(Math.Abs(momentum)==a.WheelMomentumCapacity)result.SaturatedWheels++;
                        result.Wheels++;
                        Vec acceleration=controlFrameWorld.Inverse.Rotate(OwnedRuntimeDynamics.AngularVelocity(state.Properties,state.Motion.Orientation,axis*a.MaximumForceOrTorque));
                        angularPositive=angularPositive+new Vec(WheelContribution(controlAxis.X,acceleration.X,momentum,a.WheelMomentumCapacity,true),
                            WheelContribution(controlAxis.Y,acceleration.Y,momentum,a.WheelMomentumCapacity,true),
                            WheelContribution(controlAxis.Z,acceleration.Z,momentum,a.WheelMomentumCapacity,true));
                        angularNegative=angularNegative+new Vec(WheelContribution(controlAxis.X,acceleration.X,momentum,a.WheelMomentumCapacity,false),
                            WheelContribution(controlAxis.Y,acceleration.Y,momentum,a.WheelMomentumCapacity,false),
                            WheelContribution(controlAxis.Z,acceleration.Z,momentum,a.WheelMomentumCapacity,false));
                    }
                }
                result.LinearAcceleration=new Vec(Math.Min(positive.X,negative.X),Math.Min(positive.Y,negative.Y),Math.Min(positive.Z,negative.Z));
                result.PositiveAngularAcceleration=new Vec(Math.Max(0,angularPositive.X),Math.Max(0,angularPositive.Y),Math.Max(0,angularPositive.Z));
                result.NegativeAngularAcceleration=new Vec(Math.Max(0,angularNegative.X),Math.Max(0,angularNegative.Y),Math.Max(0,angularNegative.Z));
                result.AngularAcceleration=new Vec(Math.Min(result.PositiveAngularAcceleration.X,result.NegativeAngularAcceleration.X),
                    Math.Min(result.PositiveAngularAcceleration.Y,result.NegativeAngularAcceleration.Y),Math.Min(result.PositiveAngularAcceleration.Z,result.NegativeAngularAcceleration.Z));
                OwnedRuntimeDynamics.Require(result.LinearAcceleration);OwnedRuntimeDynamics.Require(result.AngularAcceleration);
                OwnedRuntimeDynamics.Require(result.PositiveAngularAcceleration);OwnedRuntimeDynamics.Require(result.NegativeAngularAcceleration);
                if(!world.Stamp.Matches(read.Stamp)||systems.ConfigurationRevision!=configuration)throw new InvalidOperationException("Authority observation changed epoch/configuration.");
                return result;
            }
        }
        static double WheelContribution(double controlComponent,double accelerationComponent,double momentum,double capacity,bool positiveInput)
        {
            double request=positiveInput?controlComponent:-controlComponent;
            if(request==0)return 0;
            double headroom=request>0?momentum+capacity:capacity-momentum;
            return headroom>0?accelerationComponent*controlComponent:0;
        }
        static int Region(OwnedRuntimeReadLease read,long id)
        {for(int i=0;i<read.RegionCount;i++)if(read.GetRegion(i).Id==id)return i;throw new ArgumentException("Control region is absent.");}
        static bool Available(OwnedRuntimeReadLease read,OwnedRuntimeSystems systems,OwnedRuntimeActuatorSpec a,int region)
        {
            var d=read.Definition;
            if(a.ElectricUnitsPerSecond>0 &&(!systems.IsResourceFlowEnabled(a.ElectricResource)||read.GetResourceQuantity(a.ElectricResource)<=0
                ||read.Topology.RegionOfPart(d.PartSlot(d.GetResource(a.ElectricResource).PartId))!=region))return false;
            for(int f=0;f<a.FuelCount;f++)
            {
                bool found=false;
                if(a.FuelResource>=0)found=systems.IsResourceFlowEnabled(a.FuelResource)&&read.GetResourceQuantity(a.FuelResource)>0
                    &&read.Topology.RegionOfPart(d.PartSlot(d.GetResource(a.FuelResource).PartId))==region;
                else for(int r=0;r<d.ResourceCount&&!found;r++)found=d.GetResource(r).Kind==a.FuelKind(f)&&systems.IsResourceFlowEnabled(r)
                    &&read.GetResourceQuantity(r)>0&&read.Topology.RegionOfPart(d.PartSlot(d.GetResource(r).PartId))==region;
                if(!found)return false;
            }
            return true;
        }

        public static OwnedFlightGuidance AttitudeHold(Rotation currentOrientation,Vec worldAngularVelocity,
            Rotation targetOrientation,Rotation controlFrameWorld,Vec accelerationAuthority,
            double maximumRateRadiansPerSecond,double responseSeconds)
        {return AttitudeHold(currentOrientation,worldAngularVelocity,targetOrientation,controlFrameWorld,
            accelerationAuthority,accelerationAuthority,maximumRateRadiansPerSecond,responseSeconds);}

        public static OwnedFlightGuidance AttitudeHold(Rotation currentOrientation,Vec worldAngularVelocity,
            Rotation targetOrientation,Rotation controlFrameWorld,Vec positiveAccelerationAuthority,Vec negativeAccelerationAuthority,
            double maximumRateRadiansPerSecond,double responseSeconds)
        {
            RigidAssemblyProperties.RequireRotation(currentOrientation);RigidAssemblyProperties.RequireRotation(targetOrientation);
            RigidAssemblyProperties.RequireRotation(controlFrameWorld);OwnedRuntimeDynamics.Require(worldAngularVelocity);
            Authority(positiveAccelerationAuthority);Authority(negativeAccelerationAuthority);
            AssemblyModel.Positive(maximumRateRadiansPerSecond);AssemblyModel.Positive(responseSeconds);
            Rotation difference=targetOrientation*currentOrientation.Inverse;
            double sign=difference.W<0?-1:1;
            Vec axis=new Vec(difference.X*sign,difference.Y*sign,difference.Z*sign);
            double length=Math.Sqrt(OwnedRuntimeDynamics.Dot(axis,axis));
            double angle=2*Math.Atan2(length,difference.W*sign);
            Vec desired=length==0?new Vec():axis*(Math.Min(maximumRateRadiansPerSecond,angle/responseSeconds)/length);
            Vec demand=controlFrameWorld.Inverse.Rotate((desired+worldAngularVelocity*-1)*(1/responseSeconds));
            var result=new OwnedFlightGuidance {AngleErrorRadians=angle};
            Vec authority=new Vec(demand.X<0?negativeAccelerationAuthority.X:positiveAccelerationAuthority.X,
                demand.Y<0?negativeAccelerationAuthority.Y:positiveAccelerationAuthority.Y,
                demand.Z<0?negativeAccelerationAuthority.Z:positiveAccelerationAuthority.Z);
            Vec bounded=Bound(demand,authority,ref result);
            result.Input=new OwnedRuntimeInput(0,bounded.X,bounded.Y,bounded.Z);return result;
        }

        public static OwnedFlightGuidance VelocityHold(Vec currentWorldVelocity,Vec targetWorldVelocity,
            Rotation controlFrameWorld,Vec accelerationAuthority,double responseSeconds)
        {
            OwnedRuntimeDynamics.Require(currentWorldVelocity);OwnedRuntimeDynamics.Require(targetWorldVelocity);
            RigidAssemblyProperties.RequireRotation(controlFrameWorld);Authority(accelerationAuthority);AssemblyModel.Positive(responseSeconds);
            Vec error=targetWorldVelocity+currentWorldVelocity*-1;
            Vec demand=controlFrameWorld.Inverse.Rotate(error*(1/responseSeconds));
            var result=new OwnedFlightGuidance {VelocityErrorMetersPerSecond=Math.Sqrt(OwnedRuntimeDynamics.Dot(error,error))};
            Vec bounded=Bound(demand,accelerationAuthority,ref result);
            result.Input=new OwnedRuntimeInput(0,0,0,0,bounded.X,bounded.Y,bounded.Z);return result;
        }

        public static OwnedRuntimeControls Controls(OwnedRuntimeInput input,bool engines=false,bool rcs=true,bool wheels=true)
        {
            input.Validate();return new OwnedRuntimeControls {Throttle=input.Throttle,Attitude=new Vec(input.Pitch,input.Yaw,input.Roll),
                Translation=new Vec(input.X,input.Y,input.Z),EnginesEnabled=engines,RcsEnabled=rcs,WheelsEnabled=wheels};
        }

        static Vec Bound(Vec demand,Vec authority,ref OwnedFlightGuidance result)
        {
            OwnedRuntimeDynamics.Require(demand);
            return new Vec(Axis(demand.X,authority.X,ref result),Axis(demand.Y,authority.Y,ref result),Axis(demand.Z,authority.Z,ref result));
        }
        static double Axis(double demand,double authority,ref OwnedFlightGuidance result)
        {
            if(authority==0){if(demand!=0)result.UnavailableAxes++;return 0;}
            double value=demand/authority;
            if(value>1){result.SaturatedAxes++;return 1;}
            if(value< -1){result.SaturatedAxes++;return -1;}
            return value;
        }
        static void Authority(Vec value)
        {
            OwnedRuntimeDynamics.Require(value);
            if(value.X<0||value.Y<0||value.Z<0)throw new ArgumentException("Control acceleration authority cannot be negative.");
        }
    }
}
