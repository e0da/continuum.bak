using System;
using System.IO;
using KspContinuum;

internal static class FlightPresentationTests
{
    static string Digest=new string('a',64);
    static double[] Identity(double x=0,double y=0,double z=0){return new[]{1d,0,0,x,0,1,0,y,0,0,1,z,0,0,0,1};}
    static void Need(bool ok,string reason){if(!ok)throw new Exception(reason);}
    static void Near(double a,double b,string reason){Need(Math.Abs(a-b)<=1e-12,reason+": "+a+" != "+b);}
    static void Reject(Action action,string reason){bool failed=false;try{action();}catch(InvalidDataException){failed=true;}catch(ArgumentException){failed=true;}Need(failed,reason);}
    public static void Run()
    {
        var definition=new OwnedRuntimeDefinition("visual-analytic",0,new[]{new OwnedRuntimePartDefinition(10,"part",20,new Vec(1,0,0),Rotation.Identity,100)},new[]{new OwnedRuntimeBodyDefinition(20,new RigidAssemblyMember(0,1,new Vec(),Rotation.Identity,new Vec(),new Vec(1,2,3),Rotation.Identity))},new OwnedRuntimeResourceDefinition[0]);
        var world=new OwnedRuntimeWorld(definition,OwnedRuntimeTopology.Compile(definition,new OwnedRuntimeConnection[0]),new OwnedRuntimeMotion{Orientation=Rotation.AxisAngle(new Vec(0,0,1),Math.PI*.5)});
        var imported=new OwnedRuntimeImportResult{World=world,Source=new OwnedRuntimeSourceBinding{nativeManifestSha256=Digest},NativeScene=new NativeAssemblySceneManifest{runId="analytic",vesselId="craft",processId=1,unityFrame=2,fixedTime=.25,universalTime=10,nativeAnchor=new float[3],hierarchyRootWorld=Identity(10,20,30)},CapturedParts=new OwnedRuntimeCapturedParts{rootPositionRelativeAnchor=new[]{10d,20,30},rootRotation=new[]{0d,0,0,1},parts=new[]{new OwnedRuntimeCapturedPart{partId=10,positionInRoot=new[]{1d,0,0},rotationInRoot=new[]{0d,0,0,1}}}}};
        var scene=new OwnedFlightVisualScene{runId="analytic",vesselId="craft",bodyId="planet",bodyName="planet",engine="analytic",colorSpace="Gamma",nativeManifestSha256=Digest,processId=1,unityFrame=2,fixedTime=.25,universalTime=10,clockFrequency=1,wholeCraft="whole/manifest.json",wholeCraftSha256=Digest,bodyGeometry="body.bin",bodyGeometrySha256=Digest,bodyRadiusMeters=10,bodyMaterialRecipe="continuum-body-diffuse-v1",originalBodyShader="captured",bodyTextureProperty="_MainTex",bodyTextureCapture="rgba-readback-mip0",rootPositionBodyRelative=new[]{0d,0,20},bodyRotation=new[]{0d,0,0,1},bodyLocalToMeters=Identity(),directionToSun=new[]{0d,0,1},sunColor=new[]{1f,1,1,1},ambientColor=new[]{.1f,.1f,.1f,1},bodyColor=new[]{1f,1,1,1},bodyTextureScaleOffset=new[]{1f,1,0,0},bodyTexture=new ResidentTextureAsset{id="tex",payload="texture.bin",payloadSha256=Digest,payloadLength=4,width=1,height=1,format=4,mipCount=1}};
        var geometry=new WholeCraftGeometry{meshes=new[]{new WholeCraftMeshData{positions=new[]{0f,0,0,1,0,0,0,1,0},normals=new float[0],uv=new float[0],tangents=new float[0],colors=new float[0],submeshes=new[]{new WholeCraftSubmesh{indices=new[]{0,1,2}}}}}};
        var whole=new WholeCraftSceneManifest{schema="continuum-whole-craft-scene/v1",runId="analytic",vesselId="craft",engine="analytic",colorSpace="Gamma",processId=1,geometry="geometry.bin",geometrySha256=Digest,rendererInventory=1,capturedRenderers=1,drawSlots=1,rootToWorld=new[]{1f,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1},textures=new ResidentTextureAsset[0],textureCaptureModes=new string[0],materials=new[]{new WholeCraftMaterialAsset{id="mat",name="mat",shader="source",keywords=new string[0],properties=new ResidentMaterialProperty[0]}},draws=new[]{new WholeCraftDraw{id="row",rendererName="model",poseSource="ancestor-local-double",posePrecision="unity-float-local-trs-composed-double",rendererId=1,meshIndex=0,materialIndex=0,submeshIndex=0,partId=10,enabled=true,active=true,matrix=Identity(3)}},omissions=new WholeCraftOmission[0]};
        var bindings=new OwnedFlightPresentationBindings(imported,scene,whole,geometry);var output=new double[16];Vec origin=new Vec(1e9,2e9,-3e9),camera=origin+new Vec(0,0,-10);
        using(var read=world.AcquireRead())bindings.Fill(read,origin,camera,output);
        Near(output[3],0,"Native-root/part binding X");Near(output[7],3,"Rotated actual part+renderer local offset");Near(output[11],10,"Large-origin subtraction before float");Near(output[1],-1,"Full local affine orientation");Near(output[4],1,"Full local affine orientation");
        var shifted=new double[16];Vec translation=new Vec(1e6,-2e6,3e6);using(var read=world.AcquireRead())bindings.Fill(read,origin+translation,camera+translation,shifted);for(int i=0;i<16;i++)Near(output[i],shifted[i],"Origin invariance");
        scene.unityFrame++;Reject(()=>OwnedFlightPresentationBindings.Validate(scene,imported),"Mixed visual epoch admitted.");scene.unityFrame--;
        scene.bodyLocalToMeters[0]=-1;Reject(()=>OwnedFlightPresentationBindings.Validate(scene,imported),"Reflected body transform admitted.");scene.bodyLocalToMeters[0]=1;
        whole.draws[0].partId=99;Reject(()=>new OwnedFlightPresentationBindings(imported,scene,whole,geometry),"Dangling real renderer part admitted.");whole.draws[0].partId=10;
        Reject(()=>{using(var read=world.AcquireRead())bindings.Fill(read,origin,camera,new double[15]);},"Wrong resident output shape admitted.");
        Console.WriteLine("PASS owned real-geometry part binding, full affine pose, large-origin invariance and source/epoch rejection");
    }
}
