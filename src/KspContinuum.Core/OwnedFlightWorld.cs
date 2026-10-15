using System;

namespace KspContinuum
{
    [Serializable] public sealed class OwnedFlightCapture
    {
        public string schema = "continuum-owned-flight-world/v1", runId, vesselId, bodyId;
        public string nativeManifestSha256, partSpecsSha256;
        public string frame = "central-body-inertial/world-axes";
        public string atmosphereModel = "captured-altitude-linear/v1";
        public int processId;
        public double sourceEpochSeconds, gravitationalParameter, radiusMeters, atmosphereDepthMeters;
        public double solarFluxWattsPerSquareMeter, convectiveCoefficient, radiationTemperature = 3;
        public double vesselBoundingRadiusMeters;
        public double[] angularVelocity, sunDirection, bodyRotation;
        public double[] altitudeMeters, densityKilogramsPerCubicMeter, temperatureKelvin;
        public double[] rootPositionBodyRelative, rootRotation, centerOfMassVelocityInertial;
        public string[] omissions;
    }

    public sealed class OwnedFlightWorld
    {
        public const int MaximumAtmosphereSamples = 4096;
        readonly OwnedFlightCapture captured;
        readonly double[] altitudes, densities, temperatures;
        public readonly string BodyId, RunId, VesselId, SourceSha256, NativeManifestSha256, PartSpecsSha256, Frame;
        public readonly int ProcessId;
        public readonly double Epoch, Mu, Radius, AtmosphereDepth, SolarFlux, ConvectiveCoefficient, RadiationTemperature, VesselBoundingRadius;
        public readonly Vec AngularVelocity, SunDirection, CapturedRootPosition, CapturedCenterVelocity;
        public readonly Rotation CapturedRootRotation, CapturedBodyRotation;

        OwnedFlightWorld(OwnedFlightCapture data, string hash)
        {
            captured = Copy(data); altitudes = captured.altitudeMeters; densities = captured.densityKilogramsPerCubicMeter;
            temperatures = captured.temperatureKelvin; BodyId=data.bodyId; RunId=data.runId; VesselId=data.vesselId;
            ProcessId=data.processId; SourceSha256=hash; NativeManifestSha256=data.nativeManifestSha256;
            PartSpecsSha256=data.partSpecsSha256; Frame=data.frame; Epoch=data.sourceEpochSeconds;
            Mu=data.gravitationalParameter; Radius=data.radiusMeters; AtmosphereDepth=data.atmosphereDepthMeters;
            SolarFlux=data.solarFluxWattsPerSquareMeter; ConvectiveCoefficient=data.convectiveCoefficient;
            RadiationTemperature=data.radiationTemperature; VesselBoundingRadius=data.vesselBoundingRadiusMeters;
            AngularVelocity=Vector(data.angularVelocity); SunDirection=Vector(data.sunDirection);
            CapturedRootPosition=Vector(data.rootPositionBodyRelative); CapturedCenterVelocity=Vector(data.centerOfMassVelocityInertial);
            CapturedRootRotation=Quaternion(data.rootRotation); CapturedBodyRotation=Quaternion(data.bodyRotation);
        }

