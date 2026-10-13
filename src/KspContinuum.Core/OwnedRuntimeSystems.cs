using System;
using System.Diagnostics;

namespace KspContinuum
{
    public enum OwnedRuntimeActuatorKind { Engine, Rcs, ReactionWheel }

    public sealed class OwnedRuntimeActuatorSpec
    {
        public readonly int Part, FuelResource, ElectricResource;
        public readonly OwnedRuntimeActuatorKind Kind;
        public readonly Vec LocalPoint, LocalAxis;
        public readonly double MaximumForceOrTorque, SpecificImpulseSeconds, ElectricUnitsPerSecond, HeatWatts, WheelMomentumCapacity;
        readonly string[] fuelKinds;
        readonly double[] massFractions;
        public OwnedRuntimeActuatorSpec(int member, OwnedRuntimeActuatorKind kind, Vec localPoint, Vec localAxis,
            double maximumForceOrTorque, int fuelResource=-1, double specificImpulseSeconds=0,
            int electricResource=-1, double electricUnitsPerSecond=0, double heatWatts=0, double wheelMomentumCapacity=100)
        {
            if(member<0 || fuelResource< -1 || electricResource< -1 || !Enum.IsDefined(typeof(OwnedRuntimeActuatorKind),kind))
                throw new ArgumentException("Invalid actuator slot or kind.");
            OwnedRuntimeDynamics.Require(localPoint); OwnedRuntimeDynamics.Require(localAxis);
            double length=Math.Sqrt(OwnedRuntimeDynamics.Dot(localAxis,localAxis)); AssemblyModel.Positive(length);
            AssemblyModel.Positive(maximumForceOrTorque); OwnedRuntimeEnvironment.Nonnegative(electricUnitsPerSecond);
            OwnedRuntimeEnvironment.Nonnegative(heatWatts); OwnedRuntimeEnvironment.Nonnegative(specificImpulseSeconds);
            AssemblyModel.Positive(wheelMomentumCapacity);
            if(kind!=OwnedRuntimeActuatorKind.ReactionWheel && (fuelResource<0 || specificImpulseSeconds<=0))
                throw new ArgumentException("Propulsive actuators require fuel and positive specific impulse.");
            if(kind==OwnedRuntimeActuatorKind.ReactionWheel && fuelResource!=-1)
                throw new ArgumentException("Reaction wheel cannot consume propellant.");
            if(electricUnitsPerSecond>0 && electricResource<0)throw new ArgumentException("Electrical rate requires a resource.");
            if(fuelResource>=0 && fuelResource==electricResource)throw new ArgumentException("Fuel and electric resource slots must differ.");
            Part=member; Kind=kind; LocalPoint=localPoint; LocalAxis=localAxis*(1/length);
            MaximumForceOrTorque=maximumForceOrTorque; FuelResource=fuelResource; SpecificImpulseSeconds=specificImpulseSeconds;
            ElectricResource=electricResource; ElectricUnitsPerSecond=electricUnitsPerSecond; HeatWatts=heatWatts;
            WheelMomentumCapacity=wheelMomentumCapacity;
            fuelKinds=new string[fuelResource<0?0:1];massFractions=new double[fuelKinds.Length];
            if(massFractions.Length!=0)massFractions[0]=1;
        }
        public OwnedRuntimeActuatorSpec(int part, OwnedRuntimeActuatorKind kind, Vec point, Vec axis,
            double maximumForce, string[] kinds, double[] fractions, double isp,
            int electricResource=-1, double electricRate=0, double heatWatts=0)
            : this(part,kind,point,axis,maximumForce,kind==OwnedRuntimeActuatorKind.ReactionWheel?-1:(electricResource==0?1:0),isp,
                electricResource,electricRate,heatWatts)
        {
            if(kind==OwnedRuntimeActuatorKind.ReactionWheel || kinds==null || fractions==null || kinds.Length==0 || kinds.Length!=fractions.Length)
                throw new ArgumentException("Propellant mixture is required.");
            double sum=0;
            for(int i=0;i<kinds.Length;i++)
            {
                if(string.IsNullOrWhiteSpace(kinds[i]))throw new ArgumentException("Fuel kind is required.");
                AssemblyModel.Positive(fractions[i]);sum+=fractions[i];
                for(int j=0;j<i;j++)if(kinds[j]==kinds[i])throw new ArgumentException("Duplicate fuel kind.");
            }
            if(Math.Abs(sum-1)>1e-12)throw new ArgumentException("Propellant mass fractions must sum to one.");
            FuelResource=-1;ElectricResource=electricResource;
            if(electricRate>0 && electricResource<0)throw new ArgumentException("Electrical rate requires a resource.");
            fuelKinds=(string[])kinds.Clone();massFractions=(double[])fractions.Clone();
        }
        public int FuelCount {get{return massFractions.Length;}}
        public string FuelKind(int index){return fuelKinds[index];}
        public double FuelMassFraction(int index){return massFractions[index];}
        OwnedRuntimeActuatorSpec(OwnedRuntimeActuatorSpec source,Vec retainedAxis)
        {
            RequireUnit(retainedAxis);
            Part=source.Part;FuelResource=source.FuelResource;ElectricResource=source.ElectricResource;Kind=source.Kind;
            LocalPoint=source.LocalPoint;LocalAxis=retainedAxis;MaximumForceOrTorque=source.MaximumForceOrTorque;
            SpecificImpulseSeconds=source.SpecificImpulseSeconds;ElectricUnitsPerSecond=source.ElectricUnitsPerSecond;
            HeatWatts=source.HeatWatts;WheelMomentumCapacity=source.WheelMomentumCapacity;
            fuelKinds=source.fuelKinds;massFractions=source.massFractions;
        }
        internal static void RequireUnit(Vec axis)
        {
            OwnedRuntimeDynamics.Require(axis);double squared=OwnedRuntimeDynamics.Dot(axis,axis);AssemblyModel.Finite(squared);
            if(Math.Abs(squared-1)>1e-12)throw new ArgumentException("Retained axis must already be unit length.");
        }
        internal static OwnedRuntimeActuatorSpec RestoreUnitAxis(int part,OwnedRuntimeActuatorKind kind,Vec point,Vec axis,
            double maximum,int fuelResource,double isp,int electricResource,double electricRate,double heatWatts,double wheelCapacity)
        {
            RequireUnit(axis);
            return new OwnedRuntimeActuatorSpec(new OwnedRuntimeActuatorSpec(part,kind,point,axis,maximum,fuelResource,isp,
                electricResource,electricRate,heatWatts,wheelCapacity),axis);
        }
        internal static OwnedRuntimeActuatorSpec RestoreUnitAxis(int part,OwnedRuntimeActuatorKind kind,Vec point,Vec axis,
            double maximum,string[] kinds,double[] fractions,double isp,int electricResource,double electricRate,double heatWatts)
        {
            RequireUnit(axis);
            return new OwnedRuntimeActuatorSpec(new OwnedRuntimeActuatorSpec(part,kind,point,axis,maximum,kinds,fractions,isp,
                electricResource,electricRate,heatWatts),axis);
        }
    }

