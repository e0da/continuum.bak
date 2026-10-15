using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

public static class NativeImportReceipt
{
    static void Need(bool ok,string why) {if(!ok)throw new InvalidDataException("Native import receipt: "+why);}
    static double N(JsonElement e,string key)
    {
        bool native=e.TryGetProperty("bodyId",out _)&&(key=="mass"||key=="radius"||key=="height"||key=="contactOffset");
        double v=native?(double)e.GetProperty(key).GetSingle():e.GetProperty(key).GetDouble();Need(double.IsFinite(v),"nonfinite "+key);return v;
    }
    static double[] A(JsonElement e,string key,int count=3)
    {
        // Decode source binary32 columns before promoting; decimal 4.55 denotes a float, not an exact double.
        bool native=e.TryGetProperty("bodyId",out _)&&key!="position"&&key!="worldCenterOfMass"&&key!="localToBody";
        var a=e.GetProperty(key).EnumerateArray().Select(x=>native?(double)x.GetSingle():x.GetDouble()).ToArray();Need(a.Length==count&&a.All(double.IsFinite),"layout/finite "+key);return a;
    }
    static JsonElement[] Rows(JsonElement e,string key,int max=50000) {var a=e.GetProperty(key).EnumerateArray().ToArray();Need(a.Length<=max,"bounded "+key);return a;}
    static int Id(JsonElement e,string key) {int id=e.GetProperty(key).GetInt32();Need(id!=0,"zero "+key);return id;}
    static double[] Add(double[] a,double[] b)=>a.Zip(b,(x,y)=>x+y).ToArray();
    static double[] Sub(double[] a,double[] b)=>a.Zip(b,(x,y)=>x-y).ToArray();
    static double[] Scale(double[] a,double v)=>a.Select(x=>x*v).ToArray();
    static double Dot(double[] a,double[] b)=>a.Zip(b,(x,y)=>x*y).Sum();
    static double[] Cross(double[] a,double[] b)=>new[]{a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]};
    static void Near(double[] a,double[] b,double budget,string why,bool absolute=false) {Need(a.Length==b.Length&&a.Concat(b).All(double.IsFinite)&&Math.Sqrt(Dot(Sub(a,b),Sub(a,b)))<=budget*(absolute?1:Math.Max(1,Math.Sqrt(Dot(b,b)))),why);}
    static void Scalar(double a,double b,double budget,string why) {Need(double.IsFinite(a)&&double.IsFinite(b)&&Math.Abs(a-b)<=budget*Math.Max(1,Math.Abs(b)),why);}
    static double[,] Rotation(double[] q) {Need(q.Length==4&&Math.Abs(Dot(q,q)-1)<=1e-4,"unit quaternion");double length=Math.Sqrt(Dot(q,q));double x=q[0]/length,y=q[1]/length,z=q[2]/length,w=q[3]/length;return new[,]{{1-2*(y*y+z*z),2*(x*y-z*w),2*(x*z+y*w)},{2*(x*y+z*w),1-2*(x*x+z*z),2*(y*z-x*w)},{2*(x*z-y*w),2*(y*z+x*w),1-2*(x*x+y*y)}};}
    static double[] Mul(double[,] m,double[] v)=>Enumerable.Range(0,3).Select(i=>Enumerable.Range(0,3).Sum(j=>m[i,j]*v[j])).ToArray();
    static double[,] Mul(double[,] a,double[,] b) {var m=new double[3,3];for(int i=0;i<3;i++)for(int j=0;j<3;j++)for(int k=0;k<3;k++)m[i,j]+=a[i,k]*b[k,j];return m;}
    static double[,] Tensor(double[,] r,double[] p) {Need(p.All(x=>x>0),"positive principal inertia");var m=new double[3,3];for(int i=0;i<3;i++)for(int j=0;j<3;j++)for(int k=0;k<3;k++)m[i,j]+=r[i,k]*p[k]*r[j,k];return m;}
    static double[] Flat(double[,] m)=>Enumerable.Range(0,9).Select(i=>m[i/3,i%3]).ToArray();
    static double[] COM(JsonElement m)=>Add(A(m,"position"),Mul(Rotation(A(m,"rotation",4)),A(m,"localCenterOfMass")));
    static double[,] MemberTensor(JsonElement m)=>Tensor(Mul(Rotation(A(m,"rotation",4)),Rotation(A(m,"principalRotation",4))),A(m,"principalInertia"));
    static (double Mass,double[] Center,double[,] Inertia) Properties(JsonElement[] members)
    {
        double mass=members.Sum(m=>N(m,"mass"));Need(mass>0&&double.IsFinite(mass),"positive total mass");var center=new double[3];foreach(var m in members){Need(N(m,"mass")>0,"positive member mass");center=Add(center,Scale(COM(m),N(m,"mass")/mass));}
        var tensor=new double[3,3];foreach(var m in members){var t=MemberTensor(m);var d=Sub(COM(m),center);double d2=Dot(d,d),weight=N(m,"mass");for(int i=0;i<3;i++)for(int j=0;j<3;j++)tensor[i,j]+=t[i,j]+weight*((i==j?d2:0)-d[i]*d[j]);}return(mass,center,tensor);
    }
    static double[] Solve(double[,] a,double[] b)
    {
        var m=new double[3,4];for(int i=0;i<3;i++){for(int j=0;j<3;j++)m[i,j]=a[i,j];m[i,3]=b[i];}
        for(int k=0;k<3;k++){int pivot=k;for(int i=k+1;i<3;i++)if(Math.Abs(m[i,k])>Math.Abs(m[pivot,k]))pivot=i;Need(Math.Abs(m[pivot,k])>0,"invertible tensor");for(int j=k;j<4;j++){double t=m[k,j];m[k,j]=m[pivot,j];m[pivot,j]=t;}double p=m[k,k];for(int j=k;j<4;j++)m[k,j]/=p;for(int i=0;i<3;i++)if(i!=k){double f=m[i,k];for(int j=k;j<4;j++)m[i,j]-=f*m[k,j];}}
        return new[]{m[0,3],m[1,3],m[2,3]};
    }
    static int[] ColliderIds(JsonElement m) {var ids=m.GetProperty("colliders").EnumerateArray().Select(x=>x.GetInt32()).ToArray();Need(ids.All(x=>x!=0)&&ids.Distinct().Count()==ids.Length,"member collider identities");Need(m.GetProperty("collider").GetInt32()==(ids.Length==0?0:ids[0]),"first collider alias");return ids;}
    static double[] Affine(JsonElement shape)
    {
        var r=Rotation(A(shape,"localRotation",4));var p=A(shape,"localPosition");var scale=A(shape,"localScale");Need(scale.All(x=>x>0),"positive shape scale");var matrix=new double[16];
        for(int i=0;i<3;i++){for(int j=0;j<3;j++)matrix[i*4+j]=r[i,j]*scale[j];matrix[i*4+3]=p[i];}matrix[15]=1;return matrix;
    }
    static double[] QuaternionProduct(double[] a,double[] b)
    {
        double an=Math.Sqrt(Dot(a,a)),bn=Math.Sqrt(Dot(b,b));a=Scale(a,1/an);b=Scale(b,1/bn);
        return new[]{a[3]*b[0]+a[0]*b[3]+a[1]*b[2]-a[2]*b[1],a[3]*b[1]+a[1]*b[3]+a[2]*b[0]-a[0]*b[2],a[3]*b[2]+a[2]*b[3]+a[0]*b[1]-a[1]*b[0],a[3]*b[3]-a[0]*b[0]-a[1]*b[1]-a[2]*b[2]};
    }
    static void SameRotation(double[] a,double[] b,string why) {Rotation(a);Rotation(b);Need(1-Math.Abs(Dot(a,b)/Math.Sqrt(Dot(a,a)*Dot(b,b)))<=1e-5,why);}
    static void Shape(JsonElement actual,JsonElement source,JsonElement member)
    {
        Need(actual.GetProperty("kind").GetString()==source.GetProperty("kind").GetString(),"source shape kind");
        foreach(string key in new[]{"enabled","active","trigger"})Need(actual.GetProperty(key).GetBoolean()==source.GetProperty(key).GetBoolean(),"source shape flag "+key);
        Near(A(actual,"center"),A(source,"center"),1e-5,"source shape center",true);Scalar(N(actual,"contactOffset"),N(source,"contactOffset"),1e-5,"source contact offset");
        string kind=source.GetProperty("kind").GetString();
        if(kind=="box")Near(A(actual,"size"),A(source,"size"),1e-5,"source box dimensions",true);
        else if(kind=="sphere")Scalar(N(actual,"radius"),N(source,"radius"),1e-5,"source sphere radius");
        else if(kind=="capsule"){Scalar(N(actual,"radius"),N(source,"radius"),1e-5,"source capsule radius");Scalar(N(actual,"height"),N(source,"height"),1e-5,"source capsule height");Need(actual.GetProperty("direction").GetInt32()==source.GetProperty("direction").GetInt32(),"source capsule direction");}
        else {Need(kind=="convex-mesh"&&source.GetProperty("convex").GetBoolean(),"supported mesh kind");Need(actual.GetProperty("cookingOptions").GetInt32()==source.GetProperty("cookingOptions").GetInt32(),"mesh cooking inputs");}
        var original=A(source,"localToBody",16);Need(original[12]==0&&original[13]==0&&original[14]==0&&original[15]==1,"source collider affine row");
        var recovered=Affine(actual);Near(new[]{recovered[3],recovered[7],recovered[11]},new[]{original[3],original[7],original[11]},1e-4,"source collider local translation",true);
        Near(Enumerable.Range(0,9).Select(i=>recovered[(i/3)*4+i%3]).ToArray(),Enumerable.Range(0,9).Select(i=>original[(i/3)*4+i%3]).ToArray(),1e-4,"source collider rotation/scale matrix");
        Near(A(actual,"position"),Add(A(member,"position"),Mul(Rotation(A(member,"rotation",4)),A(actual,"localPosition"))),1e-4,"native world collider position",true);
        SameRotation(A(actual,"rotation",4),QuaternionProduct(A(member,"rotation",4),A(actual,"localRotation",4)),"native world collider rotation");
    }

    public static void Verify(string manifestPath,string nativeStatusPath)
    {
        byte[] manifestBytes=File.ReadAllBytes(manifestPath),statusBytes=File.ReadAllBytes(nativeStatusPath);
        using var manifestDoc=JsonDocument.Parse(manifestBytes);using var statusDoc=JsonDocument.Parse(statusBytes);
        var manifest=manifestDoc.RootElement;var status=statusDoc.RootElement;var import=status.GetProperty("import");
        Need(manifest.GetProperty("schema").GetString()=="continuum-native-assembly-scene/v1"&&manifest.GetProperty("coordinateFrame").GetString()=="native-world-axes-anchor-relative","source schema/frame");
        Need(manifest.GetProperty("nativePoseSource").GetString()=="Unity-Rigidbody-float-physics"&&manifest.GetProperty("hierarchyPoseSource").GetString()=="Unity-float-local-TRS-composed-double"&&manifest.GetProperty("clockFrequency").GetInt64()>0&&manifest.GetProperty("copiedTicks").GetInt64()>=manifest.GetProperty("startedTicks").GetInt64(),"source provenance/clock");
        Need(manifest.GetProperty("epochUnchanged").GetBoolean()&&manifest.GetProperty("identityUnchanged").GetBoolean()&&manifest.GetProperty("unityFrame").GetInt32()==manifest.GetProperty("unityFrameEnd").GetInt32()&&N(manifest,"fixedTime")==N(manifest,"fixedTimeEnd")&&N(manifest,"universalTime")==N(manifest,"universalTimeEnd"),"source epoch");
        Need(manifest.GetProperty("processId").GetInt32()>0&&!string.IsNullOrEmpty(manifest.GetProperty("runId").GetString())&&!string.IsNullOrEmpty(manifest.GetProperty("vesselId").GetString()),"source identity");
        Need(import.GetProperty("manifestSha256").GetString()==Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(),"actual source hash");
        Need(status.GetProperty("schema").GetString()=="continuum-rigid-owner/v1"&&status.GetProperty("status").GetString()=="passed"&&N(status,"timestep")==.02,"complete native protocol");
        var source=Rows(manifest,"bodies");Need(source.Length>0&&source.Select(b=>Id(b,"bodyId")).Distinct().Count()==source.Length,"distinct source bodies");
        var selected=source.Where(b=>b.GetProperty("active").GetBoolean()&&!b.GetProperty("isKinematic").GetBoolean()).OrderBy(b=>b.GetProperty("bodyId").GetInt32()).ToArray();Need(selected.Length>=2,"nontrivial dynamic import");
        foreach(var b in source){Need(N(b,"mass")>0,"source mass");A(b,"position");A(b,"worldCenterOfMass");Rotation(A(b,"rotation",4));Rotation(A(b,"principalRotation",4));Need(A(b,"principalInertia").All(x=>x>0),"source inertia");A(b,"velocity");A(b,"angularVelocity");A(b,"localCenterOfMass");}
        Need(selected.All(b=>b.GetProperty("constraints").GetInt32()==0),"dynamic constrained import unsupported");
        var bodyIds=new HashSet<int>(source.Select(b=>b.GetProperty("bodyId").GetInt32()));var parts=Rows(manifest,"parts");Need(parts.Length>0&&parts.Select(p=>p.GetProperty("partId").GetUInt32()).All(x=>x!=0)&&parts.Select(p=>p.GetProperty("partId").GetUInt32()).Distinct().Count()==parts.Length,"source part aliases");
        foreach(var p in parts)foreach(string key in new[]{"ownBodyId","effectiveBodyId","servoBodyId"})Need(p.GetProperty(key).GetInt32()==0||bodyIds.Contains(p.GetProperty(key).GetInt32()),"dangling source body alias");
        var sourceIds=selected.Select(b=>b.GetProperty("bodyId").GetInt32()).ToArray();Need(import.GetProperty("sourceBodyIds").EnumerateArray().Select(x=>x.GetInt32()).SequenceEqual(sourceIds),"sorted source-to-dense binding");
        Need(import.GetProperty("sourceBodies").GetInt32()==source.Length&&import.GetProperty("importedBodies").GetInt32()==selected.Length,"body coverage counts");
        var sourceColliders=Rows(manifest,"colliders");Need(sourceColliders.Select(c=>Id(c,"colliderId")).Distinct().Count()==sourceColliders.Length,"distinct source colliders");
        var sourceOmissions=Rows(manifest,"omissions");Need(sourceOmissions.Select(c=>Id(c,"colliderId")).Distinct().Count()==sourceOmissions.Length,"distinct source omissions");
        foreach(var c in sourceColliders)if(c.GetProperty("supported").GetBoolean())Need(bodyIds.Contains(Id(c,"bodyId"))&&!sourceOmissions.Any(o=>o.GetProperty("colliderId").GetInt32()==c.GetProperty("colliderId").GetInt32()),"supported source collider owner");else Need(sourceOmissions.Count(o=>o.GetProperty("colliderId").GetInt32()==c.GetProperty("colliderId").GetInt32()&&o.GetProperty("reason").GetString()==c.GetProperty("omissionReason").GetString())==1&&!string.IsNullOrEmpty(c.GetProperty("omissionReason").GetString()),"unsupported source collider reason");
        Need(sourceOmissions.Length==sourceColliders.Count(c=>!c.GetProperty("supported").GetBoolean())&&manifest.GetProperty("completeColliderCoverage").GetBoolean()==(sourceOmissions.Length==0),"source omission coverage");
        var expectedColliders=sourceColliders.Where(c=>c.GetProperty("supported").GetBoolean()&&sourceIds.Contains(c.GetProperty("bodyId").GetInt32())).ToArray();
        int omitted=sourceColliders.Length-expectedColliders.Length;Need(import.GetProperty("importedColliders").GetInt32()==expectedColliders.Length&&import.GetProperty("omittedColliders").GetInt32()==omitted&&import.GetProperty("completeColliderCoverage").GetBoolean()==(manifest.GetProperty("completeColliderCoverage").GetBoolean()&&omitted==0),"collider coverage scope");
        var colliderBindings=Rows(import,"colliderBindings");Need(colliderBindings.Length==expectedColliders.Length&&colliderBindings.Select(c=>Id(c,"sourceColliderId")).Distinct().Count()==colliderBindings.Length&&colliderBindings.Select(c=>Id(c,"colliderId")).Distinct().Count()==colliderBindings.Length,"source-to-native collider bijection");
        foreach(var binding in colliderBindings){int member=binding.GetProperty("memberId").GetInt32();Need(member>=0&&member<selected.Length&&expectedColliders.Count(c=>c.GetProperty("colliderId").GetInt32()==binding.GetProperty("sourceColliderId").GetInt32()&&c.GetProperty("bodyId").GetInt32()==sourceIds[member])==1,"source collider member binding");}
        var props=Properties(selected);double mass=props.Mass;var pSource=new double[3];var lSource=new double[3];double energy=0;
        foreach(var b in selected){double m=N(b,"mass");var v=A(b,"velocity");var w=A(b,"angularVelocity");var tensor=MemberTensor(b);var linear=Scale(v,m);pSource=Add(pSource,linear);lSource=Add(lSource,Add(Mul(tensor,w),Cross(Sub(COM(b),props.Center),linear)));energy+=.5*m*Dot(v,v)+.5*Dot(w,Mul(tensor,w));}
        var velocity=Scale(pSource,1/mass);var omega=Solve(props.Inertia,lSource);double rigidEnergy=.5*mass*Dot(velocity,velocity)+.5*Dot(omega,Mul(props.Inertia,omega));
        Near(A(import,"sourceLinearMomentum"),pSource,1e-8,"source P");Near(A(import,"sourceAngularMomentumAboutCenter"),lSource,1e-8,"source L");Near(A(import,"sourceVelocity"),velocity,1e-8,"projected source V");Near(A(import,"sourceAngularVelocity"),omega,1e-8,"projected source omega");
        Scalar(N(import,"sourceKineticEnergy"),energy,1e-8,"source kinetic energy");Scalar(N(import,"rigidKineticEnergy"),rigidEnergy,1e-8,"rigid kinetic energy");Scalar(N(import,"removedKineticEnergy"),energy-rigidEnergy,1e-8,"removed internal motion energy");Need(energy-rigidEnergy>=-1e-8*Math.Max(1,energy),"projection increases energy");
        var states=Rows(status,"states",6);Need(states.Length==6,"six boundary states");string[] operations={"imported rigid projection","off-center impulse","closed mass transfer","updated original owner impulse","separated owners","continued native simulation"};int[] steps={0,1,1,2,2,12};
        int owner=Id(states[0].GetProperty("bodies")[0],"id");var colliderLedger=new Dictionary<int,int[]>();var moments=new List<(double[] P,double[] L)>();int splitAt=(selected.Length+1)/2;double moved=.1*N(selected[0],"mass");
        for(int s=0;s<6;s++)
        {
            var state=states[s];Need(state.GetProperty("operation").GetString()==operations[s]&&state.GetProperty("step").GetInt32()==steps[s],"ordered native boundaries");var bodies=Rows(state,"bodies");var members=Rows(state,"members");
            var shapes=Rows(state,"shapes");Need(shapes.Length==expectedColliders.Length&&shapes.Select(c=>Id(c,"id")).Distinct().Count()==shapes.Length,"all imported shapes retained");
            foreach(var shape in shapes)
            {
                var binding=colliderBindings.Single(c=>c.GetProperty("colliderId").GetInt32()==shape.GetProperty("id").GetInt32());int memberId=binding.GetProperty("memberId").GetInt32();var member=members.Single(m=>m.GetProperty("id").GetInt32()==memberId);var sourceShape=expectedColliders.Single(c=>c.GetProperty("colliderId").GetInt32()==binding.GetProperty("sourceColliderId").GetInt32());
                Need(shape.GetProperty("memberId").GetInt32()==memberId&&shape.GetProperty("body").GetInt32()==member.GetProperty("body").GetInt32()&&ColliderIds(member).Contains(shape.GetProperty("id").GetInt32()),"native attached-body shape ownership");Shape(shape,sourceShape,member);
                if(s>0){var original=Rows(states[0],"shapes").Single(c=>c.GetProperty("id").GetInt32()==shape.GetProperty("id").GetInt32());Near(A(shape,"localPosition"),A(original,"localPosition"),1e-4,"retained shape local position",true);Near(A(shape,"localScale"),A(original,"localScale"),1e-5,"retained shape scale");SameRotation(A(shape,"localRotation",4),A(original,"localRotation",4),"retained shape local rotation");}
                if(s==2||s==4){var before=Rows(states[s-1],"shapes").Single(c=>c.GetProperty("id").GetInt32()==shape.GetProperty("id").GetInt32());Near(A(shape,"position"),A(before,"position"),1e-4,"transaction retained shape world position",true);SameRotation(A(shape,"rotation",4),A(before,"rotation",4),"transaction retained shape world rotation");}
            }
            Need(members.Length==selected.Length&&members.Select(m=>m.GetProperty("id").GetInt32()).Order().SequenceEqual(Enumerable.Range(0,selected.Length)),"all dense members retained");Need(bodies.Length==(s<4?1:2)&&state.GetProperty("dynamicBodies").GetInt32()==bodies.Length&&bodies.Select(b=>Id(b,"id")).Distinct().Count()==bodies.Length,"native motion owners");
            Need(s<4?bodies[0].GetProperty("id").GetInt32()==owner:bodies.All(b=>b.GetProperty("id").GetInt32()!=owner),"owner transaction identity");
            if(s==5)Need(bodies.Select(b=>b.GetProperty("id").GetInt32()).Order().SequenceEqual(Rows(states[4],"bodies").Select(b=>b.GetProperty("id").GetInt32()).Order()),"retained successor owners");
            var p=new double[3];var l=new double[3];var allColliderIds=new HashSet<int>();
            foreach(var m in members)
            {
                int id=m.GetProperty("id").GetInt32();var src=selected[id];Rotation(A(m,"localRotation",4));double expectedMass=N(src,"mass")+(s<2?0:id==0?-moved:id==selected.Length-1?moved:0);Scalar(N(m,"mass"),expectedMass,1e-8,"actual nonzero mass transfer");
                Near(A(m,"localPosition"),A(src,"position"),1e-4,"retained source local position",true);SameRotation(A(m,"localRotation",4),A(src,"rotation",4),"retained source rotation");Near(A(m,"localCenterOfMass"),A(src,"localCenterOfMass"),1e-5,"source COM offset");Near(A(m,"principalInertia"),Scale(A(src,"principalInertia"),expectedMass/N(src,"mass")),1e-5,"specific member inertia");SameRotation(A(m,"principalRotation",4),A(src,"principalRotation",4),"principal frame");
                var colliders=ColliderIds(m);Need(colliders.Length==expectedColliders.Count(c=>c.GetProperty("bodyId").GetInt32()==sourceIds[id]),"per-source collider coverage");foreach(int c in colliders)Need(allColliderIds.Add(c),"collider shared by members");if(s==0)colliderLedger.Add(id,colliders);else Need(colliders.SequenceEqual(colliderLedger[id]),"retained collider identities");
                if(s==0){Near(A(m,"position"),A(src,"position"),1e-4,"initial source position",true);SameRotation(A(m,"rotation",4),A(src,"rotation",4),"initial source rotation");}
            }
            foreach(var b in bodies)
            {
                int id=Id(b,"id");Need(b.GetProperty("generation").GetInt32()==(s<4?0:1),"body generation");var group=members.Where(m=>m.GetProperty("body").GetInt32()==id).ToArray();Need(group.Length>0,"nonempty owner");Need(b.GetProperty("members").EnumerateArray().Select(x=>x.GetInt32()).Order().SequenceEqual(group.Select(m=>m.GetProperty("id").GetInt32()).Order()),"owner member ledger");Need(b.GetProperty("colliders").EnumerateArray().Select(x=>x.GetInt32()).Order().SequenceEqual(group.SelectMany(ColliderIds).Order()),"owner collider ledger");
                var properties=Properties(group);Scalar(N(b,"mass"),properties.Mass,1e-5,"independent mass");Near(A(b,"com"),properties.Center,1e-4,"independent COM",true);var tensor=Tensor(Mul(Rotation(A(b,"rotation",4)),Rotation(A(b,"inertiaRotation",4))),A(b,"inertia"));Near(Flat(tensor),Flat(properties.Inertia),1e-4,"independent full inertia");var linear=Scale(A(b,"velocity"),properties.Mass);p=Add(p,linear);l=Add(l,Add(Mul(tensor,A(b,"angularVelocity")),Cross(properties.Center,linear)));
            }
            Need(members.All(m=>bodies.Any(b=>b.GetProperty("id").GetInt32()==m.GetProperty("body").GetInt32())),"dangling member owner");Near(A(state,"momentum"),p,1e-4,"independent state P");Near(A(state,"angularMomentum"),l,1e-4,"independent state L");moments.Add((p,l));
        }
        var initial=states[0].GetProperty("bodies")[0];Near(A(initial,"position"),new double[3],1e-4,"lab owner origin",true);Near(A(initial,"rotation",4),new[]{0.0,0.0,0.0,1.0},1e-5,"identity lab owner");Near(A(initial,"velocity"),new double[3],1e-4,"Galilean rest frame",true);Near(A(initial,"angularVelocity"),omega,1e-4,"retained source angular momentum");
        Near(A(status,"impulse"),new[]{0.0,mass*.03,0.0},1e-6,"frozen first impulse");Near(A(status,"updatedImpulse"),new[]{mass*.02,0.0,mass*.01},1e-6,"frozen second impulse");
        Near(A(status,"impulsePoint"),Add(A(initial,"position"),Add(A(selected[selected.Length-1],"position"),new[]{.3,.4,.2})),1e-5,"first application point",true);
        var updated=states[2].GetProperty("bodies")[0];Near(A(status,"updatedImpulsePoint"),Add(A(updated,"position"),Mul(Rotation(A(updated,"rotation",4)),Add(A(selected[0],"position"),new[]{-.2,.3,.1}))),1e-5,"updated application point",true);
        foreach(var pair in new[]{(Before:initial,After:states[1].GetProperty("bodies")[0],Impulse:A(status,"impulse"),Point:A(status,"impulsePoint")),(Before:updated,After:states[3].GetProperty("bodies")[0],Impulse:A(status,"updatedImpulse"),Point:A(status,"updatedImpulsePoint"))})
        {
            var tensor=Tensor(Mul(Rotation(A(pair.Before,"rotation",4)),Rotation(A(pair.Before,"inertiaRotation",4))),A(pair.Before,"inertia"));
            Near(Scale(Sub(A(pair.After,"velocity"),A(pair.Before,"velocity")),N(pair.Before,"mass")),pair.Impulse,1e-4,"direct linear impulse response");
            Near(Mul(tensor,Sub(A(pair.After,"angularVelocity"),A(pair.Before,"angularVelocity"))),Cross(Sub(pair.Point,A(pair.Before,"com")),pair.Impulse),.005,"direct angular impulse response");
        }
        Near(moments[1].P,Add(moments[0].P,A(status,"impulse")),1e-4,"first impulse P");Near(moments[1].L,Add(moments[0].L,Cross(A(status,"impulsePoint"),A(status,"impulse"))),.005,"first impulse L");Near(moments[2].P,moments[1].P,1e-4,"transfer P");Near(moments[2].L,moments[1].L,1e-4,"transfer L");Near(moments[3].P,Add(moments[2].P,A(status,"updatedImpulse")),1e-4,"updated impulse P");Near(moments[3].L,Add(moments[2].L,Cross(A(status,"updatedImpulsePoint"),A(status,"updatedImpulse"))),.005,"updated impulse L");Near(moments[4].P,moments[3].P,1e-4,"split P");Near(moments[4].L,moments[3].L,1e-4,"split L");Near(moments[5].P,moments[4].P,1e-4,"continued P");Near(moments[5].L,moments[4].L,.02,"continued L");
        foreach(int s in new[]{2,4})foreach(var m in Rows(states[s],"members")){var before=Rows(states[s-1],"members").Single(x=>x.GetProperty("id").GetInt32()==m.GetProperty("id").GetInt32());Near(A(m,"position"),A(before,"position"),1e-4,"transaction world pose",true);SameRotation(A(m,"rotation",4),A(before,"rotation",4),"transaction world rotation");}
        var split=Rows(states[4],"members");int firstOwner=split.Single(m=>m.GetProperty("id").GetInt32()==0).GetProperty("body").GetInt32();Need(split.All(m=>(m.GetProperty("id").GetInt32()<splitAt)==(m.GetProperty("body").GetInt32()==firstOwner)),"intended split partition");
        foreach(var b in Rows(states[5],"bodies")){var before=Rows(states[4],"bodies").Single(x=>x.GetProperty("id").GetInt32()==b.GetProperty("id").GetInt32());Need(Math.Sqrt(Dot(Sub(A(b,"position"),A(before,"position")),Sub(A(b,"position"),A(before,"position"))))>1e-5,"successor native motion advances");}
        Need(File.ReadAllBytes(manifestPath).SequenceEqual(manifestBytes)&&File.ReadAllBytes(nativeStatusPath).SequenceEqual(statusBytes),"immutable input bytes changed");
        double nativeComResidual=selected.Max(b=>Math.Sqrt(Dot(Sub(COM(b),A(b,"worldCenterOfMass")),Sub(COM(b),A(b,"worldCenterOfMass")))));
        Scalar(N(import,"maxReconstructedNativeComErrorMeters"),nativeComResidual,1e-8,"reported raw-native COM residual");
        Console.WriteLine("Independent actual import: "+selected.Length+" distinct dynamic bodies, "+expectedColliders.Length+" source-bound retained colliders, six states; source projection, mass/COM/full inertia, shape flags/transforms/dimensions, transfer/split and native momentum passed. Max raw-native versus reconstructed COM residual="+nativeComResidual.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+" m. No contact/cooked-hull/gameplay equivalence claim.");
    }
}
