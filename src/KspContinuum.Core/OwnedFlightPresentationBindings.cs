using System;
using System.Collections.Generic;
using System.IO;

namespace KspContinuum
{
    [Serializable] public sealed class OwnedFlightVisualScene
    {
        public string schema="continuum-owned-flight-visuals/v1",runId,vesselId,bodyId,bodyName,engine,colorSpace,nativeManifestSha256;
        public string wholeCraft,wholeCraftSha256,bodyGeometry,bodyGeometrySha256,bodyMaterialRecipe,originalBodyShader,bodyTextureProperty,bodyTextureCapture;
        public int processId,unityFrame;
        public double fixedTime,universalTime,bodyRadiusMeters;
        public long startedTicks,copiedTicks,clockFrequency;
        public double[] rootPositionBodyRelative,bodyRotation,bodyLocalToMeters,directionToSun;
        public float[] bodyColor,bodyTextureScaleOffset,sunColor,ambientColor;
        public ResidentTextureAsset bodyTexture;
    }
    public sealed class OwnedFlightPresentationBindings
    {
        readonly int[] rowParts;
        readonly double[][] sourceLocal;
        readonly double[] bodyMatrices;
        readonly long[] partIds,bodyIds;
        public int DrawCount{get{return rowParts.Length;}}
        public readonly string WholeCraftSha256;
        static void Need(bool ok,string reason){if(!ok)throw new InvalidDataException(reason);}
        static bool Finite(double value){return !double.IsNaN(value)&&!double.IsInfinity(value);}
        static void Numbers(double[] value,int n){Need(value!=null&&value.Length==n,"Owned visual vector shape differs.");foreach(double x in value)Need(Finite(x),"Owned visual nonfinite vector.");}
        static void Numbers(float[] value,int n){Need(value!=null&&value.Length==n,"Owned visual float shape differs.");foreach(float x in value)Need(Finite(x),"Owned visual nonfinite float.");}
        static void Digest(string value){OwnedRuntimeImport.Digest(value);}
        static Vec V(double[] a){return new Vec(a[0],a[1],a[2]);}
        static Vec Sub(Vec a,Vec b){return new Vec(a.X-b.X,a.Y-b.Y,a.Z-b.Z);}
        static Rotation Q(double[] a){return new Rotation(a[0],a[1],a[2],a[3]);}
        public static void Validate(OwnedFlightVisualScene s,OwnedRuntimeImportResult imported)
        {
            Need(s!=null&&imported!=null&&s.schema=="continuum-owned-flight-visuals/v1","Owned visual schema absent/different.");var n=imported.NativeScene;
            Need(n!=null&&s.runId==n.runId&&s.vesselId==n.vesselId&&s.processId==n.processId&&s.nativeManifestSha256==imported.Source.nativeManifestSha256&&s.unityFrame==n.unityFrame&&s.fixedTime==n.fixedTime&&s.universalTime==n.universalTime,"Owned visual source/epoch differs.");
            OwnedRuntimeImport.Text(s.bodyId);OwnedRuntimeImport.Text(s.bodyName);OwnedRuntimeImport.Text(s.engine);OwnedRuntimeImport.Text(s.originalBodyShader);
            Need(s.bodyId!=s.vesselId&&(s.colorSpace=="Gamma"||s.colorSpace=="Linear")&&s.startedTicks>=0&&s.copiedTicks>=s.startedTicks&&s.clockFrequency>0,"Owned visual identity/clock differs.");
            MultiscaleSceneFormat.PortablePath(s.wholeCraft);Digest(s.wholeCraftSha256);MultiscaleSceneFormat.PortablePath(s.bodyGeometry);Digest(s.bodyGeometrySha256);
            Numbers(s.rootPositionBodyRelative,3);Numbers(s.bodyRotation,4);OwnedRuntimeImport.Quaternion(s.bodyRotation);Numbers(s.bodyLocalToMeters,16);Need(NativeAssemblySceneFormat.SupportedAffine(s.bodyLocalToMeters),"Owned body mesh transform is reflected/sheared/degenerate.");
            Need(Finite(s.bodyRadiusMeters)&&s.bodyRadiusMeters>0&&s.bodyMaterialRecipe=="continuum-body-diffuse-v1"&&s.bodyTextureProperty=="_MainTex","Owned body recipe/radius unsupported.");
            Need(s.bodyTextureCapture=="prepared-raw"||s.bodyTextureCapture=="readable-raw"||s.bodyTextureCapture=="rgba-readback-mip0","Owned body texture capture unsupported.");
            Numbers(s.directionToSun,3);double length=s.directionToSun[0]*s.directionToSun[0]+s.directionToSun[1]*s.directionToSun[1]+s.directionToSun[2]*s.directionToSun[2];Need(Math.Abs(length-1)<=1e-6,"Owned visual Sun direction is not normalized.");
            Numbers(s.sunColor,4);Numbers(s.ambientColor,4);Numbers(s.bodyColor,4);Numbers(s.bodyTextureScaleOffset,4);
            var t=s.bodyTexture;Need(t!=null,"Owned body texture absent.");OwnedRuntimeImport.Text(t.id);MultiscaleSceneFormat.PortablePath(t.payload);Digest(t.payloadSha256);
            Need(t.width>0&&t.width<=16384&&t.height>0&&t.height<=16384&&t.payloadLength>0&&t.payloadLength<=1073741824&&t.mipCount>0&&t.mipCount<=15&&t.filter>=0&&t.filter<=2&&t.wrap>=0&&t.wrap<=3&&t.aniso>=0&&t.aniso<=16&&Finite(t.mipBias),"Owned body texture metadata exceeds bounds.");
            if(s.bodyTextureCapture=="rgba-readback-mip0")Need(t.mipCount==1&&t.format==4,"Owned body readback texture shape differs.");
        }
        public OwnedFlightPresentationBindings(OwnedRuntimeImportResult imported,OwnedFlightVisualScene scene,WholeCraftSceneManifest whole,WholeCraftGeometry geometry)
        {
            Validate(scene,imported);WholeCraftSceneFormat.Validate(whole,geometry);
            Need(whole.runId==scene.runId&&whole.vesselId==scene.vesselId&&whole.processId==scene.processId,"Owned whole renderer source differs.");
            WholeCraftSha256=scene.wholeCraftSha256;var definition=imported.World.Definition;
            partIds=new long[definition.PartCount];bodyIds=new long[definition.BodyCount];for(int p=0;p<partIds.Length;p++)partIds[p]=definition.GetPart(p).Id;for(int b=0;b<bodyIds.Length;b++)bodyIds[b]=definition.GetBody(b).Id;
            var parts=new Dictionary<uint,OwnedRuntimeCapturedPart>();foreach(var p in imported.CapturedParts.parts)parts.Add(p.partId,p);
            Rotation root=Q(imported.CapturedParts.rootRotation);Vec rootP=V(imported.CapturedParts.rootPositionRelativeAnchor);
            var sourceRoot=new double[16];for(int i=0;i<16;i++)sourceRoot[i]=whole.rootToWorld[i];var native=imported.NativeScene;
            sourceRoot[3]=native.hierarchyRootWorld[3]-native.nativeAnchor[0];sourceRoot[7]=native.hierarchyRootWorld[7]-native.nativeAnchor[1];sourceRoot[11]=native.hierarchyRootWorld[11]-native.nativeAnchor[2];
            rowParts=new int[whole.draws.Length];sourceLocal=new double[whole.draws.Length][];bodyMatrices=new double[definition.BodyCount*16];
            for(int i=0;i<rowParts.Length;i++)
            {
                var d=whole.draws[i];OwnedRuntimeCapturedPart p;Need(parts.TryGetValue(d.partId,out p),"Owned renderer part is absent from physical snapshot.");rowParts[i]=definition.PartSlot(d.partId);
                var partWorld=new double[16];FillMatrix(root*Q(p.rotationInRoot),rootP+root.Rotate(V(p.positionInRoot)),partWorld,0);
                sourceLocal[i]=WholeCraftSceneFormat.RelativeAffine(partWorld,WholeCraftSceneFormat.MultiplyAffine(sourceRoot,d.matrix));
            }
        }
        public void Fill(OwnedRuntimeReadLease read,Vec worldOriginBodyMetres,Vec cameraBodyOriginMetres,double[] output)
        {
            Need(read!=null&&output!=null&&output.Length==rowParts.Length*16&&read.BodyCount==bodyIds.Length&&read.PartCount==partIds.Length,"Owned presentation storage shape changed.");
            RigidAssemblyProperties.RequireVector(worldOriginBodyMetres);RigidAssemblyProperties.RequireVector(cameraBodyOriginMetres);
            for(int b=0;b<bodyIds.Length;b++)
            {
                Need(read.Definition.GetBody(b).Id==bodyIds[b],"Owned presentation body identity changed.");var member=read.GetBody(b);var region=read.GetRegion(read.Topology.RegionOfBody(b));
                Vec position=Sub(worldOriginBodyMetres+region.Motion.RootPosition,cameraBodyOriginMetres)+region.Motion.Orientation.Rotate(member.LocalPosition);
                FillMatrix(region.Motion.Orientation*member.LocalRotation,position,bodyMatrices,b*16);
            }
            for(int row=0;row<rowParts.Length;row++)
            {
                int slot=rowParts[row];var part=read.Definition.GetPart(slot);Need(part.Id==partIds[slot],"Owned presentation part identity changed.");int body=read.Definition.BodySlot(part.BodyId),offset=body*16,outOffset=row*16;
                Rotation q=part.LocalRotation;Vec x=q.Rotate(new Vec(1,0,0)),y=q.Rotate(new Vec(0,1,0)),z=q.Rotate(new Vec(0,0,1));Vec p=part.LocalPosition;var local=sourceLocal[row];
                for(int c=0;c<4;c++)
                {
                    double a=x.X*local[c]+y.X*local[4+c]+z.X*local[8+c]+p.X*local[12+c];
                    double b=x.Y*local[c]+y.Y*local[4+c]+z.Y*local[8+c]+p.Y*local[12+c];
                    double d=x.Z*local[c]+y.Z*local[4+c]+z.Z*local[8+c]+p.Z*local[12+c];double w=local[12+c];
                    for(int r=0;r<4;r++){double value=bodyMatrices[offset+r*4]*a+bodyMatrices[offset+r*4+1]*b+bodyMatrices[offset+r*4+2]*d+bodyMatrices[offset+r*4+3]*w;Need(Finite(value)&&Math.Abs(value)<=float.MaxValue,"Owned camera-relative affine exceeds native float range.");output[outOffset+r*4+c]=value;}
                }
            }
        }
        static void FillMatrix(Rotation q,Vec p,double[] output,int i)
        {Vec x=q.Rotate(new Vec(1,0,0)),y=q.Rotate(new Vec(0,1,0)),z=q.Rotate(new Vec(0,0,1));output[i]=x.X;output[i+1]=y.X;output[i+2]=z.X;output[i+3]=p.X;output[i+4]=x.Y;output[i+5]=y.Y;output[i+6]=z.Y;output[i+7]=p.Y;output[i+8]=x.Z;output[i+9]=y.Z;output[i+10]=z.Z;output[i+11]=p.Z;output[i+12]=output[i+13]=output[i+14]=0;output[i+15]=1;}
    }
}