    public sealed class OwnedRuntimeSurfaceSpec
    {
        public readonly int Part;
        public readonly Vec LocalPoint, LocalNormal;
        public readonly double DragArea, LiftArea, ThermalArea, Emissivity, HeatingFraction;
        public OwnedRuntimeSurfaceSpec(int member, Vec localPoint, Vec localNormal, double dragArea,
            double liftArea, double thermalArea, double emissivity, double heatingFraction=0)
        {
            if(member<0)throw new ArgumentOutOfRangeException("member");
            OwnedRuntimeDynamics.Require(localPoint); OwnedRuntimeDynamics.Require(localNormal);
            double length=Math.Sqrt(OwnedRuntimeDynamics.Dot(localNormal,localNormal));AssemblyModel.Positive(length);
            OwnedRuntimeEnvironment.Nonnegative(dragArea);OwnedRuntimeEnvironment.Nonnegative(liftArea);
            OwnedRuntimeEnvironment.Nonnegative(thermalArea);OwnedRuntimeEnvironment.Nonnegative(emissivity);
            if(emissivity>1)throw new ArgumentOutOfRangeException("emissivity");
            OwnedRuntimeEnvironment.Nonnegative(heatingFraction);if(heatingFraction>1)throw new ArgumentOutOfRangeException("heatingFraction");
            Part=member;LocalPoint=localPoint;LocalNormal=localNormal*(1/length);
            DragArea=dragArea;LiftArea=liftArea;ThermalArea=thermalArea;Emissivity=emissivity;
            HeatingFraction=heatingFraction;
        }
        OwnedRuntimeSurfaceSpec(OwnedRuntimeSurfaceSpec source,Vec retainedNormal)
        {
            OwnedRuntimeActuatorSpec.RequireUnit(retainedNormal);Part=source.Part;LocalPoint=source.LocalPoint;LocalNormal=retainedNormal;
            DragArea=source.DragArea;LiftArea=source.LiftArea;ThermalArea=source.ThermalArea;
            Emissivity=source.Emissivity;HeatingFraction=source.HeatingFraction;
        }
        internal static OwnedRuntimeSurfaceSpec RestoreUnitNormal(int part,Vec point,Vec normal,double dragArea,
            double liftArea,double thermalArea,double emissivity,double heatingFraction)
        {
            OwnedRuntimeActuatorSpec.RequireUnit(normal);
            return new OwnedRuntimeSurfaceSpec(new OwnedRuntimeSurfaceSpec(part,point,normal,dragArea,liftArea,thermalArea,emissivity,heatingFraction),normal);
        }
    }

