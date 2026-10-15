using System;
using System.Text.Json;
using KspContinuum;

internal static class CheckpointTests
{
    static readonly string NativeSha=new string('a',64),PartsSha=new string('b',64);
    static void Need(bool ok,string message){if(!ok)throw new Exception(message);}
    static void Near(double a,double b,double tolerance,string message){Need(Math.Abs(a-b)<=tolerance,message+": "+a+" != "+b);}
    static void Reject(Action action,string message)
    {bool failed=false;try{action();}catch(ArgumentException){failed=true;}catch(System.IO.InvalidDataException){failed=true;}catch(InvalidOperationException){failed=true;}Need(failed,message);}
    static OwnedRuntimeSourceBinding Source()
    {return new OwnedRuntimeSourceBinding{runId="analytic",vesselId="fixture",processId=1,nativeManifestSha256=NativeSha,partSpecsSha256=PartsSha,scope="independent analytic model",subtractedNativeCenterVelocity=new double[3],sourceFrameVelocity=new double[3],bodies=new[]{new OwnedRuntimeBodyBinding{sourceBodyId=20,bodyId=20,bodySlot=0}},renderers=new OwnedRuntimeRendererBinding[0],omissions=new string[0]};}
    static OwnedRuntimeImportResult Fixture()
    {
        var body=new RigidAssemblyMember(0,3,new Vec(),Rotation.Identity,new Vec(),new Vec(2,3,4),Rotation.Identity);
        var definition=new OwnedRuntimeDefinition("analytic",2,
            new[]{new OwnedRuntimePartDefinition(10,"part",20,new Vec(0,1,0),Rotation.Identity,1000,320)},
            new[]{new OwnedRuntimeBodyDefinition(20,body)},
            new[]{new OwnedRuntimeResourceDefinition(30,10,"Fuel",10,1,1),new OwnedRuntimeResourceDefinition(40,10,"ElectricCharge",100,0,100)},new long[]{50});
        var world=new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new OwnedRuntimeConnection[0]),new OwnedRuntimeMotion{Orientation=Rotation.AxisAngle(new Vec(0,1,0),.2)});
        var specs=new[]{new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.ReactionWheel,new Vec(),new Vec(0,0,1),.5,-1,0,1,.2,1,10),
            new OwnedRuntimeActuatorSpec(0,OwnedRuntimeActuatorKind.Rcs,new Vec(),new Vec(1,0,0),1,0,100)};
        var systems=new OwnedRuntimeSystems(world,specs,new[]{new OwnedRuntimeSurfaceSpec(0,new Vec(),new Vec(1,0,0),.1,0,.2,.5,.01)},null,new[]{true,true},false,new[]{0,-1},Rotation.AxisAngle(new Vec(1,0,0),.1));
        systems.SetEnabled(1,false);systems.SetResourceFlowEnabled(0,false);
        return new OwnedRuntimeImportResult{World=world,Systems=systems,Source=Source()};
    }
    static void Advance(OwnedRuntimeWorld world,OwnedRuntimeSystems systems,OwnedRuntimeControls c,OwnedRuntimeEnvironment e)
    {
        var input=new OwnedRuntimeInput(c.Throttle,c.Attitude.X,c.Attitude.Y,c.Attitude.Z,c.Translation.X,c.Translation.Y,c.Translation.Z);
        using(var step=world.BeginStep(world.Stamp,.02,input)){systems.Step(step,e,c);step.Commit();}
    }
    static OwnedRuntimeCheckpointData Roundtrip(OwnedRuntimeCheckpointData value)
    {var options=new JsonSerializerOptions{IncludeFields=true};return JsonSerializer.Deserialize<OwnedRuntimeCheckpointData>(ReportJson.Encode(value),options);}
    public static void Run()
    {
        var runtime=Fixture();var controls=new OwnedRuntimeControls{Attitude=new Vec(0,0,.4),Translation=new Vec(.2,0,0),WheelsEnabled=true,RcsEnabled=true};
        var env=new OwnedRuntimeEnvironment("analytic-air",3,0,.01,300,new Vec(1,0,0),new Vec(0,-.1,0),.01,280);var plan=new OwnedRuntimeStepPlan(.02);
        for(int i=0;i<3;i++)Advance(runtime.World,runtime.Systems,controls,env);
        var saved=Roundtrip(OwnedRuntimeCheckpoint.Capture(runtime,controls,env,plan));
        var restored=OwnedRuntimeCheckpoint.Restore(saved,NativeSha,PartsSha);
        Need(restored.Systems.ConfigurationRevision==runtime.Systems.ConfigurationRevision&&!restored.Systems.IsEnabled(1)&&!restored.Systems.IsResourceFlowEnabled(0),"System enabled/flow/revision state lost.");
        Near(restored.Systems.ControlOrientation.X,runtime.Systems.ControlOrientation.X,0,"Control orientation changed on restore");
        for(int i=0;i<5;i++){Advance(runtime.World,runtime.Systems,controls,env);Advance(restored.World,restored.Systems,restored.Controls,restored.Environment);}
        var a=runtime.World.CaptureSnapshot();var b=restored.World.CaptureSnapshot();
        Need(a.Stamp.Matches(b.Stamp)&&a.InputHistory.Length==b.InputHistory.Length,"Continuation epoch/history lost.");
        for(int i=0;i<a.ResourceQuantity.Length;i++)Near(a.ResourceQuantity[i],b.ResourceQuantity[i],0,"Resource continuation");
        for(int i=0;i<a.ProviderState.Length;i++)Near(a.ProviderState[i],b.ProviderState[i],0,"Provider history continuation");
        for(int i=0;i<a.Temperature.Length;i++)Near(a.Temperature[i],b.Temperature[i],0,"Temperature continuation");
        Near(a.RegionMotion[0].RootPosition.Y,b.RegionMotion[0].RootPosition.Y,0,"Position continuation");Near(a.RegionMotion[0].Orientation.Z,b.RegionMotion[0].Orientation.Z,0,"Orientation continuation");
        var before=runtime.World.Stamp;var corrupt=Roundtrip(saved);corrupt.resourceQuantity=new double[0];Reject(()=>OwnedRuntimeCheckpoint.Restore(corrupt,NativeSha,PartsSha),"Missing reservoir admitted.");
        corrupt=Roundtrip(saved);corrupt.providerState[0]=double.NaN;Reject(()=>OwnedRuntimeCheckpoint.Restore(corrupt,NativeSha,PartsSha),"Nonfinite provider history admitted.");
        corrupt=Roundtrip(saved);corrupt.members[0].mass+=1;Reject(()=>OwnedRuntimeCheckpoint.Restore(corrupt,NativeSha,PartsSha),"Foreign wet body state admitted.");
        corrupt=Roundtrip(saved);corrupt.resources[1].id=corrupt.resources[0].id;Reject(()=>OwnedRuntimeCheckpoint.Restore(corrupt,NativeSha,PartsSha),"Duplicate resource ID admitted.");
        Reject(()=>OwnedRuntimeCheckpoint.Restore(saved,new string('c',64),PartsSha),"Foreign native source admitted.");Need(runtime.World.Stamp.Matches(before),"Rejected candidate changed current world.");
        saved.resourceQuantity[0]=999;using(var read=runtime.World.AcquireRead())Need(read.GetResourceQuantity(0)<=10,"Checkpoint arrays alias resident world.");
        ImportPointResource();
        SplitContinuation();
        HostCheckpoint();
        Console.WriteLine("PASS complete checkpoint serialized continuation, independent owner configuration and atomic corruption rejection");
    }
    sealed class LoadedSchedule : IOwnedRuntimeSchedule
    {
        public int calls;
        public string Schema{get{return "continuum-owned-runtime-schedule/v1";}}
        public string StateSchema{get{return "world-bank-state-only/v1";}}
        public string ImplementationId{get{return "independent-loaded-continuation";}}
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,OwnedRuntimeEnvironment env,OwnedRuntimeControls controls)
        {calls++;systems.Step(step,env,controls);}
    }
    static void HostCheckpoint()
    {
        var runtime=Fixture();var host=new OwnedRuntimeHost(runtime.World,runtime.Systems);var code=new LoadedSchedule();
        host.SwapSchedule(code,new string('f',64),runtime.World.Stamp);
        var controls=new OwnedRuntimeControls{WheelsEnabled=true,Attitude=new Vec(0,0,.3)};var input=new OwnedRuntimeInput(0,0,0,.3);
        var env=new OwnedRuntimeEnvironment("loaded-vacuum",0,0,0,300,new Vec(),new Vec());var plan=new OwnedRuntimeStepPlan(.02);
        host.Advance(.02,input,env,controls);
        var encoded=ReportJson.Encode(host.CaptureCheckpoint(runtime,controls,env,plan));
        var data=JsonSerializer.Deserialize<OwnedRuntimeHostCheckpoint>(encoded,new JsonSerializerOptions{IncludeFields=true});
        OwnedRuntimeRestoredState state;var restored=host.RestoreCheckpoint(data,NativeSha,PartsSha,out state);
        Need(restored.ScheduleImplementation==host.ScheduleImplementation&&restored.ScheduleSourceSha256==host.ScheduleSourceSha256&&restored.CodeGeneration==host.CodeGeneration,"Host restore silently selected builtin/foreign executable.");
        host.Advance(.02,input,env,controls);restored.Advance(.02,input,state.Environment,state.Controls);
        Need(code.calls==3,"Restored host did not execute the same loaded schedule.");
        var original=host.CaptureCheckpoint(runtime,controls,env,plan);
        var rebuilt=new OwnedRuntimeImportResult{World=state.World,Systems=state.Systems,Source=state.Source};
        Need(ReportJson.Encode(original)==ReportJson.Encode(restored.CaptureCheckpoint(rebuilt,state.Controls,state.Environment,state.Plan)),"Loaded-code checkpoint continuation differs.");
        host.SwapSchedule(new LoadedSchedule(),new string('e',64),host.World.Stamp);var before=host.World.Stamp;
        Reject(()=>{OwnedRuntimeRestoredState unused;host.RestoreCheckpoint(data,NativeSha,PartsSha,out unused);},"Stale executable generation checkpoint admitted.");
        Need(host.World.Stamp.Matches(before)&&host.CodeGeneration==2,"Rejected old code checkpoint changed current owner/state.");
    }
    static void SplitContinuation()
    {
        var parts=new[]{new OwnedRuntimePartDefinition(10,"a",20,new Vec(),Rotation.Identity,1000),new OwnedRuntimePartDefinition(11,"b",21,new Vec(),Rotation.Identity,1000)};
        var bodies=new[]{new OwnedRuntimeBodyDefinition(20,new RigidAssemblyMember(0,2,new Vec(-2,0,0),Rotation.Identity,new Vec(),new Vec(2,3,4),Rotation.Identity)),new OwnedRuntimeBodyDefinition(21,new RigidAssemblyMember(1,3,new Vec(3,0,0),Rotation.Identity,new Vec(),new Vec(3,4,5),Rotation.Identity))};
        var d=new OwnedRuntimeDefinition("split-fixture",0,parts,bodies,new OwnedRuntimeResourceDefinition[0]);
        var world=new OwnedRuntimeWorld(d,OwnedRuntimeTopology.Compile(d,new[]{new OwnedRuntimeConnection(100,10,11)}),new OwnedRuntimeMotion{Orientation=Rotation.AxisAngle(new Vec(1,2,3),.4),Velocity=new Vec(2,1,3),AngularMomentum=new Vec(1,2,3)});
        world.Split(world.Stamp,100);Need(world.Topology.RegionCount==2,"Independent split did not produce two regions.");
        var source=Source();source.bodies=new[]{source.bodies[0],new OwnedRuntimeBodyBinding{sourceBodyId=21,bodyId=21,bodySlot=1}};
        var runtime=new OwnedRuntimeImportResult{World=world,Systems=new OwnedRuntimeSystems(world,new OwnedRuntimeActuatorSpec[0],new OwnedRuntimeSurfaceSpec[0]),Source=source};
        var controls=new OwnedRuntimeControls();var env=new OwnedRuntimeEnvironment("vacuum",0,0,0,300,new Vec(),new Vec());
        var restored=OwnedRuntimeCheckpoint.Restore(Roundtrip(OwnedRuntimeCheckpoint.Capture(runtime,controls,env,new OwnedRuntimeStepPlan(.02))),NativeSha,PartsSha);
        Need(restored.World.Topology.RegionCount==2,"Checkpoint silently rejoined staged regions.");
        Advance(world,runtime.Systems,controls,env);Advance(restored.World,restored.Systems,controls,env);
        world.Dock(world.Stamp,101,10,11);restored.World.Dock(restored.World.Stamp,101,10,11);
        Need(world.Topology.RegionCount==1&&restored.World.Topology.RegionCount==1,"Joined continuation region count differs.");
        var a=world.CaptureSnapshot();var b=restored.World.CaptureSnapshot();Need(a.Stamp.Matches(b.Stamp),"Split/join continuation revisions differ.");
        Near(a.RegionMotion[0].Velocity.X,b.RegionMotion[0].Velocity.X,0,"Split/join momentum continuation");
        Near(a.BodyMembers[1].LocalPosition.X,b.BodyMembers[1].LocalPosition.X,0,"Split/join body frame continuation");
    }
    static void ImportPointResource()
    {
        var scene=new NativeAssemblySceneManifest{schema="continuum-native-assembly-scene/v1",runId="analytic",vesselId="fixture",engine="analytic-native-float",coverageScope="primitives-empty",coordinateFrame="native-world-axes-anchor-relative",nativePoseSource="Unity-Rigidbody-float-physics",hierarchyPoseSource="Unity-float-local-TRS-composed-double",processId=1,unityFrame=1,unityFrameEnd=1,rootTransformId=1,rootTransformIdEnd=1,clockFrequency=1,epochUnchanged=true,identityUnchanged=true,completeColliderCoverage=true,nativeAnchor=new float[3],frameVelocity=new double[3],hierarchyRootWorld=new[]{1d,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1},
            bodies=new[]{new NativeAssemblyBody{bodyId=20,transformId=1,ownerPartId=10,ownershipSource="explicit-part-alias",mass=4,position=new double[3],rotation=new[]{0f,0,0,1},localCenterOfMass=new[]{0f,.25f,0},worldCenterOfMass=new[]{0d,.25,0},principalInertia=new[]{1.25f,2,3.25f},principalRotation=new[]{0f,0,0,1},velocity=new float[3],angularVelocity=new float[3],active=true}},
            parts=new[]{new NativeAssemblyPartAlias{partId=10,partInstanceId=1,ownBodyId=20,effectiveBodyId=20,name="analytic"}},colliders=new NativeAssemblyCollider[0],omissions=new NativeAssemblyOmission[0]};
        var captured=new OwnedRuntimeCapturedParts{runId="analytic",vesselId="fixture",processId=1,unityFrame=1,unityFrameEnd=1,clockFrequency=1,epochUnchanged=true,identityUnchanged=true,nativeManifestSha256=NativeSha,sourceAssemblySha256=new string('d',64),sourceAssemblyMvid="10657063-2fc3-43a7-84fa-d39e75e877bf",modelScope="independent point fixture",rootPositionRelativeAnchor=new double[3],rootRotation=new[]{0d,0,0,1},controlRotationInRoot=new[]{0d,0,0,1},omissions=new string[0],
            parts=new[]{new OwnedRuntimeCapturedPart{partId=10,name="analytic",definitionSourceSha256=new string('e',64),temperatureKelvin=300,maximumTemperatureKelvin=1000,heatCapacityJoulesPerKelvin=1000,positionInRoot=new[]{0d,1,0},rotationInRoot=new[]{0d,0,0,1},actuators=new OwnedRuntimeCapturedActuator[0],omissions=new string[0],resources=new[]{new OwnedRuntimeCapturedResource{name="Fuel",amount=1,capacity=2,densityKilogramsPerUnit=1000,flowEnabled=true}}}}};
        var imported=OwnedRuntimeImport.Create(scene,null,captured,NativeSha,PartsSha);
        var dry=imported.World.Definition.GetBody(0).Member;Near(dry.Mass,3000,1e-10,"Independent dry mass");Near(dry.LocalCenterOfMass.Y,0,1e-14,"Independent dry COM");
        Near(dry.PrincipalInertia.X,500,1e-9,"Independent dry moment X");Near(dry.PrincipalInertia.Y,2000,1e-9,"Independent dry moment Y");Near(dry.PrincipalInertia.Z,2500,1e-9,"Independent dry moment Z");
        using(var read=imported.World.AcquireRead()){Near(read.GetBody(0).Mass,4000,1e-9,"Independent restored wet mass");Near(read.GetBody(0).LocalCenterOfMass.Y,.25,1e-12,"Independent wet COM");}
        captured.parts[0].resources[0].amount=5;Reject(()=>OwnedRuntimeImport.Create(scene,null,captured,NativeSha,PartsSha),"Over-capacity acquired resource was clamped/admitted.");
        captured.parts[0].resources[0].amount=1;captured.parts[0].resources[0].capacity=0;
        Reject(()=>OwnedRuntimeImport.Create(scene,null,captured,NativeSha,PartsSha),"Acquired surplus was implicitly admitted.");
        var surplus=OwnedRuntimeImport.Create(scene,null,captured,NativeSha,PartsSha,allowAcquiredSurplus:true);
        Need(surplus.World.Definition.GetResource(0).Capacity==0&&surplus.World.Definition.GetResource(0).InitialQuantity==1&&surplus.World.Definition.GetResource(0).AcquiredSurplus,"Original surplus/capacity facts were rewritten.");
        using(var step=surplus.World.BeginStep(surplus.World.Stamp,.02,new OwnedRuntimeInput(0))){step.SetResourceQuantity(0,.5);step.Commit();}
        using(var step=surplus.World.BeginStep(surplus.World.Stamp,.02,new OwnedRuntimeInput(0)))Reject(()=>step.SetResourceQuantity(0,.75),"Surplus refill above original capacity admitted.");
        var saved=OwnedRuntimeCheckpoint.Capture(surplus,new OwnedRuntimeControls(),new OwnedRuntimeEnvironment("surplus-vacuum",0,0,0,300,new Vec(),new Vec()),new OwnedRuntimeStepPlan(.02));
        var restored=OwnedRuntimeCheckpoint.Restore(Roundtrip(saved),NativeSha,PartsSha);
        using(var read=restored.World.AcquireRead())Near(read.GetResourceQuantity(0),.5,0,"Decaying acquired surplus continuation");
    }
}
