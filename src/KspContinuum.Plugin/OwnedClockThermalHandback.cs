using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;

namespace KspContinuum
{
    public sealed class OwnedClockThermalHandback
    {
        const string StockSha="8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
        const string StockMvid="10657063-2fc3-43a7-84fa-d39e75e877bf";
        [Serializable] public sealed class SourceRow {public string declaringType,method,ilSha256;public int metadataToken;}
        [Serializable] public sealed class PartRow
        {
            public uint partId;public double temperature,beforeSkinUnexposedTemperature;
            public double beforeInternalEnergy,beforeSkinEnergy,beforeExposedEnergy;
            public double beforeInternalFluxPrevious,beforeSkinFluxPrevious,beforeExposedFluxPrevious;
        }
        [Serializable] public sealed class Receipt
        {
            public string schema="continuum-owned-clock-thermal-handback/v1",status="not-applied",assemblySha256=StockSha,assemblyMvid=StockMvid,vesselId;
            public string scope="Committed owned single-temperature handoff; discard recorded stale stock buffered thermal history. No replay of owned elapsed interval or stock skin-model equivalence.";
            public double committedUniversalTime,beforeLastUniversalTime,beforeThermalCadence,beforeThermalCadenceReciprocal,beforeElapsed,beforeTimeFactor;
            public double afterLastUniversalTime,afterThermalCadence,afterElapsed;
            public bool beforeAnalytical,readbackPassed;public long startedTicks,endedTicks,clockFrequency=Stopwatch.Frequency;
            public SourceRow[] sources;public PartRow[] parts;
        }
        readonly Vessel vessel;readonly FlightIntegrator integrator;readonly Part[] parts;readonly int thread;
        readonly FieldInfo cadence,reciprocal,timeFactor;readonly MethodInfo[] methods;readonly byte[][] originalIl;
        public readonly Receipt Report=new Receipt();
        static void Need(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        static bool Finite(double value){return !double.IsNaN(value)&&!double.IsInfinity(value);}
        static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
        static string FileHash(string path){using(var file=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();}
        static FieldInfo DoubleField(string name)
        {var field=AccessTools.DeclaredField(typeof(FlightIntegrator),name);Need(field!=null&&field.FieldType==typeof(double)&&!field.IsStatic,"Pinned thermal field absent: "+name);return field;}
        public OwnedClockThermalHandback(Vessel vessel)
        {
            Need(vessel!=null&&vessel.loaded&&!vessel.packed,"Thermal handback requires the bound physical vessel.");this.vessel=vessel;thread=Thread.CurrentThread.ManagedThreadId;
            integrator=vessel.GetComponent<FlightIntegrator>();Need(integrator!=null&&integrator.GetType()==typeof(FlightIntegrator),"Original thermal owner absent/derived.");
            Need(typeof(FlightIntegrator).Module.ModuleVersionId.ToString("D")==StockMvid&&FileHash(typeof(FlightIntegrator).Assembly.Location)==StockSha,"Thermal source assembly differs.");
            cadence=DoubleField("fTimeSinceThermo");reciprocal=DoubleField("fTimeSinceThermoRecip");timeFactor=DoubleField("timeFactor");
            parts=vessel.parts.ToArray();Need(parts.Length>0&&parts.Length<=8192,"Thermal part capacity differs.");Report.vesselId=vessel.id.ToString("D");
            methods=new[]{AccessTools.DeclaredMethod(typeof(FlightIntegrator),"UpdateThermodynamics",Type.EmptyTypes),AccessTools.DeclaredMethod(typeof(FlightIntegrator),"ThermalIntegrationPass",new[]{typeof(bool)})};
            originalIl=new byte[methods.Length][];Report.sources=new SourceRow[methods.Length];
            for(int i=0;i<methods.Length;i++){var method=methods[i];Need(method!=null&&method.GetMethodBody()!=null,"Thermal source method absent.");originalIl[i]=method.GetMethodBody().GetILAsByteArray();Report.sources[i]=new SourceRow{declaringType=method.DeclaringType.FullName,method=method.Name,metadataToken=method.MetadataToken,ilSha256=Hash(originalIl[i])};}
        }
        public void Apply(OwnedFlightPresentationLease view)
        {
            Need(Thread.CurrentThread.ManagedThreadId==thread&&Report.status=="not-applied","Thermal handback thread/one-shot ownership differs.");
            Need(view!=null&&view.Context.VesselId==Report.vesselId&&vessel!=null&&vessel.loaded&&!vessel.packed&&ReferenceEquals(vessel.GetComponent<FlightIntegrator>(),integrator),"Thermal handback vessel/owner differs.");
            double ut=view.UniversalTime;Need(Finite(ut)&&Planetarium.GetUniversalTime()==ut,"Thermal handback requires the already published committed UT.");
            Need(view.Read.ThermalPendingDuration==0&&view.Read.ThermalIntegratedTime==view.Read.Stamp.Time,"Owned pending thermal forcing must commit before stock handback.");
            Need(vessel.parts.Count==parts.Length&&view.Read.PartCount==parts.Length,"Thermal topology changed.");
            for(int i=0;i<methods.Length;i++){var current=methods[i].GetMethodBody().GetILAsByteArray();Need(current.Length==originalIl[i].Length,"Thermal method changed.");for(int j=0;j<current.Length;j++)Need(current[j]==originalIl[i][j],"Thermal method changed.");}
            var data=integrator.partThermalDataList;Need(data!=null&&data.Count==parts.Length,"Original thermal graph coverage differs.");var graph=new Dictionary<Part,PartThermalData>();
            foreach(var row in data)Need(row!=null&&row.part!=null&&!graph.ContainsKey(row.part)&&Add(graph,row),"Thermal graph alias differs.");
            var rows=new PartRow[parts.Length];
            for(int i=0;i<parts.Length;i++)
            {
                var part=parts[i];Need(part!=null&&ReferenceEquals(vessel.parts[i],part)&&ReferenceEquals(part.vessel,vessel)&&graph.ContainsKey(part),"Thermal part identity changed.");
                double temperature=view.Read.GetTemperature(view.Read.Definition.PartSlot(part.flightID));Need(Finite(temperature)&&temperature>0&&part.temperature==temperature&&part.skinTemperature==temperature,"Accepted temperature must publish before thermal history handoff.");
                rows[i]=new PartRow{partId=part.flightID,temperature=temperature,beforeSkinUnexposedTemperature=part.skinUnexposedTemperature,beforeInternalEnergy=part.thermalInternalFlux,beforeSkinEnergy=part.thermalSkinFlux,beforeExposedEnergy=part.thermalExposedFlux,beforeInternalFluxPrevious=part.thermalInternalFluxPrevious,beforeSkinFluxPrevious=part.thermalSkinFluxPrevious,beforeExposedFluxPrevious=part.thermalExposedFluxPrevious};
                Need(Finite(rows[i].beforeSkinUnexposedTemperature)&&Finite(rows[i].beforeInternalEnergy)&&Finite(rows[i].beforeSkinEnergy)&&Finite(rows[i].beforeExposedEnergy)&&Finite(rows[i].beforeInternalFluxPrevious)&&Finite(rows[i].beforeSkinFluxPrevious)&&Finite(rows[i].beforeExposedFluxPrevious),"Stock thermal history is nonfinite.");
            }
            Report.beforeLastUniversalTime=vessel.lastUT;Report.beforeThermalCadence=(double)cadence.GetValue(integrator);Report.beforeThermalCadenceReciprocal=(double)reciprocal.GetValue(integrator);Report.beforeElapsed=integrator.timeSinceLastUpdate;Report.beforeTimeFactor=(double)timeFactor.GetValue(integrator);Report.beforeAnalytical=integrator.isAnalytical;
            Need(Finite(Report.beforeLastUniversalTime)&&Finite(Report.beforeThermalCadence)&&Finite(Report.beforeThermalCadenceReciprocal)&&Finite(Report.beforeElapsed)&&Finite(Report.beforeTimeFactor),"Stock thermal cadence is nonfinite.");
            Report.parts=rows;Report.committedUniversalTime=ut;Report.startedTicks=Stopwatch.GetTimestamp();Report.status="applying";
            vessel.lastUT=ut;cadence.SetValue(integrator,0d);reciprocal.SetValue(integrator,0d);timeFactor.SetValue(integrator,0d);integrator.timeSinceLastUpdate=0;integrator.isAnalytical=false;
            for(int i=0;i<parts.Length;i++)
            {
                var part=parts[i];double temperature=rows[i].temperature;part.skinUnexposedTemperature=temperature;
                part.thermalInternalFlux=part.thermalSkinFlux=part.thermalExposedFlux=0;
                part.thermalInternalFluxPrevious=part.thermalSkinFluxPrevious=part.thermalExposedFluxPrevious=0;
                var row=graph[part];row.previousTemperature=row.previousSkinTemperature=row.previousSkinUnexposedTemperature=temperature;
            }
            Need(vessel.lastUT==ut&&(double)cadence.GetValue(integrator)==0&&(double)reciprocal.GetValue(integrator)==0&&(double)timeFactor.GetValue(integrator)==0&&integrator.timeSinceLastUpdate==0&&!integrator.isAnalytical,"Thermal cadence handoff readback differs.");
            for(int i=0;i<parts.Length;i++){var part=parts[i];var row=graph[part];Need(part.temperature==rows[i].temperature&&part.skinTemperature==rows[i].temperature&&part.skinUnexposedTemperature==rows[i].temperature&&part.thermalInternalFlux==0&&part.thermalSkinFlux==0&&part.thermalExposedFlux==0&&part.thermalInternalFluxPrevious==0&&part.thermalSkinFluxPrevious==0&&part.thermalExposedFluxPrevious==0&&row.previousTemperature==rows[i].temperature&&row.previousSkinTemperature==rows[i].temperature&&row.previousSkinUnexposedTemperature==rows[i].temperature,"Thermal part history handoff readback differs.");}
            Report.afterLastUniversalTime=vessel.lastUT;Report.afterThermalCadence=(double)cadence.GetValue(integrator);Report.afterElapsed=integrator.timeSinceLastUpdate;Report.endedTicks=Stopwatch.GetTimestamp();Report.readbackPassed=true;Report.status="completed";
        }
        static bool Add(Dictionary<Part,PartThermalData> rows,PartThermalData row){rows.Add(row.part,row);return true;}
    }
}
