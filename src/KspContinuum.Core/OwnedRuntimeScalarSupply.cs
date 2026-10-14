using System;

namespace KspContinuum
{
    public sealed class OwnedRuntimeSupplyCurve
    {
        readonly double[] time,value,inTangent,outTangent;
        public int Count {get{return time.Length;}}
        public double Time(int i){return time[i];}public double Value(int i){return value[i];}
        public double InTangent(int i){return inTangent[i];}public double OutTangent(int i){return outTangent[i];}
        public OwnedRuntimeSupplyCurve(double[] time,double[] value,double[] inTangent,double[] outTangent)
        {
            if(time==null||value==null||inTangent==null||outTangent==null||time.Length<1||time.Length>128||value.Length!=time.Length||inTangent.Length!=time.Length||outTangent.Length!=time.Length)throw new ArgumentException("Supply curve cardinality differs.");
            for(int i=0;i<time.Length;i++){AssemblyModel.Finite(time[i]);OwnedRuntimeEnvironment.Nonnegative(value[i]);AssemblyModel.Finite(inTangent[i]);AssemblyModel.Finite(outTangent[i]);if(i>0){if(time[i]<=time[i-1])throw new ArgumentException("Supply curve keys must increase.");AssemblyModel.Finite(time[i]-time[i-1]);}}
            this.time=(double[])time.Clone();this.value=(double[])value.Clone();this.inTangent=(double[])inTangent.Clone();this.outTangent=(double[])outTangent.Clone();
        }
        public double Evaluate(double argument)
        {
            AssemblyModel.Finite(argument);if(argument<=time[0])return value[0];int last=time.Length-1;if(argument>=time[last])return value[last];
            int low=0,high=last;while(high-low>1){int mid=low+(high-low)/2;if(time[mid]<=argument)low=mid;else high=mid;}
            double dt=time[high]-time[low],u=(argument-time[low])/dt,u2=u*u,u3=u2*u;
            double result=(2*u3-3*u2+1)*value[low]+(u3-2*u2+u)*dt*outTangent[low]+(-2*u3+3*u2)*value[high]+(u3-u2)*dt*inTangent[high];
            OwnedRuntimeEnvironment.Nonnegative(result);return result;
        }
    }
    public sealed class OwnedRuntimeSolarEphemeris
    {
        public readonly string SourceSha256,Model="frozen-body-relative-sun/bounded-source-v1";
        public readonly double Epoch,ValidSeconds,ReferenceFluxMultiplier,ReferenceDistanceMeters,BodyRadius;
        public readonly Vec SunBodyRelative,ReferencePositionBodyMeters;
        public OwnedRuntimeSolarEphemeris(string sourceSha256,double epoch,double validSeconds,Vec sunBodyRelative,Vec referencePositionBodyMeters,double referenceFluxMultiplier,double bodyRadius)
        {
            OwnedRuntimeScalarSupply.Digest(sourceSha256);AssemblyModel.Finite(epoch);AssemblyModel.Positive(validSeconds);if(validSeconds>300)throw new ArgumentException("Frozen solar source exceeds300 seconds.");
            AssemblyModel.Finite(epoch+validSeconds);if(!(epoch+validSeconds>epoch))throw new ArgumentException("Solar source interval is unrepresentable.");
            OwnedRuntimeDynamics.Require(sunBodyRelative);OwnedRuntimeDynamics.Require(referencePositionBodyMeters);AssemblyModel.Positive(referenceFluxMultiplier);AssemblyModel.Positive(bodyRadius);
            if(OwnedRuntimeScalarSupply.Norm(sunBodyRelative)<=bodyRadius||OwnedRuntimeScalarSupply.Norm(referencePositionBodyMeters)<=bodyRadius)throw new ArgumentException("Solar source geometry is inside the occulting body.");
            ReferenceDistanceMeters=OwnedRuntimeScalarSupply.Norm(sunBodyRelative+referencePositionBodyMeters*-1);AssemblyModel.Positive(ReferenceDistanceMeters);
            SourceSha256=sourceSha256;Epoch=epoch;ValidSeconds=validSeconds;SunBodyRelative=sunBodyRelative;ReferencePositionBodyMeters=referencePositionBodyMeters;ReferenceFluxMultiplier=referenceFluxMultiplier;BodyRadius=bodyRadius;
        }
        public void RequireTime(double universalTime)
        {AssemblyModel.Finite(universalTime);if(universalTime<Epoch||universalTime>Epoch+ValidSeconds)throw new InvalidOperationException("Solar ephemeris requires source reacquisition outside its declared interval.");}
    }
    public sealed class OwnedRuntimeSolarSupplySpec
    {
        public readonly long Id;public readonly int Part;public readonly string ResourceKind;
        public readonly double NominalUnitsPerSecond,EfficiencyMultiplier,LaunchUniversalTime;
        public readonly Vec LocalPoint,LocalNormal;public readonly bool Enabled;
        public readonly OwnedRuntimeSupplyCurve TemperatureEfficiency,AgeEfficiency;
        public OwnedRuntimeSolarSupplySpec(long id,int part,string resourceKind,double nominalRate,Vec localPoint,Vec localNormal,double efficiencyMultiplier,double launchUniversalTime,OwnedRuntimeSupplyCurve temperature,OwnedRuntimeSupplyCurve age,bool enabled=true)
        {
            OwnedRuntimeScalarSupply.Identity(id,part,resourceKind);OwnedRuntimeEnvironment.Nonnegative(nominalRate);OwnedRuntimeEnvironment.Nonnegative(efficiencyMultiplier);AssemblyModel.Finite(launchUniversalTime);OwnedRuntimeDynamics.Require(localPoint);OwnedRuntimeScalarSupply.Unit(localNormal);
            if(temperature==null||age==null)throw new ArgumentNullException("efficiency curves");
            Id=id;Part=part;ResourceKind=resourceKind;NominalUnitsPerSecond=nominalRate;LocalPoint=localPoint;LocalNormal=localNormal;EfficiencyMultiplier=efficiencyMultiplier;LaunchUniversalTime=launchUniversalTime;TemperatureEfficiency=temperature;AgeEfficiency=age;Enabled=enabled;
        }
    }
    public sealed class OwnedRuntimeCommandLoadSpec
    {
        public readonly long Id;public readonly int Part,ReadinessProviderSlot;public readonly string ResourceKind;
        public readonly double UnitsPerSecond,HibernationMultiplier;public readonly bool Hibernating,Enabled;
        public OwnedRuntimeCommandLoadSpec(long id,int part,string resourceKind,double rate,double hibernationMultiplier,int readinessProviderSlot,bool hibernating,bool enabled=true)
        {
            OwnedRuntimeScalarSupply.Identity(id,part,resourceKind);OwnedRuntimeEnvironment.Nonnegative(rate);OwnedRuntimeEnvironment.Nonnegative(hibernationMultiplier);if(hibernationMultiplier>1||readinessProviderSlot<0)throw new ArgumentException("Command load state differs.");
            Id=id;Part=part;ResourceKind=resourceKind;UnitsPerSecond=rate;HibernationMultiplier=hibernationMultiplier;ReadinessProviderSlot=readinessProviderSlot;Hibernating=hibernating;Enabled=enabled;
        }
    }
    [Serializable] public struct OwnedRuntimeSolarSupplySample
    {
        public long Id;public double UniversalTime,UnitsPerSecond,Incidence,DistanceMeters,DistanceMultiplier,TemperatureMultiplier,AgeMultiplier,AgeDays;
        public bool Eclipsed,Enabled;
    }
    public static class OwnedRuntimeScalarSupply
    {
        public static OwnedRuntimeSolarSupplySample Evaluate(OwnedRuntimeSolarSupplySpec spec,OwnedRuntimeSolarEphemeris ephemeris,double universalTime,Vec panelPositionBodyMeters,Vec panelNormalBody,double temperatureKelvin)
        {
            if(spec==null||ephemeris==null)throw new ArgumentNullException("solar source");ephemeris.RequireTime(universalTime);OwnedRuntimeDynamics.Require(panelPositionBodyMeters);Unit(panelNormalBody);AssemblyModel.Positive(temperatureKelvin);
            if(Norm(panelPositionBodyMeters)<=ephemeris.BodyRadius)throw new InvalidOperationException("Solar panel reaches the no-contact body domain.");
            if(universalTime<spec.LaunchUniversalTime)throw new InvalidOperationException("Solar provider launch time is in the future.");
            Vec ray=ephemeris.SunBodyRelative+panelPositionBodyMeters*-1;double distance=Norm(ray);AssemblyModel.Positive(distance);Vec direction=ray*(1/distance);
            double incidence=Math.Max(0,Math.Min(1,Dot(panelNormalBody,direction))),along=-Dot(panelPositionBodyMeters,direction);
            bool shadow=along>0&&along<distance&&Norm(panelPositionBodyMeters+direction*along)<=ephemeris.BodyRadius;
            double ratio=ephemeris.ReferenceDistanceMeters/distance,distanceMultiplier=ephemeris.ReferenceFluxMultiplier*ratio*ratio,days=(universalTime-spec.LaunchUniversalTime)/86400;
            double temp=spec.TemperatureEfficiency.Evaluate(temperatureKelvin),age=spec.AgeEfficiency.Evaluate(days);
            double rate=spec.Enabled&&!shadow?spec.NominalUnitsPerSecond*incidence*distanceMultiplier*temp*age*spec.EfficiencyMultiplier:0;
            OwnedRuntimeEnvironment.Nonnegative(distanceMultiplier);OwnedRuntimeEnvironment.Nonnegative(rate);
            return new OwnedRuntimeSolarSupplySample{Id=spec.Id,UniversalTime=universalTime,UnitsPerSecond=rate,Incidence=incidence,Eclipsed=shadow,DistanceMeters=distance,DistanceMultiplier=distanceMultiplier,TemperatureMultiplier=temp,AgeMultiplier=age,AgeDays=days,Enabled=spec.Enabled};
        }
        public static double CommandRate(OwnedRuntimeCommandLoadSpec spec)
        {if(spec==null)throw new ArgumentNullException("spec");double rate=spec.Enabled?spec.UnitsPerSecond*(spec.Hibernating?spec.HibernationMultiplier:1):0;OwnedRuntimeEnvironment.Nonnegative(rate);return rate;}
        public static bool CommandReady(OwnedRuntimeCommandLoadSpec spec,double requested,double withdrawn)
        {
            if(spec==null)throw new ArgumentNullException("spec");OwnedRuntimeEnvironment.Nonnegative(requested);OwnedRuntimeEnvironment.Nonnegative(withdrawn);if(withdrawn>requested)throw new ArgumentException("Command withdrawal exceeds its request.");
            return spec.Enabled&&!spec.Hibernating&&(requested==0||withdrawn/requested>=.9);
        }
        internal static void Identity(long id,int part,string kind)
        {if(id<0||part<0||string.IsNullOrWhiteSpace(kind)||kind.Length>128)throw new ArgumentException("Supply stable identity differs.");}
        internal static void Unit(Vec v){OwnedRuntimeDynamics.Require(v);if(Math.Abs(Dot(v,v)-1)>1e-12)throw new ArgumentException("Supply direction must be a retained unit vector.");}
        internal static void Digest(string value){if(value==null||value.Length!=64)throw new ArgumentException("Supply source digest differs.");foreach(char c in value)if(!(c>='0'&&c<='9'||c>='a'&&c<='f'))throw new ArgumentException("Supply source digest differs.");}
        internal static double Dot(Vec a,Vec b){return a.X*b.X+a.Y*b.Y+a.Z*b.Z;}
        internal static double Norm(Vec v){double scale=Math.Max(Math.Abs(v.X),Math.Max(Math.Abs(v.Y),Math.Abs(v.Z)));if(scale==0)return 0;double result=scale*Math.Sqrt((v.X/scale)*(v.X/scale)+(v.Y/scale)*(v.Y/scale)+(v.Z/scale)*(v.Z/scale));AssemblyModel.Finite(result);return result;}
    }
}