    public sealed class OwnedRuntimeThermalLink
    {
        public readonly int A,B;
        public readonly double ConductanceWattsPerKelvin;
        public OwnedRuntimeThermalLink(int a,int b,double conductance)
        {
            if(a<0||b<0||a==b)throw new ArgumentException("Thermal link requires distinct members.");
            OwnedRuntimeEnvironment.Nonnegative(conductance);A=a;B=b;ConductanceWattsPerKelvin=conductance;
        }
    }

    public struct OwnedRuntimeControls
    {
        public double Throttle;
        public Vec Translation, Attitude;
        public bool EnginesEnabled, RcsEnabled, WheelsEnabled;
        public void Validate()
        {
            AssemblyModel.Finite(Throttle);
            if (Throttle < 0 || Throttle > 1) throw new ArgumentOutOfRangeException("Throttle");
            ValidateAxis(Translation); ValidateAxis(Attitude);
        }
        static void ValidateAxis(Vec v)
        {
            OwnedRuntimeDynamics.Require(v);
            if (Math.Abs(v.X)>1 || Math.Abs(v.Y)>1 || Math.Abs(v.Z)>1)
                throw new ArgumentOutOfRangeException("controls");
        }
    }

    public sealed class OwnedRuntimeEnvironment
    {
        public readonly string SourceId;
        public readonly long Revision;
        public readonly double Epoch, Density, Temperature, ConvectiveCoefficient, RadiationTemperature;
        public readonly Vec WindVelocity, Gravity;
        public OwnedRuntimeEnvironment(string sourceId, long revision, double epoch, double density,
            double temperature, Vec windVelocity, Vec gravity, double convectiveCoefficient = 0,
            double radiationTemperature = 3)
        {
            if (string.IsNullOrWhiteSpace(sourceId) || revision < 0) throw new ArgumentException("Environment identity is required.");
            AssemblyModel.Finite(epoch); Nonnegative(density); AssemblyModel.Positive(temperature);
            Nonnegative(convectiveCoefficient); AssemblyModel.Positive(radiationTemperature);
            OwnedRuntimeDynamics.Require(windVelocity); OwnedRuntimeDynamics.Require(gravity);
            SourceId=sourceId; Revision=revision; Epoch=epoch; Density=density; Temperature=temperature;
            WindVelocity=windVelocity; Gravity=gravity; ConvectiveCoefficient=convectiveCoefficient;
            RadiationTemperature=radiationTemperature;
        }
        internal static void Nonnegative(double x)
        { AssemblyModel.Finite(x); if (x<0) throw new ArgumentOutOfRangeException("value"); }
    }

    public sealed class OwnedRuntimeStepPlan
    {
        public readonly double FixedDelta, WarpRate;
        public OwnedRuntimeStepPlan(double fixedDelta, double warpRate=1)
        {
            AssemblyModel.Positive(fixedDelta); AssemblyModel.Positive(warpRate);
            if (fixedDelta*warpRate>.1) throw new ArgumentOutOfRangeException("warpRate", "Physical step must not exceed 0.1 seconds.");
            FixedDelta=fixedDelta; WarpRate=warpRate;
        }
        public double Delta { get { return FixedDelta*WarpRate; } }
    }

    public sealed class OwnedRuntimeSystemCounters
    {
        public long Steps, FailedSteps, ActuatorRows, SurfaceRows, ThermalLinks, RegionIntegrations, ObservedTicks;
        public double DeliveredForceNewtonSeconds, DeliveredTorqueNewtonMeterSeconds, PropellantKilograms,
            ElectricUnits, ActuatorHeatJoules;
        public readonly long ClockFrequency=Stopwatch.Frequency;
    }

    public struct OwnedRuntimeExternalForce
    {
        public readonly int Part;
        public readonly Vec WorldPoint, WorldForce, WorldTorque;
        public OwnedRuntimeExternalForce(int part,Vec worldPoint,Vec worldForce,Vec worldTorque)
        {
            if(part<0)throw new ArgumentOutOfRangeException("part");
            OwnedRuntimeDynamics.Require(worldPoint);OwnedRuntimeDynamics.Require(worldForce);OwnedRuntimeDynamics.Require(worldTorque);
            Part=part;WorldPoint=worldPoint;WorldForce=worldForce;WorldTorque=worldTorque;
        }
    }

