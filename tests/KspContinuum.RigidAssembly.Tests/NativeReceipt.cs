using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

static class NativeReceipt
{
    static double[] A(JsonElement e,string field)
    {var values=e.GetProperty(field).EnumerateArray().Select(x=>x.GetDouble()).ToArray();Check(values.Length==(field.EndsWith("rotation",StringComparison.OrdinalIgnoreCase)?4:3),"frozen vector layout "+field);return values;}
    static double[] Add(double[] a,double[] b)=>a.Zip(b,(x,y)=>x+y).ToArray();
    static double[] Sub(double[] a,double[] b)=>a.Zip(b,(x,y)=>x-y).ToArray();
    static double[] Scale(double[] a,double b)=>a.Select(x=>x*b).ToArray();
    static double Dot(double[] a,double[] b)=>a.Zip(b,(x,y)=>x*y).Sum();
    static double[] Cross(double[] a,double[] b)=>new[] {a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]};
    static void Check(bool ok,string why){if(!ok)throw new Exception("Native receipt: "+why);}
    static void Near(double[] actual,double[] expected,double budget,string why)
    {Check(actual.Length==expected.Length&&actual.Concat(expected).All(double.IsFinite)&&Math.Sqrt(Dot(Sub(actual,expected),Sub(actual,expected)))<=budget*Math.Max(1,Math.Sqrt(Dot(expected,expected))),why);}
    static void Absolute(double[] actual,double[] expected,double budget,string why)
    {Check(actual.Length==expected.Length&&actual.Concat(expected).All(double.IsFinite)&&Math.Sqrt(Dot(Sub(actual,expected),Sub(actual,expected)))<=budget,why);}
    static double[,] Matrix(double[] q)
    {
        double x=q[0],y=q[1],z=q[2],w=q[3];Check(Math.Abs(Dot(q,q)-1)<1e-5,"unit quaternion");
        return new[,] {{1-2*(y*y+z*z),2*(x*y-z*w),2*(x*z+y*w)},{2*(x*y+z*w),1-2*(x*x+z*z),2*(y*z-x*w)},{2*(x*z-y*w),2*(y*z+x*w),1-2*(x*x+y*y)}};
    }
    static double[] Multiply(double[,] m,double[] v)=>Enumerable.Range(0,3).Select(i=>Enumerable.Range(0,3).Sum(j=>m[i,j]*v[j])).ToArray();
    static double[,] Multiply(double[,] a,double[,] b)
    {var m=new double[3,3];for(int i=0;i<3;i++)for(int j=0;j<3;j++)for(int k=0;k<3;k++)m[i,j]+=a[i,k]*b[k,j];return m;}
    static double[,] Tensor(double[,] r,double[] principal)
    {var m=new double[3,3];for(int i=0;i<3;i++)for(int j=0;j<3;j++)for(int k=0;k<3;k++)m[i,j]+=r[i,k]*principal[k]*r[j,k];return m;}
    static double[] Flatten(double[,] m)=>Enumerable.Range(0,9).Select(i=>m[i/3,i%3]).ToArray();
    static double[] WorldCOM(JsonElement member)=>Add(A(member,"position"),Multiply(Matrix(A(member,"rotation")),A(member,"localCenterOfMass")));

    public static void Verify(string path)
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(path));var root=doc.RootElement;
        Check(root.GetProperty("schema").GetString()=="continuum-rigid-owner/v1"&&root.GetProperty("status").GetString()=="passed","complete native scenario");
        Check(root.GetProperty("timestep").GetDouble()==.02,"frozen timestep");
        var states=root.GetProperty("states").EnumerateArray().ToArray();Check(states.Length==6,"six distinct boundary states");
        string[] operations={"initial","off-center impulse","closed mass transfer","updated original owner impulse","separated owners","continued native simulation"};
        int[] steps={0,1,1,2,2,12};var moments=new List<(double[] P,double[] L)>();
        var identities=new Dictionary<int,int>();
        int originalBody=states[0].GetProperty("bodies")[0].GetProperty("id").GetInt32();
        for(int s=0;s<states.Length;s++)
        {
            var state=states[s];Check(state.GetProperty("operation").GetString()==operations[s]&&state.GetProperty("step").GetInt32()==steps[s],"scenario sequence/time");
            var bodies=state.GetProperty("bodies").EnumerateArray().ToArray();var members=state.GetProperty("members").EnumerateArray().ToArray();
            int expectedCount=s<4?1:2;Check(bodies.Length==expectedCount&&state.GetProperty("dynamicBodies").GetInt32()==expectedCount&&members.Length==3,"native body/member cardinality");
            if(s<4)Check(bodies[0].GetProperty("id").GetInt32()==originalBody,"same original motion owner through mass-update dynamics");
            else Check(bodies.Select(b=>b.GetProperty("id").GetInt32()).Distinct().Count()==2&&bodies.All(b=>b.GetProperty("id").GetInt32()!=originalBody),"two distinct successor owners");
            if(s==5)Check(bodies.Select(b=>b.GetProperty("id").GetInt32()).Order().SequenceEqual(states[4].GetProperty("bodies").EnumerateArray().Select(b=>b.GetProperty("id").GetInt32()).Order()),"continued successor identity");
            Check(members.Select(m=>m.GetProperty("id").GetInt32()).Order().SequenceEqual(new[] {0,1,2}),"retained member identity");
            double[] p=new double[3],l=new double[3];
            foreach(var b in bodies)
            {
                int id=b.GetProperty("id").GetInt32();Check(b.GetProperty("generation").GetInt32()==(s<4?0:1),"ownership generation");
                var group=members.Where(m=>m.GetProperty("body").GetInt32()==id).ToArray();Check(group.Length>0,"nonempty compound");
                Check(b.GetProperty("members").EnumerateArray().Select(x=>x.GetInt32()).Order().SequenceEqual(group.Select(m=>m.GetProperty("id").GetInt32()).Order()),"native ownership ledger");
                Check(b.GetProperty("colliders").EnumerateArray().Select(x=>x.GetInt32()).Order().SequenceEqual(group.Select(m=>m.GetProperty("collider").GetInt32()).Order()),"native collider ledger");
                double mass=group.Sum(m=>m.GetProperty("mass").GetDouble());double[] com=new double[3];
                foreach(var member in group)com=Add(com,Scale(WorldCOM(member),member.GetProperty("mass").GetDouble()/mass));
                Check(Math.Abs(mass-b.GetProperty("mass").GetDouble())<1e-5,"aggregate mass");Absolute(A(b,"com"),com,1e-4,"independent world COM");
                var expectedTensor=new double[3,3];
                foreach(var member in group)
                {
                    var r=Multiply(Matrix(A(member,"rotation")),Matrix(A(member,"principalRotation")));var tensor=Tensor(r,A(member,"principalInertia"));
                    var d=Sub(WorldCOM(member),com);double m=member.GetProperty("mass").GetDouble(),d2=Dot(d,d);
                    for(int i=0;i<3;i++)for(int j=0;j<3;j++)expectedTensor[i,j]+=tensor[i,j]+m*((i==j?d2:0)-d[i]*d[j]);
                }
                var nativeTensor=Tensor(Multiply(Matrix(A(b,"rotation")),Matrix(A(b,"inertiaRotation"))),A(b,"inertia"));
                // Norm over all nine entries; this oracle never calls the production aggregate calculator.
                var e=Flatten(expectedTensor);var a=Flatten(nativeTensor);Check(Math.Sqrt(Dot(Sub(a,e),Sub(a,e)))<=1e-4*Math.Max(1,Math.Sqrt(Dot(e,e))),"independent full inertia tensor");
                var linear=Scale(A(b,"velocity"),mass);p=Add(p,linear);l=Add(l,Add(Multiply(nativeTensor,A(b,"angularVelocity")),Cross(com,linear)));
            }
            Near(A(state,"momentum"),p,1e-4,"independent P readback");Near(A(state,"angularMomentum"),l,1e-4,"independent L readback");moments.Add((p,l));
            foreach(var m in members)
            {
                int id=m.GetProperty("id").GetInt32(),collider=m.GetProperty("collider").GetInt32();
                double mass=m.GetProperty("mass").GetDouble(),expectedMass=(s<2?new[] {4.0,7.0,3.0}:new[] {3.25,7.0,3.75})[id];
                Check(Math.Abs(mass-expectedMass)<1e-8,"actual frozen member mass transfer");
                if(s==0)identities.Add(id,collider);else Check(identities[id]==collider,"collider identity across all transitions");
                var original=states[0].GetProperty("members").EnumerateArray().Single(x=>x.GetProperty("id").GetInt32()==id);
                Absolute(A(m,"localPosition"),A(original,"localPosition"),1e-4,"retained rigid local pose");
                Check(1-Math.Abs(Dot(A(m,"localRotation"),A(original,"localRotation")))<=1e-5,"retained rigid local rotation");
                Near(A(m,"principalInertia"),Scale(A(original,"principalInertia"),mass/original.GetProperty("mass").GetDouble()),1e-5,"specific member inertia after transfer");
                Near(A(m,"localCenterOfMass"),A(original,"localCenterOfMass"),1e-5,"retained member COM offset");
                Check(1-Math.Abs(Dot(A(m,"principalRotation"),A(original,"principalRotation")))<=1e-5,"retained member principal frame");
            }
        }
        Absolute(A(root,"impulse"),new[] {.5,1.2,-.3},1e-6,"frozen first impulse");Absolute(A(root,"updatedImpulse"),new[] {-.4,.3,.2},1e-6,"frozen updated-owner impulse");
        var originalState=states[0].GetProperty("bodies")[0];var updatedState=states[2].GetProperty("bodies")[0];
        Absolute(A(root,"impulsePoint"),Add(A(originalState,"position"),Multiply(Matrix(A(originalState,"rotation")),new[] {-2.3,.4,.1})),1e-5,"frozen original-owner application point");
        Absolute(A(root,"updatedImpulsePoint"),Add(A(updatedState,"position"),Multiply(Matrix(A(updatedState,"rotation")),new[] {2.4,-.2,.3})),1e-5,"frozen updated-owner application point");
        var updatedTensor=Tensor(Multiply(Matrix(A(updatedState,"rotation")),Matrix(A(updatedState,"inertiaRotation"))),A(updatedState,"inertia"));
        var omegaDelta=Sub(A(states[3].GetProperty("bodies")[0],"angularVelocity"),A(updatedState,"angularVelocity"));
        Near(Multiply(updatedTensor,omegaDelta),Cross(Sub(A(root,"updatedImpulsePoint"),A(updatedState,"com")),A(root,"updatedImpulse")),.005,"independent updated-owner angular impulse response");
        Near(moments[1].P,Add(moments[0].P,A(root,"impulse")),.0001,"first impulse P");Near(moments[1].L,Add(moments[0].L,Cross(A(root,"impulsePoint"),A(root,"impulse"))),.005,"first impulse L");
        Near(moments[2].P,moments[1].P,.0001,"transfer P");Near(moments[2].L,moments[1].L,.0001,"transfer L");
        Near(moments[3].P,Add(moments[2].P,A(root,"updatedImpulse")),.0001,"updated-body impulse P");Near(moments[3].L,Add(moments[2].L,Cross(A(root,"updatedImpulsePoint"),A(root,"updatedImpulse"))),.005,"updated-body impulse L");
        Near(moments[4].P,moments[3].P,.0001,"split P");Near(moments[4].L,moments[3].L,.0001,"split L");Near(moments[5].P,moments[4].P,.0001,"continued P");Near(moments[5].L,moments[4].L,.02,"continued L");
        foreach(int index in new[] {2,4})foreach(var after in states[index].GetProperty("members").EnumerateArray())
        {
            var before=states[index-1].GetProperty("members").EnumerateArray().Single(m=>m.GetProperty("id").GetInt32()==after.GetProperty("id").GetInt32());
            Absolute(A(after,"position"),A(before,"position"),1e-4,"transaction retained world pose");Check(1-Math.Abs(Dot(A(after,"rotation"),A(before,"rotation")))<=1e-5,"transaction retained world rotation");
        }
        var split=states[4].GetProperty("members").EnumerateArray().OrderBy(m=>m.GetProperty("id").GetInt32()).ToArray();
        foreach(var b in states[5].GetProperty("bodies").EnumerateArray())
        {
            var prior=states[4].GetProperty("bodies").EnumerateArray().Single(x=>x.GetProperty("id").GetInt32()==b.GetProperty("id").GetInt32());
            Check(Math.Sqrt(Dot(Sub(A(b,"position"),A(prior,"position")),Sub(A(b,"position"),A(prior,"position"))))>1e-5,"native successor actually advances");
        }
        Check(split[0].GetProperty("body").GetInt32()==split[1].GetProperty("body").GetInt32()&&split[1].GetProperty("body").GetInt32()!=split[2].GetProperty("body").GetInt32(),"two intended split regions");
        Console.WriteLine("Independent native receipt: six states, body ownership/collider identity, full mass tensors, impulse/transfer/split momentum, continued motion and retained rigid geometry passed.");
    }
}