        public static OwnedFlightWorld Admit(OwnedFlightCapture data, string expectedNativeSha, string expectedPartSpecsSha, string contextSha256)
        {
            Digest(expectedNativeSha); Digest(expectedPartSpecsSha); Digest(contextSha256);
            Need(data!=null && data.schema=="continuum-owned-flight-world/v1" && data.frame=="central-body-inertial/world-axes", "Owned flight source schema/frame differs.");
            Need(data.nativeManifestSha256==expectedNativeSha && data.partSpecsSha256==expectedPartSpecsSha, "Owned flight source capture differs.");
            Text(data.runId); Text(data.vesselId); Text(data.bodyId); Need(data.processId>0, "Owned flight source process is absent.");
            AssemblyModel.Finite(data.sourceEpochSeconds); AssemblyModel.Positive(data.gravitationalParameter); AssemblyModel.Positive(data.radiusMeters);
            Nonnegative(data.atmosphereDepthMeters); Nonnegative(data.solarFluxWattsPerSquareMeter); Nonnegative(data.convectiveCoefficient);
            Nonnegative(data.vesselBoundingRadiusMeters); AssemblyModel.Positive(data.radiationTemperature);
            Vector(data.angularVelocity); Vec sun=Vector(data.sunDirection);
            Need(Math.Abs(Dot(sun,sun)-1)<=1e-6, "Owned flight Sun direction must be unit length.");
            Quaternion(data.bodyRotation); Quaternion(data.rootRotation); Vector(data.rootPositionBodyRelative); Vector(data.centerOfMassVelocityInertial);
            Need(data.atmosphereModel=="captured-altitude-linear/v1", "Unsupported atmosphere recipe.");
            Need(data.altitudeMeters!=null && data.densityKilogramsPerCubicMeter!=null && data.temperatureKelvin!=null
                && data.altitudeMeters.Length==data.densityKilogramsPerCubicMeter.Length && data.altitudeMeters.Length==data.temperatureKelvin.Length
                && data.altitudeMeters.Length<=MaximumAtmosphereSamples, "Owned flight atmosphere table shape differs.");
            if(data.atmosphereDepthMeters==0) Need(data.altitudeMeters.Length==0, "Airless body must have an empty atmosphere table.");
            else
            {
                Need(data.altitudeMeters.Length>=2 && data.altitudeMeters[0]==0
                    && data.altitudeMeters[data.altitudeMeters.Length-1]==data.atmosphereDepthMeters
                    && data.densityKilogramsPerCubicMeter[data.altitudeMeters.Length-1]==0, "Atmosphere table must cover surface through exact vacuum boundary.");
                for(int i=0;i<data.altitudeMeters.Length;i++)
                {
                    Nonnegative(data.altitudeMeters[i]); Nonnegative(data.densityKilogramsPerCubicMeter[i]); AssemblyModel.Positive(data.temperatureKelvin[i]);
                    Need(i==0 || data.altitudeMeters[i]>data.altitudeMeters[i-1], "Atmosphere altitudes must increase strictly.");
                }
            }
            Need(data.omissions!=null && data.omissions.Length<=4096, "Owned flight omission table absent/excessive.");
            foreach(string omission in data.omissions) Text(omission);
            return new OwnedFlightWorld(data,contextSha256);
        }

