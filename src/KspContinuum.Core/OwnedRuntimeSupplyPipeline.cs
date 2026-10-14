using System;
using System.Collections.Generic;

namespace KspContinuum
{
    [Serializable] public sealed class OwnedRuntimeSupplyConfiguration
    {
        public OwnedRuntimeSupplyCapturePacket packet;public string supplySha256,frameId;
        public double[] originBodyMeters;public double runtimeEpoch;public long frameRevision;
    }
    [Serializable] public struct OwnedRuntimeSupplyObservation
    {
        public OwnedRuntimeStamp SourceStamp,PublishedStamp;
        public double UniversalTime,DeltaSeconds,GeneratedUnits,CreditedUnits,SpilledUnits,CommandRequestedUnits,CommandWithdrawnUnits;
        public int SolarRows,CommandRows,ReadyCommands;public string SourceSha256;
    }
    [Serializable] public struct OwnedRuntimeSupplyTotals
    {
        public long AcceptedIntervals;public double GeneratedUnits,CreditedUnits,SpilledUnits,CommandRequestedUnits,CommandWithdrawnUnits;
    }
    public sealed class OwnedRuntimeSupplyPipeline
    {
        readonly OwnedRuntimeDefinition definition;readonly OwnedRuntimeSupplyConfiguration config;
        readonly OwnedRuntimeSolarEphemeris ephemeris;readonly OwnedRuntimeSolarSupplySpec[] solar;readonly OwnedRuntimeCommandLoadSpec[] commands;
        readonly int[] resourceParts,ec,solarProviderSlots;readonly double[] rates,requested,initialResources;
        internal int BoundaryResource {get;private set;}
        public int SolarCount {get{return solar.Length;}}public int CommandCount {get{return commands.Length;}}
        public string SourceSha256 {get{return config.supplySha256;}}
        public OwnedRuntimeSupplyPipeline(OwnedRuntimeDefinition definition,OwnedRuntimeSupplyConfiguration configuration)
        {
            if(definition==null||configuration==null)throw new ArgumentNullException("Supply configuration");if(configuration.packet==null||configuration.packet.solar==null||configuration.packet.commands==null||configuration.packet.solar.Length>4096||configuration.packet.commands.Length>4096||configuration.packet.engines==null||configuration.packet.engines.Length>4096)throw new ArgumentException("Supply capture bounds differ.");this.definition=definition;config=Copy(configuration);OwnedRuntimeScalarSupply.Digest(config.supplySha256);
            var packet=config.packet;if(packet==null||packet.schema!="continuum-owned-clock-providers/v1"||!packet.epochUnchanged||!packet.identityUnchanged||packet.unityFrame!=packet.unityFrameEnd||packet.fixedTime!=packet.fixedTimeEnd||packet.universalTime!=packet.universalTimeEnd||packet.unsupported==null||packet.unsupported.Length!=0||packet.ephemeris==null||packet.solar==null||packet.commands==null||packet.solar.Length>4096||packet.commands.Length>4096)throw new ArgumentException("Supply acquisition/admission differs.");
            if(string.IsNullOrEmpty(config.frameId)||config.frameRevision<0)throw new ArgumentException("Supply frame differs.");AssemblyModel.Finite(config.runtimeEpoch);Vec origin=Vector(config.originBodyMeters);OwnedRuntimeDynamics.Require(origin);
            var e=packet.ephemeris;OwnedRuntimeScalarSupply.Digest(packet.nativeManifestSha256);OwnedRuntimeScalarSupply.Digest(packet.partSpecsSha256);if(e.sourceSha256!=packet.nativeManifestSha256||e.epoch!=packet.universalTime||e.model!="frozen-body-relative-sun/bounded-source-v1")throw new ArgumentException("Supply ephemeris source differs.");
            ephemeris=new OwnedRuntimeSolarEphemeris(e.sourceSha256,e.epoch,e.validSeconds,Vector(e.sunBodyRelative),Vector(e.referencePositionBodyMeters),e.referenceFluxMultiplier,e.bodyRadius);
            var pool=new List<int>();resourceParts=new int[definition.ResourceCount];for(int r=0;r<resourceParts.Length;r++){var resource=definition.GetResource(r);resourceParts[r]=definition.PartSlot(resource.PartId);if(resource.Kind=="ElectricCharge"){if(resource.MassPerUnit!=0)throw new ArgumentException("Electrical supply requires massless EC.");pool.Add(r);}}
            ec=pool.ToArray();if(ec.Length==0&&(packet.solar.Length>0||packet.commands.Length>0))throw new ArgumentException("Supply has no acquired EC storage.");
            solarProviderSlots=new int[packet.solar.Length];solar=new OwnedRuntimeSolarSupplySpec[packet.solar.Length];commands=new OwnedRuntimeCommandLoadSpec[packet.commands.Length];var ids=new HashSet<long>();var slots=new HashSet<int>();
            for(int i=0;i<solar.Length;i++)
            {var s=packet.solar[i];if(s==null||!ids.Add(s.id)||s.resourceKind!="ElectricCharge"||s.isTracking||s.useAnimation||s.useRaycastForTrackingDot||s.isBreakable||s.panelType!="FLAT")throw new ArgumentException("Solar support/identity differs.");int rateSlot=-1;for(int j=0;j<definition.ProviderStateCount;j++)if(definition.GetProviderKey(j)==s.rateProviderKey)rateSlot=j;if(s.rateProviderKey!=long.MinValue+s.id||rateSlot<0||!slots.Add(rateSlot))throw new ArgumentException("Solar rate provider binding differs.");solarProviderSlots[i]=rateSlot;solar[i]=new OwnedRuntimeSolarSupplySpec(s.id,definition.PartSlot(s.partId),s.resourceKind,s.nominalUnitsPerSecond,Vector(s.localPoint),Vector(s.localNormal),s.efficiencyMultiplier,s.launchUniversalTime,Curve(s.temperatureEfficiency),Curve(s.ageEfficiency),s.enabled);}
            for(int i=0;i<commands.Length;i++)
            {var c=packet.commands[i];if(c==null||!ids.Add(c.id)||c.resourceKind!="ElectricCharge"||c.minimumCrew!=0||c.readinessProviderKey!=long.MinValue+c.id)throw new ArgumentException("Command support/identity differs.");int slot=-1;for(int p=0;p<definition.ProviderStateCount;p++)if(definition.GetProviderKey(p)==c.readinessProviderKey)slot=p;if(slot<0||!slots.Add(slot))throw new ArgumentException("Command readiness provider binding differs.");commands[i]=new OwnedRuntimeCommandLoadSpec(c.id,definition.PartSlot(c.partId),c.resourceKind,c.unitsPerSecond,c.hibernationMultiplier,slot,c.hibernating,c.enabled);}
            rates=new double[solar.Length];requested=new double[commands.Length];initialResources=new double[definition.ResourceCount];
        }
        public OwnedRuntimeSupplyConfiguration CaptureConfiguration(){return Copy(config);}
        public void RequireSource(OwnedRuntimeStamp stamp,double delta)
        {
            AssemblyModel.Positive(delta);if(stamp.FrameRevision!=config.frameRevision||stamp.FrameId!=config.frameId)throw new InvalidOperationException("Supply frame changed; reconfigure its numeric origin before advancing.");
            double start=config.packet.universalTime+(stamp.Time-config.runtimeEpoch),end=config.packet.universalTime+((stamp.Time+delta)-config.runtimeEpoch);ephemeris.RequireTime(start);ephemeris.RequireTime(end);
        }
        internal OwnedRuntimeSupplyObservation Apply(OwnedRuntimeBank bank,OwnedRuntimeStamp stamp,double delta,bool[] flow)
        {
            if(bank==null||!ReferenceEquals(bank.Definition,definition)||flow==null||flow.Length!=definition.ResourceCount)throw new ArgumentException("Supply bank/configuration differs.");RequireSource(stamp,delta);
            Array.Copy(bank.ResourceQuantity,initialResources,initialResources.Length);BoundaryResource=-1;double ut=config.packet.universalTime+(stamp.Time-config.runtimeEpoch);Vec origin=Vector(config.originBodyMeters);
            // Validate every geometry/curve/request before changing provisional resource or provider rows.
            for(int i=0;i<solar.Length;i++)
            {
                var s=solar[i];var p=definition.GetPart(s.Part);int b=definition.BodySlot(p.BodyId),r=bank.Topology.BodyRegions[b];var member=bank.Members[b];var motion=bank.RegionMotion[r];
                Vec point=origin+motion.RootPosition+motion.Orientation.Rotate(member.LocalPosition+member.LocalRotation.Rotate(p.LocalPosition+p.LocalRotation.Rotate(s.LocalPoint)));
                Vec normal=motion.Orientation.Rotate((member.LocalRotation*p.LocalRotation).Rotate(s.LocalNormal));
                rates[i]=OwnedRuntimeScalarSupply.Evaluate(s,ephemeris,ut,point,normal,bank.Temperature[s.Part]).UnitsPerSecond;OwnedRuntimeEnvironment.Nonnegative(rates[i]*delta);
            }
            for(int i=0;i<commands.Length;i++){double rate=OwnedRuntimeScalarSupply.CommandRate(commands[i]);requested[i]=rate*delta;OwnedRuntimeEnvironment.Nonnegative(requested[i]);if(rate>0&&requested[i]==0)throw new ArgumentException("Positive command demand is not representable at this interval.");}
            var result=new OwnedRuntimeSupplyObservation{SourceStamp=stamp,UniversalTime=ut,DeltaSeconds=delta,SourceSha256=config.supplySha256,SolarRows=solar.Length,CommandRows=commands.Length};
            for(int i=0;i<solar.Length;i++)
            {
                bank.ProviderState[solarProviderSlots[i]]=rates[i];double left=rates[i]*delta;result.GeneratedUnits+=left;int region=bank.Topology.PartRegions[solar[i].Part];
                foreach(int resource in ec)if(flow[resource]&&bank.Topology.PartRegions[resourceParts[resource]]==region&&left>0)
                {double capacity=definition.GetResource(resource).Capacity,available=Math.Max(0,capacity-bank.ResourceQuantity[resource]),take=Math.Min(left,available);double old=bank.ResourceQuantity[resource],next=Math.Min(capacity,old+take);if(old>capacity)continue;if(next-old>take)next=Previous(next);double credited=next-old;bank.ResourceQuantity[resource]=next;left=Math.Max(0,left-credited);result.CreditedUnits+=credited;}
                result.SpilledUnits+=left;
            }
            for(int i=0;i<commands.Length;i++)
            {
                var c=commands[i];double left=requested[i],withdrawn=0;int region=bank.Topology.PartRegions[c.Part];result.CommandRequestedUnits+=left;
                foreach(int resource in ec)if(flow[resource]&&bank.Topology.PartRegions[resourceParts[resource]]==region&&left>0)
                {double old=bank.ResourceQuantity[resource],take=Math.Min(old,left),next=Math.Max(0,old-take);if(old-next>take)next=Next(next);double actual=old-next;bank.ResourceQuantity[resource]=next;if(initialResources[resource]>0&&next==0&&BoundaryResource<0)BoundaryResource=resource;left=Math.Max(0,left-actual);withdrawn+=actual;}
                bool ready=OwnedRuntimeScalarSupply.CommandReady(c,requested[i],Math.Min(requested[i],withdrawn));bank.ProviderState[c.ReadinessProviderSlot]=ready?1:0;if(ready)result.ReadyCommands++;result.CommandWithdrawnUnits+=withdrawn;
            }
            OwnedRuntimeEnvironment.Nonnegative(result.GeneratedUnits);OwnedRuntimeEnvironment.Nonnegative(result.CreditedUnits);OwnedRuntimeEnvironment.Nonnegative(result.SpilledUnits);OwnedRuntimeEnvironment.Nonnegative(result.CommandRequestedUnits);OwnedRuntimeEnvironment.Nonnegative(result.CommandWithdrawnUnits);return result;
        }
        static Vec Vector(double[] v){if(v==null||v.Length!=3)throw new ArgumentException("Supply vector shape differs.");var result=new Vec(v[0],v[1],v[2]);OwnedRuntimeDynamics.Require(result);return result;}
        static double Previous(double value){return BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(value)-1);}
        static double Next(double value){return BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(value)+1);}
        static OwnedRuntimeSupplyCurve Curve(OwnedRuntimeSupplyCurveData data){if(data==null||data.model!="finite-unweighted-Hermite/clamped-endpoints-v1"||data.preWrapMode==2||data.preWrapMode==4||data.postWrapMode==2||data.postWrapMode==4)throw new ArgumentException("Supply curve absent/unsupported.");return new OwnedRuntimeSupplyCurve(data.time,data.value,data.inTangent,data.outTangent);}
        static double[] A(double[] a){return a==null?null:(double[])a.Clone();}
        static OwnedRuntimeSupplyCurveData C(OwnedRuntimeSupplyCurveData c){return c==null?null:new OwnedRuntimeSupplyCurveData{model=c.model,preWrapMode=c.preWrapMode,postWrapMode=c.postWrapMode,time=A(c.time),value=A(c.value),inTangent=A(c.inTangent),outTangent=A(c.outTangent)};}
        internal static OwnedRuntimeSupplyConfiguration Copy(OwnedRuntimeSupplyConfiguration source)
        {
            if(source==null)return null;var p=source.packet;if(p==null)throw new ArgumentException("Supply packet absent.");var e=p.ephemeris;
            var q=new OwnedRuntimeSupplyCapturePacket{schema=p.schema,runId=p.runId,vesselId=p.vesselId,nativeManifestSha256=p.nativeManifestSha256,partSpecsSha256=p.partSpecsSha256,sourceAssemblySha256=p.sourceAssemblySha256,sourceAssemblyMvid=p.sourceAssemblyMvid,modelScope=p.modelScope,processId=p.processId,unityFrame=p.unityFrame,unityFrameEnd=p.unityFrameEnd,fixedTime=p.fixedTime,fixedTimeEnd=p.fixedTimeEnd,universalTime=p.universalTime,universalTimeEnd=p.universalTimeEnd,startedTicks=p.startedTicks,copiedTicks=p.copiedTicks,clockFrequency=p.clockFrequency,epochUnchanged=p.epochUnchanged,identityUnchanged=p.identityUnchanged,unsupported=p.unsupported==null?null:(string[])p.unsupported.Clone(),idleTransmitters=p.idleTransmitters==null?null:(string[])p.idleTransmitters.Clone(),ephemeris=e==null?null:new OwnedRuntimeSupplyEphemerisData{sourceSha256=e.sourceSha256,bodyId=e.bodyId,model=e.model,epoch=e.epoch,validSeconds=e.validSeconds,bodyRadius=e.bodyRadius,referenceDistanceMeters=e.referenceDistanceMeters,referenceFluxMultiplier=e.referenceFluxMultiplier,solarLuminosity=e.solarLuminosity,solarLuminosityAtHome=e.solarLuminosityAtHome,sunBodyRelative=A(e.sunBodyRelative),referencePositionBodyMeters=A(e.referencePositionBodyMeters)}};
            if(p.solar!=null){q.solar=new OwnedRuntimeCapturedSolarSupply[p.solar.Length];for(int i=0;i<q.solar.Length;i++){var s=p.solar[i];if(s==null)continue;q.solar[i]=new OwnedRuntimeCapturedSolarSupply{id=s.id,rateProviderKey=s.rateProviderKey,partId=s.partId,moduleIndex=s.moduleIndex,sourceType=s.sourceType,definitionSha256=s.definitionSha256,panelType=s.panelType,deployState=s.deployState,resourceKind=s.resourceKind,enabled=s.enabled,isTracking=s.isTracking,useAnimation=s.useAnimation,useRaycastForTrackingDot=s.useRaycastForTrackingDot,isBreakable=s.isBreakable,nominalUnitsPerSecond=s.nominalUnitsPerSecond,efficiencyMultiplier=s.efficiencyMultiplier,launchUniversalTime=s.launchUniversalTime,capturedSkinTemperature=s.capturedSkinTemperature,localPoint=A(s.localPoint),localNormal=A(s.localNormal),temperatureEfficiency=C(s.temperatureEfficiency),ageEfficiency=C(s.ageEfficiency)};}}
            if(p.commands!=null){q.commands=new OwnedRuntimeCapturedCommandLoad[p.commands.Length];for(int i=0;i<q.commands.Length;i++){var c=p.commands[i];if(c==null)continue;q.commands[i]=new OwnedRuntimeCapturedCommandLoad{id=c.id,readinessProviderKey=c.readinessProviderKey,partId=c.partId,moduleIndex=c.moduleIndex,minimumCrew=c.minimumCrew,sourceType=c.sourceType,definitionSha256=c.definitionSha256,sourceModuleState=c.sourceModuleState,resourceKind=c.resourceKind,enabled=c.enabled,hibernating=c.hibernating,hibernateOnWarp=c.hibernateOnWarp,initialReady=c.initialReady,unitsPerSecond=c.unitsPerSecond,hibernationMultiplier=c.hibernationMultiplier};}}
            // Engine metadata remains source evidence; actual actuator definitions live in the existing systems owner.
            if(p.engines!=null){q.engines=new OwnedRuntimeCapturedEngineState[p.engines.Length];for(int i=0;i<q.engines.Length;i++){var s=p.engines[i];if(s==null)continue;var d=new OwnedRuntimeCapturedEngineState{id=s.id,partId=s.partId,moduleIndex=s.moduleIndex,sourceType=s.sourceType,definitionSha256=s.definitionSha256,modelRecipe=s.modelRecipe,thrustFrame=s.thrustFrame,enabled=s.enabled,ignited=s.ignited,flameout=s.flameout,useEngineResponseTime=s.useEngineResponseTime,throttleLocked=s.throttleLocked,useAtmCurve=s.useAtmCurve,useVelCurve=s.useVelCurve,useAtmCurveIsp=s.useAtmCurveIsp,useVelCurveIsp=s.useVelCurveIsp,currentThrottle=s.currentThrottle,requestedThrottle=s.requestedThrottle,authorityFraction=s.authorityFraction,maxForceNewtons=s.maxForceNewtons,specificImpulseSeconds=s.specificImpulseSeconds,atmosphereIsp=C(s.atmosphereIsp),thrustTransformMultipliers=A(s.thrustTransformMultipliers)};
                if(s.thrusters!=null){d.thrusters=new OwnedRuntimeCapturedThruster[s.thrusters.Length];for(int t=0;t<d.thrusters.Length;t++)if(s.thrusters[t]!=null)d.thrusters[t]=new OwnedRuntimeCapturedThruster{positionInRoot=A(s.thrusters[t].positionInRoot),forceDirectionInRoot=A(s.thrusters[t].forceDirectionInRoot)};}if(s.propellants!=null){d.propellants=new OwnedRuntimeCapturedPropellant[s.propellants.Length];for(int t=0;t<d.propellants.Length;t++)if(s.propellants[t]!=null)d.propellants[t]=new OwnedRuntimeCapturedPropellant{resource=s.propellants[t].resource,ratio=s.propellants[t].ratio};}q.engines[i]=d;}}
            return new OwnedRuntimeSupplyConfiguration{packet=q,supplySha256=source.supplySha256,frameId=source.frameId,frameRevision=source.frameRevision,runtimeEpoch=source.runtimeEpoch,originBodyMeters=A(source.originBodyMeters)};
        }
    }
}
