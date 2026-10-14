using System;

namespace KspContinuum
{
    public enum OwnedRuntimeDeadlineStrategy { ExactOracle=0, Retained=1 }
    internal struct OwnedRuntimeDeadlinePrediction
    {internal double Delta,Lower,Upper;internal string Kind;internal int Actuator,Resource;}

    internal sealed class OwnedRuntimeDeadlinePlan
    {
        struct Row
        {
            internal int Region,Electric,Provider;
            internal double Request,FuelRate,ElectricRate,TorqueRate,Capacity,Numerator,FuelDenominator;
            internal int[][] Routes;
            internal double[] Fractions;
        }
        readonly OwnedRuntimeDefinition definition;
        readonly OwnedRuntimeTopology topology;
        readonly long configuration;
        readonly OwnedRuntimeControls controls;
        readonly Row[] rows;
        readonly double[] rates,densities;
        readonly int[] consumers,firstConsumers;
        readonly string unsupported;
        internal OwnedRuntimeDeadlinePlan(OwnedRuntimeBank bank,OwnedRuntimeActuatorSpec[] actuators,int[][][] pools,bool[] enabled,bool[] flows,int[] providers,Rotation orientation,OwnedRuntimeControls controls,long configuration)
        {
            definition=bank.Definition;topology=bank.Topology;this.configuration=configuration;this.controls=controls;
            rows=new Row[actuators.Length];rates=new double[definition.ResourceCount];densities=new double[rates.Length];consumers=new int[rates.Length];firstConsumers=new int[rates.Length];
            for(int r=0;r<densities.Length;r++)densities[r]=definition.GetResource(r).MassPerUnit;
            Vec translation=orientation.Rotate(controls.Translation),attitude=orientation.Rotate(controls.Attitude);
            for(int i=0;i<rows.Length;i++)
            {
                var a=actuators[i];var part=definition.GetPart(a.Part);int body=definition.BodySlot(part.BodyId),region=topology.BodyRegions[body];
                bool allowed=enabled[i]&&(a.Kind==OwnedRuntimeActuatorKind.Engine?controls.EnginesEnabled:a.Kind==OwnedRuntimeActuatorKind.Rcs?controls.RcsEnabled:controls.WheelsEnabled);
                Vec axis=(bank.Members[body].LocalRotation*part.LocalRotation).Rotate(a.LocalAxis);
                double request=allowed?(a.Kind==OwnedRuntimeActuatorKind.Engine?controls.Throttle:a.Kind==OwnedRuntimeActuatorKind.Rcs?Math.Max(0,OwnedRuntimeDynamics.Dot(axis,translation)):OwnedRuntimeDynamics.Dot(axis,attitude)):0;
                request=Math.Max(-1,Math.Min(1,request));double magnitude=Math.Abs(request);
                var row=new Row{Region=region,Request=request,Electric=a.ElectricResource,Provider=providers[i],Capacity=a.WheelMomentumCapacity,Routes=new int[pools[i].Length][],Fractions=new double[pools[i].Length]};
                if(request!=0)
                {
                    row.Numerator=a.MaximumForceOrTorque*magnitude;row.FuelDenominator=a.SpecificImpulseSeconds*9.80665;
                    row.FuelRate=a.Kind==OwnedRuntimeActuatorKind.ReactionWheel?0:a.MaximumForceOrTorque*magnitude/(a.SpecificImpulseSeconds*9.80665);
                    row.ElectricRate=a.ElectricUnitsPerSecond*magnitude;row.TorqueRate=a.Kind==OwnedRuntimeActuatorKind.ReactionWheel?a.MaximumForceOrTorque*magnitude:0;
                    if(!Finite(row.FuelRate)||!Finite(row.ElectricRate)||!Finite(row.TorqueRate)||(a.Kind!=OwnedRuntimeActuatorKind.ReactionWheel&&row.FuelRate<=0)||(a.ElectricUnitsPerSecond>0&&row.ElectricRate<=0)||(a.Kind==OwnedRuntimeActuatorKind.ReactionWheel&&row.TorqueRate<=0))unsupported="nonrepresentable-rate";
                }
                for(int f=0;f<row.Routes.Length;f++)
                {
                    var source=pools[i][f];int count=0;for(int j=0;j<source.Length;j++)if(flows[source[j]]&&topology.BodyRegions[definition.ResourceBodies[source[j]]]==region)count++;
                    var route=new int[count];int next=0;for(int j=0;j<source.Length;j++)if(flows[source[j]]&&topology.BodyRegions[definition.ResourceBodies[source[j]]]==region)route[next++]=source[j];
                    row.Routes[f]=route;row.Fractions[f]=a.FuelMassFraction(f);
                }
                if(row.Electric>=0&&(!flows[row.Electric]||topology.BodyRegions[definition.ResourceBodies[row.Electric]]!=region))row.Electric=-1;
                rows[i]=row;
            }
        }
        internal bool Matches(OwnedRuntimeBank bank,long revision,OwnedRuntimeControls value)
        {return ReferenceEquals(definition,bank.Definition)&&ReferenceEquals(topology,bank.Topology)&&configuration==revision&&Same(controls,value);}
        internal bool Seed(double[] quantities,double[] providers,double horizon,out OwnedRuntimeDeadlinePrediction prediction,out string reason)
        {
            prediction=new OwnedRuntimeDeadlinePrediction{Delta=double.PositiveInfinity,Lower=double.PositiveInfinity,Upper=double.PositiveInfinity,Kind="none",Actuator=-1,Resource=-1};reason=unsupported;if(reason!=null)return false;Array.Clear(rates,0,rates.Length);Array.Clear(consumers,0,consumers.Length);
            for(int i=0;i<rows.Length;i++)
            {
                var row=rows[i];if(row.Request==0)continue;bool active=true;
                if(!Finite(row.Numerator*horizon)||!Finite(row.ElectricRate*horizon)||(row.Provider<0&&!Finite(row.Numerator*horizon/row.FuelDenominator))){reason="nonrepresentable-horizon-demand";return false;}
                if(row.ElectricRate>0&&(row.Electric<0||quantities[row.Electric]==0))active=false;
                double headroom=0;
                if(row.Provider>=0)
                {
                    double h=providers[row.Provider];if(!Finite(h)||Math.Abs(h)>row.Capacity){reason="provider-outside-capacity";return false;}
                    headroom=row.Request>0?h+row.Capacity:row.Capacity-h;if(headroom==0)active=false;
                }
                for(int f=0;f<row.Routes.Length;f++)
                {
                    double available=0;var route=row.Routes[f];for(int j=0;j<route.Length;j++)available+=quantities[route[j]]*densities[route[j]];
                    if(!Finite(available)){reason="nonfinite-availability";return false;}if(available==0)active=false;
                }
                if(!active)continue;
                if(row.Provider>=0)
                {
                    double signed=row.Request>0?providers[row.Provider]:-providers[row.Provider],sum=row.Capacity+signed;
                    double low=SumError(row.Capacity,signed,sum),halfGap=(row.Capacity-Previous(row.Capacity))*.5;
                    double needed=sum-halfGap,neededError=low+SumError(sum,-halfGap,needed);
                    double time=(needed+neededError)/row.TorqueRate;
                    double radius=(Math.Abs(neededError)+Gamma(6)*Math.Abs(needed))/row.TorqueRate+2*Ulp(time);
                    if(!PositiveNormal(time)||!PositiveNormal(headroom)||!Finite(radius)||!Finite(time+radius)||!Finite(Next(time+radius))){reason="nonrepresentable-wheel-rounding-cell";return false;}
                    AdmitPrediction(ref prediction,time,radius,"wheel-capacity",i,-1);
                }
                for(int f=0;f<row.Routes.Length;f++)
                {
                    var route=row.Routes[f];int first=-1;for(int j=0;j<route.Length;j++)if(quantities[route[j]]>0){first=route[j];break;}
                    double rate=row.FuelRate*row.Fractions[f]/densities[first];if(!PositiveNormal(rate)||!PositiveNormal(quantities[first])){reason="nonrepresentable-resource-rate";return false;}if(consumers[first]++==0)firstConsumers[first]=i;rates[first]+=rate;
                }
                if(row.ElectricRate>0){if(!PositiveNormal(row.ElectricRate)||!PositiveNormal(quantities[row.Electric])){reason="nonrepresentable-electric-rate";return false;}if(consumers[row.Electric]++==0)firstConsumers[row.Electric]=i;rates[row.Electric]+=row.ElectricRate;}
            }
            for(int r=0;r<rates.Length;r++)if(rates[r]>0)
            {double time=quantities[r]/rates[r];if(!PositiveNormal(time)||!Finite(rates[r])){reason="nonrepresentable-resource-root";return false;}double radius=Gamma(12+2*consumers[r])*time+2*Ulp(time);if(!Finite(radius)||!Finite(time+radius)||!Finite(Next(time+radius))){reason="nonrepresentable-resource-bracket";return false;}AdmitPrediction(ref prediction,time,radius,"resource-empty",consumers[r]==1?firstConsumers[r]:-1,r);}
            reason=null;return true;
        }
        static void AdmitPrediction(ref OwnedRuntimeDeadlinePrediction prediction,double time,double radius,string kind,int actuator,int resource)
        {
            double lower=time-radius,upper=time+radius;lower=lower>0?Previous(lower):0;upper=Next(upper);
            prediction.Lower=Math.Min(prediction.Lower,lower);prediction.Upper=Math.Min(prediction.Upper,upper);
            if(time<prediction.Delta){prediction.Delta=time;prediction.Kind=kind;prediction.Actuator=actuator;prediction.Resource=resource;}
        }
        static double SumError(double a,double b,double sum){double split=sum-a;return (a-(sum-split))+(b-split);}
        static double Gamma(int operations){double value=operations*1.1102230246251565e-16;return value/(1-value);}
        static double Ulp(double value){return PositiveNormal(value)?Next(value)-value:0;}
        static double Previous(double value){return BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(value)-1);}
        static double Next(double value){return BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(value)+1);}
        static bool PositiveNormal(double value){return Finite(value)&&value>=2.2250738585072014e-308;}
        static bool Finite(double value){return !double.IsNaN(value)&&!double.IsInfinity(value);}
        static bool Same(OwnedRuntimeControls a,OwnedRuntimeControls b)
        {return Bits(a.Throttle,b.Throttle)&&Bits(a.Attitude.X,b.Attitude.X)&&Bits(a.Attitude.Y,b.Attitude.Y)&&Bits(a.Attitude.Z,b.Attitude.Z)&&Bits(a.Translation.X,b.Translation.X)&&Bits(a.Translation.Y,b.Translation.Y)&&Bits(a.Translation.Z,b.Translation.Z)&&a.EnginesEnabled==b.EnginesEnabled&&a.RcsEnabled==b.RcsEnabled&&a.WheelsEnabled==b.WheelsEnabled;}
        static bool Bits(double a,double b){return BitConverter.DoubleToInt64Bits(a)==BitConverter.DoubleToInt64Bits(b);}
    }
}
