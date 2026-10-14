using System;
using System.Reflection;
using KspContinuum;

static class Program
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Need(bool b,string s){if(!b)throw new Exception(s);}
    static object Call(OwnedRuntimeThermalState s,string name,params object[] args){return typeof(OwnedRuntimeThermalState).GetMethod(name,Flags).Invoke(s,args);}
    static void Reject(Action action){try{action();}catch(ArgumentException){return;}catch(TargetInvocationException e)when(e.InnerException is ArgumentException){return;}throw new Exception("Invalid compact history was admitted.");}
    static OwnedRuntimeThermalState Empty(bool dense=false)
    {
        int count=dense?16:0;return new OwnedRuntimeThermalState{heatJoules=new double[2],convectionExposure=new double[2],convectionSource=new double[2],radiationExposure=new double[2],radiationSource=new double[2],segmentDuration=new double[8],segmentHeat=new double[count],segmentConvection=new double[count],segmentConvectionSource=new double[count],segmentRadiation=new double[count],segmentRadiationSource=new double[count]};
    }
    static void Bits(double a,double b){Need(BitConverter.DoubleToInt64Bits(a)==BitConverter.DoubleToInt64Bits(b),"IEEE thermal history bits changed.");}
    static void Arrays(OwnedRuntimeThermalState a,OwnedRuntimeThermalState b)
    {
        foreach(var field in typeof(OwnedRuntimeThermalState).GetFields(BindingFlags.Instance|BindingFlags.Public))if(field.FieldType==typeof(double[]))
        {var x=(double[])field.GetValue(a);var y=(double[])field.GetValue(b);Need(x.Length==y.Length,"Legacy dense shape changed.");for(int i=0;i<x.Length;i++)Bits(x[i],y[i]);}
    }
    static void Main()
    {
        var compact=Empty();var legacy=compact.Clone();Need(compact.segmentHeat.Length==0&&legacy.segmentHeat.Length==16&&legacy.segmentRadiationSource.Length==16,"Public Clone must materialize legacy forcing columns without changing source.");
        Arrays(legacy,legacy.Clone());var cold=(OwnedRuntimeThermalState)Call(legacy,"CloneCompact");Need(cold.segmentHeat.Length==0,"Dormant positive-zero clone remained dense.");
        var signed=Empty(true);double minusZero=BitConverter.Int64BitsToDouble(long.MinValue);signed.segmentHeat[15]=minusZero;signed.heatJoules[0]=minusZero;signed.segmentDuration[7]=minusZero;
        var signedClone=signed.Clone();Arrays(signed,signedClone);Need(((OwnedRuntimeThermalState)Call(signed,"CloneCompact")).segmentHeat.Length==16,"Signed zero was collapsed to positive zero.");
        var bad=Empty();bad.heatJoules[0]=minusZero;Reject(()=>bad.Clone());bad=Empty();bad.segmentConvection=new double[16];Reject(()=>bad.Clone());
        var pending=Empty();pending.independent=true;pending.planId="independent analytic forcing";pending.segmentCount=1;pending.pendingDuration=.02;pending.causalRevision=1;pending.segmentDuration[0]=.02;Reject(()=>pending.Clone());
        pending.segmentCount=0;pending.pendingDuration=0;pending.causalRevision=0;pending.segmentDuration[0]=0;Call(pending,"PrepareSegmentAppend");Need(pending.segmentHeat.Length==16,"Append did not allocate all logical columns.");
        pending.segmentCount=1;pending.pendingDuration=.02;pending.causalRevision=1;pending.segmentDuration[0]=.02;pending.segmentHeat[0]=1;pending.heatJoules[0]=1;
        var pendingClone=pending.Clone();Arrays(pending,pendingClone);var target=Empty();Call(target,"Validate",2);Call(target,"CopyFrom",pendingClone);Arrays(target,pendingClone);
        Call(target,"Clear");target.integratedTime=.02;target.integratedCausalRevision=1;Call(target,"Validate",2);Need(target.segmentCount==0&&target.heatJoules[0]==0&&target.segmentHeat[0]==0,"Clear retained forcing.");
        var clearCompact=(OwnedRuntimeThermalState)Call(target,"CloneCompact");Need(clearCompact.segmentHeat.Length==0,"Cleared exact-zero history did not compact.");Call(target,"CopyFrom",compact);Need(target.segmentCount==0&&target.pendingDuration==0,"Dense-to-compact source copy retained chronology.");
        ThermalTests.Run();
        Console.WriteLine("PASS dormant allocation, append materialization, legacy dense IEEE clone, pending chronology, signed-zero and mixed/active compact rejection; existing thermal suite passed.");
    }
}