    public sealed class OwnedRuntimeSystems
    {
        public const int MaximumRows=1048576;
        const double StandardGravity=9.80665, StefanBoltzmann=5.670374419e-8;
        readonly OwnedRuntimeWorld world;
        readonly OwnedRuntimeDefinition definition;
        readonly OwnedRuntimeActuatorSpec[] actuators;
        readonly OwnedRuntimeSurfaceSpec[] surfaces;
        readonly OwnedRuntimeThermalLink[] links;
        readonly bool[] enabled, flowEnabled;
        readonly int[][][] fuelPools;
        readonly int[] resourceParts,partBodies;
        readonly int[] wheelProviderSlots;
        readonly Vec[] forces, torques, oldCenters, oldWheel, newWheel;
        readonly double[] heat, wheelMomentum, stagedWheelMomentum;
        readonly bool observeClock;
        public readonly Rotation ControlOrientation;
        long configurationRevision;
        readonly object configurationGate=new object();
        bool executing;
        int executionThread;
        public readonly OwnedRuntimeSystemCounters Counters=new OwnedRuntimeSystemCounters();

        public OwnedRuntimeSystems(OwnedRuntimeWorld world, OwnedRuntimeActuatorSpec[] actuators,
            OwnedRuntimeSurfaceSpec[] surfaces, OwnedRuntimeThermalLink[] thermalLinks=null,
            bool[] resourceFlowEnabled=null, bool observeClock=false, int[] wheelProviderSlots=null, Rotation? controlOrientation=null)
        {
            if(world==null || actuators==null || surfaces==null)throw new ArgumentNullException("systems");
            if(actuators.Length>MaximumRows || surfaces.Length>MaximumRows || (thermalLinks!=null && thermalLinks.Length>MaximumRows))
                throw new ArgumentException("System plan exceeds bounded row capacity.");
            this.world=world;definition=world.Definition;
            ControlOrientation=controlOrientation??Rotation.Identity;RigidAssemblyProperties.RequireRotation(ControlOrientation);
            partBodies=new int[definition.PartCount];
            for(int p=0;p<partBodies.Length;p++)partBodies[p]=definition.BodySlot(definition.GetPart(p).BodyId);
            this.actuators=(OwnedRuntimeActuatorSpec[])actuators.Clone();this.surfaces=(OwnedRuntimeSurfaceSpec[])surfaces.Clone();
            links=thermalLinks==null?new OwnedRuntimeThermalLink[0]:(OwnedRuntimeThermalLink[])thermalLinks.Clone();
            enabled=new bool[actuators.Length];for(int i=0;i<enabled.Length;i++)enabled[i]=true;
            if(wheelProviderSlots!=null && wheelProviderSlots.Length!=actuators.Length)throw new ArgumentException("Wheel provider cardinality differs.");
            this.wheelProviderSlots=new int[actuators.Length];
            for(int i=0;i<actuators.Length;i++)this.wheelProviderSlots[i]=wheelProviderSlots==null?-1:wheelProviderSlots[i];
            flowEnabled=new bool[definition.ResourceCount];
            if(resourceFlowEnabled!=null && resourceFlowEnabled.Length!=flowEnabled.Length)throw new ArgumentException("Flow-state cardinality differs.");
            for(int i=0;i<flowEnabled.Length;i++)flowEnabled[i]=resourceFlowEnabled==null || resourceFlowEnabled[i];
            resourceParts=new int[flowEnabled.Length];
            for(int i=0;i<resourceParts.Length;i++)resourceParts[i]=definition.PartSlot(definition.GetResource(i).PartId);
            fuelPools=new int[actuators.Length][][];
            for(int i=0;i<actuators.Length;i++)
            {
                var a=this.actuators[i];if(a==null)throw new ArgumentException("Null actuator.");Part(a.Part);
                int provider=this.wheelProviderSlots[i];
                if(a.Kind==OwnedRuntimeActuatorKind.ReactionWheel)
                {
                    if(provider<0 || provider>=definition.ProviderStateCount)throw new ArgumentException("Wheel requires a captured provider-state slot.");
                    for(int j=0;j<i;j++)if(this.wheelProviderSlots[j]==provider)throw new ArgumentException("Wheels require unique momentum reservoirs.");
                }
                else if(provider!=-1)throw new ArgumentException("Nonwheel cannot claim a momentum reservoir.");
                if(a.ElectricResource>=definition.ResourceCount)throw new ArgumentException("Invalid electric resource.");
                fuelPools[i]=new int[a.FuelCount][];
                for(int f=0;f<a.FuelCount;f++)
                {
                    if(a.FuelResource>=0)
                    {
                        if(a.FuelResource>=definition.ResourceCount || definition.GetResource(a.FuelResource).MassPerUnit<=0)
                            throw new ArgumentException("Fuel must have positive declared mass.");
                        fuelPools[i][f]=new[]{a.FuelResource};
                    }
                    else
                    {
                        int count=0;
                        for(int r=0;r<definition.ResourceCount;r++)if(definition.GetResource(r).Kind==a.FuelKind(f))count++;
                        if(count==0)throw new ArgumentException("Fuel kind has no recorded resource pool: "+a.FuelKind(f));
                        var pool=new int[count];count=0;
                        for(int r=0;r<definition.ResourceCount;r++)if(definition.GetResource(r).Kind==a.FuelKind(f))
                        {
                            if(definition.GetResource(r).MassPerUnit<=0)throw new ArgumentException("Propellant density must be positive.");
                            if(r==a.ElectricResource)throw new ArgumentException("Fuel and electrical pools overlap.");
                            pool[count++]=r;
                        }
                        fuelPools[i][f]=pool;
                    }
                }
            }
            foreach(var s in this.surfaces){if(s==null)throw new ArgumentException("Null surface.");Part(s.Part);}
            foreach(var link in links){if(link==null)throw new ArgumentException("Null thermal link.");Part(link.A);Part(link.B);}
            forces=new Vec[definition.BodyCount];torques=new Vec[definition.BodyCount];oldCenters=new Vec[definition.BodyCount];
            oldWheel=new Vec[definition.BodyCount];newWheel=new Vec[definition.BodyCount];
            heat=new double[definition.PartCount];wheelMomentum=new double[actuators.Length];stagedWheelMomentum=new double[actuators.Length];
            this.observeClock=observeClock;
        }

