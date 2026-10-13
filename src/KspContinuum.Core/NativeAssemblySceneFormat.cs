using System;
using System.Collections.Generic;
using System.IO;

namespace KspContinuum
{
    [Serializable] public sealed class NativeAssemblySceneManifest
    {
        public string schema, runId, vesselId, engine, geometry, geometrySha256;
        public string coordinateFrame, nativePoseSource, hierarchyPoseSource, coverageScope;
        public int processId, unityFrame, unityFrameEnd, rootTransformId, rootTransformIdEnd;
        public double fixedTime, fixedTimeEnd, universalTime, universalTimeEnd;
        public long startedTicks, copiedTicks, clockFrequency;
        public bool epochUnchanged, identityUnchanged, completeColliderCoverage;
        public float[] nativeAnchor;
        public double[] hierarchyRootWorld, frameVelocity;
        public NativeAssemblyBody[] bodies;
        public NativeAssemblyPartAlias[] parts;
        public NativeAssemblyCollider[] colliders;
        public NativeAssemblyOmission[] omissions;
    }
    [Serializable] public sealed class NativeAssemblyBody
    {
        public uint ownerPartId; public string ownershipSource;
        public int bodyId, transformId, constraints, interpolation, collisionDetection;
        public float mass, drag, angularDrag, maxAngularVelocity, sleepThreshold;
        public double[] position, worldCenterOfMass;
        public float[] rotation, localCenterOfMass, principalInertia, principalRotation, velocity, angularVelocity;
        public bool isKinematic, useGravity, detectCollisions, sleeping, active;
    }
    [Serializable] public sealed class NativeAssemblyPartAlias
    {
        public uint partId, parentPartId;
        public int partInstanceId, ownBodyId, effectiveBodyId, servoBodyId;
        public string name;
    }
    [Serializable] public sealed class NativeAssemblyCollider
    {
        public int colliderId, transformId, bodyId, meshIndex, layer, direction, cookingOptions;
        public uint partId;
        public string name, kind, poseSource, omissionReason;
        public double[] localToBody;
        public float[] center, size;
        public float radius, height, contactOffset, dynamicFriction, staticFriction, bounciness;
        public int frictionCombine, bounceCombine;
        public bool supported, enabled, active, trigger, convex, materialPresent;
    }
    [Serializable] public sealed class NativeAssemblyOmission
    {
        public int colliderId; public string reason;
    }

