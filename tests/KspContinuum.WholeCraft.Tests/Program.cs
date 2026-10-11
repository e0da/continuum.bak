using System;
using System.IO;
using KspContinuum;

static class Program
{
    static void Reject(Action action) { try { action(); } catch(InvalidDataException) { return; } throw new Exception("Corruption was accepted."); }
    static void Main()
    {
        var mesh=new WholeCraftMeshData {positions=new float[] {0,0,0,1,0,0,0,1,0},normals=new float[0],uv=new float[] {0,0,1,0,0,1},tangents=new float[0],colors=new float[] {1,0,0,1,0,1,0,1,0,0,1,1},submeshes=new[] {new WholeCraftSubmesh {indices=new[] {0,1,2}}}};
        var geometry=new WholeCraftGeometry {meshes=new[] {mesh}};
        var manifest=new WholeCraftSceneManifest {schema="continuum-whole-craft-scene/v1",runId="analytic",processId=1,vesselId="vessel",engine="2019.4.18f1",colorSpace="Gamma",geometry="geometry.bin",geometrySha256=new string('a',64),rootToWorld=new float[] {1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1},rendererInventory=1,capturedRenderers=1,omittedRenderers=0,drawSlots=1,textures=new ResidentTextureAsset[0],textureCaptureModes=new string[0],materials=new[] {new WholeCraftMaterialAsset {id="material",name="actual",shader="unknown-original",keywords=new string[0],properties=new ResidentMaterialProperty[0],omittedProperties=new string[0]}},omissions=new WholeCraftOmission[0],draws=new[] {new WholeCraftDraw {id="part/source/0",rendererId=-1,partId=1,meshIndex=0,submeshIndex=0,materialIndex=0,matrix=new double[] {1,0,0,3,0,1,0,4,0,0,1,5,0,0,0,1}}}};
        WholeCraftSceneFormat.Validate(manifest,geometry);
        string file=Path.Combine(Path.GetTempPath(),"continuum-whole-"+Guid.NewGuid().ToString("N")+".bin");
        try { WholeCraftSceneFormat.WriteGeometry(file,geometry); var read=WholeCraftSceneFormat.ReadGeometry(file); if(read.meshes[0].colors[0]!=1||read.meshes[0].colors[5]!=1||read.meshes[0].submeshes[0].indices[2]!=2||read.meshes[0].positions[3]!=1||read.meshes[0].normals.Length!=0) throw new Exception("Independent triangle/channel roundtrip differs."); }
        finally { if(File.Exists(file)) File.Delete(file); }
        mesh.submeshes[0].indices[2]=3; Reject(()=>WholeCraftSceneFormat.Validate(manifest,geometry)); mesh.submeshes[0].indices[2]=2;
        manifest.draws[0].matrix[3]=double.NaN; Reject(()=>WholeCraftSceneFormat.Validate(manifest,geometry)); manifest.draws[0].matrix[3]=3;
        manifest.omissions=new[] {new WholeCraftOmission {rendererId=-1,name="same",reason="duplicate"}}; Reject(()=>WholeCraftSceneFormat.Validate(manifest,geometry)); manifest.omissions=new WholeCraftOmission[0];
        manifest.geometry="../escape.bin"; Reject(()=>WholeCraftSceneFormat.Validate(manifest,geometry)); manifest.geometry="geometry.bin";
        manifest.geometrySha256="short"; Reject(()=>WholeCraftSceneFormat.Validate(manifest,geometry)); manifest.geometrySha256=new string('a',64);
        string json=ReportJson.Encode(manifest); if(!json.Contains("\"draws\":[{")||!json.Contains("\"rendererId\":-1")) throw new Exception("Actual report serializer dropped whole-craft arrays.");
        var scene=new MultiscaleSceneManifest {schema="continuum-multiscale-scene/v1",runId="analytic",processId=1,vesselId="vessel",bodyId="body",bodyName="body",engine="2019.4.18f1",originalBodyShader="original",colorSpace="Gamma",sourceFrame="body-centred-world-axes",vesselFixture="vessel.json",vesselFixtureSha256=new string('a',64),bodyGeometry="body.bin",bodyGeometrySha256=new string('b',64),vesselAnchorMeters=new double[3],bodyAnchorMeters=new double[3],initialCameraOffsetMeters=new[] {0d,0d,1d},bodyLocalToMeters=new double[] {1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1},bodyRadiusMeters=1,fov=60,initialCameraTarget="vessel",bodyMaterialRecipe="continuum-body-diffuse-v1",bodyTextureProperty="_MainTex",bodyTextureCapture="rgba-readback-mip0",bodyColor=new float[] {1,1,1,1},bodyTextureScaleOffset=new float[] {1,1,0,0},bodyTexture=new ResidentTextureAsset {id="texture",payload="texture.bin",payloadSha256=new string('c',64),payloadLength=4,width=1,height=1,format=4,mipCount=1}};
        MultiscaleSceneFormat.Validate(scene); scene.schema="continuum-multiscale-scene/v2"; Reject(()=>MultiscaleSceneFormat.Validate(scene)); scene.wholeCraft="whole/manifest.json"; scene.wholeCraftSha256=new string('d',64); MultiscaleSceneFormat.Validate(scene);
        Console.WriteLine("Whole-craft triangle/color roundtrip, corruption, coverage, path/digest and serializer guards passed.");
    }
}
