using System;
using KspContinuum;
static class Program
{
    static void Need(bool b,string s){if(!b)throw new Exception(s);}
    static void Near(double a,double b){Need(Math.Abs(a-b)<1e-12,"Analytic scalar balance differs.");}
    static OwnedRuntimeSupplyCurveData Curve(){return new OwnedRuntimeSupplyCurveData{time=new[]{0d},value=new[]{1d},inTangent=new[]{0d},outTangent=new[]{0d}};}
    static void Main()
    {
        long solar=65536,command=65537;string hash=new string('a',64);
        var definition=new OwnedRuntimeDefinition("supply",0,new[]{new OwnedRuntimePartDefinition(1,"probe",10,new Vec(),Rotation.Identity,100)},new[]{new OwnedRuntimeBodyDefinition(10,new RigidAssemblyMember(0,1,new Vec(),Rotation.Identity,new Vec(),new Vec(1,1,1),Rotation.Identity))},new[]{new OwnedRuntimeResourceDefinition(1,1,"ElectricCharge",1,0,.5)},new[]{long.MinValue+solar,long.MinValue+command});
        var world=new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new OwnedRuntimeConnection[0]),new OwnedRuntimeMotion{Orientation=Rotation.Identity});
        var systems=new OwnedRuntimeSystems(world,new OwnedRuntimeActuatorSpec[0],new OwnedRuntimeSurfaceSpec[0]);
        var packet=new OwnedRuntimeSupplyCapturePacket{runId="test",epochUnchanged=true,identityUnchanged=true,nativeManifestSha256=hash,partSpecsSha256=hash,universalTime=100,universalTimeEnd=100,unsupported=new string[0],engines=new OwnedRuntimeCapturedEngineState[0],ephemeris=new OwnedRuntimeSupplyEphemerisData{sourceSha256=hash,epoch=100,validSeconds=300,bodyRadius=1,referenceFluxMultiplier=1,sunBodyRelative=new[]{10d,0,0},referencePositionBodyMeters=new[]{2d,0,0}},solar=new[]{new OwnedRuntimeCapturedSolarSupply{id=solar,rateProviderKey=long.MinValue+solar,partId=1,moduleIndex=0,panelType="FLAT",enabled=true,nominalUnitsPerSecond=4,efficiencyMultiplier=1,launchUniversalTime=100,localPoint=new[]{0d,0,0},localNormal=new[]{1d,0,0},temperatureEfficiency=Curve(),ageEfficiency=Curve()}},commands=new[]{new OwnedRuntimeCapturedCommandLoad{id=command,readinessProviderKey=long.MinValue+command,partId=1,moduleIndex=1,enabled=true,unitsPerSecond=1,hibernationMultiplier=.01}}};
        systems.ConfigureScalarSupply(world.Stamp,packet,hash,new Vec(2,0,0),0);
        var environment=new OwnedRuntimeEnvironment("vacuum",0,0,0,300,new Vec(),new Vec());
        using(var step=world.BeginStep(world.Stamp,.1,new OwnedRuntimeInput())){systems.Step(step,environment,new OwnedRuntimeControls());step.Commit();}
        using(var read=world.AcquireRead()){Near(read.GetResourceQuantity(0),.8);}
        var observation=systems.LastScalarSupplyObservation;Near(observation.GeneratedUnits,.4);Near(observation.CommandWithdrawnUnits,.1);Need(observation.ReadyCommands==1,"Command readiness did not publish.");
        var before=world.CaptureSnapshot();var boundary=systems.FindActuatorBoundary(world.Stamp,new OwnedRuntimeControls(),.1);Need(boundary.Certification=="supply-augmented-64-bisection-upper","Old constant-rate certificate remained enabled.");
        var after=world.CaptureSnapshot();Need(before.Stamp.Matches(after.Stamp),"Deadline published a state.");for(int i=0;i<before.ProviderState.Length;i++)Need(before.ProviderState[i]==after.ProviderState[i],"Deadline mutated readiness/rate rows.");
        using(var step=world.BeginStep(world.Stamp,.1,new OwnedRuntimeInput())){systems.Step(step,environment,new OwnedRuntimeControls());}
        Near(world.CaptureSnapshot().ResourceQuantity[0],.8);Need(systems.ScalarSupplyTotals.AcceptedIntervals==1,"Rejected step counted accepted delivery.");
        // A capacity clamp changes credited supply, not a control or force frontier.
        // Solar replenishes the prior .005-unit command withdrawal each interval.
        var chargedDefinition=new OwnedRuntimeDefinition("charged-supply",0,new[]{new OwnedRuntimePartDefinition(1,"probe",10,new Vec(),Rotation.Identity,100)},new[]{new OwnedRuntimeBodyDefinition(10,new RigidAssemblyMember(0,1,new Vec(),Rotation.Identity,new Vec(),new Vec(1,1,1),Rotation.Identity))},new[]{new OwnedRuntimeResourceDefinition(1,1,"ElectricCharge",1,0,1)},new[]{long.MinValue+solar,long.MinValue+command});
        var charged=new OwnedRuntimeWorld(chargedDefinition,OwnedRuntimeTopology.Compile(chargedDefinition,new OwnedRuntimeConnection[0]),new OwnedRuntimeMotion{Orientation=Rotation.Identity});
        var chargingSystems=new OwnedRuntimeSystems(charged,new OwnedRuntimeActuatorSpec[0],new OwnedRuntimeSurfaceSpec[0]);packet.commands[0].unitsPerSecond=.05;
        chargingSystems.ConfigureScalarSupply(charged.Stamp,packet,hash,new Vec(2,0,0),0);
        for(int tick=0;tick<12;tick++)
        {
            var source=charged.Stamp;var deadline=chargingSystems.FindActuatorBoundary(source,new OwnedRuntimeControls(),.1);
            Need(deadline.Supported&&!deadline.Reached&&deadline.DeltaSeconds==.1,"Recharging capacity clamp generated an artificial physical frontier.");
            using(var step=charged.BeginStep(source,.1,new OwnedRuntimeInput())){chargingSystems.Step(step,environment,new OwnedRuntimeControls());step.Commit();}
            var state=charged.CaptureSnapshot();Near(state.ResourceQuantity[0],.995);Near(state.ProviderState[0],4);Near(state.ProviderState[1],1);
        }
        Near(charged.Stamp.Time,1.2);Need(chargingSystems.ScalarSupplyTotals.AcceptedIntervals==12,"Full solar/command case requires finite twelve-step work.");
        var config=systems.CaptureScalarSupplyConfiguration();config.packet.solar[0].nominalUnitsPerSecond=999;Near(systems.CaptureScalarSupplyConfiguration().packet.solar[0].nominalUnitsPerSecond,4);
        bool rejected=false;var stamp=world.Stamp;try{systems.RestoreScalarSupplyConfiguration(stamp,new OwnedRuntimeSupplyConfiguration());}catch(ArgumentException){rejected=true;}Need(rejected&&world.Stamp.Matches(stamp),"Invalid cold configuration changed the world.");
        Console.WriteLine("PASS supply fold balance/order, accepted ledger, probe isolation, immutable config and atomic rejection.");
    }
}
