using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace KspContinuum
{
    internal static class OwnedRuntimeKspCapture
    {
        static void Need(bool value,string reason) {if(!value)throw new InvalidDataException(reason);}
        static bool Finite(double value) {return !double.IsNaN(value)&&!double.IsInfinity(value);}
        static string Hash(string path) {using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
        static string TextHash(string text) {using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-","").ToLowerInvariant();}
        static double[] V(Vector3 value) {return new[]{(double)value.x,value.y,value.z};}
        static double[] Q(Quaternion value) {return new[]{(double)value.x,value.y,value.z,value.w};}
        static OwnedRuntimeCapturedPropellant[] Propellants(List<Propellant> source)
        {
            var rows=new List<OwnedRuntimeCapturedPropellant>();
            if(source!=null)foreach(var value in source)
            {
                Need(value!=null&&!string.IsNullOrEmpty(value.name)&&Finite(value.ratio)&&value.ratio>0,"Invalid acquired propellant definition.");
                rows.Add(new OwnedRuntimeCapturedPropellant {resource=value.name,ratio=value.ratio});
            }
            return rows.ToArray();
        }
        static OwnedRuntimeCapturedThruster Thruster(Transform source,Vector3 origin,Quaternion rotation,Vector3 direction)
        {
            Need(source!=null&&source.gameObject.activeInHierarchy,"Inactive acquired thruster.");
            Quaternion inverse=Quaternion.Inverse(rotation);
            return new OwnedRuntimeCapturedThruster {positionInRoot=V(inverse*(source.position-origin)),forceDirectionInRoot=V((inverse*direction).normalized)};
        }
        static OwnedRuntimeCapturedActuator Base(Part part,PartModule module,int index,string kind)
        {
            return new OwnedRuntimeCapturedActuator {id=part.flightID+"/"+index,kind=kind,sourceType=module.GetType().FullName,moduleIndex=index,
                modelRecipe="captured-static-SI-owned-v1; constant captured vacuum Isp; owned control/resource schedule; no stock curve/response/fuel-route equivalence",
                enabled=module.moduleIsEnabled&&module.enabled,authorityFraction=1,enablePitch=true,enableRoll=true,enableYaw=true,enableX=true,enableY=true,enableZ=true,
                maxTorqueNewtonMeters=new double[3],thrusters=new OwnedRuntimeCapturedThruster[0],propellants=new OwnedRuntimeCapturedPropellant[0],resourceRates=new OwnedRuntimeCapturedResourceRate[0]};
        }
        public static void Capture(Vessel vessel,NativeAssemblySceneManifest native,string manifestPath,string output)
        {OwnedRuntimeCapturedParts ignored;Capture(vessel,native,manifestPath,output,out ignored);}
        public static void Capture(Vessel vessel,NativeAssemblySceneManifest native,string manifestPath,string output,out OwnedRuntimeCapturedParts captured)
        {
            captured=null;
            Need(vessel!=null&&vessel.loaded&&!vessel.packed&&vessel.transform!=null&&vessel.rootPart!=null,"Owned runtime capture requires loaded craft.");
            Need(!File.Exists(output)&&!Directory.Exists(output),"Owned runtime cold capture must be new.");
            long started=Stopwatch.GetTimestamp();int frame=Time.frameCount;double fixedTime=Time.fixedTime,ut=Planetarium.GetUniversalTime();
            Need(frame==native.unityFrame&&fixedTime==native.fixedTime&&ut==native.universalTime&&vessel.id.ToString("D")==native.vesselId,"Cold specifications and native geometry require one epoch.");
            Transform root=vessel.transform; Part rootPart=vessel.rootPart;
            Rigidbody rootBody=rootPart.rb!=null?rootPart.rb:rootPart.Rigidbody;
            Need(rootBody!=null,"A native root body is required for the acquired canonical frame.");
            Vector3 origin=rootBody.position;Quaternion rotation=rootBody.rotation;
            Vector3 anchor=new Vector3(native.nativeAnchor[0],native.nativeAnchor[1],native.nativeAnchor[2]);
            var parts=new List<OwnedRuntimeCapturedPart>();var original=new List<Part>();var omissions=new List<string>();var aero=new List<OwnedRuntimeCapturedAeroPart>();
            string assemblyHash=Hash(typeof(Part).Assembly.Location);
            foreach(Part part in vessel.parts)
            {
                Need(part!=null&&part.vessel==vessel&&part.flightID!=0,"Owned runtime part identity is absent.");original.Add(part);
                var resources=new List<OwnedRuntimeCapturedResource>();var actuators=new List<OwnedRuntimeCapturedActuator>();var omitted=new List<string>();
                foreach(PartResource resource in part.Resources)
                {
                    Need(resource!=null&&Finite(resource.amount)&&Finite(resource.maxAmount)&&resource.maxAmount>=0,
                        "Nonfinite acquired resource for part "+part.flightID+" "+(resource==null?"null":resource.resourceName+" amount="+resource.amount.ToString("R")+" capacity="+resource.maxAmount.ToString("R")));
                    PartResourceDefinition definition=resource.info;
                    if(definition==null&&PartResourceLibrary.Instance!=null)
                    {definition=PartResourceLibrary.Instance.GetDefinition(resource.resourceName);omitted.Add("resource-definition-acquired-from-library:"+resource.resourceName);}
                    Need(definition!=null,"Acquired resource definition absent: "+resource.resourceName);
                    resources.Add(new OwnedRuntimeCapturedResource {name=resource.resourceName,amount=resource.amount,capacity=resource.maxAmount,densityKilogramsPerUnit=definition.density*1000.0,flowEnabled=resource.flowState});
                }
                for(int index=0;index<part.Modules.Count;index++)
                {
                    PartModule module=part.Modules[index];Need(module!=null,"Missing acquired module.");
                    if(module.GetType().Assembly!=typeof(Part).Assembly) {omitted.Add("external-module:"+module.GetType().FullName);continue;}
                    var wheel=module as ModuleReactionWheel;
                    if(wheel!=null)
                    {
                        var actuator=Base(part,module,index,"wheel");actuator.actuatorMode=wheel.actuatorModeCycle;
                        actuator.enabled&=wheel.State==ModuleReactionWheel.WheelState.Active;
                        actuator.authorityFraction=wheel.authorityLimiter*.01;
                        actuator.maxTorqueNewtonMeters=new[]{wheel.PitchTorque*1000.0,wheel.RollTorque*1000.0,wheel.YawTorque*1000.0};
                        var rates=new List<OwnedRuntimeCapturedResourceRate>();
                        if(wheel.resHandler!=null&&wheel.resHandler.inputResources!=null)foreach(ModuleResource rate in wheel.resHandler.inputResources)
                            rates.Add(new OwnedRuntimeCapturedResourceRate {resource=rate.name,unitsPerSecond=rate.rate,varyTime=rate.varyTime});
                        actuator.resourceRates=rates.ToArray();actuators.Add(actuator);continue;
                    }
                    var rcs=module as ModuleRCS;
                    if(rcs!=null)
                    {
                        var actuator=Base(part,module,index,"rcs");actuator.enabled&=rcs.rcsEnabled;actuator.useThrottle=rcs.useThrottle;
                        actuator.enablePitch=rcs.enablePitch;actuator.enableRoll=rcs.enableRoll;actuator.enableYaw=rcs.enableYaw;actuator.enableX=rcs.enableX;actuator.enableY=rcs.enableY;actuator.enableZ=rcs.enableZ;
                        actuator.authorityFraction=rcs.thrustPercentage*.01;actuator.maxForceNewtons=rcs.thrusterPower*1000.0;
                        actuator.specificImpulseSeconds=rcs.atmosphereCurve==null?0:rcs.atmosphereCurve.Evaluate(0);
                        actuator.propellants=Propellants(rcs.propellants);
                        if(actuator.propellants.Length==0&&!string.IsNullOrEmpty(rcs.resourceName))actuator.propellants=new[]{new OwnedRuntimeCapturedPropellant {resource=rcs.resourceName,ratio=1}};
                        var thrusters=new List<OwnedRuntimeCapturedThruster>();
                        if(rcs.thrusterTransforms!=null)foreach(Transform thruster in rcs.thrusterTransforms)
                            if(thruster!=null&&thruster.gameObject.activeInHierarchy)thrusters.Add(Thruster(thruster,origin,rotation,rcs.useZaxis?-thruster.forward:-thruster.up));
                        actuator.thrusters=thrusters.ToArray();if(thrusters.Count==0)omitted.Add("rcs-without-active-thrusters:"+index);else actuators.Add(actuator);continue;
                    }
                    var engine=module as ModuleEngines;
                    if(engine!=null)
                    {
                        if(engine.thrustTransforms==null||engine.thrustTransformMultipliers==null||engine.thrustTransforms.Count!=engine.thrustTransformMultipliers.Count)
                        {omitted.Add("engine-transform-multipliers-unavailable:"+index);continue;}
                        for(int thrusterIndex=0;thrusterIndex<engine.thrustTransforms.Count;thrusterIndex++)
                        {
                            Transform thruster=engine.thrustTransforms[thrusterIndex];if(thruster==null||!thruster.gameObject.activeInHierarchy)continue;
                            var actuator=Base(part,module,index,"engine");actuator.id+="/"+thrusterIndex;actuator.enabled&=engine.EngineIgnited;
                            actuator.useThrottle=true;actuator.authorityFraction=engine.thrustPercentage*.01;
                            actuator.maxForceNewtons=engine.maxThrust*1000.0*engine.thrustTransformMultipliers[thrusterIndex];
                            actuator.specificImpulseSeconds=engine.atmosphereCurve==null?0:engine.atmosphereCurve.Evaluate(0);
                            actuator.propellants=Propellants(engine.propellants);actuator.thrusters=new[]{Thruster(thruster,origin,rotation,-thruster.forward)};
                            actuators.Add(actuator);
                        }
                        continue;
                    }
                    omitted.Add("untranslated-stock-module:"+module.GetType().FullName);
                }
                var cube=part.DragCubes;var faceAreas=new double[6];
                if(cube!=null)
                {
                    Need(cube.WeightedArea!=null&&cube.WeightedArea.Length==6,"Acquired drag face shape differs.");
                    for(int face=0;face<6;face++){double area=cube.WeightedArea[face];Need(Finite(area)&&area>=0,"Acquired face area is invalid.");faceAreas[face]=area;}
                }
                aero.Add(new OwnedRuntimeCapturedAeroPart {partId=part.flightID,weightedFaceAreas=faceAreas,disabled=cube==null||cube.None,procedural=cube!=null&&cube.Procedural});
                Need(part.partInfo!=null&&part.partInfo.partConfig!=null,"Acquired authored definition is absent.");
                Need(Finite(part.temperature)&&part.temperature>0&&Finite(part.maxTemp)&&part.maxTemp>0&&Finite(part.thermalMass)&&part.thermalMass>0,"Acquired thermal scalar is invalid.");
                parts.Add(new OwnedRuntimeCapturedPart {partId=part.flightID,name=part.name,definitionSourceSha256=TextHash(part.partInfo.partConfig.ToString()),temperatureKelvin=part.temperature,
                    maximumTemperatureKelvin=part.maxTemp,heatCapacityJoulesPerKelvin=part.thermalMass*1000.0,positionInRoot=V(Quaternion.Inverse(rotation)*(part.transform.position-origin)),
                    rotationInRoot=Q(Quaternion.Inverse(rotation)*part.transform.rotation),resources=resources.ToArray(),actuators=actuators.ToArray(),omissions=omitted.ToArray()});
            }
            Quaternion control=root.rotation; if(vessel.ReferenceTransform!=null)control=vessel.ReferenceTransform.rotation;
            int frameEnd=Time.frameCount;double fixedEnd=Time.fixedTime,utEnd=Planetarium.GetUniversalTime();
            bool identity=vessel.rootPart==rootPart&&vessel.transform==root&&original.Count==vessel.parts.Count;
            for(int i=0;i<original.Count;i++)identity&=ReferenceEquals(original[i],vessel.parts[i])&&original[i].vessel==vessel;
            Need(identity&&frame==frameEnd&&fixedTime==fixedEnd&&ut==utEnd,"Cold specification epoch changed.");
            var packet=new OwnedRuntimeCapturedParts {runId=native.runId,vesselId=native.vesselId,nativeManifestSha256=Hash(manifestPath),sourceAssemblySha256=assemblyHash,
                sourceAssemblyMvid=typeof(Part).Module.ModuleVersionId.ToString("D"),modelScope="Acquired SI resources, thermal capacities and selected stock wheel/RCS/engine parameters. Owned static actuator model, common-region resource routing and independent aero/thermal dynamics. Untranslated modules retained as explicit omissions; this is not a complete stock/mod simulation snapshot.",
                processId=native.processId,unityFrame=frame,unityFrameEnd=frameEnd,fixedTime=fixedTime,fixedTimeEnd=fixedEnd,universalTime=ut,universalTimeEnd=utEnd,
                startedTicks=started,copiedTicks=Stopwatch.GetTimestamp(),clockFrequency=Stopwatch.Frequency,epochUnchanged=true,identityUnchanged=true,massScale=1000,forceScale=1000,
                rootPositionRelativeAnchor=V(origin-anchor),rootRotation=Q(rotation),controlRotationInRoot=Q(Quaternion.Inverse(rotation)*control),parts=parts.ToArray(),omissions=omissions.ToArray()};
            using(var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write))using(var writer=new StreamWriter(file,new UTF8Encoding(false)))writer.Write(ReportJson.Encode(packet));
            var aeroPacket=new OwnedRuntimeCapturedAero {nativeManifestSha256=packet.nativeManifestSha256,runId=packet.runId,vesselId=packet.vesselId,unityFrame=frame,fixedTime=fixedTime,universalTime=ut,
                recipe="Acquired six stock weighted face areas; owned isotropic mean-face drag area at part origin, Cd=1, no lift, total-face thermal area, emissivity=.8, heating fraction=.0001. No stock occlusion/Mach/curve/force equivalence.",parts=aero.ToArray()};
            using(var file=new FileStream(Path.Combine(Path.GetDirectoryName(output),"owned-aero.json"),FileMode.CreateNew,FileAccess.Write))using(var writer=new StreamWriter(file,new UTF8Encoding(false)))writer.Write(ReportJson.Encode(aeroPacket));
            captured=packet;
        }
    }
}
