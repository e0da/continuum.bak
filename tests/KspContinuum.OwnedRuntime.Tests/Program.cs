using System;
using KspContinuum;

class Program
{
    sealed class Schedule : IOwnedRuntimeSchedule
    {
        public bool Bad,Fail,CommitEarly,DisposeEarly;
        public int Calls;
        public string Schema {get{return Bad?"bad":"continuum-owned-runtime-schedule/v1";}}
        public string StateSchema {get{return "world-bank-state-only/v1";}}
        public string ImplementationId {get{return "test-reloaded-schedule";}}
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,OwnedRuntimeEnvironment env,OwnedRuntimeControls controls)
        {Calls++;systems.Step(step,env,controls);if(CommitEarly)step.Commit();if(DisposeEarly)step.Dispose();if(Fail)throw new InvalidOperationException("Injected after candidate execution, before publication.");}
    }
    static void Require(bool ok,string reason){if(!ok)throw new Exception(reason);}
    static void Host()
    {
        var body=new RigidAssemblyMember(0,2,new Vec(),Rotation.Identity,new Vec(),new Vec(2,3,4),Rotation.Identity);
        var definition=new OwnedRuntimeDefinition("host-fixture",0,new[]{new OwnedRuntimePartDefinition(10,"part",20,new Vec(),Rotation.Identity,100)},new[]{new OwnedRuntimeBodyDefinition(20,body)},new OwnedRuntimeResourceDefinition[0]);
        var world=new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new OwnedRuntimeConnection[0]),new OwnedRuntimeMotion {Orientation=Rotation.Identity});
        var systems=new OwnedRuntimeSystems(world,new OwnedRuntimeActuatorSpec[0],new OwnedRuntimeSurfaceSpec[0]);
        var host=new OwnedRuntimeHost(world,systems);var input=new OwnedRuntimeInput(0);var controls=new OwnedRuntimeControls();
        using(var file=System.IO.File.OpenRead(typeof(OwnedRuntimeBuiltinSchedule).Assembly.Location))using(var sha=System.Security.Cryptography.SHA256.Create())
            Require(host.ScheduleSourceSha256==BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant(),"Builtin schedule is not pinned to its executable image.");
        var env=new OwnedRuntimeEnvironment("vacuum",0,0,0,300,new Vec(),new Vec());
        host.Advance(.02,input,env,controls);var before=world.Stamp;var reloaded=new Schedule();
        host.SwapSchedule(reloaded,new string('a',64),before);Require(world.Stamp.Matches(before)&&host.CodeGeneration==1,"Code swap changed committed state.");
        host.Advance(.02,input,env,controls);Require(reloaded.Calls==1,"Reloaded executable schedule did not run.");
        before=world.Stamp;
        try{host.SwapSchedule(new Schedule {Bad=true},new string('b',64),before);throw new Exception("Bad ABI admitted.");}catch(ArgumentException){}
        Require(host.CodeGeneration==1&&world.Stamp.Matches(before),"Rejected reload lost previous generation/state.");
        host.SwapSchedule(new Schedule {Fail=true},new string('c',64),before);
        try{host.Advance(.02,input,env,controls);throw new Exception("Failed candidate was published.");}catch(InvalidOperationException){}
        Require(world.Stamp.Matches(before),"Failed candidate changed committed epoch.");
        host.SwapSchedule(new Schedule {CommitEarly=true},new string('e',64),before);
        try{host.Advance(.02,input,env,controls);throw new Exception("Schedule acquired publication authority.");}catch(InvalidOperationException){}
        Require(world.Stamp.Matches(before),"Early publication escaped host ownership.");
        host.SwapSchedule(new Schedule {DisposeEarly=true},new string('f',64),before);
        try{host.Advance(.02,input,env,controls);throw new Exception("Disposed hosted token was committed.");}catch(InvalidOperationException){}
        Require(world.Stamp.Matches(before),"Disposed token changed committed state.");
        host.SwapSchedule(new OwnedRuntimeBuiltinSchedule(),new string('d',64),before);host.Advance(.02,input,env,controls);
        Console.WriteLine("Owned runtime executable reload, rejected reload, rollback and recovery passed.");
    }
    static void Main()
    {
        FoundationCases.Run();SystemsTests.Run();CheckpointTests.Run();BinaryCheckpointTests.Run();ThermalTests.Run();Host();
        FlightWorldTests.Run();FlightControlTests.Run();FlightPresentationTests.Run();
        var captured=new OwnedRuntimeCapturedParts {parts=new[]{new OwnedRuntimeCapturedPart {partId=1,name="export",resources=new[]{new OwnedRuntimeCapturedResource {name="Fuel",amount=2,capacity=1,densityKilogramsPerUnit=3}},actuators=new[]{new OwnedRuntimeCapturedActuator {kind="rcs",thrusters=new[]{new OwnedRuntimeCapturedThruster {positionInRoot=new double[3],forceDirectionInRoot=new[]{1.0,0,0}}},propellants=new[]{new OwnedRuntimeCapturedPropellant {resource="Fuel",ratio=1}},resourceRates=new[]{new OwnedRuntimeCapturedResourceRate {resource="EC",unitsPerSecond=1,varyTime=true}}}}}}};
        using(var json=System.Text.Json.JsonDocument.Parse(ReportJson.Encode(captured)))
        {
            var resource=json.RootElement.GetProperty("parts")[0].GetProperty("resources")[0];
            Require(resource.GetProperty("amount").GetDouble()==2&&resource.GetProperty("capacity").GetDouble()==1,"Raw acquisition values were clamped or lost.");
        }
        Console.WriteLine("Cold captured-part export preserves exact scalar facts and all nested schema types.");
        using(var json=System.Text.Json.JsonDocument.Parse(ReportJson.Encode(new OwnedRuntimeCapturedAero {parts=new[]{new OwnedRuntimeCapturedAeroPart {partId=1,weightedFaceAreas=new[]{1d,2,3,4,5,6}}}})))
            Require(json.RootElement.GetProperty("parts")[0].GetProperty("weightedFaceAreas")[5].GetDouble()==6,"Acquired aero export lost source areas.");
        Console.WriteLine("Owned runtime integrated portable checks passed.");
    }
}