        public int ActuatorCount{get{return actuators.Length;}}
        public int SurfaceCount{get{return surfaces.Length;}}
        public int ThermalLinkCount{get{return links.Length;}}
        public long ConfigurationRevision{get{return configurationRevision;}}
        public OwnedRuntimeActuatorSpec GetActuator(int index){return actuators[index];}
        public OwnedRuntimeSurfaceSpec GetSurface(int index){return surfaces[index];}
        public OwnedRuntimeThermalLink GetThermalLink(int index){return links[index];}
        public bool IsEnabled(int index){return enabled[index];}
        public bool IsResourceFlowEnabled(int index){return flowEnabled[index];}
        public int WheelProviderSlot(int index){return wheelProviderSlots[index];}
        public double GetWheelMomentum(OwnedRuntimeStep step,int index)
        {world.CheckStep(step);if(!ReferenceEquals(step.World,world))throw new ArgumentException("Foreign step.");return step.Bank.ProviderState[wheelProviderSlots[index]];}
        public void SetWheelMomentum(OwnedRuntimeStep step,int index,double value)
        {
            AssemblyModel.Finite(value);if(actuators[index].Kind!=OwnedRuntimeActuatorKind.ReactionWheel || Math.Abs(value)>actuators[index].WheelMomentumCapacity)
                throw new ArgumentException("Wheel momentum exceeds the declared reservoir.");
            if(!ReferenceEquals(step.World,world))throw new ArgumentException("Foreign step.");
            world.CheckStep(step);
            step.Bank.ProviderState[wheelProviderSlots[index]]=value;
        }
        public void SetEnabled(int index,bool value)
        {lock(configurationGate){RequireConfigurationIdle();if(enabled[index]!=value){long next=checked(configurationRevision+1);enabled[index]=value;configurationRevision=next;}}}
        public void SetResourceFlowEnabled(int index,bool value)
        {lock(configurationGate){RequireConfigurationIdle();if(flowEnabled[index]!=value){long next=checked(configurationRevision+1);flowEnabled[index]=value;configurationRevision=next;}}}
        internal void RestoreConfigurationRevision(long value)
        {lock(configurationGate){RequireConfigurationIdle();if(value<0)throw new ArgumentOutOfRangeException("value");configurationRevision=value;}}
        void RequireConfigurationIdle()
        {if(executing)throw new InvalidOperationException("Cold system configuration cannot change during schedule execution.");}
        internal void EnterExecution()
        {lock(configurationGate){RequireConfigurationIdle();executing=true;executionThread=System.Threading.Thread.CurrentThread.ManagedThreadId;}}
        internal void ExitExecution()
        {
            lock(configurationGate)
            {
                if(!executing||executionThread!=System.Threading.Thread.CurrentThread.ManagedThreadId)
                    throw new InvalidOperationException("System execution scope is not owned by this thread.");
                executing=false;executionThread=0;
            }
        }

        public void Step(OwnedRuntimeStep step, OwnedRuntimeEnvironment environment)
        {
            world.CheckStep(step);var input=step.Bank.Input;
            Step(step,environment,new OwnedRuntimeControls {Throttle=input.Throttle,Attitude=new Vec(input.Pitch,input.Yaw,input.Roll),
                Translation=new Vec(input.X,input.Y,input.Z),EnginesEnabled=true,RcsEnabled=true,WheelsEnabled=true});
        }

        public void Step(OwnedRuntimeStep step,OwnedRuntimeEnvironment environment,OwnedRuntimeControls controls)
        {Step(step,environment,controls,null);}

