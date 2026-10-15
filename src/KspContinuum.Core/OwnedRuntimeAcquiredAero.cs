using System;
using System.Collections.Generic;
using System.IO;

namespace KspContinuum
{
    [Serializable] public sealed class OwnedRuntimeCapturedAero
    {
        public string schema="continuum-owned-runtime-acquired-aero/v1",nativeManifestSha256,runId,vesselId,recipe;
        public int unityFrame;
        public double fixedTime,universalTime;
        public OwnedRuntimeCapturedAeroPart[] parts;
    }
    [Serializable] public sealed class OwnedRuntimeCapturedAeroPart
    {
        public uint partId;
        public double[] weightedFaceAreas;
        public bool disabled,procedural;
    }
    public static class OwnedRuntimeAcquiredAero
    {
        public static OwnedRuntimeSurfaceSpec[] Build(OwnedRuntimeImportResult imported,OwnedRuntimeCapturedAero captured,double dragCoefficient=1,double heatingFraction=.0001)
        {
            if(imported==null||captured==null||captured.schema!="continuum-owned-runtime-acquired-aero/v1"||captured.nativeManifestSha256!=imported.Source.nativeManifestSha256
                ||captured.runId!=imported.Source.runId||captured.vesselId!=imported.Source.vesselId||captured.unityFrame!=imported.NativeScene.unityFrame
                ||captured.fixedTime!=imported.NativeScene.fixedTime||captured.universalTime!=imported.NativeScene.universalTime||captured.parts==null)
                throw new InvalidDataException("Acquired aero source/epoch differs.");
            OwnedRuntimeEnvironment.Nonnegative(dragCoefficient);OwnedRuntimeEnvironment.Nonnegative(heatingFraction);
            if(heatingFraction>1)throw new ArgumentOutOfRangeException("heatingFraction");
            var definition=imported.World.Definition;var slots=new Dictionary<long,int>();
            for(int i=0;i<definition.PartCount;i++)slots.Add(definition.GetPart(i).Id,i);
            var seen=new HashSet<uint>();var rows=new List<OwnedRuntimeSurfaceSpec>();
            foreach(var part in captured.parts)
            {
                int slot;if(part==null||!seen.Add(part.partId)||!slots.TryGetValue(part.partId,out slot)||part.weightedFaceAreas==null||part.weightedFaceAreas.Length!=6)
                    throw new InvalidDataException("Acquired aero part/area shape differs.");
                double sum=0;foreach(double area in part.weightedFaceAreas){OwnedRuntimeEnvironment.Nonnegative(area);sum+=area;}
                OwnedRuntimeEnvironment.Nonnegative(sum);
                if(!part.disabled&&sum>0)
                    rows.Add(new OwnedRuntimeSurfaceSpec(slot,new Vec(),new Vec(0,1,0),sum/6*dragCoefficient,0,sum,.8,heatingFraction));
            }
            if(seen.Count!=definition.PartCount)throw new InvalidDataException("Acquired aero part coverage differs.");
            return rows.ToArray();
        }
        public static OwnedRuntimeSystems Attach(OwnedRuntimeImportResult imported,OwnedRuntimeSurfaceSpec[] surfaces)
        {
            var old=imported.Systems;var actuators=new OwnedRuntimeActuatorSpec[old.ActuatorCount];var links=new OwnedRuntimeThermalLink[old.ThermalLinkCount];
            var slots=new int[actuators.Length];var flow=new bool[imported.World.Definition.ResourceCount];
            for(int i=0;i<actuators.Length;i++){actuators[i]=old.GetActuator(i);slots[i]=old.WheelProviderSlot(i);}
            for(int i=0;i<links.Length;i++)links[i]=old.GetThermalLink(i);
            for(int i=0;i<flow.Length;i++)flow[i]=old.IsResourceFlowEnabled(i);
            var systems=new OwnedRuntimeSystems(imported.World,actuators,surfaces,links,flow,false,slots,old.ControlOrientation);
            for(int i=0;i<actuators.Length;i++)systems.SetEnabled(i,old.IsEnabled(i));
            imported.Systems=systems;return systems;
        }
    }
}
