using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace KspContinuum
{
    internal static class OwnedClockProviderCapture
    {
        static void Need(bool yes,string reason){if(!yes)throw new InvalidDataException(reason);}
        static bool Finite(double v){return !double.IsNaN(v)&&!double.IsInfinity(v);}
        static string Hash(string path){using(var file=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();}
        static string TextHash(string text){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-","").ToLowerInvariant();}
        static void Digest(string value){Need(value!=null&&value.Length==64,"Provider source digest differs.");foreach(char c in value)Need(c>='0'&&c<='9'||c>='a'&&c<='f',"Provider source digest differs.");}
        static double[] V(Vector3 v){return new[]{(double)v.x,v.y,v.z};}
        static double[] V(Vector3d v){return new[]{v.x,v.y,v.z};}
        static long Id(uint part,int module){Need(part!=0&&module>=0&&module<65536,"Provider stable module identity differs.");return ((long)part<<16)|((long)module);}
        static double[] Unit(Vector3 source)
        {double x=source.x,y=source.y,z=source.z,n=Math.Sqrt(x*x+y*y+z*z);Need(Finite(n)&&n>0,"Provider normal is absent.");return new[]{x/n,y/n,z/n};}
        static OwnedRuntimeSupplyCurveData Curve(FloatCurve source)
        {
            Need(source!=null&&source.Curve!=null,"Provider curve is absent.");var curve=source.Curve;
            Need(curve.preWrapMode!=WrapMode.Loop&&curve.preWrapMode!=WrapMode.PingPong&&curve.postWrapMode!=WrapMode.Loop&&curve.postWrapMode!=WrapMode.PingPong,"Looping provider curve unsupported.");
            var keys=curve.keys;Need(keys.Length>=1&&keys.Length<=128,"Provider curve cardinality unsupported.");
            var result=new OwnedRuntimeSupplyCurveData{preWrapMode=(int)curve.preWrapMode,postWrapMode=(int)curve.postWrapMode,time=new double[keys.Length],value=new double[keys.Length],inTangent=new double[keys.Length],outTangent=new double[keys.Length]};
            for(int i=0;i<keys.Length;i++)
            {var key=keys[i];Need(key.weightedMode==WeightedMode.None&&Finite(key.time)&&Finite(key.value)&&Finite(key.inTangent)&&Finite(key.outTangent),"Weighted or nonfinite provider curve unsupported.");result.time[i]=key.time;result.value[i]=key.value;result.inTangent[i]=key.inTangent;result.outTangent[i]=key.outTangent;}
            new OwnedRuntimeSupplyCurve(result.time,result.value,result.inTangent,result.outTangent);return result;
        }
        static bool Enabled(PartModule module){return module.enabled&&module.moduleIsEnabled;}
        static void AddUnsupported(List<string> rows,Part part,int index,string reason){rows.Add(part.flightID+"/"+index+":"+reason);}
        static bool HasCommandOutputs(ModuleCommand command)
        {
            if(command.resHandler.outputResources!=null)return command.resHandler.outputResources.Count!=0;
            // Stock OnAwake normalizes an absent output list to empty. For a cold
            // null list, require the acquired definition to corroborate no output.
            var nodes=command.part.partInfo.partConfig.GetNodes("MODULE");bool found=false;
            foreach(var node in nodes)if(node.GetValue("name")=="ModuleCommand")
            {found=true;if(node.GetNodes("OUTPUT_RESOURCE").Length!=0)return true;}
            Need(found,"Null command output list lacks its acquired module definition.");return false;
        }
        public static OwnedRuntimeSupplyCapturePacket Capture(Vessel vessel,NativeAssemblySceneManifest native,string nativeSha,string partsSha,string runId)
        {
            try{return CaptureCore(vessel,native,nativeSha,partsSha,runId);}
            catch(Exception error){throw new InvalidDataException("Owned provider acquisition failed; run="+(runId??"<null>")+", vessel="+(vessel==null?"<null>":vessel.id.ToString("D"))+".",error);}
        }
        static OwnedRuntimeSupplyCapturePacket CaptureCore(Vessel vessel,NativeAssemblySceneManifest native,string nativeSha,string partsSha,string runId)
        {
            Need(SynchronizationContext.Current!=null,"Provider capture requires the Unity main-thread synchronization context.");Digest(nativeSha);Digest(partsSha);
            Need(vessel!=null&&native!=null&&vessel.loaded&&!vessel.packed&&vessel.rootPart!=null&&vessel.mainBody!=null&&Planetarium.fetch!=null&&Planetarium.fetch.Sun!=null,"Provider capture requires the original loaded physical vessel/body/Sun.");
            Need(vessel.GetCrewCount()==0,"Initial scalar provider domain requires an actual uncrewed probe vessel.");
            Need(!CheatOptions.InfinitePropellant,"Infinite resource mode is outside scalar provider ownership.");
            long started=Stopwatch.GetTimestamp();int frame=Time.frameCount;double fixedTime=Time.fixedTime,ut=Planetarium.GetUniversalTime();
            Need(native.unityFrame==frame&&native.fixedTime==fixedTime&&native.universalTime==ut&&native.vesselId==vessel.id.ToString("D")&&native.runId==runId,"Provider packet and native ledger differ in epoch/identity.");
            var originalParts=new List<Part>();var moduleObjects=new List<PartModule>();var ids=new HashSet<uint>();var unsupported=new List<string>();var idle=new List<string>();
            var solar=new List<OwnedRuntimeCapturedSolarSupply>();var commands=new List<OwnedRuntimeCapturedCommandLoad>();var engines=new List<OwnedRuntimeCapturedEngineState>();
            CelestialBody body=vessel.mainBody,sun=Planetarium.fetch.Sun;Part rootPart=vessel.rootPart;
            Rigidbody rootBody=rootPart.rb!=null?rootPart.rb:rootPart.Rigidbody;Need(rootBody!=null,"Provider native root body is absent.");
            Vector3d reference=(Vector3d)rootBody.position-body.position,sunRelative=sun.position-body.position;
            double distance=(sunRelative-reference).magnitude,luminosity=PhysicsGlobals.SolarLuminosity,home=PhysicsGlobals.SolarLuminosityAtHome;
            Need(Finite(distance)&&distance>0&&Finite(luminosity)&&luminosity>0&&Finite(home)&&home>0,"Provider unattenuated solar constants differ.");
            var ephemeris=new OwnedRuntimeSupplyEphemerisData{sourceSha256=nativeSha,bodyId=body.bodyName,epoch=ut,validSeconds=300,sunBodyRelative=V(sunRelative),referencePositionBodyMeters=V(reference),bodyRadius=body.Radius,referenceDistanceMeters=distance,solarLuminosity=luminosity,solarLuminosityAtHome=home,referenceFluxMultiplier=luminosity/(4*Math.PI*distance*distance*home)};
            new OwnedRuntimeSolarEphemeris(nativeSha,ut,300,new Vec(sunRelative.x,sunRelative.y,sunRelative.z),new Vec(reference.x,reference.y,reference.z),ephemeris.referenceFluxMultiplier,body.Radius);
            foreach(var part in vessel.parts)
            {
                Need(part!=null&&part.vessel==vessel&&ids.Add(part.flightID)&&part.flightID!=0&&part.partInfo!=null&&part.partInfo.partConfig!=null,"Provider part identity/definition differs.");originalParts.Add(part);
                string definition=TextHash(part.partInfo.partConfig.ToString());
                for(int index=0;index<part.Modules.Count;index++)
                {
                    var module=part.Modules[index];
                    try
                    {
                    Need(module!=null,"Provider module disappeared.");moduleObjects.Add(module);long id=Id(part.flightID,index);
                    var panel=module as ModuleDeployableSolarPanel;
                    if(panel!=null)
                    {
                        if(panel.GetType()!=typeof(ModuleDeployableSolarPanel)||panel.isTracking||panel.useAnimation||panel.panelType!=ModuleDeployableSolarPanel.PanelType.FLAT||panel.useRaycastForTrackingDot||panel.trackingDotTransform==null||!panel.trackingDotTransform.gameObject.activeInHierarchy||panel.isBreakable||part.submergedPortion!=0)
                        {AddUnsupported(unsupported,part,index,"tracking/deployment/nonflat/breakable/submerged solar unsupported");continue;}
                        try
                        {
                            Need(panel.resourceName=="ElectricCharge"&&panel.launchUT>=0&&Finite(panel.efficiencyMult)&&panel.efficiencyMult>=0&&Finite(panel.chargeRate)&&panel.chargeRate>=0,"Fixed solar acquired state differs.");
                            Need(panel.powerCurve!=null&&panel.powerCurve.Curve!=null&&panel.powerCurve.Curve.length==0,"Distance power curve requires a separate owned model.");
                            Need(panel.flowMult==1,"Solar display flow multiplier requires explicit mapping.");
                            Quaternion inverse=Quaternion.Inverse(part.transform.rotation);
                            var row=new OwnedRuntimeCapturedSolarSupply{id=id,rateProviderKey=long.MinValue+id,partId=part.flightID,moduleIndex=index,sourceType=module.GetType().FullName,definitionSha256=definition,panelType=panel.panelType.ToString(),deployState=panel.deployState.ToString(),enabled=Enabled(panel),isTracking=panel.isTracking,useAnimation=panel.useAnimation,useRaycastForTrackingDot=panel.useRaycastForTrackingDot,isBreakable=panel.isBreakable,nominalUnitsPerSecond=panel.chargeRate,efficiencyMultiplier=panel.efficiencyMult,launchUniversalTime=panel.launchUT,capturedSkinTemperature=part.skinTemperature,localPoint=V(inverse*(panel.trackingDotTransform.position-part.transform.position)),localNormal=Unit(inverse*panel.trackingDotTransform.forward),temperatureEfficiency=Curve(panel.temperatureEfficCurve),ageEfficiency=Curve(panel.timeEfficCurve)};
                            solar.Add(row);
                        }
                        catch(InvalidDataException error){AddUnsupported(unsupported,part,index,error.Message);}catch(ArgumentException error){AddUnsupported(unsupported,part,index,error.Message);}
                        continue;
                    }
                    var command=module as ModuleCommand;
                    if(command!=null)
                    {
                        if(command.GetType()!=typeof(ModuleCommand)||command.minimumCrew!=0||command.resHandler==null||command.resHandler.inputResources==null||HasCommandOutputs(command))
                        {AddUnsupported(unsupported,part,index,"Crewed/derived/absent probe command load unsupported");continue;}
                        double rate=0;bool valid=Finite(command.hibernationMultiplier)&&command.hibernationMultiplier>=0&&command.hibernationMultiplier<=1;
                        foreach(var resource in command.resHandler.inputResources){if(resource==null||resource.name!="ElectricCharge"||!Finite(resource.rate)||resource.rate<0){valid=false;break;}rate+=resource.rate;}
                        if(!valid||!Finite(rate)){AddUnsupported(unsupported,part,index,"Non-EC/invalid command rate unsupported");continue;}
                        commands.Add(new OwnedRuntimeCapturedCommandLoad{id=id,partId=part.flightID,moduleIndex=index,sourceType=module.GetType().FullName,definitionSha256=definition,minimumCrew=command.minimumCrew,enabled=Enabled(command),hibernating=command.hibernation,hibernateOnWarp=command.hibernateOnWarp,hibernationMultiplier=command.hibernationMultiplier,unitsPerSecond=rate,readinessProviderKey=long.MinValue+id,initialReady=command.ModuleState==ModuleCommand.ModuleControlState.Nominal&&!command.hibernation&&Enabled(command),sourceModuleState=command.ModuleState.ToString()});continue;
                    }
                    var engine=module as ModuleEngines;
                    if(engine!=null)
                    {
                        if((engine.GetType()!=typeof(ModuleEngines)&&engine.GetType()!=typeof(ModuleEnginesFX))||engine.useEngineResponseTime||engine.throttleLocked||engine.useAtmCurve||engine.useVelCurve||engine.useAtmCurveIsp||engine.useVelCurveIsp||engine.thrustTransforms==null||engine.thrustTransformMultipliers==null||engine.thrustTransforms.Count!=engine.thrustTransformMultipliers.Count)
                        {AddUnsupported(unsupported,part,index,"Transient/air-dependent/locked or absent engine transforms unsupported");continue;}
                        try
                        {
                            var thrusters=new List<OwnedRuntimeCapturedThruster>();var multipliers=new List<double>();var propellants=new List<OwnedRuntimeCapturedPropellant>();Quaternion inverse=Quaternion.Inverse(rootBody.rotation);
                            for(int t=0;t<engine.thrustTransforms.Count;t++){var transform=engine.thrustTransforms[t];Need(transform!=null&&transform.gameObject.activeInHierarchy&&Finite(engine.thrustTransformMultipliers[t])&&engine.thrustTransformMultipliers[t]>=0,"Engine thrust transform/multiplier unsupported.");thrusters.Add(new OwnedRuntimeCapturedThruster{positionInRoot=V(inverse*(transform.position-rootBody.position)),forceDirectionInRoot=Unit(inverse*-transform.forward)});multipliers.Add(engine.thrustTransformMultipliers[t]);}
                            foreach(var p in engine.propellants){Need(p!=null&&(p.name=="LiquidFuel"||p.name=="Oxidizer")&&Finite(p.ratio)&&p.ratio>0,"Only acquired LiquidFuel/Oxidizer chemical engine supported.");propellants.Add(new OwnedRuntimeCapturedPropellant{resource=p.name,ratio=p.ratio});}
                            Need(thrusters.Count>0&&propellants.Count>0&&Finite(engine.currentThrottle)&&Finite(engine.requestedThrottle),"Chemical engine facts absent.");
                            engines.Add(new OwnedRuntimeCapturedEngineState{id=id,partId=part.flightID,moduleIndex=index,sourceType=module.GetType().FullName,definitionSha256=definition,enabled=Enabled(engine),ignited=engine.EngineIgnited,flameout=engine.flameout,useEngineResponseTime=engine.useEngineResponseTime,throttleLocked=engine.throttleLocked,useAtmCurve=engine.useAtmCurve,useVelCurve=engine.useVelCurve,useAtmCurveIsp=engine.useAtmCurveIsp,useVelCurveIsp=engine.useVelCurveIsp,currentThrottle=engine.currentThrottle,requestedThrottle=engine.requestedThrottle,authorityFraction=engine.thrustPercentage*.01,maxForceNewtons=engine.maxThrust*1000,specificImpulseSeconds=engine.atmosphereCurve.Evaluate(0),atmosphereIsp=Curve(engine.atmosphereCurve),thrusters=thrusters.ToArray(),thrustTransformMultipliers=multipliers.ToArray(),propellants=propellants.ToArray()});
                        }
                        catch(InvalidDataException error){AddUnsupported(unsupported,part,index,error.Message);}catch(ArgumentException error){AddUnsupported(unsupported,part,index,error.Message);}continue;
                    }
                    var transmitter=module as ModuleDataTransmitter;
                    if(transmitter!=null){if(transmitter.IsBusy())AddUnsupported(unsupported,part,index,"Active transmitter requires owned state");else idle.Add(part.flightID+"/"+index+":"+module.GetType().FullName+":handback-before-transmission");continue;}
                    if(module is ModuleGenerator||module is ModuleResourceConverter||module is ModuleAlternator)AddUnsupported(unsupported,part,index,"Generator/converter/alternator requires owned state");
                    }
                    catch(Exception error){throw new InvalidDataException("Provider part="+part.flightID+", moduleIndex="+index+", sourceType="+(module==null?"<null>":module.GetType().FullName)+".",error);}
                }
            }
            Need(vessel.parts.Count==originalParts.Count&&native.parts.Length==originalParts.Count&&vessel.rootPart==rootPart&&vessel.mainBody==body&&Planetarium.fetch.Sun==sun,"Provider capture topology/body changed.");
            int m=0;for(int p=0;p<originalParts.Count;p++){Need(ReferenceEquals(vessel.parts[p],originalParts[p])&&native.parts[p].partId==originalParts[p].flightID,"Provider ordered part ledger differs.");foreach(var module in originalParts[p].Modules)Need(ReferenceEquals(module,moduleObjects[m++]),"Provider ordered module ledger differs.");}Need(m==moduleObjects.Count,"Provider module cardinality changed.");
            int endFrame=Time.frameCount;double endFixed=Time.fixedTime,endUT=Planetarium.GetUniversalTime();Need(endFrame==frame&&endFixed==fixedTime&&endUT==ut,"Provider capture epoch advanced.");
            return new OwnedRuntimeSupplyCapturePacket{runId=runId,vesselId=vessel.id.ToString("D"),nativeManifestSha256=nativeSha,partSpecsSha256=partsSha,sourceAssemblySha256=Hash(typeof(Part).Assembly.Location),sourceAssemblyMvid=typeof(Part).Module.ModuleVersionId.ToString("D"),processId=native.processId,unityFrame=frame,unityFrameEnd=endFrame,fixedTime=fixedTime,fixedTimeEnd=endFixed,universalTime=ut,universalTimeEnd=endUT,startedTicks=started,copiedTicks=Stopwatch.GetTimestamp(),clockFrequency=Stopwatch.Frequency,epochUnchanged=true,identityUnchanged=true,ephemeris=ephemeris,solar=solar.ToArray(),commands=commands.ToArray(),engines=engines.ToArray(),unsupported=unsupported.ToArray(),idleTransmitters=idle.ToArray()};
        }
    }
}