        public void Step(OwnedRuntimeStep step, OwnedRuntimeEnvironment environment, OwnedRuntimeControls controls,
            OwnedRuntimeExternalForce[] externalForces)
        {
            if(environment==null)throw new ArgumentNullException("environment");world.CheckStep(step);
            controls.Validate();var input=step.Bank.Input;
            if(controls.Throttle!=input.Throttle || controls.Attitude.X!=input.Pitch || controls.Attitude.Y!=input.Yaw
                || controls.Attitude.Z!=input.Roll || controls.Translation.X!=input.X || controls.Translation.Y!=input.Y || controls.Translation.Z!=input.Z)
                throw new InvalidOperationException("Controls must match the transaction's recorded input.");
            if(!ReferenceEquals(step.World,world) || !ReferenceEquals(step.Bank.Definition,definition))
                throw new InvalidOperationException("System plan belongs to a different definition/world.");
            if(externalForces!=null)for(int i=0;i<externalForces.Length;i++)
            {Part(externalForces[i].Part);OwnedRuntimeDynamics.Require(externalForces[i].WorldPoint);OwnedRuntimeDynamics.Require(externalForces[i].WorldForce);OwnedRuntimeDynamics.Require(externalForces[i].WorldTorque);}
            if(environment.Epoch>step.ExpectedStamp.Time)throw new InvalidOperationException("Environment observation is from the future.");
            double dt=step.DeltaTime;if(dt>.1)throw new ArgumentOutOfRangeException("dt", "Physical model step exceeds 0.1 seconds.");
            step.ClaimSystemsExecution();
            long begin=observeClock?Stopwatch.GetTimestamp():0;
            try
            {
                var bank=step.Bank;int regions=bank.RegionMotion.Length;
                Array.Clear(forces,0,regions);Array.Clear(torques,0,regions);Array.Clear(heat,0,heat.Length);
                Array.Clear(oldWheel,0,regions);Array.Clear(newWheel,0,regions);
                for(int i=0;i<actuators.Length;i++)
                {
                    wheelMomentum[i]=wheelProviderSlots[i]<0?0:bank.ProviderState[wheelProviderSlots[i]];
                    AssemblyModel.Finite(wheelMomentum[i]);
                    if(Math.Abs(wheelMomentum[i])>actuators[i].WheelMomentumCapacity)throw new InvalidOperationException("Wheel momentum exceeds admitted capacity.");
                    stagedWheelMomentum[i]=wheelMomentum[i];
                }
                for(int r=0;r<regions;r++)oldCenters[r]=bank.Properties[r].CenterOfMass;
                Actuate(step,controls);
                step.RefreshMassProperties();
                for(int r=0;r<regions;r++)
                {
                    Vec displacement=bank.RegionMotion[r].Orientation.Rotate(oldCenters[r]+bank.Properties[r].CenterOfMass*-1);
                    torques[r]=torques[r]+Vec.Cross(displacement,forces[r]);
                    forces[r]=forces[r]+environment.Gravity*bank.Properties[r].Mass;
                }
                Aerothermal(step,environment);
                if(externalForces!=null)for(int i=0;i<externalForces.Length;i++)
                {
                    var command=externalForces[i];int region=bank.Topology.PartRegions[command.Part];
                    AddForce(bank,region,command.WorldPoint,command.WorldForce);torques[region]=torques[region]+command.WorldTorque;
                }
                Thermal(step,environment);
                for(int r=0;r<regions;r++)
                {
                    OwnedRuntimeDynamics.StepCoupled(ref bank.RegionMotion[r],bank.Properties[r],forces[r],torques[r],oldWheel[r],newWheel[r],dt);
                    Counters.RegionIntegrations++;
                }
                Counters.Steps++;
                for(int i=0;i<actuators.Length;i++)if(wheelProviderSlots[i]>=0)bank.ProviderState[wheelProviderSlots[i]]=stagedWheelMomentum[i];
            }
            catch{Counters.FailedSteps++;step.Dispose();throw;}
            finally{if(observeClock)Counters.ObservedTicks+=Stopwatch.GetTimestamp()-begin;}
        }

