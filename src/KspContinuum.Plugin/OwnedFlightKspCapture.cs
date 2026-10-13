using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace KspContinuum
{
    internal static class OwnedFlightKspCapture
    {
        static void Need(bool ok,string message){if(!ok)throw new InvalidDataException(message);}
        static double[] D(Vector3d v){return new[]{v.x,v.y,v.z};}
        static double[] Q(Quaternion q){return new[]{(double)q.x,q.y,q.z,q.w};}
        static float[] C(Color c){return new[]{c.r,c.g,c.b,c.a};}
        static string Hash(string path){return ScaleCheckpointLoadState.HashFile(path);}
        static void WriteNew(string path,object value)
        {using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(file))writer.Write(ReportJson.Encode(value));}
        static double Bound(Vessel vessel,Vector3 origin)
        {
            double maximum=0;foreach(var part in vessel.parts)foreach(var shape in part.GetComponentsInChildren<Collider>(true))
            {
                if(shape==null||!shape.enabled||!shape.gameObject.activeInHierarchy)continue;Bounds b=shape.bounds;
                for(int corner=0;corner<8;corner++)
                {
                    Vector3 p=b.center+Vector3.Scale(b.extents,new Vector3((corner&1)==0?-1:1,(corner&2)==0?-1:1,(corner&4)==0?-1:1));
                    double x=(double)p.x-origin.x,y=(double)p.y-origin.y,z=(double)p.z-origin.z;maximum=Math.Max(maximum,Math.Sqrt(x*x+y*y+z*z));
                }
            }
            Need(maximum>0&&!double.IsNaN(maximum)&&!double.IsInfinity(maximum),"Captured craft collision envelope is absent/nonfinite.");return maximum;
        }
        public static void Capture(Vessel vessel,NativeAssemblySceneManifest native,string manifestPath,string partsPath,string directory)
        {
            Need(vessel!=null&&vessel.loaded&&!vessel.packed&&vessel.mainBody!=null&&vessel.rootPart!=null,"Owned flight requires actual loaded craft/body.");
            Need(Time.frameCount==native.unityFrame&&Time.fixedTime==native.fixedTime&&Planetarium.GetUniversalTime()==native.universalTime,"Owned flight native epoch differs.");
            var body=vessel.mainBody;var root=vessel.rootPart.rb!=null?vessel.rootPart.rb:vessel.rootPart.Rigidbody;Need(root!=null&&vessel.orbit!=null,"Owned flight canonical native root/orbit is absent.");
            var sun=Planetarium.fetch.Sun;Need(sun!=null&&Sun.Instance!=null&&Sun.Instance.sunLight!=null,"Actual Sun and lighting source required.");
            Vector3d center=vessel.GetWorldPos3D(),rootPosition=(center-body.position)+((Vector3d)root.position-center);
            Vector3d direction=(sun.position-body.position).normalized;
            var heights=new List<double>();var densities=new List<double>();var temperatures=new List<double>();
            double depth=body.atmosphere?body.atmosphereDepth:0;
            if(depth>0)for(int i=0;i<=128;i++)
            {
                double height=depth*i/128.0,temperature=body.GetTemperature(i==128?depth-1e-6:height),density=body.GetDensity(body.GetPressure(height),temperature);
                Need(!double.IsNaN(density)&&!double.IsInfinity(density)&&density>=0&&temperature>0&&!double.IsNaN(temperature)&&!double.IsInfinity(temperature),"Acquired atmosphere scalar is invalid.");
                heights.Add(height);densities.Add(density);temperatures.Add(temperature);
            }
            var capture=new OwnedFlightCapture {runId=native.runId,vesselId=native.vesselId,bodyId="body/"+body.bodyName,processId=native.processId,
                nativeManifestSha256=Hash(manifestPath),partSpecsSha256=Hash(partsPath),sourceEpochSeconds=native.universalTime,gravitationalParameter=body.gravParameter,
                radiusMeters=body.Radius,atmosphereDepthMeters=depth,solarFluxWattsPerSquareMeter=vessel.solarFlux,angularVelocity=D(body.angularVelocity),sunDirection=D(direction),
                bodyRotation=Q(body.transform.rotation),rootPositionBodyRelative=D(rootPosition),rootRotation=Q(root.rotation),centerOfMassVelocityInertial=D(vessel.orbit.GetRelativeVel()),
                altitudeMeters=heights.ToArray(),densityKilogramsPerCubicMeter=densities.ToArray(),temperatureKelvin=temperatures.ToArray(),vesselBoundingRadiusMeters=Bound(vessel,root.position),
                convectiveCoefficient=0,radiationTemperature=PhysicsGlobals.SpaceTemperature,
                omissions=new[]{"Captured altitude-only atmosphere sampled129points and linearly interpolated; no latitude/sun/stock curve exactness. Boundary temperature uses the1micrometre left limit because stock returns0K at vacuum entry; density is sampled at the actual boundary.","Declared zero convection; actual stock space radiation temperature captured.",
                    "Central-body inertial patched-conic research context; no full solar ephemeris or third-body forces.","No terrain/contact solver; conservative source collider envelope supplies spherical entry guards.","Solar flux captured for diagnostics; existing owned thermal schedule has no direct solar absorption."}};
            OwnedFlightWorld.Admit(capture,capture.nativeManifestSha256,capture.partSpecsSha256,new string('0',64));
            WriteNew(Path.Combine(directory,"flight-world.json"),capture);
            string visualDirectory=Path.Combine(directory,"visuals");Need(!Directory.Exists(visualDirectory),"New owned visual source required.");Directory.CreateDirectory(visualDirectory);
            CaptureVisual(vessel,native,capture,visualDirectory);
            Need(Time.frameCount==native.unityFrame&&Time.fixedTime==native.fixedTime&&Planetarium.GetUniversalTime()==native.universalTime&&ReferenceEquals(vessel.mainBody,body),"Owned flight acquisition epoch/body changed.");
        }
        static void CaptureVisual(Vessel vessel,NativeAssemblySceneManifest native,OwnedFlightCapture context,string directory)
        {
            long started=Stopwatch.GetTimestamp();var body=vessel.mainBody;
            var filter=body.scaledBody.GetComponent<MeshFilter>();var renderer=body.scaledBody.GetComponent<MeshRenderer>();
            Need(filter!=null&&filter.sharedMesh!=null&&renderer!=null&&renderer.sharedMaterials.Length==1,"Actual scaled body mesh/material required.");
            Mesh mesh=filter.sharedMesh;Material material=renderer.sharedMaterial;
            Need(material!=null&&material.shader!=null&&material.HasProperty("_MainTex")&&material.GetTexture("_MainTex") is Texture2D,"Actual scaled body MainTex required.");
            Vector3[] vertices=mesh.vertices,normals=mesh.normals;Vector2[] uv=mesh.uv;Vector4[] tangents=mesh.tangents;
            Need(vertices.Length>0&&normals.Length==vertices.Length&&uv.Length==vertices.Length&&mesh.subMeshCount==1,"Captured scaled body layout is unsupported.");
            var data=new ResidentMeshData {positions=new float[vertices.Length*3],normals=new float[vertices.Length*3],uv=new float[vertices.Length*2],tangents=new float[vertices.Length*4],indices=mesh.triangles};
            for(int i=0;i<vertices.Length;i++)
            {
                data.positions[i*3]=vertices[i].x;data.positions[i*3+1]=vertices[i].y;data.positions[i*3+2]=vertices[i].z;
                data.normals[i*3]=normals[i].x;data.normals[i*3+1]=normals[i].y;data.normals[i*3+2]=normals[i].z;data.uv[i*2]=uv[i].x;data.uv[i*2+1]=uv[i].y;
                Vector4 t;if(tangents.Length==vertices.Length)t=tangents[i];else {Vector3 axis=Vector3.Cross(normals[i],Math.Abs(normals[i].y)<.9?Vector3.up:Vector3.right).normalized;t=new Vector4(axis.x,axis.y,axis.z,1);}
                data.tangents[i*4]=t.x;data.tangents[i*4+1]=t.y;data.tangents[i*4+2]=t.z;data.tangents[i*4+3]=t.w;
            }
            string geometry=Path.Combine(directory,"body-geometry.bin");ResidentMaterialFormat.WriteGeometry(geometry,new ResidentGeometry {meshes=new[]{data}});
            string textureMode;var texture=MultiscaleSceneCapture.CaptureTexture((Texture2D)material.GetTexture("_MainTex"),directory,"body-texture.bin",out textureMode);
            string whole=WholeCraftSceneCapture.Capture(vessel,Path.Combine(directory,"whole-craft"),native.runId);
            Matrix4x4 scaled=filter.transform.localToWorldMatrix;Vector3 origin=body.scaledBody.transform.position;double scale=ScaledSpace.ScaleFactor;var matrix=new double[16];matrix[15]=1;
            for(int r=0;r<3;r++){for(int c=0;c<3;c++)matrix[r*4+c]=(double)scaled[r,c]*scale;matrix[r*4+3]=((double)scaled[r,3]-origin[r])*scale;}
            Vector2 tiling=material.GetTextureScale("_MainTex"),offset=material.GetTextureOffset("_MainTex");Light light=Sun.Instance.sunLight;
            var scene=new OwnedFlightVisualScene {runId=native.runId,vesselId=native.vesselId,processId=native.processId,nativeManifestSha256=context.nativeManifestSha256,unityFrame=native.unityFrame,
                fixedTime=native.fixedTime,universalTime=native.universalTime,startedTicks=started,copiedTicks=Stopwatch.GetTimestamp(),clockFrequency=Stopwatch.Frequency,
                bodyId=context.bodyId,bodyName=body.bodyName,engine=Application.unityVersion,colorSpace=QualitySettings.activeColorSpace.ToString(),wholeCraft="whole-craft/manifest.json",wholeCraftSha256=Hash(whole),
                bodyGeometry="body-geometry.bin",bodyGeometrySha256=Hash(geometry),rootPositionBodyRelative=context.rootPositionBodyRelative,bodyRotation=context.bodyRotation,bodyLocalToMeters=matrix,
                directionToSun=context.sunDirection,bodyRadiusMeters=body.Radius,bodyMaterialRecipe="continuum-body-diffuse-v1",originalBodyShader=material.shader.name,bodyTextureProperty="_MainTex",bodyTextureCapture=textureMode,
                bodyTexture=texture,bodyColor=C(material.HasProperty("_Color")?material.GetColor("_Color"):Color.white),bodyTextureScaleOffset=new[]{tiling.x,tiling.y,offset.x,offset.y},sunColor=C(light.color*light.intensity),ambientColor=C(RenderSettings.ambientLight)};
            WriteNew(Path.Combine(directory,"manifest.json"),scene);
        }
    }
}
