using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KspContinuum
{
    [Serializable] public sealed class OwnedRuntimeCapturedParts
    {
        public string schema="continuum-owned-runtime-part-specs/v1",runId,vesselId,nativeManifestSha256,sourceAssemblySha256,sourceAssemblyMvid,modelScope;
        public int processId,unityFrame,unityFrameEnd;
        public double fixedTime,fixedTimeEnd,universalTime,universalTimeEnd;
        public long startedTicks,copiedTicks,clockFrequency;
        public bool epochUnchanged,identityUnchanged;
        public double massScale=1000,forceScale=1000;
        public double[] rootPositionRelativeAnchor,rootRotation,controlRotationInRoot;
        public OwnedRuntimeCapturedPart[] parts;
        public string[] omissions;
    }
    [Serializable] public sealed class OwnedRuntimeCapturedPart
    {
        public uint partId;
        public string name,definitionSourceSha256;
        public double temperatureKelvin,maximumTemperatureKelvin,heatCapacityJoulesPerKelvin;
        public double[] positionInRoot,rotationInRoot;
        public OwnedRuntimeCapturedResource[] resources;
        public OwnedRuntimeCapturedActuator[] actuators;
        public string[] omissions;
    }
    [Serializable] public sealed class OwnedRuntimeCapturedResource
    {
        public string name;
        public double amount,capacity,densityKilogramsPerUnit;
        public bool flowEnabled;
    }
    [Serializable] public sealed class OwnedRuntimeCapturedActuator
    {
        public string id,kind,sourceType,modelRecipe;
        public int moduleIndex,actuatorMode;
        public bool enabled,useThrottle,enablePitch,enableRoll,enableYaw,enableX,enableY,enableZ;
        public double authorityFraction,maxForceNewtons,specificImpulseSeconds;
        public double[] maxTorqueNewtonMeters;
        public OwnedRuntimeCapturedThruster[] thrusters;
        public OwnedRuntimeCapturedPropellant[] propellants;
        public OwnedRuntimeCapturedResourceRate[] resourceRates;
    }
    [Serializable] public sealed class OwnedRuntimeCapturedThruster
    {
        public double[] positionInRoot,forceDirectionInRoot;
    }
    [Serializable] public sealed class OwnedRuntimeCapturedPropellant
    {
        public string resource;
        public double ratio;
    }
    [Serializable] public sealed class OwnedRuntimeCapturedResourceRate
    {
        public string resource;
        public double unitsPerSecond;
        public bool varyTime;
    }
    [Serializable] public sealed class OwnedRuntimeBodyBinding
    {
        public int sourceBodyId,bodySlot;
        public long bodyId;
    }
    [Serializable] public sealed class OwnedRuntimeRendererBinding
    {
        public string id;
        public int sourceRendererId,meshIndex,submeshIndex,materialIndex;
        public long partId;
        public bool visible;
        public double[] localToPart;
    }
    [Serializable] public sealed class OwnedRuntimeSourceBinding
    {
        public string schema="continuum-owned-runtime-source/v1",runId,vesselId,nativeManifestSha256,partSpecsSha256,rendererManifestSha256,colliderGeometrySha256,supplyCaptureSha256,scope;
        public string resourcePolicy="strict-capacity/v1";
        public int processId;
        public double[] subtractedNativeCenterVelocity,sourceFrameVelocity;
        public OwnedRuntimeBodyBinding[] bodies;
        public OwnedRuntimeRendererBinding[] renderers;
        public string[] omissions;
    }
    public sealed class OwnedRuntimeImportResult
    {
        public OwnedRuntimeWorld World;
        public OwnedRuntimeSystems Systems;
        public OwnedRuntimeSourceBinding Source;
        public OwnedRuntimeCapturedParts CapturedParts;
        public NativeAssemblySceneManifest NativeScene;
        public WholeCraftGeometry ColliderGeometry;
        public OwnedRuntimeSupplyCapturePacket CapturedSupply;
        public OwnedRuntimeBodyBinding[] BodyBindings {get{return Source.bodies;}}
        public OwnedRuntimeRendererBinding[] RendererBindings {get{return Source.renderers;}}
        public string[] Omissions {get{return Source.omissions;}}
    }
    public static class OwnedRuntimeImport
    {
        internal static void Need(bool pass,string reason){if(!pass)throw new InvalidDataException(reason);}
        internal static void Digest(string value){Need(value!=null&&value.Length==64,"Owned runtime digest shape differs.");foreach(char c in value)Need(c>='0'&&c<='9'||c>='a'&&c<='f',"Owned runtime digest encoding differs.");}
        internal static void Text(string value){Need(!string.IsNullOrEmpty(value)&&value.Length<=2048,"Owned runtime identity absent/oversize.");foreach(char c in value)Need(!char.IsControl(c),"Owned runtime identity contains controls.");}
        internal static void Number(double value){Need(!double.IsNaN(value)&&!double.IsInfinity(value),"Owned runtime nonfinite value.");}
        internal static void Nonnegative(double value){Number(value);Need(value>=0,"Owned runtime negative value.");}
        internal static void Positive(double value){Number(value);Need(value>0,"Owned runtime positive value required.");}
        internal static void Vector(double[] value,int count){Need(value!=null&&value.Length==count,"Owned runtime vector shape differs.");foreach(double v in value)Number(v);}
        internal static void Quaternion(double[] q){Vector(q,4);double n=0;foreach(double v in q)n+=v*v;Need(!double.IsInfinity(n)&&Math.Abs(n-1)<=1e-4,"Acquired native float quaternion not normalized.");}
        public static void ValidateCapturedParts(OwnedRuntimeCapturedParts captured,NativeAssemblySceneManifest scene,string sceneSha256,bool allowAcquiredSurplus=false)
        {
            Digest(sceneSha256);Need(captured!=null&&scene!=null&&captured.schema=="continuum-owned-runtime-part-specs/v1","Owned acquired-parts schema differs.");
            Need(captured.nativeManifestSha256==sceneSha256&&captured.runId==scene.runId&&captured.vesselId==scene.vesselId&&captured.processId==scene.processId,"Owned acquired-parts source binding differs.");
            Digest(captured.sourceAssemblySha256);Guid parsed;Need(Guid.TryParse(captured.sourceAssemblyMvid,out parsed),"Owned acquired-parts assembly MVID invalid.");Text(captured.modelScope);
            Need(captured.unityFrame==scene.unityFrame&&captured.unityFrameEnd==captured.unityFrame&&captured.fixedTime==scene.fixedTime&&captured.fixedTimeEnd==captured.fixedTime&&captured.universalTime==scene.universalTime&&captured.universalTimeEnd==captured.universalTime&&captured.epochUnchanged&&captured.identityUnchanged,"Owned acquired-parts epoch changed.");
            Number(captured.fixedTime);Number(captured.universalTime);Need(captured.startedTicks>=0&&captured.copiedTicks>=captured.startedTicks&&captured.clockFrequency>0,"Owned acquired-parts clocks invalid.");
            Need(captured.massScale==1000&&captured.forceScale==1000,"Owned KSP import requires explicit tonnes/kN to SI scale1000.");Vector(captured.rootPositionRelativeAnchor,3);Quaternion(captured.rootRotation);Quaternion(captured.controlRotationInRoot);
            Need(captured.parts!=null&&scene.parts!=null&&captured.parts.Length==scene.parts.Length&&captured.parts.Length>0&&captured.parts.Length<=50000&&captured.omissions!=null&&captured.omissions.Length<=50000,"Owned acquired-parts coverage shape differs.");
            var source=new HashSet<uint>();foreach(var part in scene.parts)Need(part!=null&&source.Add(part.partId),"Owned source part IDs duplicate.");
            var ids=new HashSet<uint>();var actuatorIds=new HashSet<string>(StringComparer.Ordinal);int resources=0,actuators=0,thrusters=0;
            foreach(var part in captured.parts)
            {
                Need(part!=null&&source.Contains(part.partId)&&ids.Add(part.partId),"Owned acquired-parts IDs missing/duplicate.");Text(part.name);Digest(part.definitionSourceSha256);
                Positive(part.temperatureKelvin);Positive(part.maximumTemperatureKelvin);Positive(part.heatCapacityJoulesPerKelvin);
                Vector(part.positionInRoot,3);Quaternion(part.rotationInRoot);
                Need(part.resources!=null&&part.resources.Length<=256&&part.actuators!=null&&part.actuators.Length<=256&&part.omissions!=null&&part.omissions.Length<=256,"Owned acquired-part table bound differs.");
                resources=checked(resources+part.resources.Length);actuators=checked(actuators+part.actuators.Length);Need(resources<=200000&&actuators<=50000,"Owned acquired table aggregate bound exceeded.");
                var names=new HashSet<string>(StringComparer.Ordinal);
                foreach(var r in part.resources){Need(r!=null,"Owned acquired resource null.");Text(r.name);Need(names.Add(r.name),"Owned acquired resource duplicate.");Nonnegative(r.amount);Nonnegative(r.capacity);Nonnegative(r.densityKilogramsPerUnit);Need(r.amount<=r.capacity||allowAcquiredSurplus,"Owned acquired resource exceeds capacity; explicit acquired-surplus policy required.");}
                var modules=new Dictionary<int,OwnedRuntimeCapturedActuator>();
                foreach(var a in part.actuators)
                {
                    Need(a!=null&&a.moduleIndex>=0&&actuatorIds.Add(a.id??""),"Owned acquired actuator duplicate/null.");Text(a.id);Text(a.sourceType);Text(a.modelRecipe);
                    OwnedRuntimeCapturedActuator previous;
                    if(modules.TryGetValue(a.moduleIndex,out previous))Need(a.kind=="engine"&&previous.kind=="engine"&&a.sourceType==previous.sourceType&&a.specificImpulseSeconds==previous.specificImpulseSeconds&&a.enabled==previous.enabled&&a.authorityFraction==previous.authorityFraction&&a.propellants!=null&&previous.propellants!=null&&SameFuel(a.propellants,previous.propellants),"Repeated module is not a compatible captured multi-nozzle engine.");else modules.Add(a.moduleIndex,a);
                    Need(a.kind=="engine"||a.kind=="rcs"||a.kind=="wheel","Owned acquired actuator kind unsupported.");
                    Nonnegative(a.authorityFraction);Need(a.authorityFraction<=1,"Owned acquired authority exceeds1.");Nonnegative(a.maxForceNewtons);Nonnegative(a.specificImpulseSeconds);Vector(a.maxTorqueNewtonMeters,3);foreach(double torque in a.maxTorqueNewtonMeters)Nonnegative(torque);
                    Need(a.thrusters!=null&&a.thrusters.Length<=256&&a.propellants!=null&&a.propellants.Length<=32&&a.resourceRates!=null&&a.resourceRates.Length<=32,"Owned acquired actuator tables absent/oversize.");
                    thrusters=checked(thrusters+a.thrusters.Length);Need(thrusters<=200000,"Owned acquired thruster bound exceeded.");
                    foreach(var t in a.thrusters){Need(t!=null,"Owned acquired thruster null.");Vector(t.positionInRoot,3);Vector(t.forceDirectionInRoot,3);double n=0;foreach(double x in t.forceDirectionInRoot)n+=x*x;Need(!double.IsInfinity(n)&&Math.Abs(n-1)<=1e-6,"Owned acquired thruster direction not unit.");}
                    var fuels=new HashSet<string>(StringComparer.Ordinal);foreach(var fuel in a.propellants){Need(fuel!=null,"Owned acquired propellant null.");Text(fuel.resource);Positive(fuel.ratio);Need(fuels.Add(fuel.resource),"Owned acquired propellant duplicate.");}
                    var rates=new HashSet<string>(StringComparer.Ordinal);foreach(var rate in a.resourceRates){Need(rate!=null,"Owned acquired resource rate null.");Text(rate.resource);Nonnegative(rate.unitsPerSecond);Need(rates.Add(rate.resource),"Owned acquired resource rate duplicate.");}
                }
                foreach(string omission in part.omissions)Text(omission);
            }
            foreach(string omission in captured.omissions)Text(omission);
        }
        static bool SameFuel(OwnedRuntimeCapturedPropellant[] a,OwnedRuntimeCapturedPropellant[] b)
        {if(a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]==null||b[i]==null||a[i].resource!=b[i].resource||a[i].ratio!=b[i].ratio)return false;return true;}

        public static OwnedRuntimeImportResult Create(NativeAssemblySceneManifest nativeScene,WholeCraftGeometry colliderGeometry,
            OwnedRuntimeCapturedParts capturedParts,string nativeManifestSha256,string partSpecsSha256,
            WholeCraftSceneManifest rendererScene=null,WholeCraftGeometry rendererGeometry=null,string rendererManifestSha256=null,
            double wheelMomentumCapacity=100,bool allowAcquiredSurplus=false,OwnedRuntimeSupplyCapturePacket supplyCapture=null,string supplyCaptureSha256=null)
        {
            NativeAssemblySceneFormat.Validate(nativeScene,colliderGeometry);
            ValidateCapturedParts(capturedParts,nativeScene,nativeManifestSha256,allowAcquiredSurplus);Digest(partSpecsSha256);Positive(wheelMomentumCapacity);
            if(supplyCapture!=null)ValidateSupplyCapture(supplyCapture,capturedParts,nativeScene,nativeManifestSha256,partSpecsSha256,supplyCaptureSha256);
            else Need(string.IsNullOrEmpty(supplyCaptureSha256),"Supply hash has no captured packet.");
            var omissions=new List<string>(capturedParts.omissions);
            foreach(var o in nativeScene.omissions)omissions.Add("collider:"+o.colliderId+":"+o.reason);
            var acquired=capturedParts.parts.ToDictionary(p=>p.partId);
            var aliases=nativeScene.parts.OrderBy(p=>p.partId).ToArray();
            var bodies=nativeScene.bodies.OrderBy(b=>b.bodyId).ToArray();
            var bodySlots=new Dictionary<int,int>();for(int i=0;i<bodies.Length;i++)bodySlots.Add(bodies[i].bodyId,i);
            Rotation root=Q(capturedParts.rootRotation);Vec rootPosition=V(capturedParts.rootPositionRelativeAnchor);
            var bodyPositions=new Vec[bodies.Length];var bodyRotations=new Rotation[bodies.Length];var wet=new RigidAssemblyMember[bodies.Length];
            for(int i=0;i<bodies.Length;i++)
            {
                var b=bodies[i];Need(!b.isKinematic&&b.active&&b.constraints==0,"Owned import requires active unconstrained native body contributors.");
                bodyPositions[i]=root.Inverse.Rotate(Sub(V(b.position),rootPosition));bodyRotations[i]=root.Inverse*Q(b.rotation);
                wet[i]=new RigidAssemblyMember(i,b.mass*capturedParts.massScale,bodyPositions[i],bodyRotations[i],V(b.localCenterOfMass),V(b.principalInertia)*capturedParts.massScale,Q(b.principalRotation));
            }
            var partDefinitions=new OwnedRuntimePartDefinition[aliases.Length];var partSlots=new Dictionary<uint,int>();
            var resourceDefinitions=new List<OwnedRuntimeResourceDefinition>();var resourceFlow=new Dictionary<long,bool>();
            var densities=new Dictionary<string,double>(StringComparer.Ordinal);
            for(int i=0;i<aliases.Length;i++)
            {
                var a=aliases[i];var p=acquired[a.partId];Need(a.servoBodyId==0,"Owned rigid import does not flatten servo aliases.");
                int bodyId=a.ownBodyId!=0?a.ownBodyId:a.effectiveBodyId;Need(bodyId!=0&&bodySlots.ContainsKey(bodyId),"Owned part has no supported physical owner.");
                int b=bodySlots[bodyId];Rotation inverse=bodyRotations[b].Inverse;
                partDefinitions[i]=new OwnedRuntimePartDefinition(a.partId,p.name,bodyId,inverse.Rotate(Sub(V(p.positionInRoot),bodyPositions[b])),inverse*Q(p.rotationInRoot),p.heatCapacityJoulesPerKelvin,p.temperatureKelvin);
                partSlots.Add(a.partId,i);foreach(string o in p.omissions)omissions.Add("part:"+a.partId+":"+o);
                var resources=p.resources.OrderBy(r=>r.name,StringComparer.Ordinal).ToArray();
                for(int r=0;r<resources.Length;r++)
                {
                    var resource=resources[r];double density;
                    if(densities.TryGetValue(resource.name,out density))Need(density==resource.densityKilogramsPerUnit,"Captured resource density differs for the same kind.");else densities.Add(resource.name,resource.densityKilogramsPerUnit);
                    bool surplus=resource.amount>resource.capacity;
                    long id=checked((long)a.partId*1024+r);resourceDefinitions.Add(new OwnedRuntimeResourceDefinition(id,a.partId,resource.name,resource.capacity,resource.densityKilogramsPerUnit,resource.amount,surplus));resourceFlow.Add(id,resource.flowEnabled);
                    if(surplus)omissions.Add("resource:"+a.partId+":"+resource.name+":acquired-initial-surplus-consume-only; original-capacity="+resource.capacity.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            var dryBodies=new OwnedRuntimeBodyDefinition[bodies.Length];
            for(int b=0;b<bodies.Length;b++)
            {
                var points=new List<Tuple<double,Vec>>();
                foreach(var r in resourceDefinitions)if(partDefinitions[partSlots[(uint)r.PartId]].BodyId==bodies[b].bodyId)
                    points.Add(Tuple.Create(r.InitialQuantity*r.MassPerUnit,partDefinitions[partSlots[(uint)r.PartId]].LocalPosition));
                dryBodies[b]=new OwnedRuntimeBodyDefinition(bodies[b].bodyId,SubtractResources(wet[b],points));
            }
            var providerKeys=new List<long>();
            foreach(var p in aliases)
                foreach(var a in acquired[p.partId].actuators)
                    if(a.kind=="wheel"&&a.actuatorMode==0)
                        for(int axis=0;axis<3;axis++)if(a.maxTorqueNewtonMeters[axis]*a.authorityFraction>0)
                            providerKeys.Add(checked((long)p.partId*1000000+(long)a.moduleIndex*4+axis));
            if(supplyCapture!=null){foreach(var solar in supplyCapture.solar)providerKeys.Add(solar.rateProviderKey);foreach(var command in supplyCapture.commands)providerKeys.Add(command.readinessProviderKey);}
            var definition=new OwnedRuntimeDefinition("native/"+nativeScene.vesselId+"/"+nativeManifestSha256,0,partDefinitions,dryBodies,resourceDefinitions.ToArray(),providerKeys.ToArray());
            var connections=new List<OwnedRuntimeConnection>();foreach(var a in aliases)if(a.parentPartId!=0)connections.Add(new OwnedRuntimeConnection(a.partId,a.parentPartId,a.partId));
            var topology=OwnedRuntimeTopology.Compile(definition,connections);Need(topology.RegionCount==1,"Initial captured craft does not form one rigid region.");
            var sourceMotion=new RigidAssemblyMotion[bodies.Length];for(int b=0;b<bodies.Length;b++)sourceMotion[b]=new RigidAssemblyMotion(wet[b],root.Inverse.Rotate(V(bodies[b].velocity)),root.Inverse.Rotate(V(bodies[b].angularVelocity)));
            var projection=RigidAssemblyProjection.Project(sourceMotion);
            var motion=new OwnedRuntimeMotion{RootPosition=new Vec(0,0,0),Orientation=root,Velocity=new Vec(0,0,0),AngularMomentum=root.Rotate(projection.AngularMomentumAboutCenter)};
            var world=new OwnedRuntimeWorld(definition,topology,motion,0,"captured-native-Galilean-rest/metres");
            if(supplyCapture!=null)
            {
                var initial=world.CaptureSnapshot();
                foreach(var command in supplyCapture.commands)initial.ProviderState[providerKeys.IndexOf(command.readinessProviderKey)]=command.initialReady?1:0;
                world=OwnedRuntimeWorld.RestoreSnapshot(initial);
            }
            using(var read=world.AcquireRead())for(int b=0;b<wet.Length;b++)CheckWet(read.GetBody(b),wet[b]);
            var actuators=new List<OwnedRuntimeActuatorSpec>();var enabled=new List<bool>();var wheelSlots=new List<int>();
            Rotation control=Q(capturedParts.controlRotationInRoot);
            foreach(var alias in aliases)
            {
                var p=acquired[alias.partId];int part=definition.PartSlot(alias.partId);Rotation partInverse=Q(p.rotationInRoot).Inverse;
                foreach(var a in p.actuators)
                {
                    double electricRate=0;bool supported=true;
                    foreach(var rate in a.resourceRates)
                    {if(rate.resource!="ElectricCharge"||!rate.varyTime){supported=false;break;}electricRate+=rate.unitsPerSecond;}
                    int electric=-1;for(int r=0;r<definition.ResourceCount;r++)if(definition.GetResource(r).Kind=="ElectricCharge"&&(electric<0||definition.GetResource(r).PartId==alias.partId))electric=r;
                    if(electricRate>0&&electric<0)supported=false;
                    if(!supported){omissions.Add("actuator:"+a.id+":unsupported-resource-rate");continue;}
                    if(a.kind=="wheel")
                    {
                        if(a.actuatorMode!=0){omissions.Add("actuator:"+a.id+":non-All-wheel-controller");continue;}
                        int[] sourceAxes={0,2,1};Vec[] axes={new Vec(1,0,0),new Vec(0,0,1),new Vec(0,1,0)};
                        double totalTorque=a.maxTorqueNewtonMeters.Sum()*a.authorityFraction;
                        for(int axis=0;axis<3;axis++)
                        {
                            double maximum=a.maxTorqueNewtonMeters[sourceAxes[axis]]*a.authorityFraction;if(maximum==0)continue;
                            long key=checked((long)alias.partId*1000000+(long)a.moduleIndex*4+sourceAxes[axis]);int slot=providerKeys.IndexOf(key);Need(slot>=0,"Wheel provider source identity absent.");
                            actuators.Add(new OwnedRuntimeActuatorSpec(part,OwnedRuntimeActuatorKind.ReactionWheel,new Vec(0,0,0),partInverse.Rotate(control.Rotate(axes[axis])),maximum,-1,0,electric,electricRate*maximum/totalTorque,0,wheelMomentumCapacity));
                            enabled.Add(a.enabled);wheelSlots.Add(slot);
                        }
                        continue;
                    }
                    if(a.maxForceNewtons*a.authorityFraction==0||a.specificImpulseSeconds<=0||a.thrusters.Length==0||a.propellants.Length==0)
                    {omissions.Add("actuator:"+a.id+":no-positive-propulsive-model");continue;}
                    var kinds=new string[a.propellants.Length];var fractions=new double[kinds.Length];double sum=0;
                    for(int i=0;i<kinds.Length;i++){var f=a.propellants[i];double density;Need(densities.TryGetValue(f.resource,out density)&&density>0,"Propellant density/resource kind missing.");kinds[i]=f.resource;fractions[i]=f.ratio*density;sum+=fractions[i];}
                    Positive(sum);for(int i=0;i<fractions.Length;i++)fractions[i]/=sum;
                    foreach(var t in a.thrusters)
                    {
                        actuators.Add(new OwnedRuntimeActuatorSpec(part,a.kind=="engine"?OwnedRuntimeActuatorKind.Engine:OwnedRuntimeActuatorKind.Rcs,
                            partInverse.Rotate(Sub(V(t.positionInRoot),V(p.positionInRoot))),partInverse.Rotate(V(t.forceDirectionInRoot)),a.maxForceNewtons*a.authorityFraction,kinds,fractions,a.specificImpulseSeconds,electric,electricRate));
                        enabled.Add(a.enabled);wheelSlots.Add(-1);
                    }
                }
            }
            var flows=new bool[definition.ResourceCount];for(int r=0;r<flows.Length;r++)flows[r]=resourceFlow[definition.GetResource(r).Id];
            Rotation controller=control*Rotation.AxisAngle(new Vec(0,1,-1),Math.PI);
            var systems=new OwnedRuntimeSystems(world,actuators.ToArray(),new OwnedRuntimeSurfaceSpec[0],null,flows,false,wheelSlots.ToArray(),controller);
            for(int i=0;i<enabled.Count;i++)systems.SetEnabled(i,enabled[i]);
            var rendererBindings=new List<OwnedRuntimeRendererBinding>();
            if(rendererScene!=null)
            {
                Digest(rendererManifestSha256);WholeCraftSceneFormat.Validate(rendererScene,rendererGeometry);
                Need(rendererScene.runId==nativeScene.runId&&rendererScene.vesselId==nativeScene.vesselId&&rendererScene.processId==nativeScene.processId,"Renderer source identity differs from native acquisition.");
                var sourceRoot=rendererScene.rootToWorld.Select(x=>(double)x).ToArray();sourceRoot[3]=nativeScene.hierarchyRootWorld[3]-nativeScene.nativeAnchor[0];sourceRoot[7]=nativeScene.hierarchyRootWorld[7]-nativeScene.nativeAnchor[1];sourceRoot[11]=nativeScene.hierarchyRootWorld[11]-nativeScene.nativeAnchor[2];
                foreach(var draw in rendererScene.draws)
                {
                    OwnedRuntimeCapturedPart p;Need(acquired.TryGetValue(draw.partId,out p),"Renderer part binding absent.");
                    var partWorld=Matrix(root*Q(p.rotationInRoot),rootPosition+root.Rotate(V(p.positionInRoot)));
                    var worldMatrix=WholeCraftSceneFormat.MultiplyAffine(sourceRoot,draw.matrix);
                    rendererBindings.Add(new OwnedRuntimeRendererBinding{id=draw.id,partId=draw.partId,sourceRendererId=draw.rendererId,meshIndex=draw.meshIndex,submeshIndex=draw.submeshIndex,materialIndex=draw.materialIndex,visible=draw.enabled&&draw.active&&!draw.forceRenderingOff,localToPart=WholeCraftSceneFormat.RelativeAffine(partWorld,worldMatrix)});
                }
            }
            else Need(rendererGeometry==null&&rendererManifestSha256==null,"Renderer geometry/hash lacks manifest.");
            var bindings=new OwnedRuntimeBodyBinding[bodies.Length];for(int b=0;b<bodies.Length;b++)bindings[b]=new OwnedRuntimeBodyBinding{sourceBodyId=bodies[b].bodyId,bodyId=bodies[b].bodyId,bodySlot=b};
            var source=new OwnedRuntimeSourceBinding{runId=nativeScene.runId,vesselId=nativeScene.vesselId,processId=nativeScene.processId,nativeManifestSha256=nativeManifestSha256,partSpecsSha256=partSpecsSha256,rendererManifestSha256=rendererManifestSha256,colliderGeometrySha256=nativeScene.geometrySha256,
                supplyCaptureSha256=supplyCaptureSha256??"",
                resourcePolicy=allowAcquiredSurplus?"acquired-initial-surplus-consume-only/v1":"strict-capacity/v1",
                scope="SI; captured native Galilean rest; unique-body rigid momentum projection; zero-intrinsic-inertia point resources at acquired part origins; authored finite wheel reserve and acquired module EC maximum partitioned by torque capacity; declared negative-PYR controller and deterministic shared-region propellant pools; no stock wheel-demand/crossfeed/RCS-allocation/contact/atmospheric parity; frozen renderer-local bindings",
                subtractedNativeCenterVelocity=A(root.Rotate(projection.Velocity)),sourceFrameVelocity=(double[])nativeScene.frameVelocity.Clone(),bodies=bindings,renderers=rendererBindings.ToArray(),omissions=omissions.ToArray()};
            return new OwnedRuntimeImportResult{World=world,Systems=systems,Source=source,CapturedParts=capturedParts,NativeScene=nativeScene,ColliderGeometry=colliderGeometry,CapturedSupply=supplyCapture};
        }
        public static void ValidateSupplyCapture(OwnedRuntimeSupplyCapturePacket packet,OwnedRuntimeCapturedParts parts,NativeAssemblySceneManifest scene,string nativeSha,string partsSha,string supplySha)
        {
            Digest(supplySha);Need(packet!=null&&packet.schema=="continuum-owned-clock-providers/v1","Supply schema differs.");
            Need(packet.runId==scene.runId&&packet.vesselId==scene.vesselId&&packet.processId==scene.processId&&packet.nativeManifestSha256==nativeSha&&packet.partSpecsSha256==partsSha&&packet.sourceAssemblySha256==parts.sourceAssemblySha256&&packet.sourceAssemblyMvid==parts.sourceAssemblyMvid,"Supply provenance differs.");
            Need(packet.unityFrame==scene.unityFrame&&packet.unityFrameEnd==packet.unityFrame&&packet.fixedTime==scene.fixedTime&&packet.fixedTimeEnd==packet.fixedTime&&packet.universalTime==scene.universalTime&&packet.universalTimeEnd==packet.universalTime&&packet.epochUnchanged&&packet.identityUnchanged,"Supply acquisition epoch differs.");
            Need(packet.clockFrequency>0&&packet.startedTicks>=0&&packet.copiedTicks>=packet.startedTicks,"Supply clocks differ.");
            Text(packet.modelScope);Need(packet.solar!=null&&packet.commands!=null&&packet.engines!=null&&packet.unsupported!=null&&packet.idleTransmitters!=null&&packet.solar.Length<=50000&&packet.commands.Length<=50000&&packet.engines.Length<=50000,"Supply tables absent or excessive.");
            Need(packet.unsupported.Length==0,"Supply packet includes unsupported writers.");
            var ids=new HashSet<long>();var sourceParts=new HashSet<uint>(parts.parts.Select(p=>p.partId));
            foreach(var command in packet.commands)
            {
                Need(command!=null&&sourceParts.Contains(command.partId)&&command.moduleIndex>=0&&command.moduleIndex<65536&&command.id==((long)command.partId<<16)+command.moduleIndex&&command.readinessProviderKey==long.MinValue+command.id&&ids.Add(command.id),"Command source identity differs.");
                Nonnegative(command.unitsPerSecond);Nonnegative(command.hibernationMultiplier);Need(command.hibernationMultiplier<=1&&command.minimumCrew==0,"Command model unsupported.");
                Digest(command.definitionSha256);Text(command.sourceType);Text(command.resourceKind);
            }
            foreach(var solar in packet.solar){Need(solar!=null&&sourceParts.Contains(solar.partId)&&solar.moduleIndex>=0&&solar.moduleIndex<65536&&solar.id==((long)solar.partId<<16)+solar.moduleIndex&&solar.rateProviderKey==long.MinValue+solar.id&&ids.Add(solar.id),"Solar source identity differs.");Digest(solar.definitionSha256);}
            foreach(var engine in packet.engines){Need(engine!=null&&sourceParts.Contains(engine.partId)&&engine.moduleIndex>=0&&engine.moduleIndex<65536&&engine.id==((long)engine.partId<<16)+engine.moduleIndex&&ids.Add(engine.id),"Engine source identity differs.");Digest(engine.definitionSha256);}
        }
        static RigidAssemblyMember SubtractResources(RigidAssemblyMember wet,List<Tuple<double,Vec>> resources)
        {
            double mass=wet.Mass;Vec first=wet.LocalCenterOfMass*wet.Mass;
            foreach(var r in resources){Nonnegative(r.Item1);mass-=r.Item1;first=Sub(first,r.Item2*r.Item1);}Positive(mass);Vec center=first*(1/mass);
            var tensor=RigidAssemblyProperties.Combine(new[]{new RigidAssemblyMember(0,wet.Mass,new Vec(0,0,0),Rotation.Identity,wet.LocalCenterOfMass,wet.PrincipalInertia,wet.PrincipalRotation)}).Inertia;
            double[] a={tensor.XX,tensor.XY,tensor.XZ,tensor.YY,tensor.YZ,tensor.ZZ};
            foreach(var r in resources)SubtractParallel(a,r.Item1,Sub(r.Item2,wet.LocalCenterOfMass));SubtractParallel(a,mass,Sub(center,wet.LocalCenterOfMass));
            Vec moments;Rotation axes;RigidAssemblyProperties.Diagonalize(new InertiaTensor6Dof(a[0],a[1],a[2],a[3],a[4],a[5]),out moments,out axes);
            Need(moments.X/moments.Z>=RigidAssemblyProjection.MinimumPrincipalMomentRatio,"Dry resource subtraction is ill conditioned.");
            return new RigidAssemblyMember(wet.Id,mass,wet.LocalPosition,wet.LocalRotation,center,moments,axes);
        }
        static void SubtractParallel(double[] t,double mass,Vec d)
        {t[0]-=mass*(d.Y*d.Y+d.Z*d.Z);t[1]+=mass*d.X*d.Y;t[2]+=mass*d.X*d.Z;t[3]-=mass*(d.X*d.X+d.Z*d.Z);t[4]+=mass*d.Y*d.Z;t[5]-=mass*(d.X*d.X+d.Y*d.Y);}
        static void CheckWet(RigidAssemblyMember actual,RigidAssemblyMember source)
        {
            Need(Math.Abs(actual.Mass-source.Mass)<=1e-9*Math.Max(1,source.Mass),"Imported wet mass reconstruction differs.");
            Vec d=Sub(actual.LocalCenterOfMass,source.LocalCenterOfMass);Need(Math.Sqrt(d.X*d.X+d.Y*d.Y+d.Z*d.Z)<=1e-9*Math.Max(1,Math.Sqrt(source.LocalCenterOfMass.X*source.LocalCenterOfMass.X+source.LocalCenterOfMass.Y*source.LocalCenterOfMass.Y+source.LocalCenterOfMass.Z*source.LocalCenterOfMass.Z)),"Imported wet COM reconstruction differs.");
            var a=RigidAssemblyProperties.Combine(new[]{actual}).Inertia;var b=RigidAssemblyProperties.Combine(new[]{source}).Inertia;
            double[] x={a.XX,a.XY,a.XZ,a.YY,a.YZ,a.ZZ},y={b.XX,b.XY,b.XZ,b.YY,b.YZ,b.ZZ};double scale=y.Max(z=>Math.Abs(z));for(int i=0;i<6;i++)Need(Math.Abs(x[i]-y[i])<=1e-9*Math.Max(1,scale),"Imported wet inertia reconstruction differs.");
        }
        static Vec V(double[] a){return new Vec(a[0],a[1],a[2]);}static Vec V(float[] a){return new Vec(a[0],a[1],a[2]);}
        static Vec Sub(Vec a,Vec b){return new Vec(a.X-b.X,a.Y-b.Y,a.Z-b.Z);}static double[] A(Vec v){return new[]{v.X,v.Y,v.Z};}
        static Rotation Q(double[] a){return new Rotation(a[0],a[1],a[2],a[3]);}static Rotation Q(float[] a){return new Rotation(a[0],a[1],a[2],a[3]);}
        static double[] Matrix(Rotation q,Vec p)
        {Vec x=q.Rotate(new Vec(1,0,0)),y=q.Rotate(new Vec(0,1,0)),z=q.Rotate(new Vec(0,0,1));return new[]{x.X,y.X,z.X,p.X,x.Y,y.Y,z.Y,p.Y,x.Z,y.Z,z.Z,p.Z,0d,0d,0d,1d};}
    }
}
