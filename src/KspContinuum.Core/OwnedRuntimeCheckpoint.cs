using System;

namespace KspContinuum
{
    [Serializable] public sealed class OwnedRuntimeCheckpointData
    {
        public string schema="continuum-owned-runtime-checkpoint/v4",definitionId,frameId;
        public int executionStrategy;
        public OwnedRuntimeThermalState thermal;
        public OwnedRuntimeMechanicalState mechanical;
        public long definitionRevision,epoch,stateRevision,topologyRevision,frameRevision,systemsRevision;
        public double time,fixedDelta,warpRate;
        public OwnedRuntimeSourceBinding source;
        public OwnedRuntimeCheckpointPart[] parts;
        public OwnedRuntimeCheckpointBody[] bodies;
        public OwnedRuntimeCheckpointResource[] resources;
        public long[] providerKeys,regionIds;
        public OwnedRuntimeCheckpointMember[] members;
        public OwnedRuntimeCheckpointConnection[] connections;
        public OwnedRuntimeCheckpointMotion[] motions;
        public double[] resourceQuantity,temperature,providerState,input,controls,controlOrientation;
        public bool[] controlEnabled,resourceFlowEnabled;
        public OwnedRuntimeCheckpointInput[] inputHistory;
        public OwnedRuntimeCheckpointActuator[] actuators;
        public OwnedRuntimeCheckpointSurface[] surfaces;
        public OwnedRuntimeCheckpointThermalLink[] thermalLinks;
        public OwnedRuntimeCheckpointEnvironment environment;
    }
    [Serializable] public sealed class OwnedRuntimeCheckpointPart
    {public long id,bodyId;public string name;public double[] position,rotation;public double heatCapacity,initialTemperature;}
    [Serializable] public sealed class OwnedRuntimeCheckpointBody
    {public long id;public OwnedRuntimeCheckpointMember member;}
    [Serializable] public sealed class OwnedRuntimeCheckpointMember
    {public int id;public double mass;public double[] position,rotation,center,principal,principalRotation;}
    [Serializable] public sealed class OwnedRuntimeCheckpointResource
    {public long id,partId;public string kind;public double capacity,massPerUnit,initialQuantity;public bool acquiredSurplus;}
    [Serializable] public sealed class OwnedRuntimeCheckpointConnection
    {public long id,partA,partB;public bool locked;}
    [Serializable] public sealed class OwnedRuntimeCheckpointMotion
    {public double[] rootPosition,rotation,velocity,angularMomentum;}
    [Serializable] public sealed class OwnedRuntimeCheckpointInput
    {public long sequence;public double startedAt,deltaTime;public double[] values;}
    [Serializable] public sealed class OwnedRuntimeCheckpointActuator
    {
        public long partId,fuelResourceId,electricResourceId;
        public int kind,wheelProviderSlot;
        public bool enabled;
        public double[] point,axis,fuelMassFractions;
        public string[] fuelKinds;
        public double maximum,specificImpulse,electricRate,heatWatts,wheelCapacity;
    }
    [Serializable] public sealed class OwnedRuntimeCheckpointSurface
    {public long partId;public double[] point,normal;public double dragArea,liftArea,thermalArea,emissivity,heatingFraction;}
    [Serializable] public sealed class OwnedRuntimeCheckpointThermalLink
    {public long partA,partB;public double conductance;}
    [Serializable] public sealed class OwnedRuntimeCheckpointEnvironment
    {public string sourceId;public long revision;public double epoch,density,temperature,convection,radiation;public double[] wind,gravity;}
    public sealed class OwnedRuntimeRestoredState
    {
        public OwnedRuntimeWorld World;
        public OwnedRuntimeSystems Systems;
        public OwnedRuntimeSourceBinding Source;
        public OwnedRuntimeControls Controls;
        public OwnedRuntimeEnvironment Environment;
        public OwnedRuntimeStepPlan Plan;
    }
    public static class OwnedRuntimeCheckpoint
    {
        internal static double[] V(Vec v){return new[]{v.X,v.Y,v.Z};}
        internal static double[] Q(Rotation q){return new[]{q.X,q.Y,q.Z,q.W};}
        internal static Vec V(double[] v){OwnedRuntimeImport.Vector(v,3);return new Vec(v[0],v[1],v[2]);}
        internal static Rotation Q(double[] q){OwnedRuntimeImport.Vector(q,4);return Rotation.FromUnitComponents(q[0],q[1],q[2],q[3]);}
        static double[] Input(OwnedRuntimeInput input){return new[]{input.Throttle,input.Pitch,input.Yaw,input.Roll,input.X,input.Y,input.Z};}
        static OwnedRuntimeInput Input(double[] values){OwnedRuntimeImport.Vector(values,7);return new OwnedRuntimeInput(values[0],values[1],values[2],values[3],values[4],values[5],values[6]);}
        static OwnedRuntimeCheckpointMember Member(RigidAssemblyMember m)
        {return new OwnedRuntimeCheckpointMember{id=m.Id,mass=m.Mass,position=V(m.LocalPosition),rotation=Q(m.LocalRotation),center=V(m.LocalCenterOfMass),principal=V(m.PrincipalInertia),principalRotation=Q(m.PrincipalRotation)};}
        static RigidAssemblyMember Member(OwnedRuntimeCheckpointMember m)
        {OwnedRuntimeImport.Need(m!=null,"Checkpoint member absent.");return new RigidAssemblyMember(m.id,m.mass,V(m.position),Q(m.rotation),V(m.center),V(m.principal),Q(m.principalRotation));}
        static double[] Copy(double[] a){OwnedRuntimeImport.Need(a!=null,"Checkpoint state array absent.");return (double[])a.Clone();}
        internal static OwnedRuntimeSourceBinding CopySource(OwnedRuntimeSourceBinding s)
        {
            OwnedRuntimeImport.Need(s!=null&&s.schema=="continuum-owned-runtime-source/v1","Checkpoint source schema differs.");
            OwnedRuntimeImport.Need(s.resourcePolicy=="strict-capacity/v1"||s.resourcePolicy=="acquired-initial-surplus-consume-only/v1","Checkpoint resource policy differs.");
            OwnedRuntimeImport.Digest(s.nativeManifestSha256);OwnedRuntimeImport.Digest(s.partSpecsSha256);OwnedRuntimeImport.Text(s.runId);OwnedRuntimeImport.Text(s.vesselId);OwnedRuntimeImport.Text(s.scope);
            OwnedRuntimeImport.Need(s.processId>0&&s.bodies!=null&&s.bodies.Length>0&&s.bodies.Length<=50000&&s.renderers!=null&&s.renderers.Length<=50000&&s.omissions!=null&&s.omissions.Length<=200000,"Checkpoint source coverage absent/excessive.");
            if(!string.IsNullOrEmpty(s.rendererManifestSha256))OwnedRuntimeImport.Digest(s.rendererManifestSha256);
            if(!string.IsNullOrEmpty(s.colliderGeometrySha256))OwnedRuntimeImport.Digest(s.colliderGeometrySha256);
            OwnedRuntimeImport.Vector(s.subtractedNativeCenterVelocity,3);OwnedRuntimeImport.Vector(s.sourceFrameVelocity,3);
            var result=new OwnedRuntimeSourceBinding{runId=s.runId,vesselId=s.vesselId,processId=s.processId,nativeManifestSha256=s.nativeManifestSha256,partSpecsSha256=s.partSpecsSha256,rendererManifestSha256=s.rendererManifestSha256??"",colliderGeometrySha256=s.colliderGeometrySha256??"",scope=s.scope,resourcePolicy=s.resourcePolicy,subtractedNativeCenterVelocity=Copy(s.subtractedNativeCenterVelocity),sourceFrameVelocity=Copy(s.sourceFrameVelocity),bodies=new OwnedRuntimeBodyBinding[s.bodies.Length],renderers=new OwnedRuntimeRendererBinding[s.renderers.Length],omissions=(string[])s.omissions.Clone()};
            var bodies=new System.Collections.Generic.HashSet<long>();var sourceIds=new System.Collections.Generic.HashSet<int>();
            for(int i=0;i<s.bodies.Length;i++){var b=s.bodies[i];OwnedRuntimeImport.Need(b!=null&&b.sourceBodyId!=0&&b.bodySlot==i&&b.bodyId==b.sourceBodyId&&bodies.Add(b.bodyId)&&sourceIds.Add(b.sourceBodyId),"Checkpoint source body mapping invalid.");result.bodies[i]=new OwnedRuntimeBodyBinding{sourceBodyId=b.sourceBodyId,bodyId=b.bodyId,bodySlot=b.bodySlot};}
            var rendererIds=new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            for(int i=0;i<s.renderers.Length;i++){var r=s.renderers[i];OwnedRuntimeImport.Need(r!=null&&rendererIds.Add(r.id??"")&&r.sourceRendererId!=0&&r.partId>0&&r.meshIndex>=0&&r.submeshIndex>=0&&r.materialIndex>=0,"Checkpoint renderer binding invalid.");OwnedRuntimeImport.Text(r.id);OwnedRuntimeImport.Vector(r.localToPart,16);var a=r.localToPart;double determinant=a[0]*(a[5]*a[10]-a[6]*a[9])-a[1]*(a[4]*a[10]-a[6]*a[8])+a[2]*(a[4]*a[9]-a[5]*a[8]);OwnedRuntimeImport.Number(determinant);OwnedRuntimeImport.Need(a[12]==0&&a[13]==0&&a[14]==0&&a[15]==1&&Math.Abs(determinant)>1e-30,"Checkpoint renderer binding not invertible affine.");result.renderers[i]=new OwnedRuntimeRendererBinding{id=r.id,sourceRendererId=r.sourceRendererId,partId=r.partId,meshIndex=r.meshIndex,submeshIndex=r.submeshIndex,materialIndex=r.materialIndex,visible=r.visible,localToPart=Copy(r.localToPart)};}
            foreach(string omission in result.omissions)OwnedRuntimeImport.Text(omission);
            return result;
        }
        public static OwnedRuntimeCheckpointData Capture(OwnedRuntimeImportResult runtime,OwnedRuntimeControls controls,OwnedRuntimeEnvironment environment,OwnedRuntimeStepPlan plan)
        {
            OwnedRuntimeImport.Need(runtime!=null&&runtime.World!=null&&runtime.Systems!=null&&environment!=null&&plan!=null,"Checkpoint requires a complete runtime context.");controls.Validate();
            var s=runtime.World.CaptureSnapshot();var d=s.Definition;var systems=runtime.Systems;
            var c=new OwnedRuntimeCheckpointData{definitionId=d.Id,definitionRevision=d.Revision,epoch=s.Stamp.Epoch,stateRevision=s.Stamp.StateRevision,topologyRevision=s.Stamp.TopologyRevision,frameRevision=s.Stamp.FrameRevision,time=s.Stamp.Time,frameId=s.Stamp.FrameId,systemsRevision=systems.ConfigurationRevision,source=CopySource(runtime.Source),fixedDelta=plan.FixedDelta,warpRate=plan.WarpRate,
                executionStrategy=(int)systems.ExecutionStrategy,
                parts=new OwnedRuntimeCheckpointPart[d.PartCount],bodies=new OwnedRuntimeCheckpointBody[d.BodyCount],resources=new OwnedRuntimeCheckpointResource[d.ResourceCount],providerKeys=new long[d.ProviderStateCount],members=new OwnedRuntimeCheckpointMember[s.BodyMembers.Length],connections=new OwnedRuntimeCheckpointConnection[s.Connections.Length],regionIds=(long[])s.RegionIds.Clone(),motions=new OwnedRuntimeCheckpointMotion[s.RegionMotion.Length],resourceQuantity=Copy(s.ResourceQuantity),temperature=Copy(s.Temperature),providerState=Copy(s.ProviderState),input=Input(s.Input),inputHistory=new OwnedRuntimeCheckpointInput[s.InputHistory.Length],
                controls=new[]{controls.Throttle,controls.Attitude.X,controls.Attitude.Y,controls.Attitude.Z,controls.Translation.X,controls.Translation.Y,controls.Translation.Z},controlEnabled=new[]{controls.EnginesEnabled,controls.RcsEnabled,controls.WheelsEnabled},actuators=new OwnedRuntimeCheckpointActuator[systems.ActuatorCount],surfaces=new OwnedRuntimeCheckpointSurface[systems.SurfaceCount],thermalLinks=new OwnedRuntimeCheckpointThermalLink[systems.ThermalLinkCount],resourceFlowEnabled=new bool[d.ResourceCount],
                thermal=s.Thermal.Clone(),mechanical=s.Mechanical.Clone(),controlOrientation=Q(systems.ControlOrientation),environment=new OwnedRuntimeCheckpointEnvironment{sourceId=environment.SourceId,revision=environment.Revision,epoch=environment.Epoch,density=environment.Density,temperature=environment.Temperature,convection=environment.ConvectiveCoefficient,radiation=environment.RadiationTemperature,wind=V(environment.WindVelocity),gravity=V(environment.Gravity)}};
            for(int i=0;i<d.PartCount;i++){var p=d.GetPart(i);c.parts[i]=new OwnedRuntimeCheckpointPart{id=p.Id,name=p.Name,bodyId=p.BodyId,position=V(p.LocalPosition),rotation=Q(p.LocalRotation),heatCapacity=p.HeatCapacity,initialTemperature=p.InitialTemperature};}
            for(int i=0;i<d.BodyCount;i++)c.bodies[i]=new OwnedRuntimeCheckpointBody{id=d.GetBody(i).Id,member=Member(d.GetBody(i).Member)};
            for(int i=0;i<d.ResourceCount;i++){var r=d.GetResource(i);c.resources[i]=new OwnedRuntimeCheckpointResource{id=r.Id,partId=r.PartId,kind=r.Kind,capacity=r.Capacity,massPerUnit=r.MassPerUnit,initialQuantity=r.InitialQuantity,acquiredSurplus=r.AcquiredSurplus};c.resourceFlowEnabled[i]=systems.IsResourceFlowEnabled(i);}
            for(int i=0;i<d.ProviderStateCount;i++)c.providerKeys[i]=d.GetProviderKey(i);
            for(int i=0;i<c.members.Length;i++)c.members[i]=Member(s.BodyMembers[i]);
            for(int i=0;i<c.connections.Length;i++){var a=s.Connections[i];c.connections[i]=new OwnedRuntimeCheckpointConnection{id=a.Id,partA=a.PartA,partB=a.PartB,locked=a.Locked};}
            for(int i=0;i<c.motions.Length;i++){var m=s.RegionMotion[i];c.motions[i]=new OwnedRuntimeCheckpointMotion{rootPosition=V(m.RootPosition),rotation=Q(m.Orientation),velocity=V(m.Velocity),angularMomentum=V(m.AngularMomentum)};}
            for(int i=0;i<c.inputHistory.Length;i++){var h=s.InputHistory[i];c.inputHistory[i]=new OwnedRuntimeCheckpointInput{sequence=h.Sequence,startedAt=h.StartedAt,deltaTime=h.DeltaTime,values=Input(h.Input)};}
            for(int i=0;i<c.actuators.Length;i++)
            {
                var a=systems.GetActuator(i);int n=a.FuelResource<0?a.FuelCount:0;var kinds=new string[n];var fractions=new double[n];for(int j=0;j<n;j++){kinds[j]=a.FuelKind(j);fractions[j]=a.FuelMassFraction(j);}
                c.actuators[i]=new OwnedRuntimeCheckpointActuator{partId=d.GetPart(a.Part).Id,kind=(int)a.Kind,point=V(a.LocalPoint),axis=V(a.LocalAxis),maximum=a.MaximumForceOrTorque,fuelResourceId=a.FuelResource<0?-1:d.GetResource(a.FuelResource).Id,electricResourceId=a.ElectricResource<0?-1:d.GetResource(a.ElectricResource).Id,specificImpulse=a.SpecificImpulseSeconds,electricRate=a.ElectricUnitsPerSecond,heatWatts=a.HeatWatts,wheelCapacity=a.WheelMomentumCapacity,fuelKinds=kinds,fuelMassFractions=fractions,enabled=systems.IsEnabled(i),wheelProviderSlot=systems.WheelProviderSlot(i)};
            }
            for(int i=0;i<c.surfaces.Length;i++){var a=systems.GetSurface(i);c.surfaces[i]=new OwnedRuntimeCheckpointSurface{partId=d.GetPart(a.Part).Id,point=V(a.LocalPoint),normal=V(a.LocalNormal),dragArea=a.DragArea,liftArea=a.LiftArea,thermalArea=a.ThermalArea,emissivity=a.Emissivity,heatingFraction=a.HeatingFraction};}
            for(int i=0;i<c.thermalLinks.Length;i++){var a=systems.GetThermalLink(i);c.thermalLinks[i]=new OwnedRuntimeCheckpointThermalLink{partA=d.GetPart(a.A).Id,partB=d.GetPart(a.B).Id,conductance=a.ConductanceWattsPerKelvin};}
            return c;
        }
        public static OwnedRuntimeRestoredState Restore(OwnedRuntimeCheckpointData c,string expectedNativeSha,string expectedPartSpecsSha)
        {
            OwnedRuntimeImport.Digest(expectedNativeSha);OwnedRuntimeImport.Digest(expectedPartSpecsSha);
            OwnedRuntimeImport.Need(c!=null&&(c.schema=="continuum-owned-runtime-checkpoint/v1"||c.schema=="continuum-owned-runtime-checkpoint/v2"||c.schema=="continuum-owned-runtime-checkpoint/v3"||c.schema=="continuum-owned-runtime-checkpoint/v4"),"Checkpoint schema differs.");
            OwnedRuntimeImport.Need(c.schema=="continuum-owned-runtime-checkpoint/v1"?c.thermal==null:c.thermal!=null,"Checkpoint thermal schema/presence differs.");
            OwnedRuntimeImport.Need(c.schema=="continuum-owned-runtime-checkpoint/v3"||c.schema=="continuum-owned-runtime-checkpoint/v4"?c.mechanical!=null:c.mechanical==null,"Checkpoint mechanical schema/presence differs.");
            OwnedRuntimeImport.Need(c.schema=="continuum-owned-runtime-checkpoint/v4"?c.executionStrategy>=0&&c.executionStrategy<=1:c.executionStrategy==0,"Checkpoint execution strategy differs.");
            var source=CopySource(c.source);OwnedRuntimeImport.Need(source.nativeManifestSha256==expectedNativeSha&&source.partSpecsSha256==expectedPartSpecsSha,"Checkpoint frozen source differs.");
            OwnedRuntimeImport.Need(c.parts!=null&&c.parts.Length>0&&c.parts.Length<=65536&&c.bodies!=null&&c.bodies.Length>0&&c.bodies.Length<=c.parts.Length&&c.resources!=null&&c.resources.Length<=1048576&&c.providerKeys!=null&&c.providerKeys.Length<=1048576&&c.members!=null&&c.members.Length==c.bodies.Length&&c.connections!=null&&c.connections.Length<=1048576&&c.motions!=null&&c.motions.Length>0&&c.motions.Length<=c.bodies.Length&&c.regionIds!=null&&c.regionIds.Length==c.motions.Length&&c.inputHistory!=null&&c.inputHistory.Length<=256,"Checkpoint table bounds differ.");
            OwnedRuntimeImport.Need(c.actuators!=null&&c.actuators.Length<=200000&&c.surfaces!=null&&c.surfaces.Length<=200000&&c.thermalLinks!=null&&c.thermalLinks.Length<=200000&&c.resourceFlowEnabled!=null&&c.resourceFlowEnabled.Length==c.resources.Length&&c.controlEnabled!=null&&c.controlEnabled.Length==3&&c.environment!=null,"Checkpoint systems/configuration incomplete.");
            var parts=new OwnedRuntimePartDefinition[c.parts.Length];var bodies=new OwnedRuntimeBodyDefinition[c.bodies.Length];var resources=new OwnedRuntimeResourceDefinition[c.resources.Length];
            for(int i=0;i<parts.Length;i++){var p=c.parts[i];OwnedRuntimeImport.Need(p!=null&&(i==0||c.parts[i-1].id<p.id),"Checkpoint part order/identity differs.");parts[i]=new OwnedRuntimePartDefinition(p.id,p.name,p.bodyId,V(p.position),Q(p.rotation),p.heatCapacity,p.initialTemperature);}
            for(int i=0;i<bodies.Length;i++){var b=c.bodies[i];OwnedRuntimeImport.Need(b!=null&&(i==0||c.bodies[i-1].id<b.id),"Checkpoint body order/identity differs.");bodies[i]=new OwnedRuntimeBodyDefinition(b.id,Member(b.member));}
            for(int i=0;i<resources.Length;i++){var r=c.resources[i];OwnedRuntimeImport.Need(r!=null&&(i==0||c.resources[i-1].id<r.id),"Checkpoint resource order/identity differs.");OwnedRuntimeImport.Need(!r.acquiredSurplus||source.resourcePolicy=="acquired-initial-surplus-consume-only/v1","Checkpoint surplus resource lacks policy.");resources[i]=new OwnedRuntimeResourceDefinition(r.id,r.partId,r.kind,r.capacity,r.massPerUnit,r.initialQuantity,r.acquiredSurplus);}
            var d=new OwnedRuntimeDefinition(c.definitionId,c.definitionRevision,parts,bodies,resources,(long[])c.providerKeys.Clone());
            OwnedRuntimeImport.Need(source.bodies.Length==d.BodyCount,"Checkpoint native body coverage differs.");for(int i=0;i<source.bodies.Length;i++)OwnedRuntimeImport.Need(source.bodies[i].bodyId==d.GetBody(i).Id,"Checkpoint body alias binding differs.");foreach(var r in source.renderers)d.PartSlot(r.partId);
            var s=new OwnedRuntimeSnapshot{Definition=d,Stamp=new OwnedRuntimeStamp(c.epoch,c.stateRevision,c.topologyRevision,c.definitionRevision,c.frameRevision,c.time,c.frameId),Connections=new OwnedRuntimeConnection[c.connections.Length],BodyMembers=new RigidAssemblyMember[c.members.Length],RegionIds=(long[])c.regionIds.Clone(),RegionMotion=new OwnedRuntimeMotion[c.motions.Length],ResourceQuantity=Copy(c.resourceQuantity),Temperature=Copy(c.temperature),ProviderState=Copy(c.providerState),Input=Input(c.input),InputHistory=new OwnedRuntimeInputRecord[c.inputHistory.Length]};
            for(int i=0;i<s.Connections.Length;i++){var a=c.connections[i];OwnedRuntimeImport.Need(a!=null,"Checkpoint connection null.");s.Connections[i]=new OwnedRuntimeConnection(a.id,a.partA,a.partB,a.locked);}
            for(int i=0;i<s.BodyMembers.Length;i++)s.BodyMembers[i]=Member(c.members[i]);
            for(int i=0;i<s.RegionMotion.Length;i++){var m=c.motions[i];OwnedRuntimeImport.Need(m!=null,"Checkpoint motion absent.");s.RegionMotion[i]=new OwnedRuntimeMotion{RootPosition=V(m.rootPosition),Orientation=Q(m.rotation),Velocity=V(m.velocity),AngularMomentum=V(m.angularMomentum)};}
            for(int i=0;i<s.InputHistory.Length;i++){var h=c.inputHistory[i];OwnedRuntimeImport.Need(h!=null,"Checkpoint history absent.");s.InputHistory[i]=new OwnedRuntimeInputRecord{Sequence=h.sequence,StartedAt=h.startedAt,DeltaTime=h.deltaTime,Input=Input(h.values)};}
            s.Thermal=c.thermal==null?null:c.thermal.Clone();s.Mechanical=c.mechanical==null?null:c.mechanical.Clone();var world=OwnedRuntimeWorld.RestoreSnapshot(s);
            var actuator=new OwnedRuntimeActuatorSpec[c.actuators.Length];var wheelSlots=new int[actuator.Length];
            for(int i=0;i<actuator.Length;i++)
            {
                var a=c.actuators[i];OwnedRuntimeImport.Need(a!=null&&a.fuelKinds!=null&&a.fuelMassFractions!=null&&a.fuelKinds.Length<=32&&a.fuelKinds.Length==a.fuelMassFractions.Length,"Checkpoint actuator absent/invalid.");
                OwnedRuntimeImport.Need(a.fuelResourceId>=-1&&a.electricResourceId>=-1,"Checkpoint resource sentinel differs.");
                OwnedRuntimeImport.Positive(a.wheelCapacity);
                var kind=(OwnedRuntimeActuatorKind)a.kind;int p=d.PartSlot(a.partId),fuel=a.fuelResourceId<0?-1:d.ResourceSlot(a.fuelResourceId),electric=a.electricResourceId<0?-1:d.ResourceSlot(a.electricResourceId);
                if(a.fuelKinds.Length>0){OwnedRuntimeImport.Need(fuel<0,"Checkpoint fuel route ambiguous.");actuator[i]=OwnedRuntimeActuatorSpec.RestoreUnitAxis(p,kind,V(a.point),V(a.axis),a.maximum,(string[])a.fuelKinds.Clone(),Copy(a.fuelMassFractions),a.specificImpulse,electric,a.electricRate,a.heatWatts);}
                else actuator[i]=OwnedRuntimeActuatorSpec.RestoreUnitAxis(p,kind,V(a.point),V(a.axis),a.maximum,fuel,a.specificImpulse,electric,a.electricRate,a.heatWatts,a.wheelCapacity);
                wheelSlots[i]=a.wheelProviderSlot;
            }
            var surfaces=new OwnedRuntimeSurfaceSpec[c.surfaces.Length];for(int i=0;i<surfaces.Length;i++){var a=c.surfaces[i];OwnedRuntimeImport.Need(a!=null,"Checkpoint surface absent.");surfaces[i]=OwnedRuntimeSurfaceSpec.RestoreUnitNormal(d.PartSlot(a.partId),V(a.point),V(a.normal),a.dragArea,a.liftArea,a.thermalArea,a.emissivity,a.heatingFraction);}
            var links=new OwnedRuntimeThermalLink[c.thermalLinks.Length];for(int i=0;i<links.Length;i++){var a=c.thermalLinks[i];OwnedRuntimeImport.Need(a!=null,"Checkpoint thermal link absent.");links[i]=new OwnedRuntimeThermalLink(d.PartSlot(a.partA),d.PartSlot(a.partB),a.conductance);}
            var systems=new OwnedRuntimeSystems(world,actuator,surfaces,links,(bool[])c.resourceFlowEnabled.Clone(),false,wheelSlots,Q(c.controlOrientation));for(int i=0;i<actuator.Length;i++)systems.SetEnabled(i,c.actuators[i].enabled);systems.SetExecutionStrategy((OwnedRuntimeExecutionStrategy)c.executionStrategy);systems.RestoreConfigurationRevision(c.systemsRevision);
            var e=c.environment;var environment=new OwnedRuntimeEnvironment(e.sourceId,e.revision,e.epoch,e.density,e.temperature,V(e.wind),V(e.gravity),e.convection,e.radiation);
            var values=Input(c.controls);var controls=new OwnedRuntimeControls{Throttle=values.Throttle,Attitude=new Vec(values.Pitch,values.Yaw,values.Roll),Translation=new Vec(values.X,values.Y,values.Z),EnginesEnabled=c.controlEnabled[0],RcsEnabled=c.controlEnabled[1],WheelsEnabled=c.controlEnabled[2]};
            return new OwnedRuntimeRestoredState{World=world,Systems=systems,Source=source,Controls=controls,Environment=environment,Plan=new OwnedRuntimeStepPlan(c.fixedDelta,c.warpRate)};
        }
    }
}
