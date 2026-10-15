using System;

namespace KspContinuum
{
    [Serializable] public sealed class OwnedRuntimeSupplyCapturePacket
    {
        public string schema="continuum-owned-clock-providers/v1",runId,vesselId,nativeManifestSha256,partSpecsSha256,sourceAssemblySha256,sourceAssemblyMvid;
        public string modelScope="Fixed flat nontracking solar; spherical main-body eclipse; frozen relative Sun <=300s; owned single-temperature efficiency; probe EC loads/readiness; static vacuum chemical engine facts. No original callback, skin, tracking, transmission or stock resource-flow parity.";
        public int processId,unityFrame,unityFrameEnd;public double fixedTime,fixedTimeEnd,universalTime,universalTimeEnd;
        public long startedTicks,copiedTicks,clockFrequency;public bool epochUnchanged,identityUnchanged;
        public OwnedRuntimeSupplyEphemerisData ephemeris;
        public OwnedRuntimeCapturedSolarSupply[] solar;public OwnedRuntimeCapturedCommandLoad[] commands;public OwnedRuntimeCapturedEngineState[] engines;
        public string[] unsupported,idleTransmitters;
    }
    [Serializable] public sealed class OwnedRuntimeSupplyCurveData
    {
        public string model="finite-unweighted-Hermite/clamped-endpoints-v1";
        public int preWrapMode,postWrapMode;public double[] time,value,inTangent,outTangent;
    }
    [Serializable] public sealed class OwnedRuntimeSupplyEphemerisData
    {
        public string sourceSha256,bodyId,model="frozen-body-relative-sun/bounded-source-v1";
        public double epoch,validSeconds=300,bodyRadius,referenceDistanceMeters,referenceFluxMultiplier,solarLuminosity,solarLuminosityAtHome;
        public double[] sunBodyRelative,referencePositionBodyMeters;
    }
    [Serializable] public sealed class OwnedRuntimeCapturedSolarSupply
    {
        public long id,rateProviderKey;public uint partId;public int moduleIndex;public string sourceType,definitionSha256,panelType,deployState,resourceKind="ElectricCharge";
        public bool enabled,isTracking,useAnimation,useRaycastForTrackingDot,isBreakable;
        public double nominalUnitsPerSecond,efficiencyMultiplier,launchUniversalTime,capturedSkinTemperature;
        public double[] localPoint,localNormal;public OwnedRuntimeSupplyCurveData temperatureEfficiency,ageEfficiency;
    }
    [Serializable] public sealed class OwnedRuntimeCapturedCommandLoad
    {
        public long id,readinessProviderKey;public uint partId;public int moduleIndex,minimumCrew;
        public string sourceType,definitionSha256,sourceModuleState,resourceKind="ElectricCharge";public bool enabled,hibernating,hibernateOnWarp,initialReady;
        public double unitsPerSecond,hibernationMultiplier;
    }
    [Serializable] public sealed class OwnedRuntimeCapturedEngineState
    {
        public long id;public uint partId;public int moduleIndex;public string sourceType,definitionSha256;
        public string modelRecipe="acquired-static-vacuum-chemical/no-response-v1";
        public string thrustFrame="captured-native-root/metres";
        public bool enabled,ignited,flameout,useEngineResponseTime,throttleLocked,useAtmCurve,useVelCurve,useAtmCurveIsp,useVelCurveIsp;
        public double currentThrottle,requestedThrottle,authorityFraction,maxForceNewtons,specificImpulseSeconds;
        public OwnedRuntimeSupplyCurveData atmosphereIsp;public OwnedRuntimeCapturedThruster[] thrusters;public OwnedRuntimeCapturedPropellant[] propellants;
        public double[] thrustTransformMultipliers;
    }
}