        public OwnedFlightCapture Capture() { return Copy(captured); }
        public int AtmosphereSampleCount {get{return altitudes.Length;}}
        public double GetAtmosphereAltitude(int index) {return altitudes[index];}
        public double Altitude(Vec position) { Require(position); return Norm(position)-Radius; }
        public Vec Gravity(Vec position)
        {
            Require(position); double radius=Norm(position); Need(radius>Radius, "Owned flight reached the central-body surface guard.");
            Vec gravity=position*((-Mu/radius/radius)/radius); Require(gravity); return gravity;
        }
        public Vec AtmosphericVelocity(Vec position) { Require(position); Vec velocity=Vec.Cross(AngularVelocity,position); Require(velocity); return velocity; }
        public void Atmosphere(double altitude, out double density, out double temperature)
        {
            AssemblyModel.Finite(altitude); Need(altitude>0, "Owned flight reached the central-body surface guard.");
            if(AtmosphereDepth==0 || altitude>=AtmosphereDepth) { density=0; temperature=RadiationTemperature; return; }
            int low=0,high=altitudes.Length-1;
            while(high-low>1) {int middle=low+(high-low)/2;if(altitudes[middle]<=altitude)low=middle;else high=middle;}
            double fraction=(altitude-altitudes[low])/(altitudes[high]-altitudes[low]);
            density=densities[low]+(densities[high]-densities[low])*fraction;
            temperature=temperatures[low]+(temperatures[high]-temperatures[low])*fraction;
            Nonnegative(density); AssemblyModel.Positive(temperature);
        }
        public OwnedRuntimeEnvironmentSample EvaluateSample(Vec bodyPosition, double universalTime, double localEpoch)
        {
            AssemblyModel.Finite(universalTime); AssemblyModel.Finite(localEpoch); double density,temperature;
            Atmosphere(Altitude(bodyPosition),out density,out temperature);
            return new OwnedRuntimeEnvironmentSample(SourceSha256,0,localEpoch,density,temperature,AtmosphericVelocity(bodyPosition),Gravity(bodyPosition),
                density==0?0:ConvectiveCoefficient,RadiationTemperature,AngularVelocity);
        }
        public OwnedRuntimeEnvironment Evaluate(Vec bodyPosition, double universalTime)
        {
            var sample=EvaluateSample(bodyPosition,universalTime,universalTime);
            return new OwnedRuntimeEnvironment(sample.SourceId,sample.Revision,sample.Epoch,sample.Density,sample.Temperature,
                sample.WindVelocity,sample.Gravity,sample.ConvectiveCoefficient,sample.RadiationTemperature);
        }
        public Rotation BodyRotationAt(double universalTime)
        {
            AssemblyModel.Finite(universalTime); double speed=Norm(AngularVelocity);
            if(speed==0)return CapturedBodyRotation;
            double angle=speed*(universalTime-Epoch); AssemblyModel.Finite(angle);
            return Rotation.AxisAngle(AngularVelocity,Math.IEEERemainder(angle,2*Math.PI))*CapturedBodyRotation;
        }
        static double[] Clone(double[] a) { return a==null?null:(double[])a.Clone(); }
        static OwnedFlightCapture Copy(OwnedFlightCapture d)
        {
            return new OwnedFlightCapture {schema=d.schema,runId=d.runId,vesselId=d.vesselId,bodyId=d.bodyId,processId=d.processId,
                nativeManifestSha256=d.nativeManifestSha256,partSpecsSha256=d.partSpecsSha256,frame=d.frame,atmosphereModel=d.atmosphereModel,
                sourceEpochSeconds=d.sourceEpochSeconds,gravitationalParameter=d.gravitationalParameter,radiusMeters=d.radiusMeters,
                atmosphereDepthMeters=d.atmosphereDepthMeters,solarFluxWattsPerSquareMeter=d.solarFluxWattsPerSquareMeter,
                convectiveCoefficient=d.convectiveCoefficient,radiationTemperature=d.radiationTemperature,vesselBoundingRadiusMeters=d.vesselBoundingRadiusMeters,
                angularVelocity=Clone(d.angularVelocity),sunDirection=Clone(d.sunDirection),bodyRotation=Clone(d.bodyRotation),
                altitudeMeters=Clone(d.altitudeMeters),densityKilogramsPerCubicMeter=Clone(d.densityKilogramsPerCubicMeter),temperatureKelvin=Clone(d.temperatureKelvin),
                rootPositionBodyRelative=Clone(d.rootPositionBodyRelative),rootRotation=Clone(d.rootRotation),centerOfMassVelocityInertial=Clone(d.centerOfMassVelocityInertial),
                omissions=d.omissions==null?null:(string[])d.omissions.Clone()};
        }
        internal static void Need(bool ok,string message) { if(!ok)throw new ArgumentException(message); }
        internal static void Require(Vec vector) { OwnedRuntimeDynamics.Require(vector); }
        internal static double Dot(Vec a,Vec b) { return a.X*b.X+a.Y*b.Y+a.Z*b.Z; }
        internal static double Norm(Vec value) { Require(value);double result=Math.Sqrt(Dot(value,value));AssemblyModel.Finite(result);return result; }
        internal static void Nonnegative(double value) { AssemblyModel.Finite(value);Need(value>=0,"Expected nonnegative owned flight quantity."); }
        internal static Vec Vector(double[] value) { Need(value!=null&&value.Length==3,"Owned flight vector shape differs.");var v=new Vec(value[0],value[1],value[2]);Require(v);return v; }
        internal static Rotation Quaternion(double[] value)
        {Need(value!=null&&value.Length==4,"Owned flight quaternion shape differs.");double square=0;foreach(double x in value){AssemblyModel.Finite(x);square+=x*x;}Need(Math.Abs(square-1)<=1e-6,"Owned flight quaternion must be unit length.");return new Rotation(value[0],value[1],value[2],value[3]);}
        internal static void Text(string value) { Need(!string.IsNullOrWhiteSpace(value)&&value.Length<=4096,"Owned flight identity/string is absent or excessive."); }
        internal static void Digest(string value)
        {Need(value!=null&&value.Length==64,"Owned flight digest shape differs.");foreach(char c in value)Need((c>='0'&&c<='9')||(c>='a'&&c<='f'),"Owned flight digest is not lowercase SHA256.");}
    }
}
