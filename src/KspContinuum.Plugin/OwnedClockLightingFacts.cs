using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    public static class OwnedClockLightingFacts
    {
        [Serializable] public sealed class Receipt
        {
            public string schema="continuum-owned-clock-lighting-facts/v1",status="completed",runId,sessionId,vesselId,bodyName,engine,renderer;
            public string scope="Read-only main-thread instantaneous stock camera/light/physical geometry. No render callback invocation or camera/light/trajectory mutation. This receipt does not capture a PNG; external pixels need an explicit frame/time binding.";
            public int processId,frame,frameEnd,width,height,cameraId,lightId,sunTargetId,scaledCameraId,scaledTargetId;
            public long ownershipEpoch,startedTicks,endedTicks,clockFrequency=Stopwatch.Frequency;
            public bool owned,epochUnchanged,sphericalMainBodyEclipse,useLocalSpaceSunLight,lightEnabled,scaledLightAvailable,scaledLightEnabled,scaledCameraAvailable;
            public double universalTime,universalTimeEnd,bodyRadiusMeters,horizonClearanceMeters,solarIncidenceDegrees,lightDirectionAgreementDegrees,cameraToSunDegrees,lightIntensity,scaledLightIntensity,sunLocalTime;
            public double fieldOfView,orthographicSize,scaledCameraTargetError,scaledCameraMainCameraError;
            public bool orthographic;
            public double[] craftWorld,craftBodyRelative,bodyWorld,sunWorld,sunBodyRelative,toSun,vesselTransformWorld,rootPartWorld;
            public double vesselTransformToComMeters,rootPartToComMeters,sunTargetToComMeters;
            public double[] cameraWorld,cameraBodyRelative,cameraForward,cameraUp,cameraRotation;
            public double[] lightForward,lightColor,sunDirection,sunTargetScaled,sunTargetLocal;
            public double[] scaledCameraPosition,scaledCameraForward,scaledTargetWorld,expectedScaledTarget,expectedScaledMainCamera;
            public double[] ambientColor,ambientSkyColor,ambientEquatorColor,ambientGroundColor;
            public double ambientBoost;public bool mapEnabled;
        }
        static void Need(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        static bool Finite(double value){return !double.IsNaN(value)&&!double.IsInfinity(value);}
        static double[] V(Vector3d value){Need(Finite(value.x)&&Finite(value.y)&&Finite(value.z),"Lighting vector is nonfinite.");return new[]{value.x,value.y,value.z};}
        static double[] V(Vector3 value){return V((Vector3d)value);}
        static double[] C(Color c){return new[]{(double)c.r,c.g,c.b,c.a};}
        static double Angle(Vector3d a,Vector3d b)
        {Need(a.sqrMagnitude>0&&b.sqrMagnitude>0,"Lighting angle direction is absent.");return Math.Atan2(Vector3d.Cross(a.normalized,b.normalized).magnitude,Vector3d.Dot(a.normalized,b.normalized))*180/Math.PI;}
        public static Receipt Capture(Vessel vessel,string runId,string sessionId,long ownershipEpoch,bool owned)
        {
            Need(SynchronizationContext.Current!=null&&SynchronizationContext.Current.GetType().Name=="UnitySynchronizationContext","Lighting facts require Unity main thread.");
            Need(vessel!=null&&vessel.mainBody!=null&&Planetarium.fetch!=null&&Planetarium.fetch.Sun!=null&&FlightCamera.fetch!=null&&FlightCamera.fetch.mainCamera!=null&&Sun.Instance!=null&&Sun.Instance.sunLight!=null&&Sun.Instance.target!=null,"Actual flight camera/body/Sun/light target required.");
            var body=vessel.mainBody;var sun=Planetarium.fetch.Sun;var lighting=Sun.Instance;var camera=FlightCamera.fetch.mainCamera;var light=lighting.sunLight;
            int frame=Time.frameCount;double ut=Planetarium.GetUniversalTime();long started=Stopwatch.GetTimestamp();
            Vector3d craft=vessel.CoMD,radial=craft-body.position,solar=sun.position-craft;
            Need(radial.magnitude>body.Radius&&solar.sqrMagnitude>0,"Main-body eclipse point must be outside its surface.");
            double along=-Vector3d.Dot(radial,solar.normalized),clearance=(along>0?(radial+along*solar.normalized).magnitude:radial.magnitude)-body.Radius;
            var r=new Receipt{runId=runId,sessionId=sessionId,ownershipEpoch=ownershipEpoch,owned=owned,vesselId=vessel.id.ToString("D"),bodyName=body.bodyName,frame=frame,universalTime=ut,startedTicks=started,width=Screen.width,height=Screen.height,engine=Application.unityVersion,renderer=SystemInfo.graphicsDeviceType.ToString(),bodyRadiusMeters=body.Radius,
                craftWorld=V(craft),craftBodyRelative=V(radial),bodyWorld=V(body.position),sunWorld=V(sun.position),sunBodyRelative=V(sun.position-body.position),toSun=V(solar.normalized),horizonClearanceMeters=clearance,sphericalMainBodyEclipse=clearance<=0,solarIncidenceDegrees=Angle(radial,solar),
                cameraId=camera.GetInstanceID(),cameraWorld=V(camera.transform.position),cameraBodyRelative=V((Vector3d)camera.transform.position-body.position),cameraForward=V(camera.transform.forward),cameraUp=V(camera.transform.up),cameraRotation=new[]{(double)camera.transform.rotation.x,camera.transform.rotation.y,camera.transform.rotation.z,camera.transform.rotation.w},fieldOfView=camera.fieldOfView,orthographic=camera.orthographic,orthographicSize=camera.orthographicSize,
                lightId=light.GetInstanceID(),lightEnabled=light.enabled,lightIntensity=light.intensity,lightForward=V(light.transform.forward),lightColor=C(light.color),lightDirectionAgreementDegrees=Angle(solar,-(Vector3d)light.transform.forward),cameraToSunDegrees=Angle(camera.transform.forward,solar),useLocalSpaceSunLight=lighting.useLocalSpaceSunLight,sunLocalTime=lighting.localTime,sunDirection=V(lighting.sunDirection),sunTargetId=lighting.target.GetInstanceID(),sunTargetScaled=V(lighting.target.position),sunTargetLocal=V(ScaledSpace.ScaledToLocalSpace(lighting.target.position)),
                ambientColor=C(RenderSettings.ambientLight),ambientSkyColor=C(RenderSettings.ambientSkyColor),ambientEquatorColor=C(RenderSettings.ambientEquatorColor),ambientGroundColor=C(RenderSettings.ambientGroundColor),ambientBoost=GameSettings.AMBIENTLIGHT_BOOSTFACTOR,mapEnabled=MapView.MapIsEnabled};
            using(var process=Process.GetCurrentProcess())r.processId=process.Id;
            Need(vessel.vesselTransform!=null&&vessel.rootPart!=null,"Vessel/root anchors required.");
            r.vesselTransformWorld=V(vessel.vesselTransform.position);r.rootPartWorld=V(vessel.rootPart.transform.position);
            r.vesselTransformToComMeters=((Vector3d)vessel.vesselTransform.position-craft).magnitude;
            r.rootPartToComMeters=((Vector3d)vessel.rootPart.transform.position-craft).magnitude;
            r.sunTargetToComMeters=(ScaledSpace.ScaledToLocalSpace(lighting.target.position)-craft).magnitude;
            var field=AccessTools.DeclaredField(typeof(Sun),"scaledSunLight");Need(field!=null&&field.FieldType==typeof(Light),"Pinned scaled sunlight field differs.");var scaledLight=field.GetValue(lighting) as Light;
            if(scaledLight!=null){r.scaledLightAvailable=true;r.scaledLightEnabled=scaledLight.enabled;r.scaledLightIntensity=scaledLight.intensity;}
            var scaled=ScaledCamera.Instance;
            if(scaled!=null)
            {
                Need(scaled.tgtRef!=null,"Actual scaled camera target absent.");r.scaledCameraAvailable=true;r.scaledCameraId=scaled.GetInstanceID();r.scaledTargetId=scaled.tgtRef.GetInstanceID();
                Vector3d actual=scaled.transform.position,expected=ScaledSpace.LocalToScaledSpace(scaled.tgtRef.position),mainExpected=ScaledSpace.LocalToScaledSpace(camera.transform.position);
                r.scaledCameraPosition=V(actual);r.scaledCameraForward=V(scaled.transform.forward);r.scaledTargetWorld=V(scaled.tgtRef.position);r.expectedScaledTarget=V(expected);r.expectedScaledMainCamera=V(mainExpected);r.scaledCameraTargetError=(actual-expected).magnitude;r.scaledCameraMainCameraError=(actual-mainExpected).magnitude;
            }
            r.frameEnd=Time.frameCount;r.universalTimeEnd=Planetarium.GetUniversalTime();r.endedTicks=Stopwatch.GetTimestamp();r.epochUnchanged=r.frame==r.frameEnd&&r.universalTime==r.universalTimeEnd;
            Need(r.epochUnchanged&&ReferenceEquals(vessel.mainBody,body)&&ReferenceEquals(Planetarium.fetch.Sun,sun)&&ReferenceEquals(FlightCamera.fetch.mainCamera,camera)&&ReferenceEquals(Sun.Instance,lighting),"Lighting observation epoch/source changed.");return r;
        }
    }
}
