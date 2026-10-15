using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Security.Cryptography;

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

    [Serializable] public sealed class OwnedRuntimeSystemCounters
    {
        public long ExecutionPlanBuilds,CompiledActuatorRows,InactiveActuatorRows,CompiledSurfaceRows,VacuumSurfaceRows,RegionMotionContexts,RetainedGeometryRows;
        public long SkippedActuatorRows;
        public long ScalarTicks,AeroTicks,ThermalTicks,RegionIntegrationTicks,PlanTicks;
        public long BoundaryProbes,BoundaryResourceCopies,BoundaryResourceRowsCopied,BoundaryTicks;
        public long DeadlinePlanBuilds,DeadlinePlanHits,DeadlineLocalProbes,DeadlineFallbacks;
        public long DeadlineAdmissionFallbacks,DeadlineLowerBracketFallbacks,DeadlineUpperBracketFallbacks,DeadlineReplayFallbacks;
        public long Steps, FailedSteps, ActuatorRows, SurfaceRows, ThermalLinks, RegionIntegrations, ObservedTicks;
        public double DeliveredForceNewtonSeconds, DeliveredTorqueNewtonMeterSeconds, PropellantKilograms,
            ElectricUnits, ActuatorHeatJoules;
        public readonly long ClockFrequency=Stopwatch.Frequency;
        public OwnedRuntimeSystemCounters Snapshot(){return (OwnedRuntimeSystemCounters)MemberwiseClone();}
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
        readonly OwnedRuntimeEnvironmentSample[] environments;
        readonly OwnedRuntimeThermalLink[] links;
        readonly OwnedRuntimeThermalSolver thermalSolver;
        OwnedRuntimeThermalExecution thermalExecution;
        readonly string thermalPlanId;
        readonly bool[] enabled, flowEnabled;
        readonly int[][][] fuelPools;
        readonly HashSet<AvailabilityRoute> validatedAvailability=new HashSet<AvailabilityRoute>();
        struct AvailabilityRoute : IEquatable<AvailabilityRoute>
        {
            internal int[] Pool;internal int Region;
            public bool Equals(AvailabilityRoute other){return ReferenceEquals(Pool,other.Pool)&&Region==other.Region;}
            public override bool Equals(object value){return value is AvailabilityRoute&&Equals((AvailabilityRoute)value);}
            public override int GetHashCode(){return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Pool)^Region;}
        }
        readonly int[] resourceParts,partBodies;
        readonly int[] wheelProviderSlots;
        readonly int[] wheelActuatorRows;
        readonly Vec[] forces, torques, oldCenters, oldWheel, newWheel;
        readonly Vec[] regionOmega;
        OwnedRuntimeExecutionPlan executionPlan;
        OwnedRuntimeExecutionStrategy executionStrategy;
        readonly double[] heat, wheelMomentum, stagedWheelMomentum;
        bool observeClock;
        readonly Vec[] localActuatorForces,localActuatorTorques;
        bool stagingLocal;
        long mechanicalToken,mechanicalBegin,mechanicalConfiguration;
        OwnedRuntimeBank mechanicalBank;
        double mechanicalForceLedger,mechanicalTorqueLedger,mechanicalFuelLedger,mechanicalElectricLedger,mechanicalHeatLedger;
        long mechanicalStepsLedger,mechanicalIntegrationsLedger;
        bool mechanicalCompleted;
        OwnedRuntimeSupplyPipeline scalarSupply;
        OwnedRuntimeBank pendingSupplyBank;OwnedRuntimeSupplyObservation pendingSupply,lastSupply;OwnedRuntimeSupplyTotals supplyTotals;
        readonly double[] boundaryInitialResources;
        OwnedRuntimeBank boundaryBank;
        OwnedRuntimeDeadlinePlan deadlinePlan;
        OwnedRuntimeDeadlineStrategy deadlineStrategy;
        OwnedRuntimeActuatorBoundary lastActuatorBoundary;
        bool hasActuatorBoundary;
        int boundaryActuator=-1,boundaryResource=-1;
        string boundaryKind;
        public readonly Rotation ControlOrientation;
        internal bool IsBoundTo(OwnedRuntimeWorld candidate){return ReferenceEquals(world,candidate);}
        long configurationRevision;
        readonly object configurationGate=new object();
        bool executing;
        int executionThread;
        public readonly OwnedRuntimeSystemCounters Counters=new OwnedRuntimeSystemCounters();

        public OwnedRuntimeSystems(OwnedRuntimeWorld world, OwnedRuntimeActuatorSpec[] actuators,
            OwnedRuntimeSurfaceSpec[] surfaces, OwnedRuntimeThermalLink[] thermalLinks=null,
            bool[] resourceFlowEnabled=null, bool observeClock=false, int[] wheelProviderSlots=null, Rotation? controlOrientation=null, OwnedRuntimeThermalPolicy thermalPolicy=null)
        {
            if(world==null || actuators==null || surfaces==null)throw new ArgumentNullException("systems");
            if(actuators.Length>MaximumRows || surfaces.Length>MaximumRows || (thermalLinks!=null && thermalLinks.Length>MaximumRows))
                throw new ArgumentException("System plan exceeds bounded row capacity.");
            this.world=world;definition=world.Definition;
            ControlOrientation=controlOrientation??Rotation.Identity;RigidAssemblyProperties.RequireRotation(ControlOrientation);
            partBodies=new int[definition.PartCount];environments=new OwnedRuntimeEnvironmentSample[definition.BodyCount];regionOmega=new Vec[definition.BodyCount];
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
            var poolRows=new Dictionary<string,List<int>>(StringComparer.Ordinal);
            var nonpositiveKinds=new HashSet<string>(StringComparer.Ordinal);
            for(int r=0;r<definition.ResourceCount;r++)
            {
                var resource=definition.GetResource(r);List<int> rows;
                if(!poolRows.TryGetValue(resource.Kind,out rows)){rows=new List<int>();poolRows.Add(resource.Kind,rows);}
                rows.Add(r);if(resource.MassPerUnit<=0)nonpositiveKinds.Add(resource.Kind);
            }
            var sharedPools=new Dictionary<string,int[]>(StringComparer.Ordinal);
            var singlePools=new Dictionary<int,int[]>();
            var wheelReservoirs=new HashSet<int>();
            var wheelRows=new List<int>();
            for(int i=0;i<actuators.Length;i++)
            {
                var a=this.actuators[i];if(a==null)throw new ArgumentException("Null actuator.");Part(a.Part);
                int provider=this.wheelProviderSlots[i];
                if(a.Kind==OwnedRuntimeActuatorKind.ReactionWheel)
                {
                    if(provider<0 || provider>=definition.ProviderStateCount)throw new ArgumentException("Wheel requires a captured provider-state slot.");
                    if(!wheelReservoirs.Add(provider))throw new ArgumentException("Wheels require unique momentum reservoirs.");
                    wheelRows.Add(i);
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
                        int[] single;if(!singlePools.TryGetValue(a.FuelResource,out single)){single=new[]{a.FuelResource};singlePools.Add(a.FuelResource,single);}
                        fuelPools[i][f]=single;
                    }
                    else
                    {
                        string kind=a.FuelKind(f);List<int> rows;
                        if(!poolRows.TryGetValue(kind,out rows))throw new ArgumentException("Fuel kind has no recorded resource pool: "+kind);
                        if(nonpositiveKinds.Contains(kind))throw new ArgumentException("Propellant density must be positive.");
                        if(a.ElectricResource>=0&&definition.GetResource(a.ElectricResource).Kind==kind)throw new ArgumentException("Fuel and electrical pools overlap.");
                        int[] pool;if(!sharedPools.TryGetValue(kind,out pool)){pool=rows.ToArray();sharedPools.Add(kind,pool);}
                        fuelPools[i][f]=pool;
                    }
                }
            }
            wheelActuatorRows=wheelRows.ToArray();
            foreach(var s in this.surfaces){if(s==null)throw new ArgumentException("Null surface.");Part(s.Part);}
            foreach(var link in links){if(link==null)throw new ArgumentException("Null thermal link.");Part(link.A);Part(link.B);}
            forces=new Vec[definition.BodyCount];torques=new Vec[definition.BodyCount];oldCenters=new Vec[definition.BodyCount];
            oldWheel=new Vec[definition.BodyCount];newWheel=new Vec[definition.BodyCount];
            localActuatorForces=new Vec[definition.BodyCount];localActuatorTorques=new Vec[definition.BodyCount];
            heat=new double[definition.PartCount];wheelMomentum=new double[actuators.Length];stagedWheelMomentum=new double[actuators.Length];
            this.observeClock=observeClock;
            boundaryInitialResources=new double[definition.ResourceCount];
            thermalSolver=new OwnedRuntimeThermalSolver(definition,links);
            var text=new StringBuilder();foreach(var surface in this.surfaces)text.Append(surface.Part).Append(':').Append(surface.ThermalArea.ToString("R",CultureInfo.InvariantCulture)).Append(':').Append(surface.Emissivity.ToString("R",CultureInfo.InvariantCulture)).Append(';');
            foreach(var link in links)text.Append(link.A).Append(':').Append(link.B).Append(':').Append(link.ConductanceWattsPerKelvin.ToString("R",CultureInfo.InvariantCulture)).Append(';');
            using(var sha=SHA256.Create())thermalPlanId=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-","").ToLowerInvariant();
            if(thermalPolicy!=null)SetThermalPolicy(world.Stamp,thermalPolicy);
        }
        public OwnedRuntimeThermalPolicy ThermalPolicy {get{return world.ThermalPolicy;}}
        public OwnedRuntimeStamp SetThermalPolicy(OwnedRuntimeStamp expected,OwnedRuntimeThermalPolicy policy)
        {if(policy==null)throw new ArgumentNullException("policy");RequireConfigurationIdle();return world.MutateThermal(expected,bank=>{if(bank.Thermal.pendingDuration!=0)throw new InvalidOperationException("Flush pending thermal interval before policy replacement.");bank.Thermal.Apply(policy);bank.Thermal.planId=thermalPlanId;});}
        public OwnedRuntimeStamp FlushThermal(OwnedRuntimeStamp expected)
        {RequireConfigurationIdle();return world.MutateThermal(expected,bank=>{RequireThermalPlan(bank);thermalSolver.Flush(bank);});}
        void RequireThermalPlan(OwnedRuntimeBank bank)
        {if(bank.Thermal.pendingDuration!=0&&bank.Thermal.planId!=thermalPlanId)throw new InvalidOperationException("Pending thermal model belongs to a different system plan.");}

        public bool HasScalarSupply {get{return scalarSupply!=null;}}
        public OwnedRuntimeSupplyObservation LastScalarSupplyObservation {get{RequireConfigurationIdle();SettleScalarSupply();return lastSupply;}}
        public OwnedRuntimeSupplyTotals ScalarSupplyTotals {get{RequireConfigurationIdle();SettleScalarSupply();return supplyTotals;}}
        public void ConfigureScalarSupply(OwnedRuntimeStamp expected,OwnedRuntimeSupplyCapturePacket packet,string supplySha256,Vec originBodyMeters,double runtimeEpoch)
        {RestoreScalarSupplyConfiguration(expected,new OwnedRuntimeSupplyConfiguration{packet=packet,supplySha256=supplySha256,frameId=expected.FrameId,frameRevision=expected.FrameRevision,runtimeEpoch=runtimeEpoch,originBodyMeters=new[]{originBodyMeters.X,originBodyMeters.Y,originBodyMeters.Z}});}
        public OwnedRuntimeSupplyConfiguration CaptureScalarSupplyConfiguration()
        {lock(configurationGate){RequireConfigurationIdle();SettleScalarSupply();return scalarSupply==null?null:scalarSupply.CaptureConfiguration();}}
        public void RestoreScalarSupplyConfiguration(OwnedRuntimeStamp expected,OwnedRuntimeSupplyConfiguration config)
        {lock(configurationGate){RequireConfigurationIdle();if(!world.Stamp.Matches(expected))throw new InvalidOperationException("Supply source is stale.");var candidate=config==null?null:new OwnedRuntimeSupplyPipeline(definition,config);if(candidate!=null)candidate.RequireSource(expected,double.Epsilon);long revision=checked(configurationRevision+1);SettleScalarSupply();scalarSupply=candidate;configurationRevision=revision;deadlinePlan=null;}}
        void SettleScalarSupply()
        {
            world.RequireWriterThread();if(pendingSupplyBank==null)return;
            if(ReferenceEquals(world.CurrentForTrial,pendingSupplyBank))
            {lastSupply=pendingSupply;lastSupply.PublishedStamp=pendingSupplyBank.Stamp;supplyTotals.AcceptedIntervals++;supplyTotals.GeneratedUnits+=lastSupply.GeneratedUnits;supplyTotals.CreditedUnits+=lastSupply.CreditedUnits;supplyTotals.SpilledUnits+=lastSupply.SpilledUnits;supplyTotals.CommandRequestedUnits+=lastSupply.CommandRequestedUnits;supplyTotals.CommandWithdrawnUnits+=lastSupply.CommandWithdrawnUnits;}
            pendingSupplyBank=null;
        }
        OwnedRuntimeControls ApplyScalarSupply(OwnedRuntimeBank bank,OwnedRuntimeStamp stamp,double delta,OwnedRuntimeControls controls,bool record)
        {
            if(scalarSupply==null)return controls;
            if(record)SettleScalarSupply();var observation=scalarSupply.Apply(bank,stamp,delta,flowEnabled);
            if(record){pendingSupplyBank=bank;pendingSupply=observation;}
            if(scalarSupply.CommandCount>0&&observation.ReadyCommands==0){controls.Throttle=0;controls.Attitude=default(Vec);controls.Translation=default(Vec);controls.EnginesEnabled=controls.RcsEnabled=controls.WheelsEnabled=false;}
            return controls;
        }
        public int ActuatorCount{get{return actuators.Length;}}
        public int SurfaceCount{get{return surfaces.Length;}}
        public int ThermalLinkCount{get{return links.Length;}}
        public long ConfigurationRevision{get{return configurationRevision;}}
        public OwnedRuntimeExecutionStrategy ExecutionStrategy {get{return executionStrategy;}}
        public OwnedRuntimeDeadlineStrategy DeadlineStrategy {get{return deadlineStrategy;}}
        public bool HasActuatorBoundary {get{return hasActuatorBoundary;}}
        public OwnedRuntimeActuatorBoundary LastActuatorBoundary {get{return lastActuatorBoundary;}}
        public void SetDeadlineStrategy(OwnedRuntimeStamp expected,OwnedRuntimeDeadlineStrategy strategy)
        {lock(configurationGate){RequireConfigurationIdle();if(!world.Stamp.Matches(expected)||!Enum.IsDefined(typeof(OwnedRuntimeDeadlineStrategy),strategy))throw new ArgumentException("Deadline selection source/strategy differs.");if(deadlineStrategy!=strategy){long revision=checked(configurationRevision+1);deadlineStrategy=strategy;configurationRevision=revision;deadlinePlan=null;}}}
        public void SetWorkObservation(bool enabled){lock(configurationGate){RequireConfigurationIdle();observeClock=enabled;}}
        internal bool WorkObservation {get{return observeClock;}}
        internal void RecordMechanicalIntegrationTicks(long ticks){if(ticks<0)throw new ArgumentOutOfRangeException("ticks");if(observeClock)Counters.RegionIntegrationTicks+=ticks;}
        public void SetExecutionStrategy(OwnedRuntimeExecutionStrategy strategy)
        {lock(configurationGate){RequireConfigurationIdle();if(!Enum.IsDefined(typeof(OwnedRuntimeExecutionStrategy),strategy))throw new ArgumentException("Unknown execution strategy.");if(executionStrategy!=strategy){long revision=checked(configurationRevision+1);var plan=strategy==OwnedRuntimeExecutionStrategy.Compiled?new OwnedRuntimeExecutionPlan(world.CurrentForTrial,actuators,surfaces,fuelPools,flowEnabled,enabled,revision):null;executionStrategy=strategy;configurationRevision=revision;executionPlan=plan;if(plan!=null)Counters.ExecutionPlanBuilds++;}}}
        public void CompileExecutionPlan(OwnedRuntimeStamp expected)
        {lock(configurationGate){RequireConfigurationIdle();if(!world.Stamp.Matches(expected))throw new InvalidOperationException("Execution plan source is stale.");PrepareExecutionPlan(world.CurrentForTrial);}}
        void PrepareExecutionPlan(OwnedRuntimeBank bank)
        {if(executionStrategy==OwnedRuntimeExecutionStrategy.Compiled&&(executionPlan==null||!executionPlan.Matches(bank,configurationRevision))){long begin=observeClock?Stopwatch.GetTimestamp():0;executionPlan=new OwnedRuntimeExecutionPlan(bank,actuators,surfaces,fuelPools,flowEnabled,enabled,configurationRevision);Counters.ExecutionPlanBuilds++;if(observeClock)Counters.PlanTicks+=Stopwatch.GetTimestamp()-begin;}}
        public OwnedRuntimeStamp SetMechanicalPolicy(OwnedRuntimeStamp expected,OwnedRuntimeMechanicalPolicy policy)
        {lock(configurationGate){RequireConfigurationIdle();return world.MutateMechanical(expected,policy);}}
        internal OwnedRuntimeSystems CreateTrialSystems(OwnedRuntimeWorld target)
        {
            lock(configurationGate)
            {
                RequireConfigurationIdle();var s=new OwnedRuntimeSystems(target,actuators,surfaces,links,flowEnabled,observeClock,wheelProviderSlots,ControlOrientation);
                Array.Copy(enabled,s.enabled,enabled.Length);s.executionStrategy=executionStrategy;s.deadlineStrategy=deadlineStrategy;s.configurationRevision=configurationRevision;if(scalarSupply!=null)s.scalarSupply=new OwnedRuntimeSupplyPipeline(s.definition,scalarSupply.CaptureConfiguration());return s;
            }
        }
        internal void ResetTrialCounters()
        {SettleScalarSupply();supplyTotals=default(OwnedRuntimeSupplyTotals);lastSupply=default(OwnedRuntimeSupplyObservation);Counters.DeadlineAdmissionFallbacks=Counters.DeadlineLowerBracketFallbacks=Counters.DeadlineUpperBracketFallbacks=Counters.DeadlineReplayFallbacks=0;Counters.DeadlinePlanBuilds=Counters.DeadlinePlanHits=Counters.DeadlineLocalProbes=Counters.DeadlineFallbacks=0;Counters.BoundaryProbes=Counters.BoundaryResourceCopies=Counters.BoundaryResourceRowsCopied=Counters.BoundaryTicks=0;Counters.ScalarTicks=Counters.AeroTicks=Counters.ThermalTicks=Counters.RegionIntegrationTicks=Counters.PlanTicks=0;Counters.Steps=Counters.FailedSteps=Counters.ActuatorRows=Counters.SurfaceRows=Counters.ThermalLinks=Counters.RegionIntegrations=Counters.ObservedTicks=0;Counters.SkippedActuatorRows=Counters.ExecutionPlanBuilds=Counters.CompiledActuatorRows=Counters.InactiveActuatorRows=Counters.CompiledSurfaceRows=Counters.VacuumSurfaceRows=Counters.RegionMotionContexts=Counters.RetainedGeometryRows=0;Counters.DeliveredForceNewtonSeconds=Counters.DeliveredTorqueNewtonMeterSeconds=Counters.PropellantKilograms=Counters.ElectricUnits=Counters.ActuatorHeatJoules=0;}
        internal void AccumulateTrialCounters(OwnedRuntimeSystems s)
        {s.SettleScalarSupply();if(s.supplyTotals.AcceptedIntervals>0){lastSupply=s.lastSupply;lastSupply.PublishedStamp=world.Stamp;supplyTotals.AcceptedIntervals+=s.supplyTotals.AcceptedIntervals;supplyTotals.GeneratedUnits+=s.supplyTotals.GeneratedUnits;supplyTotals.CreditedUnits+=s.supplyTotals.CreditedUnits;supplyTotals.SpilledUnits+=s.supplyTotals.SpilledUnits;supplyTotals.CommandRequestedUnits+=s.supplyTotals.CommandRequestedUnits;supplyTotals.CommandWithdrawnUnits+=s.supplyTotals.CommandWithdrawnUnits;}Counters.DeadlineAdmissionFallbacks+=s.Counters.DeadlineAdmissionFallbacks;Counters.DeadlineLowerBracketFallbacks+=s.Counters.DeadlineLowerBracketFallbacks;Counters.DeadlineUpperBracketFallbacks+=s.Counters.DeadlineUpperBracketFallbacks;Counters.DeadlineReplayFallbacks+=s.Counters.DeadlineReplayFallbacks;Counters.DeadlinePlanBuilds+=s.Counters.DeadlinePlanBuilds;Counters.DeadlinePlanHits+=s.Counters.DeadlinePlanHits;Counters.DeadlineLocalProbes+=s.Counters.DeadlineLocalProbes;Counters.DeadlineFallbacks+=s.Counters.DeadlineFallbacks;Counters.BoundaryProbes+=s.Counters.BoundaryProbes;Counters.BoundaryResourceCopies+=s.Counters.BoundaryResourceCopies;Counters.BoundaryResourceRowsCopied+=s.Counters.BoundaryResourceRowsCopied;Counters.BoundaryTicks+=s.Counters.BoundaryTicks;Counters.ScalarTicks+=s.Counters.ScalarTicks;Counters.AeroTicks+=s.Counters.AeroTicks;Counters.ThermalTicks+=s.Counters.ThermalTicks;Counters.RegionIntegrationTicks+=s.Counters.RegionIntegrationTicks;Counters.PlanTicks+=s.Counters.PlanTicks;Counters.Steps+=s.Counters.Steps;Counters.ActuatorRows+=s.Counters.ActuatorRows;Counters.SurfaceRows+=s.Counters.SurfaceRows;Counters.ThermalLinks+=s.Counters.ThermalLinks;Counters.RegionIntegrations+=s.Counters.RegionIntegrations;Counters.SkippedActuatorRows+=s.Counters.SkippedActuatorRows;Counters.ExecutionPlanBuilds+=s.Counters.ExecutionPlanBuilds;Counters.CompiledActuatorRows+=s.Counters.CompiledActuatorRows;Counters.InactiveActuatorRows+=s.Counters.InactiveActuatorRows;Counters.CompiledSurfaceRows+=s.Counters.CompiledSurfaceRows;Counters.VacuumSurfaceRows+=s.Counters.VacuumSurfaceRows;Counters.RegionMotionContexts+=s.Counters.RegionMotionContexts;Counters.RetainedGeometryRows+=s.Counters.RetainedGeometryRows;Counters.DeliveredForceNewtonSeconds+=s.Counters.DeliveredForceNewtonSeconds;Counters.DeliveredTorqueNewtonMeterSeconds+=s.Counters.DeliveredTorqueNewtonMeterSeconds;Counters.PropellantKilograms+=s.Counters.PropellantKilograms;Counters.ElectricUnits+=s.Counters.ElectricUnits;Counters.ActuatorHeatJoules+=s.Counters.ActuatorHeatJoules;}
        public OwnedRuntimeActuatorBoundary FindActuatorBoundary(OwnedRuntimeStamp expected,OwnedRuntimeControls controls,double maximumHorizonSeconds)
        {return scalarSupply!=null?FindActuatorBoundaryExactOracle(expected,controls,maximumHorizonSeconds):deadlineStrategy==OwnedRuntimeDeadlineStrategy.Retained?FindRetainedBoundary(expected,controls,maximumHorizonSeconds):FindActuatorBoundaryExactOracle(expected,controls,maximumHorizonSeconds);}
        public OwnedRuntimeActuatorBoundary FindActuatorBoundaryExactOracle(OwnedRuntimeStamp expected,OwnedRuntimeControls controls,double maximumHorizonSeconds)
        {
            long probes=Counters.BoundaryProbes;var value=FindExactBoundary(expected,controls,maximumHorizonSeconds,true);
            return PublishBoundary(value,expected,controls,maximumHorizonSeconds,OwnedRuntimeDeadlineStrategy.ExactOracle,scalarSupply==null?"bounded-64-bisection-upper":"supply-augmented-64-bisection-upper",scalarSupply==null?null:"time-varying-supply-retained-certificate-disabled",probes);
        }
        OwnedRuntimeActuatorBoundary PublishBoundary(OwnedRuntimeActuatorBoundary value,OwnedRuntimeStamp expected,OwnedRuntimeControls controls,double horizon,OwnedRuntimeDeadlineStrategy strategy,string certification,string fallback,long probes)
        {
            CheckedBoundary(value,expected);value.Strategy=strategy;value.Certification=certification;value.FallbackReason=fallback;value.ProbeCount=checked((int)(Counters.BoundaryProbes-probes));value.Controls=controls;value.MaximumHorizonSeconds=horizon;lastActuatorBoundary=value;hasActuatorBoundary=true;return value;
        }
        OwnedRuntimeActuatorBoundary FindExactBoundary(OwnedRuntimeStamp expected,OwnedRuntimeControls controls,double maximumHorizonSeconds,bool observeBoundaryClock)
        {
            controls.Validate();AssemblyModel.Positive(maximumHorizonSeconds);if(maximumHorizonSeconds>7200)throw new ArgumentOutOfRangeException("maximumHorizonSeconds");
            lock(configurationGate)
            {
                RequireConfigurationIdle();if(!world.Stamp.Matches(expected))throw new InvalidOperationException("Actuator boundary source is stale.");
                bool compact=scalarSupply==null&&executionStrategy==OwnedRuntimeExecutionStrategy.Compiled;
                if(compact)world.EnterAdmission(expected);
                long begin=observeClock&&observeBoundaryClock?Stopwatch.GetTimestamp():0;
                try
                {
                    var result=new OwnedRuntimeActuatorBoundary{Supported=true,Reached=false,DeltaSeconds=maximumHorizonSeconds,Actuator=-1,Resource=-1,Kind="none",SourceStamp=expected,ConfigurationRevision=configurationRevision};
                    using(var read=world.AcquireRead())
                    {
                        var source=world.CurrentForTrial;if(!read.Stamp.Matches(expected)||!source.Stamp.Matches(expected)||!ReferenceEquals(source.Definition,definition))throw new InvalidOperationException("Boundary source read changed.");
                        for(int i=0;i<boundaryInitialResources.Length;i++)boundaryInitialResources[i]=read.GetResourceQuantity(i);
                        if(compact)
                        {
                            PrepareExecutionPlan(source);
                            if(boundaryBank==null||!ReferenceEquals(boundaryBank.Topology,source.Topology))boundaryBank=new OwnedRuntimeBank(source.Definition,source.Topology);
                            boundaryBank.Members=source.Members;boundaryBank.ProviderState=source.ProviderState;boundaryBank.RegionMotion=source.RegionMotion;boundaryBank.Properties=source.Properties;
                            boundaryBank.Input=new OwnedRuntimeInput(controls.Throttle,controls.Attitude.X,controls.Attitude.Y,controls.Attitude.Z,controls.Translation.X,controls.Translation.Y,controls.Translation.Z);
                        }
                        try
                        {
                            if(!ProbeBoundary(expected,controls,maximumHorizonSeconds,compact))return CheckedBoundary(result,expected);
                            double low=0,high=maximumHorizonSeconds;
                            for(int i=0;i<64;i++){double middle=low+(high-low)*.5;if(middle==low||middle==high)break;if(ProbeBoundary(expected,controls,middle,compact))high=middle;else low=middle;}
                            ProbeBoundary(expected,controls,high,compact);result.Reached=true;result.DeltaSeconds=high;result.Actuator=boundaryActuator;result.Resource=boundaryResource;result.Kind=boundaryKind;return CheckedBoundary(result,expected);
                        }
                        finally{if(compact){boundaryBank.Members=null;boundaryBank.ProviderState=null;boundaryBank.RegionMotion=null;boundaryBank.Properties=null;}}
                    }
                }
                finally{if(compact)world.ExitAdmission();if(observeClock&&observeBoundaryClock)Counters.BoundaryTicks+=Stopwatch.GetTimestamp()-begin;}
            }
        }
        OwnedRuntimeActuatorBoundary FindRetainedBoundary(OwnedRuntimeStamp expected,OwnedRuntimeControls controls,double horizon)
        {
            controls.Validate();AssemblyModel.Positive(horizon);if(horizon>7200)throw new ArgumentOutOfRangeException("horizon");
            lock(configurationGate)
            {
                RequireConfigurationIdle();if(!world.Stamp.Matches(expected))throw new InvalidOperationException("Retained deadline source is stale.");
                long begin=observeClock?Stopwatch.GetTimestamp():0,probes=Counters.BoundaryProbes;string fallback=null,certification=null;
                var value=new OwnedRuntimeActuatorBoundary{Supported=true,Reached=false,DeltaSeconds=horizon,Actuator=-1,Resource=-1,Kind="none",SourceStamp=expected,ConfigurationRevision=configurationRevision};
                try
                {
                    world.EnterAdmission(expected);
                    try
                    {
                        using(var read=world.AcquireRead())
                        {
                            var source=world.CurrentForTrial;if(!read.Stamp.Matches(expected)||!source.Stamp.Matches(expected)||!ReferenceEquals(source.Definition,definition))throw new InvalidOperationException("Retained deadline read changed.");
                            if(deadlinePlan==null||!deadlinePlan.Matches(source,configurationRevision,controls))
                            {deadlinePlan=new OwnedRuntimeDeadlinePlan(source,actuators,fuelPools,enabled,flowEnabled,wheelProviderSlots,ControlOrientation,controls,configurationRevision);Counters.DeadlinePlanBuilds++;}
                            else Counters.DeadlinePlanHits++;
                            OwnedRuntimeDeadlinePrediction prediction;
                            if(deadlinePlan.Seed(source.ResourceQuantity,source.ProviderState,horizon,out prediction,out fallback))
                            {
                                if(!double.IsNaN(prediction.Delta)&&!double.IsInfinity(prediction.Delta))
                                {value.PredictionAvailable=true;value.PredictedDelta=prediction.Delta;value.PredictedLowerDelta=prediction.Lower;value.PredictedUpperDelta=prediction.Upper;value.PredictedKind=prediction.Kind;value.PredictedActuator=prediction.Actuator;value.PredictedResource=prediction.Resource;}
                                PrepareExecutionPlan(source);for(int i=0;i<boundaryInitialResources.Length;i++)boundaryInitialResources[i]=source.ResourceQuantity[i];
                                if(boundaryBank==null||!ReferenceEquals(boundaryBank.Topology,source.Topology))boundaryBank=new OwnedRuntimeBank(source.Definition,source.Topology);
                                boundaryBank.Members=source.Members;boundaryBank.ProviderState=source.ProviderState;boundaryBank.RegionMotion=source.RegionMotion;boundaryBank.Properties=source.Properties;
                                boundaryBank.Input=new OwnedRuntimeInput(controls.Throttle,controls.Attitude.X,controls.Attitude.Y,controls.Attitude.Z,controls.Translation.X,controls.Translation.Y,controls.Translation.Z);
                                try
                                {
                                    double upper=Math.Min(prediction.Upper,horizon),lower=Math.Min(prediction.Lower,upper);
                                    bool reached=LocalProbe(expected,controls,upper);
                                    if(!reached&&upper==horizon)certification="full-rate-horizon-unreached";
                                    else if(!reached)fallback="roundoff-bracket-upper-unreached";
                                    else if(lower>0&&LocalProbe(expected,controls,lower))fallback="roundoff-bracket-lower-reached";
                                    else
                                    {
                                        long lowBits=BitConverter.DoubleToInt64Bits(lower),highBits=BitConverter.DoubleToInt64Bits(upper);
                                        for(int iteration=0;highBits-lowBits>1&&iteration<64;iteration++)
                                        {
                                            long middle=lowBits+(highBits-lowBits)/2;
                                            if(LocalProbe(expected,controls,BitConverter.Int64BitsToDouble(middle)))highBits=middle;else lowBits=middle;
                                        }
                                        if(highBits-lowBits==1&&LocalProbe(expected,controls,BitConverter.Int64BitsToDouble(highBits)))
                                        {value.Reached=true;value.DeltaSeconds=BitConverter.Int64BitsToDouble(highBits);value.Actuator=boundaryActuator;value.Resource=boundaryResource;value.Kind=boundaryKind;certification="full-rate-adjacent-original-predicate";}
                                        else fallback="adjacent-predicate-replay-drift";
                                    }
                                }
                                finally{boundaryBank.Members=null;boundaryBank.ProviderState=null;boundaryBank.RegionMotion=null;boundaryBank.Properties=null;}
                            }
                        }
                    }
                    finally{world.ExitAdmission();}
                    if(certification==null)
                    {
                        Counters.DeadlineFallbacks++;
                        if(fallback=="roundoff-bracket-lower-reached")Counters.DeadlineLowerBracketFallbacks++;
                        else if(fallback=="roundoff-bracket-upper-unreached")Counters.DeadlineUpperBracketFallbacks++;
                        else if(fallback=="adjacent-predicate-replay-drift")Counters.DeadlineReplayFallbacks++;
                        else Counters.DeadlineAdmissionFallbacks++;
                        var original=FindExactBoundary(expected,controls,horizon,false);
                        original.PredictionAvailable=value.PredictionAvailable;original.PredictedDelta=value.PredictedDelta;original.PredictedLowerDelta=value.PredictedLowerDelta;original.PredictedUpperDelta=value.PredictedUpperDelta;original.PredictedKind=value.PredictedKind;original.PredictedActuator=value.PredictedActuator;original.PredictedResource=value.PredictedResource;
                        value=original;certification="fallback-bounded-64-bisection-upper";
                    }
                    return PublishBoundary(value,expected,controls,horizon,OwnedRuntimeDeadlineStrategy.Retained,certification,fallback,probes);
                }
                finally{if(observeClock)Counters.BoundaryTicks+=Stopwatch.GetTimestamp()-begin;}
            }
        }
        bool LocalProbe(OwnedRuntimeStamp expected,OwnedRuntimeControls controls,double delta)
        {Counters.DeadlineLocalProbes++;return ProbeBoundary(expected,controls,delta,true);}
        OwnedRuntimeActuatorBoundary CheckedBoundary(OwnedRuntimeActuatorBoundary value,OwnedRuntimeStamp expected)
        {if(!world.Stamp.Matches(expected)||value.ConfigurationRevision!=configurationRevision)throw new InvalidOperationException("Boundary source/configuration changed during search.");return value;}
        bool ProbeBoundary(OwnedRuntimeStamp expected,OwnedRuntimeControls controls,double delta,bool compact)
        {
            Counters.BoundaryProbes++;
            if(compact)
            {
                Array.Copy(boundaryInitialResources,boundaryBank.ResourceQuantity,boundaryInitialResources.Length);Counters.BoundaryResourceCopies++;Counters.BoundaryResourceRowsCopied+=boundaryInitialResources.Length;boundaryBank.InvalidateMassProperties();
                boundaryActuator=boundaryResource=-1;boundaryKind=null;
                LoadWheelMomentum(boundaryBank,false);
                ActuateBank(boundaryBank,delta,controls,false,true);return boundaryActuator>=0;
            }
            var input=new OwnedRuntimeInput(controls.Throttle,controls.Attitude.X,controls.Attitude.Y,controls.Attitude.Z,controls.Translation.X,controls.Translation.Y,controls.Translation.Z);
            using(var step=world.BeginStep(expected,delta,input))
            {
                PrepareExecutionPlan(step.Bank);
                boundaryActuator=boundaryResource=-1;boundaryKind=null;Array.Clear(forces,0,forces.Length);Array.Clear(torques,0,torques.Length);Array.Clear(heat,0,heat.Length);Array.Clear(oldWheel,0,oldWheel.Length);Array.Clear(newWheel,0,newWheel.Length);
                LoadWheelMomentum(step.Bank,false);
                controls=ApplyScalarSupply(step.Bank,expected,delta,controls,false);if(scalarSupply!=null&&scalarSupply.BoundaryResource>=0)ReachedBoundary(actuators.Length,scalarSupply.BoundaryResource,"supply-resource-boundary");Actuate(step,controls,false);return boundaryActuator>=0;
            }
        }
        void ReachedBoundary(int actuator,int resource,string kind)
        {if(boundaryActuator<0){boundaryActuator=actuator;boundaryResource=resource;boundaryKind=kind;}}
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
        {lock(configurationGate){RequireConfigurationIdle();if(value<0)throw new ArgumentOutOfRangeException("value");configurationRevision=value;executionPlan=null;deadlinePlan=null;}}
        void RequireConfigurationIdle()
        {world.RequireWriterThread();if(executing||mechanicalToken!=0)throw new InvalidOperationException("Cold system configuration cannot change during schedule execution or an unacknowledged mechanical transaction.");}
        internal void EnterExecution()
        {lock(configurationGate){RequireConfigurationIdle();executing=true;executionThread=System.Threading.Thread.CurrentThread.ManagedThreadId;}}
        internal void ExitExecution()
        {
            lock(configurationGate)
            {
                if(!executing||executionThread!=System.Threading.Thread.CurrentThread.ManagedThreadId)
                    throw new InvalidOperationException("System execution scope is not owned by this thread.");
                SettleScalarSupply();executing=false;executionThread=0;
            }
        }

        internal Vec[] OldWheelMomentum {get{return oldWheel;}}
        internal Vec[] NewWheelMomentum {get{return newWheel;}}
        internal double GetMechanicalProviderState(OwnedRuntimeStep step,int slot)
        {
            RequireMechanical(step);if(slot<0||slot>=step.Bank.ProviderState.Length)throw new ArgumentOutOfRangeException("slot");
            for(int i=0;i<wheelProviderSlots.Length;i++)if(wheelProviderSlots[i]==slot)return stagedWheelMomentum[i];
            return step.Bank.ProviderState[slot];
        }
        public void BeginMechanical(OwnedRuntimeStep step,OwnedRuntimeControls controls)
        {
            world.CheckStep(step);if(mechanicalToken!=0)throw new InvalidOperationException("Prior mechanical transaction requires acknowledgment or abort.");controls.Validate();var bank=step.Bank;var input=bank.Input;
            if(!ReferenceEquals(bank.Definition,definition)||controls.Throttle!=input.Throttle||controls.Attitude.X!=input.Pitch||controls.Attitude.Y!=input.Yaw||controls.Attitude.Z!=input.Roll||controls.Translation.X!=input.X||controls.Translation.Y!=input.Y||controls.Translation.Z!=input.Z)
                throw new InvalidOperationException("Mechanical transaction input/definition differs.");
            if(step.DeltaTime>.1)throw new ArgumentOutOfRangeException("step","Mechanical interval exceeds model bound.");
            step.ClaimSystemsExecution();step.BeginMechanicalPublication();mechanicalToken=step.Token;mechanicalBank=bank;mechanicalConfiguration=configurationRevision;mechanicalBegin=observeClock?Stopwatch.GetTimestamp():0;
            mechanicalForceLedger=Counters.DeliveredForceNewtonSeconds;mechanicalTorqueLedger=Counters.DeliveredTorqueNewtonMeterSeconds;mechanicalFuelLedger=Counters.PropellantKilograms;mechanicalElectricLedger=Counters.ElectricUnits;mechanicalHeatLedger=Counters.ActuatorHeatJoules;
            mechanicalStepsLedger=Counters.Steps;mechanicalIntegrationsLedger=Counters.RegionIntegrations;mechanicalCompleted=false;
            try
            {
                PrepareExecutionPlan(bank);int regions=bank.RegionMotion.Length;
                Array.Clear(forces,0,regions);Array.Clear(torques,0,regions);Array.Clear(heat,0,heat.Length);Array.Clear(oldWheel,0,regions);Array.Clear(newWheel,0,regions);
                Array.Clear(localActuatorForces,0,regions);Array.Clear(localActuatorTorques,0,regions);
                LoadWheelMomentum(bank,true);
                for(int r=0;r<regions;r++)oldCenters[r]=bank.Properties[r].CenterOfMass;
                long begin=observeClock?Stopwatch.GetTimestamp():0;controls=ApplyScalarSupply(bank,step.ExpectedStamp,step.DeltaTime,controls,true);stagingLocal=true;try{Actuate(step,controls);}finally{stagingLocal=false;}
                step.RefreshMassProperties();
                for(int r=0;r<regions;r++)localActuatorTorques[r]=localActuatorTorques[r]+Vec.Cross(oldCenters[r]+bank.Properties[r].CenterOfMass*-1,localActuatorForces[r]);
                if(observeClock)Counters.ScalarTicks+=Stopwatch.GetTimestamp()-begin;
            }
            catch{AbortMechanical(step);step.Dispose();throw;}
        }
        public void AbortMechanical(OwnedRuntimeStep step)
        {
            if(mechanicalToken==0)return;world.RequireWriterThread();
            if(step.Token!=mechanicalToken||!ReferenceEquals(step.World,world)||!ReferenceEquals(step.Bank,mechanicalBank))throw new InvalidOperationException("Cannot abort a foreign mechanical transaction.");
            if(ReferenceEquals(world.CurrentForTrial,step.Bank))throw new InvalidOperationException("Published mechanical state requires acknowledgment, not ledger rollback.");
            Counters.DeliveredForceNewtonSeconds=mechanicalForceLedger;Counters.DeliveredTorqueNewtonMeterSeconds=mechanicalTorqueLedger;Counters.PropellantKilograms=mechanicalFuelLedger;Counters.ElectricUnits=mechanicalElectricLedger;Counters.ActuatorHeatJoules=mechanicalHeatLedger;
            Counters.Steps=mechanicalStepsLedger;Counters.RegionIntegrations=mechanicalIntegrationsLedger;
            if(observeClock&&!mechanicalCompleted)Counters.ObservedTicks+=Stopwatch.GetTimestamp()-mechanicalBegin;
            pendingSupplyBank=null;Counters.FailedSteps++;mechanicalToken=0;mechanicalBank=null;stagingLocal=false;
        }
        internal void AcknowledgeMechanical(OwnedRuntimeStep step)
        {
            world.RequireWriterThread();
            if(!mechanicalCompleted||step.Token!=mechanicalToken||!ReferenceEquals(step.World,world)||!ReferenceEquals(step.Bank,mechanicalBank)||!ReferenceEquals(world.CurrentForTrial,step.Bank))throw new InvalidOperationException("Mechanical acknowledgment requires its actual published bank.");
            SettleScalarSupply();mechanicalToken=0;mechanicalBank=null;
        }
        void RequireMechanical(OwnedRuntimeStep step)
        {world.CheckStep(step);if(mechanicalCompleted||step.Token!=mechanicalToken||!ReferenceEquals(step.Bank,mechanicalBank)||configurationRevision!=mechanicalConfiguration)throw new InvalidOperationException("Mechanical stage is stale, foreign or already completed.");}
        public void SampleMechanical(OwnedRuntimeStep step,IOwnedRuntimeEnvironmentField field,OwnedRuntimeStamp stageStamp,OwnedRuntimeMotion[] motions,
            RigidAssemblyProperties[] properties,Vec[] nonGravityForce,Vec[] torque,OwnedRuntimeEnvironmentSample[] sampled,double[] heatWatts)
        {
            RequireMechanical(step);var source=step.ExpectedStamp;int regions=step.Bank.RegionMotion.Length;
            AssemblyModel.Finite(stageStamp.Time);
            if(field==null||motions==null||properties==null||nonGravityForce==null||torque==null||sampled==null||heatWatts==null||motions.Length!=regions||properties.Length!=regions||nonGravityForce.Length!=regions||torque.Length!=regions||sampled.Length!=regions||heatWatts.Length!=heat.Length)
                throw new ArgumentException("Mechanical sample buffers differ from admitted regions/parts.");
            if(stageStamp.Epoch!=source.Epoch||stageStamp.StateRevision!=source.StateRevision||stageStamp.TopologyRevision!=source.TopologyRevision||stageStamp.DefinitionRevision!=source.DefinitionRevision||stageStamp.FrameRevision!=source.FrameRevision||stageStamp.FrameId!=source.FrameId||stageStamp.Time<source.Time||stageStamp.Time>source.Time+step.DeltaTime)
                throw new InvalidOperationException("Mechanical stage clock/source differs.");
            long begin=observeClock?Stopwatch.GetTimestamp():0;Array.Clear(heatWatts,0,heatWatts.Length);
            for(int r=0;r<regions;r++)
            {
                OwnedRuntimeWorld.ValidateMotion(motions[r]);if(properties[r]==null)throw new ArgumentException("Mechanical stage properties absent.");
                sampled[r]=field.Sample(stageStamp,r,motions[r],properties[r]);sampled[r].Validate();if(sampled[r].Epoch>stageStamp.Time)throw new InvalidOperationException("Mechanical sample is from the future.");
                nonGravityForce[r]=motions[r].Orientation.Rotate(localActuatorForces[r]);
                Vec centerShift=motions[r].Orientation.Rotate(step.Bank.Properties[r].CenterOfMass+properties[r].CenterOfMass*-1);
                torque[r]=motions[r].Orientation.Rotate(localActuatorTorques[r])+Vec.Cross(centerShift,nonGravityForce[r]);
                if(sampled[r].Density!=0&&(executionStrategy!=OwnedRuntimeExecutionStrategy.Compiled||executionPlan.SurfaceCounts[r]>0))
                {regionOmega[r]=OwnedRuntimeDynamics.AngularVelocity(properties[r],motions[r].Orientation,motions[r].AngularMomentum);Counters.RegionMotionContexts++;}
            }
            var bank=step.Bank;bool compiled=executionStrategy==OwnedRuntimeExecutionStrategy.Compiled;
            for(int i=0;i<surfaces.Length;i++)
            {
                Counters.SurfaceRows++;var surface=surfaces[i];int body=compiled?executionPlan.Surfaces[i].Body:partBodies[surface.Part],region=compiled?executionPlan.Surfaces[i].Region:bank.Topology.BodyRegions[body];
                var environment=sampled[region];if(environment.Density==0){Counters.VacuumSurfaceRows++;continue;}
                var motion=motions[region];var property=properties[region];Vec point=compiled?executionPlan.Surfaces[i].Point:LocalPoint(bank,surface.Part,surface.LocalPoint,body);
                Vec lever=motion.Orientation.Rotate(point+property.CenterOfMass*-1),relative=motion.Velocity+Vec.Cross(regionOmega[region]+environment.FlowAngularVelocity*-1,lever)+environment.WindVelocity*-1;
                double speed2=OwnedRuntimeDynamics.Dot(relative,relative);if(speed2==0)continue;
                double speed=Math.Sqrt(speed2),q=.5*environment.Density*speed2;Vec direction=relative*(1/speed);
                Vec normal=motion.Orientation.Rotate(compiled?executionPlan.Surfaces[i].Normal:(bank.Members[body].LocalRotation*definition.GetPart(surface.Part).LocalRotation).Rotate(surface.LocalNormal));
                double incidence=OwnedRuntimeDynamics.Dot(normal,direction);Vec lift=(normal+direction*(-incidence))*(-2*incidence*q*surface.LiftArea),force=direction*(-q*surface.DragArea)+lift;
                nonGravityForce[region]=nonGravityForce[region]+force;torque[region]=torque[region]+Vec.Cross(lever,force);heatWatts[surface.Part]+=q*surface.DragArea*speed*surface.HeatingFraction;
                if(compiled){Counters.CompiledSurfaceRows++;Counters.RetainedGeometryRows++;}
            }
            for(int r=0;r<regions;r++){OwnedRuntimeDynamics.Require(nonGravityForce[r]);OwnedRuntimeDynamics.Require(torque[r]);}
            foreach(double watts in heatWatts)OwnedRuntimeEnvironment.Nonnegative(watts);
            if(observeClock)Counters.AeroTicks+=Stopwatch.GetTimestamp()-begin;
        }
        public void CompleteMechanical(OwnedRuntimeStep step,OwnedRuntimeMotion[] terminalMotions,OwnedRuntimeEnvironmentSample[] midpointEnvironments,double[] midpointHeatWatts)
        {
            RequireMechanical(step);var bank=step.Bank;int regions=bank.RegionMotion.Length;
            if(terminalMotions==null||midpointEnvironments==null||midpointHeatWatts==null||terminalMotions.Length!=regions||midpointEnvironments.Length!=regions||midpointHeatWatts.Length!=heat.Length)throw new ArgumentException("Mechanical terminal buffers differ.");
            for(int r=0;r<regions;r++){OwnedRuntimeWorld.ValidateMotion(terminalMotions[r]);midpointEnvironments[r].Validate();if(midpointEnvironments[r].Epoch>step.ExpectedStamp.Time+step.DeltaTime)throw new InvalidOperationException("Terminal environment is from the future.");}
            foreach(double watts in midpointHeatWatts)OwnedRuntimeEnvironment.Nonnegative(watts);
            try
            {
                Array.Copy(terminalMotions,bank.RegionMotion,regions);Array.Copy(midpointEnvironments,environments,regions);
                for(int p=0;p<heat.Length;p++){heat[p]+=midpointHeatWatts[p]*step.DeltaTime;AssemblyModel.Finite(heat[p]);}
                long begin=observeClock?Stopwatch.GetTimestamp():0;Thermal(step);if(observeClock)Counters.ThermalTicks+=Stopwatch.GetTimestamp()-begin;
                PublishWheelMomentum(bank);
                step.CompleteMechanicalPublication();
                Counters.Steps++;Counters.RegionIntegrations+=regions;if(observeClock)Counters.ObservedTicks+=Stopwatch.GetTimestamp()-mechanicalBegin;
                mechanicalCompleted=true;
            }
            catch{AbortMechanical(step);step.Dispose();throw;}
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
        {StepCore(step,environment,null,controls,externalForces);}
        public void Step(OwnedRuntimeStep step,IOwnedRuntimeEnvironmentField field,OwnedRuntimeControls controls)
        {StepCore(step,null,field,controls,null);}
        void StepCore(OwnedRuntimeStep step,OwnedRuntimeEnvironment environment,IOwnedRuntimeEnvironmentField field,OwnedRuntimeControls controls,OwnedRuntimeExternalForce[] externalForces)
        {
            if(environment==null&&field==null)throw new ArgumentNullException("environment/field");world.CheckStep(step);
            controls.Validate();var input=step.Bank.Input;
            if(controls.Throttle!=input.Throttle || controls.Attitude.X!=input.Pitch || controls.Attitude.Y!=input.Yaw
                || controls.Attitude.Z!=input.Roll || controls.Translation.X!=input.X || controls.Translation.Y!=input.Y || controls.Translation.Z!=input.Z)
                throw new InvalidOperationException("Controls must match the transaction's recorded input.");
            if(!ReferenceEquals(step.World,world) || !ReferenceEquals(step.Bank.Definition,definition))
                throw new InvalidOperationException("System plan belongs to a different definition/world.");
            if(externalForces!=null)for(int i=0;i<externalForces.Length;i++)
            {Part(externalForces[i].Part);OwnedRuntimeDynamics.Require(externalForces[i].WorldPoint);OwnedRuntimeDynamics.Require(externalForces[i].WorldForce);OwnedRuntimeDynamics.Require(externalForces[i].WorldTorque);}
            if(environment!=null&&environment.Epoch>step.ExpectedStamp.Time)throw new InvalidOperationException("Environment observation is from the future.");
            double dt=step.DeltaTime;if(dt>.1)throw new ArgumentOutOfRangeException("dt", "Physical model step exceeds 0.1 seconds.");
            step.ClaimSystemsExecution();
            long begin=observeClock?Stopwatch.GetTimestamp():0;
            try
            {
                var bank=step.Bank;int regions=bank.RegionMotion.Length;
                PrepareExecutionPlan(bank);
                Array.Clear(forces,0,regions);Array.Clear(torques,0,regions);Array.Clear(heat,0,heat.Length);
                Array.Clear(oldWheel,0,regions);Array.Clear(newWheel,0,regions);
                LoadWheelMomentum(bank,true);
                for(int r=0;r<regions;r++)oldCenters[r]=bank.Properties[r].CenterOfMass;
                long stage=observeClock?Stopwatch.GetTimestamp():0;
                controls=ApplyScalarSupply(bank,step.ExpectedStamp,dt,controls,true);Actuate(step,controls);
                step.RefreshMassProperties();
                if(observeClock){Counters.ScalarTicks+=Stopwatch.GetTimestamp()-stage;stage=Stopwatch.GetTimestamp();}
                for(int r=0;r<regions;r++)
                {
                    Vec displacement=bank.RegionMotion[r].Orientation.Rotate(oldCenters[r]+bank.Properties[r].CenterOfMass*-1);
                    torques[r]=torques[r]+Vec.Cross(displacement,forces[r]);
                    var sample=field==null?new OwnedRuntimeEnvironmentSample(environment):field.Sample(step.ExpectedStamp,r,bank.RegionMotion[r],bank.Properties[r]);
                    sample.Validate();if(sample.Epoch>step.ExpectedStamp.Time)throw new InvalidOperationException("Environment field observation is from the future.");
                    environments[r]=sample;forces[r]=forces[r]+sample.Gravity*bank.Properties[r].Mass;
                }
                Aerothermal(step);
                if(observeClock){Counters.AeroTicks+=Stopwatch.GetTimestamp()-stage;stage=Stopwatch.GetTimestamp();}
                if(externalForces!=null)for(int i=0;i<externalForces.Length;i++)
                {
                    var command=externalForces[i];int region=bank.Topology.PartRegions[command.Part];
                    AddForce(bank,region,command.WorldPoint,command.WorldForce);torques[region]=torques[region]+command.WorldTorque;
                }
                Thermal(step);
                if(observeClock){Counters.ThermalTicks+=Stopwatch.GetTimestamp()-stage;stage=Stopwatch.GetTimestamp();}
                for(int r=0;r<regions;r++)
                {
                    OwnedRuntimeDynamics.StepCoupled(ref bank.RegionMotion[r],bank.Properties[r],forces[r],torques[r],oldWheel[r],newWheel[r],dt);
                    Counters.RegionIntegrations++;
                }
                if(observeClock)Counters.RegionIntegrationTicks+=Stopwatch.GetTimestamp()-stage;
                Counters.Steps++;
                PublishWheelMomentum(bank);
            }
            catch{Counters.FailedSteps++;step.Dispose();throw;}
            finally{if(observeClock)Counters.ObservedTicks+=Stopwatch.GetTimestamp()-begin;}
        }

        void Actuate(OwnedRuntimeStep step,OwnedRuntimeControls controls,bool observe=true)
        {ActuateBank(step.Bank,step.DeltaTime,controls,observe,false);}
        void LoadWheelMomentum(OwnedRuntimeBank bank,bool validate)
        {
            for(int row=0;row<wheelActuatorRows.Length;row++)
            {
                int i=wheelActuatorRows[row];double value=bank.ProviderState[wheelProviderSlots[i]];
                if(validate){AssemblyModel.Finite(value);if(Math.Abs(value)>actuators[i].WheelMomentumCapacity)throw new InvalidOperationException("Wheel momentum exceeds admitted capacity.");}
                wheelMomentum[i]=stagedWheelMomentum[i]=value;
            }
        }
        void PublishWheelMomentum(OwnedRuntimeBank bank)
        {for(int row=0;row<wheelActuatorRows.Length;row++){int i=wheelActuatorRows[row];bank.ProviderState[wheelProviderSlots[i]]=stagedWheelMomentum[i];}}
        void ActuateBank(OwnedRuntimeBank bank,double dt,OwnedRuntimeControls controls,bool observe,bool boundaryOnly)
        {
            validatedAvailability.Clear();
            var input=bank.Input;
            Vec translation=ControlOrientation.Rotate(new Vec(input.X,input.Y,input.Z)),
                attitude=ControlOrientation.Rotate(new Vec(input.Pitch,input.Yaw,input.Roll));
            bool compiled=executionStrategy==OwnedRuntimeExecutionStrategy.Compiled;
            int visits=compiled?executionPlan.ActuatorVisits.Length:actuators.Length;
            if(observe&&compiled)Counters.SkippedActuatorRows+=executionPlan.SkippedActuators;
            for(int row=0;row<visits;row++)
            {
                int i=compiled?executionPlan.ActuatorVisits[row]:row;
                if(observe)Counters.ActuatorRows++;var a=actuators[i];
                int body=compiled?executionPlan.Actuators[i].Body:partBodies[a.Part],region=compiled?executionPlan.Actuators[i].Region:bank.Topology.BodyRegions[body];
                Vec regionAxis=compiled?executionPlan.Actuators[i].Axis:(bank.Members[body].LocalRotation*definition.GetPart(a.Part).LocalRotation).Rotate(a.LocalAxis);
                if(observe&&compiled){Counters.CompiledActuatorRows++;Counters.RetainedGeometryRows++;}
                if(!boundaryOnly&&a.Kind==OwnedRuntimeActuatorKind.ReactionWheel)
                    oldWheel[region]=oldWheel[region]+regionAxis*wheelMomentum[i];
                bool allowed=enabled[i] && (a.Kind==OwnedRuntimeActuatorKind.Engine?controls.EnginesEnabled:
                    a.Kind==OwnedRuntimeActuatorKind.Rcs?controls.RcsEnabled:controls.WheelsEnabled);
                if(!allowed){if(observe&&compiled)Counters.InactiveActuatorRows++;if(!boundaryOnly&&a.Kind==OwnedRuntimeActuatorKind.ReactionWheel)newWheel[region]=newWheel[region]+regionAxis*wheelMomentum[i];continue;}
                double request=a.Kind==OwnedRuntimeActuatorKind.Engine?input.Throttle:
                    a.Kind==OwnedRuntimeActuatorKind.Rcs?Math.Max(0,OwnedRuntimeDynamics.Dot(regionAxis,translation)):
                    OwnedRuntimeDynamics.Dot(regionAxis,attitude);
                request=Math.Max(-1,Math.Min(1,request));
                if(request==0){if(!boundaryOnly&&a.Kind==OwnedRuntimeActuatorKind.ReactionWheel)newWheel[region]=newWheel[region]+regionAxis*wheelMomentum[i];continue;}
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
                    double demand=fuelMass*a.FuelMassFraction(f),available=Available(bank,compiled?executionPlan.FuelRoutes[i][f]:fuelPools[i][f],region,compiled,demand);
                    if(demand>0)fraction=Math.Min(fraction,available/demand);
                }
                if(electric>0)
                {
                    int e=a.ElectricResource;
                    double available=flowEnabled[e] && bank.Topology.PartRegions[resourceParts[e]]==region?bank.ResourceQuantity[e]:0;
                    fraction=Math.Min(fraction,available/electric);
                }
                fraction=Math.Max(0,Math.Min(1,fraction));
                if(fraction==0){if(!boundaryOnly&&a.Kind==OwnedRuntimeActuatorKind.ReactionWheel)newWheel[region]=newWheel[region]+regionAxis*wheelMomentum[i];continue;}
                for(int f=0;f<a.FuelCount;f++)Withdraw(bank,compiled?executionPlan.FuelRoutes[i][f]:fuelPools[i][f],region,fuelMass*a.FuelMassFraction(f)*fraction,observe?-1:i,compiled);
                if(electric>0){bank.WriteResourceQuantity(a.ElectricResource,Math.Max(0,bank.ResourceQuantity[a.ElectricResource]-electric*fraction));if(!observe&&boundaryInitialResources[a.ElectricResource]>0&&bank.ResourceQuantity[a.ElectricResource]==0)ReachedBoundary(i,a.ElectricResource,"resource-empty");}
                double delivered=a.MaximumForceOrTorque*request*fraction;
                Vec axis=stagingLocal||boundaryOnly?new Vec():bank.RegionMotion[region].Orientation.Rotate(regionAxis);
                if(a.Kind==OwnedRuntimeActuatorKind.ReactionWheel)
                {
                    stagedWheelMomentum[i]=Math.Max(-a.WheelMomentumCapacity,Math.Min(a.WheelMomentumCapacity,wheelMomentum[i]-delivered*dt));
                    double target=request>0?-a.WheelMomentumCapacity:a.WheelMomentumCapacity;
                    if(!observe&&wheelMomentum[i]!=target&&stagedWheelMomentum[i]==target)ReachedBoundary(i,-1,"wheel-capacity");
                    if(!boundaryOnly)newWheel[region]=newWheel[region]+regionAxis*stagedWheelMomentum[i];
                    if(observe)Counters.DeliveredTorqueNewtonMeterSeconds+=Math.Abs(delivered)*dt;
                }
                else if(!boundaryOnly)
                {
                    Vec point=compiled?executionPlan.Actuators[i].Point:LocalPoint(bank,a.Part,a.LocalPoint,body);
                    if(stagingLocal){Vec localForce=regionAxis*delivered;localActuatorForces[region]=localActuatorForces[region]+localForce;localActuatorTorques[region]=localActuatorTorques[region]+Vec.Cross(point+bank.Properties[region].CenterOfMass*-1,localForce);}
                    else AddLocalForce(bank,region,point,axis*delivered);
                    if(observe)Counters.DeliveredForceNewtonSeconds+=Math.Abs(delivered)*dt;
                }
                double energy=boundaryOnly?0:a.HeatWatts*magnitude*fraction*dt;if(!boundaryOnly)heat[a.Part]+=energy;
                if(observe){Counters.ActuatorHeatJoules+=energy;Counters.PropellantKilograms+=fuelMass*fraction;Counters.ElectricUnits+=electric*fraction;}
            }
        }

        double Available(OwnedRuntimeBank bank,int[] pool,int region,bool compiled,double demand)
        {
            bool first=validatedAvailability.Add(new AvailabilityRoute{Pool=pool,Region=region});
            if(!first&&demand==0)return 0;
            double sum=0;for(int i=0;i<pool.Length;i++){int r=pool[i];if(compiled||(flowEnabled[r] && bank.Topology.PartRegions[resourceParts[r]]==region))
            {
                sum+=bank.ResourceQuantity[r]*(compiled?executionPlan.ResourceMassPerUnit[r]:definition.GetResource(r).MassPerUnit);
                if(!first&&sum>=demand){AssemblyModel.Finite(sum);return sum;}
            }}AssemblyModel.Finite(sum);return sum;
        }
        void Withdraw(OwnedRuntimeBank bank,int[] pool,int region,double mass,int boundaryRow=-1,bool compiled=false)
        {
            for(int i=0;i<pool.Length && mass>0;i++)
            {
                int r=pool[i];if(!compiled&&(!flowEnabled[r] || bank.Topology.PartRegions[resourceParts[r]]!=region))continue;
                double density=compiled?executionPlan.ResourceMassPerUnit[r]:definition.GetResource(r).MassPerUnit;
                double take=Math.Min(bank.ResourceQuantity[r],mass/density);
                bank.WriteResourceQuantity(r,Math.Max(0,bank.ResourceQuantity[r]-take));mass-=take*density;
                if(boundaryRow>=0&&boundaryInitialResources[r]>0&&bank.ResourceQuantity[r]==0)ReachedBoundary(boundaryRow,r,"resource-empty");
            }
        }

        void Aerothermal(OwnedRuntimeStep step)
        {
            var bank=step.Bank;
            bool compiled=executionStrategy==OwnedRuntimeExecutionStrategy.Compiled;
            if(compiled)
            {
                bool active=false;
                for(int r=0;r<bank.RegionMotion.Length;r++)if(executionPlan.SurfaceCounts[r]>0&&environments[r].Density!=0)
                {regionOmega[r]=OwnedRuntimeDynamics.AngularVelocity(bank.Properties[r],bank.RegionMotion[r].Orientation,bank.RegionMotion[r].AngularMomentum);Counters.RegionMotionContexts++;active=true;}
                if(!active){Counters.SurfaceRows+=surfaces.Length;Counters.VacuumSurfaceRows+=surfaces.Length;return;}
            }
            for(int i=0;i<surfaces.Length;i++)
            {
                Counters.SurfaceRows++;var s=surfaces[i];int body=compiled?executionPlan.Surfaces[i].Body:partBodies[s.Part],region=compiled?executionPlan.Surfaces[i].Region:bank.Topology.BodyRegions[body];
                var environment=environments[region];
                if(compiled&&environment.Density==0){Counters.VacuumSurfaceRows++;continue;}
                if(compiled){Counters.CompiledSurfaceRows++;Counters.RetainedGeometryRows++;}
                var motion=bank.RegionMotion[region];Vec point=compiled?executionPlan.Surfaces[i].Point:LocalPoint(bank,s.Part,s.LocalPoint,body);
                Vec lever=motion.Orientation.Rotate(point+bank.Properties[region].CenterOfMass*-1);
                Vec omega=compiled?regionOmega[region]:OwnedRuntimeDynamics.AngularVelocity(bank.Properties[region],motion.Orientation,motion.AngularMomentum);
                Vec relative=motion.Velocity+Vec.Cross(omega+environment.FlowAngularVelocity*-1,lever)+environment.WindVelocity*-1;
                double speed2=OwnedRuntimeDynamics.Dot(relative,relative);if(speed2==0 || environment.Density==0)continue;
                double speed=Math.Sqrt(speed2),q=.5*environment.Density*speed2;
                Vec direction=relative*(1/speed);
                Vec normal=motion.Orientation.Rotate(compiled?executionPlan.Surfaces[i].Normal:(bank.Members[body].LocalRotation*definition.GetPart(s.Part).LocalRotation).Rotate(s.LocalNormal));
                double incidence=OwnedRuntimeDynamics.Dot(normal,direction);
                Vec lift=(normal+direction*(-incidence))*(-2*incidence*q*s.LiftArea);
                AddLocalForce(bank,region,point,direction*(-q*s.DragArea)+lift);
                heat[s.Part]+=q*s.DragArea*speed*s.HeatingFraction*step.DeltaTime;
            }
        }

        void Thermal(OwnedRuntimeStep step)
        {
            var bank=step.Bank;var state=bank.Thermal;RequireThermalPlan(bank);
            state.causalRevision=checked(state.causalRevision+1);
            if(!state.independent)
            {
                ThermalReference(step);state.integratedTime=step.ExpectedStamp.Time+step.DeltaTime;
                state.integratedCausalRevision=state.causalRevision;state.advances++;state.lastSubsteps=1;
                for(int p=0;p<heat.Length;p++)state.sourceJoules+=heat[p];return;
            }
            if(state.pendingDuration==0){state.planId=thermalPlanId;state.topologyRevision=bank.Topology.Revision;}
            int segment=state.segmentCount;if(segment>=OwnedRuntimeThermalState.SegmentCapacity)throw new InvalidOperationException("Thermal forcing segment capacity exceeded.");
            state.PrepareSegmentAppend();
            int offset=segment*heat.Length;state.segmentDuration[segment]=step.DeltaTime;state.segmentCount++;
            state.pendingDuration=step.ExpectedStamp.Time+step.DeltaTime-state.integratedTime;
            AssemblyModel.Positive(state.pendingDuration);
            for(int p=0;p<heat.Length;p++){state.heatJoules[p]+=heat[p];state.segmentHeat[offset+p]=heat[p];AssemblyModel.Finite(state.heatJoules[p]);}
            for(int i=0;i<surfaces.Length;i++)
            {
                var surface=surfaces[i];int p=surface.Part;var environment=environments[executionStrategy==OwnedRuntimeExecutionStrategy.Compiled?executionPlan.Surfaces[i].Region:bank.Topology.PartRegions[p]];double dt=step.DeltaTime;
                double convection=environment.ConvectiveCoefficient*surface.ThermalArea*dt,radiation=StefanBoltzmann*surface.Emissivity*surface.ThermalArea*dt;
                double ambient=environment.RadiationTemperature,ambient2=ambient*ambient;
                state.segmentConvection[offset+p]+=convection;state.segmentConvectionSource[offset+p]+=convection*environment.Temperature;
                state.segmentRadiation[offset+p]+=radiation;state.segmentRadiationSource[offset+p]+=radiation*ambient2*ambient2;
            }
            for(int p=0;p<heat.Length;p++){state.convectionExposure[p]+=state.segmentConvection[offset+p];state.convectionSource[p]+=state.segmentConvectionSource[offset+p];state.radiationExposure[p]+=state.segmentRadiation[offset+p];state.radiationSource[p]+=state.segmentRadiationSource[offset+p];}
            state.lastDeferredBoundKelvin=thermalSolver.DeferredBound(bank);
            if(state.segmentCount==OwnedRuntimeThermalState.SegmentCapacity || state.pendingDuration>=state.maximumLagSeconds || state.lastDeferredBoundKelvin>=state.maximumDeferredKelvin)thermalSolver.Flush(bank);
            else state.deferredSteps++;
        }
        void ThermalReference(OwnedRuntimeStep step)
        {
            var bank=step.Bank;double dt=step.DeltaTime;
            if(executionStrategy==OwnedRuntimeExecutionStrategy.Compiled)
            {
                if(thermalExecution==null||!thermalExecution.Matches(bank,configurationRevision))
                    thermalExecution=new OwnedRuntimeThermalExecution(bank,surfaces,links,configurationRevision,StefanBoltzmann);
                thermalExecution.Execute(bank,heat,environments,dt,Counters);return;
            }
            for(int p=0;p<heat.Length;p++)bank.Temperature[p]+=heat[p]/definition.GetPart(p).HeatCapacity;
            for(int i=0;i<surfaces.Length;i++)
            {
                var s=surfaces[i];var environment=environments[executionStrategy==OwnedRuntimeExecutionStrategy.Compiled?executionPlan.Surfaces[i].Region:bank.Topology.PartRegions[s.Part]];double t=bank.Temperature[s.Part],ambient=environment.RadiationTemperature;
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
