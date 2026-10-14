using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Instantly,true)]
    public sealed class OwnedClockFactoryRegistration : MonoBehaviour
    {
        public void Awake(){if(OwnedClockFlightService.Requested)OwnedClockFlightService.RegisterFactory(new OwnedClockFlightFactory());}
    }

    public sealed class OwnedClockFlightFactory : IOwnedClockFlightFactory
    {
        sealed class NumericAcquisition
        {
            public NativeAssemblySceneManifest Native;
            public WholeCraftGeometry Geometry;
            public OwnedRuntimeCapturedParts Parts;
            public OwnedRuntimeSupplyCapturePacket Supply;
            public OwnedFlightCapture ContextCapture;
            public string NativeSha,PartsSha,SupplySha,ContextSha;
            public double MinimumAltitude;
            public OwnedFlightWorkerContext Initialize()
            {
                var imported=OwnedRuntimeImport.Create(Native,Geometry,Parts,NativeSha,PartsSha,supplyCapture:Supply,supplyCaptureSha256:SupplySha);
                var context=OwnedFlightWorld.Admit(ContextCapture,NativeSha,PartsSha,ContextSha);
                var session=OwnedFlightSession.SeedCaptured(imported.World,context,context.CapturedRootPosition,MinimumAltitude);
                imported.Systems.ConfigureScalarSupply(imported.World.Stamp,Supply,SupplySha,session.Origin,session.RuntimeEpoch);
                imported.Systems.SetExecutionStrategy(OwnedRuntimeExecutionStrategy.Compiled);
                imported.Systems.SetDeadlineStrategy(imported.World.Stamp,OwnedRuntimeDeadlineStrategy.ExactOracle);
                imported.Systems.SetThermalPolicy(imported.World.Stamp,OwnedRuntimeThermalPolicy.Reference);
                imported.Systems.SetMechanicalPolicy(imported.World.Stamp,new OwnedRuntimeMechanicalPolicy(true,method:OwnedRuntimeMechanicalMethod.HigherOrderRK23));
                return new OwnedFlightWorkerContext(new OwnedRuntimeHost(imported.World,imported.Systems),session,imported);
            }
        }
        static OwnedFlightCheckpointData CaptureNumeric(OwnedFlightWorkerContext c)
        {return OwnedFlightCheckpoint.Capture(c.Host,(OwnedRuntimeImportResult)c.State,c.Session,c.Timeline.HeldControls,new OwnedRuntimeStepPlan(.02),new OwnedFlightPilotData(),"",timeline:c.Timeline);}
        static OwnedFlightRestoredState RestoreNumeric(OwnedFlightWorkerContext c,OwnedFlightCheckpointData data)
        {Need(data!=null&&data.flight!=null&&data.flight.minimumAltitudeMeters==c.Session.MinimumAltitudeMeters,"Checkpoint numerical domain differs.");var imported=(OwnedRuntimeImportResult)c.State;return OwnedFlightCheckpoint.Restore(c.Host,imported,data,imported.Source.nativeManifestSha256,imported.Source.partSpecsSha256,c.Session.Context.SourceSha256,"");}
        static readonly FieldInfo CommandState=AccessTools.DeclaredField(typeof(ModuleCommand),"moduleState");
        static readonly FieldInfo LocalControlState=AccessTools.DeclaredField(typeof(ModuleCommand),"localVesselControlState");
        static readonly FieldInfo CommCapable=AccessTools.DeclaredField(typeof(ModuleCommand),"commCapable");
        static readonly MethodInfo AggregateControl=AccessTools.DeclaredMethod(typeof(CommNet.CommNetVessel),"UpdateControlState",Type.EmptyTypes);
        static void Need(bool condition,string reason){if(!condition)throw new InvalidOperationException(reason);}
        static string Hash(string path){return ScaleCheckpointLoadState.HashFile(path);}
        public OwnedClockFlightBinding Acquire(Vessel vessel,string freshDirectory,string runId)
        {
            Need(vessel!=null&&vessel.mainBody!=null&&vessel.mainBody.bodyName=="Kerbin"&&!vessel.mainBody.inverseRotation&&vessel.altitude>Math.Max(vessel.mainBody.atmosphereDepth,vessel.mainBody.inverseRotThresholdAltitude)+1000,"Initial owned-clock model requires a nonrotating orbital Kerbin physics frame outside the atmosphere and rotating-frame threshold.");
            Need(CommandState!=null&&CommandState.FieldType==typeof(ModuleCommand.ModuleControlState)&&LocalControlState!=null&&LocalControlState.FieldType==typeof(CommNet.VesselControlState)&&CommCapable!=null&&CommCapable.FieldType==typeof(bool)&&AggregateControl!=null,"Pinned command publication fields differ.");
            OwnedClockPhysicsSettlement.Run(vessel, freshDirectory, runId);
            NativeAssemblySceneManifest native;OwnedRuntimeCapturedParts parts;OwnedFlightCapture contextCapture;
            string nativePath=NativeAssemblySceneCapture.CaptureOwned(vessel,Path.Combine(freshDirectory,"acquired"),runId,out native,out parts,out contextCapture);
            Need(native!=null&&parts!=null&&contextCapture!=null,"Actual acquired numeric DTOs are absent.");
            string home=Path.GetDirectoryName(nativePath),partsPath=Path.Combine(home,"owned-parts.json"),contextPath=Path.Combine(home,"flight-world.json");
            string nativeSha=Hash(nativePath),partsSha=Hash(partsPath),contextSha=Hash(contextPath);
            var supply=OwnedClockProviderCapture.Capture(vessel,native,nativeSha,partsSha,runId);
            Need(supply.unsupported!=null&&supply.unsupported.Length==0,"Unimplemented provider writers: "+string.Join(";",supply.unsupported??new[]{"missing census"}));
            foreach(var command in supply.commands)Need(!command.hibernating,"Initially hibernating probe requires a separate partial-control model.");
            string supplyPath=Path.Combine(home,"owned-clock-providers.json");OwnedClockFlightService.WriteNew(supplyPath,ReportJson.Encode(supply));string supplySha=Hash(supplyPath);
            WholeCraftGeometry geometry=string.IsNullOrEmpty(native.geometry)?null:WholeCraftSceneFormat.ReadGeometry(Path.Combine(home,native.geometry));
            OwnedRuntimeImport.ValidateCapturedParts(parts,native,nativeSha);
            OwnedRuntimeImport.ValidateSupplyCapture(supply,parts,native,nativeSha,partsSha,supplySha);
            double minimumAltitude=Math.Max(vessel.mainBody.atmosphereDepth,vessel.mainBody.inverseRotThresholdAltitude)+1000;
            var providers=new List<OwnedClockProviderBinding>();
            foreach(var part in vessel.parts)
                for(int i=0;i<part.Modules.Count;i++)
                {
                    var module=part.Modules[i];Type type=module.GetType();string kind=null,recipe=null;
                    if(type==typeof(ModuleEngines)||type==typeof(ModuleEnginesFX)){kind="engine-no-response";recipe="acquired-static-vacuum-chemical/no-response-v1";}
                    else if(type==typeof(ModuleRCS)||type==typeof(ModuleRCSFX)){kind="rcs-acquired";recipe="acquired-ordered-control-allocation/v1";}
                    else if(type==typeof(ModuleReactionWheel)){kind="wheel-acquired";recipe="finite-original-wheel-reservoir/v1";}
                    else if(type==typeof(ModuleCommand)){kind="probe-command";recipe="scalar-command-load-and-readiness/v1";}
                    else if(type==typeof(ModuleDeployableSolarPanel)){kind="fixed-solar";recipe="captured-fixed-solar-spherical-eclipse/v1";}
                    else if(type==typeof(ModuleDataTransmitter)){kind="idle-transmitter";recipe="handback-before-transmission/v1";}
                    else if(type==typeof(ModuleSAS)){kind="sas-service";recipe="main-input-service-level/v1";}
                    else if(type==typeof(ModuleDeployableAntenna)){kind="fixed-antenna";recipe="stable-vacuum-antenna-handback-before-deployment/v1";}
                    else if(type==typeof(ModuleCargoPart)){kind="cargo-ui";recipe="stable-orbital-cargo-highlight/v1";}
                    else if(type==typeof(ModulePartVariants)){kind="frozen-variant";recipe="acquired-variant-handback-before-reconfiguration/v1";}
                    else if(type==typeof(FXModuleAnimateThrottle)){kind="idle-engine-animation";recipe="idle-engine-animation-no-powered-clip-proof/v1";}
                    else
                    {
                        Need(type.Assembly==typeof(Part).Assembly,"Foreign module has no selected owned-clock adapter: "+type.FullName);
                        foreach(string name in new[]{"FixedUpdate","OnFixedUpdate","Update","OnUpdate","LateUpdate"})
                            for(Type t=type;t!=null&&t!=typeof(PartModule);t=t.BaseType)Need(AccessTools.DeclaredMethod(t,name,Type.EmptyTypes)==null,"Unbound module callback: "+type.FullName+"."+name);
                        kind="passive-no-callback";recipe="pinned-shell-history-no-declared-frame-callback/v1";
                    }
                    providers.Add(new OwnedClockProviderBinding{partId=part.flightID,moduleIndex=i,sourceType=type.FullName,kind=kind,modelRecipe=recipe});
                }
            var solarModules=new ModuleDeployableSolarPanel[supply.solar.Length];var commandModules=new ModuleCommand[supply.commands.Length];
            var partById=new Dictionary<uint,Part>();foreach(var part in vessel.parts)partById.Add(part.flightID,part);
            for(int i=0;i<solarModules.Length;i++)solarModules[i]=(ModuleDeployableSolarPanel)partById[supply.solar[i].partId].Modules[supply.solar[i].moduleIndex];
            for(int i=0;i<commandModules.Length;i++)commandModules[i]=(ModuleCommand)partById[supply.commands[i].partId].Modules[supply.commands[i].moduleIndex];
            var numeric=new NumericAcquisition{Native=native,Geometry=geometry,Parts=parts,Supply=supply,ContextCapture=contextCapture,NativeSha=nativeSha,PartsSha=partsSha,SupplySha=supplySha,ContextSha=contextSha,MinimumAltitude=minimumAltitude};
            return new OwnedClockFlightBinding
            {
                Native=native,Parts=parts,NativeSha256=nativeSha,PartsSha256=partsSha,ContextSha256=contextSha,ModelScope=supply.modelScope,MinimumAltitudeMeters=minimumAltitude,
                Providers=providers.ToArray(),InitializeWorker=numeric.Initialize,
                CaptureCheckpoint=CaptureNumeric,RestoreCheckpoint=RestoreNumeric,
                PublishProviders=view=>
                {
                    var definition=view.Read.Definition;
                    for(int i=0;i<solarModules.Length;i++)
                    {
                        int slot=ProviderSlot(definition,supply.solar[i].rateProviderKey);double rate=view.Read.GetProviderState(slot);
                        Need(!double.IsNaN(rate)&&!double.IsInfinity(rate)&&rate>=0&&rate<=float.MaxValue,"Published solar rate invalid.");solarModules[i].flowRate=(float)rate;solarModules[i]._flowRate=rate;
                    }
                    for(int i=0;i<commandModules.Length;i++)
                    {
                        bool ready=view.Read.GetProviderState(ProviderSlot(definition,supply.commands[i].readinessProviderKey))==1;
                        var command=commandModules[i];var state=ready?ModuleCommand.ModuleControlState.Nominal:ModuleCommand.ModuleControlState.NotEnoughResources;
                        var authority=ready?CommNet.VesselControlState.ProbeFull:CommNet.VesselControlState.Probe;
                        bool disconnected=CommNet.CommNetScenario.CommNetEnabled&&CommNet.CommNetScenario.Instance!=null&&(command.vessel.connection==null||!command.vessel.connection.IsConnected);
                        if(ready&&disconnected)
                        {
                            if(command.SignalRequired||!command.remoteControl){state=ModuleCommand.ModuleControlState.NoControlPoint;authority=CommNet.VesselControlState.Probe;}
                            else if(command.requiresTelemetry){state=ModuleCommand.ModuleControlState.PartialProbe;authority=CommNet.VesselControlState.ProbePartial;}
                        }
                        CommandState.SetValue(command,state);LocalControlState.SetValue(command,authority);CommCapable.SetValue(command,ready);
                        command.part.isControlSource=(authority&CommNet.VesselControlState.Full)!=0?Vessel.ControlLevel.FULL:(authority&CommNet.VesselControlState.Partial)!=0?Vessel.ControlLevel.PARTIAL_UNMANNED:Vessel.ControlLevel.NONE;
                        command.controlSrcStatusText=state.ToString();
                    }
                    if(commandModules.Length>0&&commandModules[0].vessel.connection!=null)AggregateControl.Invoke(commandModules[0].vessel.connection,null);
                }
            };
        }
        static int ProviderSlot(OwnedRuntimeDefinition definition,long key)
        {for(int i=0;i<definition.ProviderStateCount;i++)if(definition.GetProviderKey(i)==key)return i;throw new InvalidOperationException("Provider bank mapping absent.");}
    }
}