    public static class NativeAssemblySceneFormat
    {
        public const int MaxRows = 50000;
        static void Need(bool ok, string reason) { if (!ok) throw new InvalidDataException(reason); }
        static bool Finite(double n) { return !double.IsNaN(n) && !double.IsInfinity(n); }
        static void Text(string s) { Need(!string.IsNullOrEmpty(s) && s.Length <= 2048, "Native assembly identity absent/oversize."); foreach(char c in s) Need(!char.IsControl(c), "Native assembly identity contains controls."); }
        static void Floats(float[] a, int n) { Need(a != null && a.Length == n, "Native assembly float shape differs."); foreach(float v in a) Need(Finite(v), "Native assembly float nonfinite."); }
        static void Doubles(double[] a, int n) { Need(a != null && a.Length == n, "Native assembly double shape differs."); foreach(double v in a) Need(Finite(v), "Native assembly double nonfinite."); }
        static void Quaternion(float[] q) { Floats(q, 4); double n=0; foreach(float v in q) n+=(double)v*v; Need(Math.Abs(n-1)<=1e-4, "Native assembly rotation is not normalized."); }
        public static bool SupportedAffine(double[] a)
        {
            if(a == null || a.Length != 16) return false;
            foreach(double v in a) if(!Finite(v)) return false;
            if(a[12]!=0 || a[13]!=0 || a[14]!=0 || a[15]!=1) return false;
            double[] s = new double[3];
            for(int c=0;c<3;c++) { for(int r=0;r<3;r++) s[c]+=a[r*4+c]*a[r*4+c]; if(!Finite(s[c]) || s[c]<=1e-30) return false; }
            for(int c=0;c<3;c++) for(int k=c+1;k<3;k++) { double dot=0; for(int r=0;r<3;r++) dot+=a[r*4+c]*a[r*4+k]; if(!Finite(dot) || Math.Abs(dot)>1e-6*Math.Sqrt(s[c])*Math.Sqrt(s[k])) return false; }
            double det=a[0]*(a[5]*a[10]-a[6]*a[9])-a[1]*(a[4]*a[10]-a[6]*a[8])+a[2]*(a[4]*a[9]-a[5]*a[8]);
            return Finite(det) && det>0;
        }
        public static void Validate(NativeAssemblySceneManifest m, WholeCraftGeometry geometry = null)
        {
            Need(m!=null && m.schema=="continuum-native-assembly-scene/v1", "Native assembly schema differs.");
            Text(m.runId); Text(m.vesselId); Text(m.engine); Text(m.coverageScope);
            Need(m.coordinateFrame=="native-world-axes-anchor-relative" && m.nativePoseSource=="Unity-Rigidbody-float-physics" && m.hierarchyPoseSource=="Unity-float-local-TRS-composed-double", "Native assembly frame provenance differs.");
            Need(m.processId>0 && m.unityFrame>=0 && m.unityFrame==m.unityFrameEnd && m.rootTransformId!=0 && m.rootTransformId==m.rootTransformIdEnd && m.epochUnchanged && m.identityUnchanged, "Native assembly epoch/identity changed.");
            Need(Finite(m.fixedTime) && m.fixedTime==m.fixedTimeEnd && Finite(m.universalTime) && m.universalTime==m.universalTimeEnd && m.startedTicks>=0 && m.copiedTicks>=m.startedTicks && m.clockFrequency>0, "Native assembly epoch/clock invalid.");
            Floats(m.nativeAnchor,3); Doubles(m.hierarchyRootWorld,16); Doubles(m.frameVelocity,3);
            Need(m.hierarchyRootWorld[12]==0 && m.hierarchyRootWorld[13]==0 && m.hierarchyRootWorld[14]==0 && m.hierarchyRootWorld[15]==1, "Native assembly root not affine.");
            Need(m.parts!=null && m.parts.Length>0 && m.parts.Length<=MaxRows && m.bodies!=null && m.bodies.Length>0 && m.bodies.Length<=MaxRows && m.colliders!=null && m.colliders.Length<=MaxRows && m.omissions!=null && m.omissions.Length<=MaxRows, "Native assembly table bound differs.");
            var bodies=new HashSet<int>();
            foreach(var b in m.bodies)
            {
                Need(b!=null && b.bodyId!=0 && b.transformId!=0 && bodies.Add(b.bodyId), "Native assembly body duplicates/absent.");
                Need(Finite(b.mass) && b.mass>0 && Finite(b.drag) && b.drag>=0 && Finite(b.angularDrag) && b.angularDrag>=0 && Finite(b.maxAngularVelocity) && b.maxAngularVelocity>=0 && Finite(b.sleepThreshold) && b.sleepThreshold>=0, "Native assembly body scalar invalid.");
                Doubles(b.position,3); Doubles(b.worldCenterOfMass,3); Quaternion(b.rotation); Quaternion(b.principalRotation);
                Floats(b.localCenterOfMass,3); Floats(b.principalInertia,3); Floats(b.velocity,3); Floats(b.angularVelocity,3);
                foreach(float v in b.principalInertia) Need(v>0, "Native assembly inertia not positive.");
                Need(b.constraints>=0 && (b.constraints & ~126)==0 && b.interpolation>=0 && b.interpolation<=2 && b.collisionDetection>=0 && b.collisionDetection<=3, "Native assembly body enum invalid.");
            }
            var parts=new HashSet<uint>(); var partObjects=new HashSet<int>(); var parents=new Dictionary<uint,uint>();
            foreach(var p in m.parts) { Need(p!=null && p.partId!=0 && parts.Add(p.partId) && p.partInstanceId!=0 && partObjects.Add(p.partInstanceId), "Native assembly part duplicates/absent."); parents.Add(p.partId,p.parentPartId); Text(p.name); foreach(int id in new[]{p.ownBodyId,p.effectiveBodyId,p.servoBodyId}) Need(id==0 || bodies.Contains(id), "Native assembly dangling body alias."); }
            int roots=0; foreach(var p in m.parts) { if(p.parentPartId==0) roots++; else Need(p.parentPartId!=p.partId && parts.Contains(p.parentPartId), "Native assembly dangling parent."); }
            Need(roots==1, "Native assembly root count differs.");
            foreach(var b in m.bodies) Need(parts.Contains(b.ownerPartId) && (b.ownershipSource=="part-ancestor" || b.ownershipSource=="explicit-part-alias"), "Native assembly body ownership missing.");
            var closed=new HashSet<uint>(); foreach(var p in m.parts) { uint parent=p.partId; var path=new HashSet<uint>(); while(parent!=0 && !closed.Contains(parent)) { Need(path.Add(parent), "Native assembly parent cycle."); parent=parents[parent]; } foreach(uint id in path) closed.Add(id); }
            Need(geometry==null || geometry.meshes!=null, "Native assembly geometry table absent.");
            int meshCount=geometry==null?0:geometry.meshes.Length;
            if(meshCount>0) { WholeCraftSceneFormat.ValidateGeometry(geometry); MultiscaleSceneFormat.PortablePath(m.geometry); Need(m.geometrySha256!=null && m.geometrySha256.Length==64, "Native assembly geometry digest invalid."); foreach(char c in m.geometrySha256) Need(c>='0'&&c<='9'||c>='a'&&c<='f', "Native assembly geometry digest encoding invalid."); }
            else Need(string.IsNullOrEmpty(m.geometry) && string.IsNullOrEmpty(m.geometrySha256), "Primitive assembly unexpectedly binds geometry.");
            var colliderIds=new HashSet<int>(); var omitted=new Dictionary<int,string>();
            foreach(var o in m.omissions) { Need(o!=null && o.colliderId!=0 && !omitted.ContainsKey(o.colliderId), "Native assembly omission duplicates."); Text(o.reason); omitted.Add(o.colliderId,o.reason); }
            int unsupported=0;
            foreach(var c in m.colliders)
            {
                Need(c!=null && c.colliderId!=0 && c.transformId!=0 && colliderIds.Add(c.colliderId) && parts.Contains(c.partId), "Native assembly collider identity invalid."); Text(c.name); Text(c.kind); Text(c.poseSource);
                Need(c.layer>=0 && c.layer<=31 && Finite(c.contactOffset) && c.contactOffset>=0 && Finite(c.dynamicFriction) && c.dynamicFriction>=0 && Finite(c.staticFriction) && c.staticFriction>=0 && Finite(c.bounciness) && c.bounciness>=0 && c.bounciness<=1 && c.frictionCombine>=0 && c.frictionCombine<=3 && c.bounceCombine>=0 && c.bounceCombine<=3, "Native assembly collider parameters invalid.");
                Doubles(c.localToBody,16); Floats(c.center,3); Floats(c.size,3); Need(Finite(c.radius)&&Finite(c.height), "Native assembly collider shape nonfinite.");
                if(!c.supported) { unsupported++; Need(omitted.ContainsKey(c.colliderId) && omitted[c.colliderId]==c.omissionReason && c.meshIndex==-1, "Native assembly unsupported coverage differs."); continue; }
                Need(c.bodyId!=0 && bodies.Contains(c.bodyId) && SupportedAffine(c.localToBody) && !omitted.ContainsKey(c.colliderId) && string.IsNullOrEmpty(c.omissionReason), "Native assembly supported owner/transform invalid.");
                if(c.kind=="box") { foreach(float v in c.size) Need(v>0, "Native assembly box size invalid."); Need(c.meshIndex==-1, "Primitive mesh reference invalid."); }
                else if(c.kind=="sphere") Need(c.radius>0 && c.meshIndex==-1, "Native assembly sphere invalid.");
                else if(c.kind=="capsule") Need(c.radius>0 && c.height>0 && c.direction>=0 && c.direction<=2 && c.meshIndex==-1, "Native assembly capsule invalid.");
                else if(c.kind=="convex-mesh") Need(c.convex && c.meshIndex>=0 && c.meshIndex<meshCount, "Native assembly convex mesh invalid.");
                else Need(false, "Native assembly supported kind unknown.");
            }
            Need(unsupported==omitted.Count && m.completeColliderCoverage==(unsupported==0), "Native assembly coverage claim differs.");
        }
    }
}