        void Actuate(OwnedRuntimeStep step,OwnedRuntimeControls controls)
        {
            var bank=step.Bank;var input=bank.Input;double dt=step.DeltaTime;
            Vec translation=ControlOrientation.Rotate(new Vec(input.X,input.Y,input.Z)),
                attitude=ControlOrientation.Rotate(new Vec(input.Pitch,input.Yaw,input.Roll));
            for(int i=0;i<actuators.Length;i++)
            {
                Counters.ActuatorRows++;var a=actuators[i];
                int body=partBodies[a.Part],region=bank.Topology.BodyRegions[body];
                Rotation localRotation=bank.Members[body].LocalRotation*definition.GetPart(a.Part).LocalRotation;
                Vec regionAxis=localRotation.Rotate(a.LocalAxis);
                if(a.Kind==OwnedRuntimeActuatorKind.ReactionWheel)
                    oldWheel[region]=oldWheel[region]+regionAxis*wheelMomentum[i];
                bool allowed=enabled[i] && (a.Kind==OwnedRuntimeActuatorKind.Engine?controls.EnginesEnabled:
                    a.Kind==OwnedRuntimeActuatorKind.Rcs?controls.RcsEnabled:controls.WheelsEnabled);
                if(!allowed){if(a.Kind==OwnedRuntimeActuatorKind.ReactionWheel)newWheel[region]=newWheel[region]+regionAxis*wheelMomentum[i];continue;}
                double request=a.Kind==OwnedRuntimeActuatorKind.Engine?input.Throttle:
                    a.Kind==OwnedRuntimeActuatorKind.Rcs?Math.Max(0,OwnedRuntimeDynamics.Dot(regionAxis,translation)):
                    OwnedRuntimeDynamics.Dot(regionAxis,attitude);
                request=Math.Max(-1,Math.Min(1,request));
                if(request==0){if(a.Kind==OwnedRuntimeActuatorKind.ReactionWheel)newWheel[region]=newWheel[region]+regionAxis*wheelMomentum[i];continue;}
                double magnitude=Math.Abs(request),fuelMass=a.Kind==OwnedRuntimeActuatorKind.ReactionWheel?0:
                    a.MaximumForceOrTorque*magnitude*dt/(a.SpecificImpulseSeconds*StandardGravity);
                double electric=a.ElectricUnitsPerSecond*magnitude*dt,fraction=1;
                AssemblyModel.Finite(fuelMass);AssemblyModel.Finite(electric);
                if(a.Kind==OwnedRuntimeActuatorKind.ReactionWheel)
                {
                    double headroom=request>0?wheelMomentum[i]+a.WheelMomentumCapacity:a.WheelMomentumCapacity-wheelMomentum[i];
                    fraction=Math.Min(fraction,headroom/(a.MaximumForceOrTorque*magnitude*dt));
                }
                for(int f=0;f<a.FuelCount;f++)
                {
                    double demand=fuelMass*a.FuelMassFraction(f),available=Available(bank,fuelPools[i][f],region);
                    if(demand>0)fraction=Math.Min(fraction,available/demand);
                }
                if(electric>0)
                {
                    int e=a.ElectricResource;
                    double available=flowEnabled[e] && bank.Topology.PartRegions[resourceParts[e]]==region?bank.ResourceQuantity[e]:0;
                    fraction=Math.Min(fraction,available/electric);
                }
                fraction=Math.Max(0,Math.Min(1,fraction));
                if(fraction==0){if(a.Kind==OwnedRuntimeActuatorKind.ReactionWheel)newWheel[region]=newWheel[region]+regionAxis*wheelMomentum[i];continue;}
                for(int f=0;f<a.FuelCount;f++)Withdraw(bank,fuelPools[i][f],region,fuelMass*a.FuelMassFraction(f)*fraction);
                if(electric>0)bank.ResourceQuantity[a.ElectricResource]=Math.Max(0,bank.ResourceQuantity[a.ElectricResource]-electric*fraction);
                double delivered=a.MaximumForceOrTorque*request*fraction;
                Vec axis=bank.RegionMotion[region].Orientation.Rotate(regionAxis);
                if(a.Kind==OwnedRuntimeActuatorKind.ReactionWheel)
                {
                    stagedWheelMomentum[i]=Math.Max(-a.WheelMomentumCapacity,Math.Min(a.WheelMomentumCapacity,wheelMomentum[i]-delivered*dt));
                    newWheel[region]=newWheel[region]+regionAxis*stagedWheelMomentum[i];
                    Counters.DeliveredTorqueNewtonMeterSeconds+=Math.Abs(delivered)*dt;
                }
                else
                {
                    Vec point=LocalPoint(bank,a.Part,a.LocalPoint,body);
                    AddLocalForce(bank,region,point,axis*delivered);
                    Counters.DeliveredForceNewtonSeconds+=Math.Abs(delivered)*dt;
                }
                double energy=a.HeatWatts*magnitude*fraction*dt;heat[a.Part]+=energy;
                Counters.ActuatorHeatJoules+=energy;Counters.PropellantKilograms+=fuelMass*fraction;Counters.ElectricUnits+=electric*fraction;
            }
        }

        double Available(OwnedRuntimeBank bank,int[] pool,int region)
        {
            double sum=0;for(int i=0;i<pool.Length;i++){int r=pool[i];if(flowEnabled[r] && bank.Topology.PartRegions[resourceParts[r]]==region)
                sum+=bank.ResourceQuantity[r]*definition.GetResource(r).MassPerUnit;}AssemblyModel.Finite(sum);return sum;
        }
        void Withdraw(OwnedRuntimeBank bank,int[] pool,int region,double mass)
        {
            for(int i=0;i<pool.Length && mass>0;i++)
            {
                int r=pool[i];if(!flowEnabled[r] || bank.Topology.PartRegions[resourceParts[r]]!=region)continue;
                double density=definition.GetResource(r).MassPerUnit;
                double take=Math.Min(bank.ResourceQuantity[r],mass/density);
                bank.ResourceQuantity[r]=Math.Max(0,bank.ResourceQuantity[r]-take);mass-=take*density;
            }
        }

        void Aerothermal(OwnedRuntimeStep step,OwnedRuntimeEnvironment environment)
        {
            var bank=step.Bank;
            for(int i=0;i<surfaces.Length;i++)
            {
                Counters.SurfaceRows++;var s=surfaces[i];int body=partBodies[s.Part],region=bank.Topology.BodyRegions[body];
                var motion=bank.RegionMotion[region];Vec point=LocalPoint(bank,s.Part,s.LocalPoint,body);
                Vec lever=motion.Orientation.Rotate(point+bank.Properties[region].CenterOfMass*-1);
                Vec omega=OwnedRuntimeDynamics.AngularVelocity(bank.Properties[region],motion.Orientation,motion.AngularMomentum);
                Vec relative=motion.Velocity+Vec.Cross(omega,lever)+environment.WindVelocity*-1;
                double speed2=OwnedRuntimeDynamics.Dot(relative,relative);if(speed2==0 || environment.Density==0)continue;
                double speed=Math.Sqrt(speed2),q=.5*environment.Density*speed2;
                Vec direction=relative*(1/speed);
                Rotation local=bank.Members[body].LocalRotation*definition.GetPart(s.Part).LocalRotation;
                Vec normal=motion.Orientation.Rotate(local.Rotate(s.LocalNormal));
                double incidence=OwnedRuntimeDynamics.Dot(normal,direction);
                Vec lift=(normal+direction*(-incidence))*(-2*incidence*q*s.LiftArea);
                AddLocalForce(bank,region,point,direction*(-q*s.DragArea)+lift);
                heat[s.Part]+=q*s.DragArea*speed*s.HeatingFraction*step.DeltaTime;
            }
        }

        void Thermal(OwnedRuntimeStep step,OwnedRuntimeEnvironment environment)
        {
            var bank=step.Bank;double dt=step.DeltaTime;
            for(int p=0;p<heat.Length;p++)bank.Temperature[p]+=heat[p]/definition.GetPart(p).HeatCapacity;
            for(int i=0;i<surfaces.Length;i++)
            {
                var s=surfaces[i];double t=bank.Temperature[s.Part],ambient=environment.RadiationTemperature;
                double radiative=StefanBoltzmann*s.Emissivity*s.ThermalArea*(t*t*t+t*t*ambient+t*ambient*ambient+ambient*ambient*ambient);
                double convective=environment.ConvectiveCoefficient*s.ThermalArea,capacity=definition.GetPart(s.Part).HeatCapacity;
                double denominator=capacity+dt*(radiative+convective);
                bank.Temperature[s.Part]=(capacity*t+dt*(radiative*ambient+convective*environment.Temperature))/denominator;
            }
            for(int i=0;i<links.Length;i++)
            {
                Counters.ThermalLinks++;var link=links[i];
                if(bank.Topology.PartRegions[link.A]!=bank.Topology.PartRegions[link.B])continue;
                double ca=definition.GetPart(link.A).HeatCapacity,cb=definition.GetPart(link.B).HeatCapacity;
                double difference=bank.Temperature[link.A]-bank.Temperature[link.B];
                double energy=difference*link.ConductanceWattsPerKelvin*dt/(1+link.ConductanceWattsPerKelvin*dt*(1/ca+1/cb));
                bank.Temperature[link.A]-=energy/ca;bank.Temperature[link.B]+=energy/cb;
            }
            for(int p=0;p<heat.Length;p++)AssemblyModel.Positive(bank.Temperature[p]);
        }

        Vec LocalPoint(OwnedRuntimeBank bank,int part,Vec point,int body)
        {
            var p=definition.GetPart(part);var member=bank.Members[body];
            Vec bodyPoint=p.LocalPosition+p.LocalRotation.Rotate(point);
            return member.LocalPosition+member.LocalRotation.Rotate(bodyPoint);
        }
        void AddLocalForce(OwnedRuntimeBank bank,int region,Vec point,Vec force)
        {
            Vec lever=bank.RegionMotion[region].Orientation.Rotate(point+bank.Properties[region].CenterOfMass*-1);
            forces[region]=forces[region]+force;torques[region]=torques[region]+Vec.Cross(lever,force);
        }
        void AddForce(OwnedRuntimeBank bank,int region,Vec point,Vec force)
        {
            var m=bank.RegionMotion[region];Vec center=m.RootPosition+m.Orientation.Rotate(bank.Properties[region].CenterOfMass);
            forces[region]=forces[region]+force;torques[region]=torques[region]+Vec.Cross(point+center*-1,force);
        }
        void Part(int slot){if(slot<0||slot>=definition.PartCount)throw new ArgumentOutOfRangeException("part");}
    }
}
