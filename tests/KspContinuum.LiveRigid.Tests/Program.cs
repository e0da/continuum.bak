using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

static class Program
{
    const string StockSha="8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27",StockMvid="10657063-2fc3-43a7-84fa-d39e75e877bf";
    const string MechSha="6851b17fd2a73cb09e399f55a3322de3eaa8f047136251f52a343fae2f3df929",MechMvid="30fbe3c4-e77e-4abc-895b-a635d5924a08";
    static void Need(bool ok,string why){if(!ok)throw new InvalidDataException("Live rigid receipt: "+why);}
    static double F(JsonElement e,string name){double x=e.GetProperty(name).GetSingle();Need(double.IsFinite(x),"nativefloat "+name);return x;}
    static double D(JsonElement e,string name){double x=e.GetProperty(name).GetDouble();Need(double.IsFinite(x),"double "+name);return x;}
    static int I(JsonElement e,string name)=>e.GetProperty(name).GetInt32();
    static bool B(JsonElement e,string name)=>e.GetProperty(name).GetBoolean();
    static double[] A(JsonElement e,string name,int count=3,bool native=true){var a=e.GetProperty(name).EnumerateArray().Select(x=>native?(double)x.GetSingle():x.GetDouble()).ToArray();Need(a.Length==count&&a.All(double.IsFinite),"array "+name);return a;}
    static JsonElement[] Rows(JsonElement e,string name,int bound){var a=e.GetProperty(name).EnumerateArray().ToArray();Need(a.Length<=bound,"bounded "+name);return a;}
    static double[] Add(double[] a,double[] b)=>a.Zip(b,(x,y)=>x+y).ToArray();
    static double[] Sub(double[] a,double[] b)=>a.Zip(b,(x,y)=>x-y).ToArray();
    static double[] Scale(double[] a,double n)=>a.Select(x=>x*n).ToArray();
    static double Dot(double[] a,double[] b)=>a.Zip(b,(x,y)=>x*y).Sum();
    static double Norm(double[] a)=>Math.Sqrt(Dot(a,a));
    static double[] Cross(double[] a,double[] b)=>new[]{a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]};
    static double[,] R(double[] q){double length=Norm(q);Need(Math.Abs(length*length-1)<=1e-4,"native unit quaternion");double x=q[0]/length,y=q[1]/length,z=q[2]/length,w=q[3]/length;return new[,]{{1-2*(y*y+z*z),2*(x*y-z*w),2*(x*z+y*w)},{2*(x*y+z*w),1-2*(x*x+z*z),2*(y*z-x*w)},{2*(x*z-y*w),2*(y*z+x*w),1-2*(x*x+y*y)}};}
    static double[,] Mul(double[,] a,double[,] b){var r=new double[3,3];for(int i=0;i<3;i++)for(int j=0;j<3;j++)for(int k=0;k<3;k++)r[i,j]+=a[i,k]*b[k,j];return r;}
    static double[] Mul(double[,] a,double[] b)=>Enumerable.Range(0,3).Select(i=>Enumerable.Range(0,3).Sum(j=>a[i,j]*b[j])).ToArray();
    static double[,] Tensor(JsonElement state){var r=Mul(R(A(state,"rotation",4)),R(A(state,"inertiaRotation",4)));var p=A(state,"inertia");Need(p.All(x=>x>0),"positive native principal inertia");var t=new double[3,3];for(int i=0;i<3;i++)for(int j=0;j<3;j++)for(int k=0;k<3;k++)t[i,j]+=r[i,k]*p[k]*r[j,k];return t;}
    static double[,] InverseTensor(JsonElement state){var r=Mul(R(A(state,"rotation",4)),R(A(state,"inertiaRotation",4)));var p=A(state,"inertia");Need(p.All(x=>x>0),"positive native principal inertia");var t=new double[3,3];for(int i=0;i<3;i++)for(int j=0;j<3;j++)for(int k=0;k<3;k++)t[i,j]+=r[i,k]/p[k]*r[j,k];return t;}
    static void State(JsonElement s,int id){Need(I(s,"id")==id&&id!=0&&F(s,"mass")>0,"native body identity/mass");A(s,"position");A(s,"center");A(s,"velocity");A(s,"angularVelocity");Tensor(s);}
    static double Error(double[] actual,double[] expected,double relative,double absolute,string why){double error=Norm(Sub(actual,expected));Need(double.IsFinite(error)&&error<=absolute+relative*Norm(expected),why+" error="+error.ToString("R",System.Globalization.CultureInfo.InvariantCulture));return error;}
    static void Wrench(JsonElement step,out double[] force,out double[] torque,out double[] impulse,out double[] angularImpulse,out int offcenter,HashSet<int> sourceIds)
    {
        force=new double[3];torque=new double[3];impulse=new double[3];angularImpulse=new double[3];offcenter=0;
        var commands=Rows(step,"nonzeroCommands",16384);Need(I(step,"commands")==commands.Length,"raw command cardinality");var center=A(step.GetProperty("before"),"center");
        foreach(var c in commands)
        {
            int id=I(c,"sourceId");Need(id!=0,"raw command source ID");sourceIds.Add(id);var source=c.GetProperty("source");State(source,id);var value=A(c,"value");var point=A(c,"point");Need(value.Any(x=>x!=0),"nonzero tape contains zero command");bool angular=B(c,"torque"),atPoint=B(c,"atPoint");Need(!angular||!atPoint,"torque at-point contradiction");int mode=I(c,"mode");Need(mode==0||mode==1||mode==2||mode==5,"force mode");
            if(mode==2||mode==5)value=angular?Mul(Tensor(source),value):Scale(value,F(source,"mass"));
            var moment=angular?value:Cross(Sub(point,center),value);
            if(!angular&&atPoint&&Norm(moment)>0)offcenter++;
            if(!angular&&!atPoint)Error(point,A(source,"center"),0,0,"centre-applied command point");
            bool instantaneous=mode==1||mode==2;
            if(instantaneous){if(!angular)impulse=Add(impulse,value);angularImpulse=Add(angularImpulse,moment);}
            else{if(!angular)force=Add(force,value);torque=Add(torque,moment);}
        }
    }
    static void Bridge(JsonElement receipt)
    {
        var rows=Rows(receipt,"bridgeSites",8192);Need(rows.Length>0,"source bridge ledger");var identities=new HashSet<string>();int replaced=0;bool integrate=false,stats=false,track=false;
        foreach(var row in rows)
        {
            string assembly=row.GetProperty("assembly").GetString();Need(assembly=="Assembly-CSharp"||assembly=="MechJeb2","bridge assembly scope");Need(row.GetProperty("assemblySha256").GetString()==(assembly=="Assembly-CSharp"?StockSha:MechSha)&&row.GetProperty("assemblyMvid").GetString()==(assembly=="Assembly-CSharp"?StockMvid:MechMvid),"source assembly pin");
            string digest=row.GetProperty("ilSha256").GetString();Need(digest!=null&&digest.Length==64&&digest.All(c=>c>='0'&&c<='9'||c>='a'&&c<='f'),"source IL digest");int token=I(row,"metadataToken");Need(token>0&&I(row,"instructionCount")>0&&identities.Add(assembly+"/"+token),"unique source method");Need(!string.IsNullOrEmpty(row.GetProperty("declaringType").GetString())&&!string.IsNullOrEmpty(row.GetProperty("method").GetString()),"method identity");
            int expected=I(row,"expectedReplacements");Need(expected>=0,"replacement count");var calls=Rows(row,"replacedCalls",50000);Need(calls.Length==expected&&calls.All(c=>!string.IsNullOrEmpty(c.GetString())&&c.GetString().Contains(" -> ")),"replacement call ledger");Rows(row,"unroutedCalls",50000);
            if(expected==0){Need(!B(row,"patched"),"unselected method patched");continue;}
            // Removing this transpiler does not invoke it; cleanup recomposition is optional.
            Need(B(row,"patched")&&B(row,"removed")&&I(row,"observedReplacements")==expected&&I(row,"shapeAdmissions")==1&&I(row,"cleanupShapeChecks")>=0&&I(row,"frameRecompositions")>=0,"exact installed/restored replacement shape");replaced+=expected;
            if(assembly=="Assembly-CSharp"&&token==100677461){Need(row.GetProperty("declaringType").GetString()=="FlightIntegrator"&&row.GetProperty("method").GetString()=="Integrate"&&expected==5&&calls.Count(c=>c.GetString().EndsWith(" -> IntegrationAcceleration",StringComparison.Ordinal))==2,"pinned uniform acceleration seam");integrate=true;}
            if(assembly=="Assembly-CSharp"&&token==100678128)stats=true;
            if(assembly=="Assembly-CSharp"&&token==100670206)track=true;
        }
        Need(replaced>0&&integrate&&stats&&track,"required consumer/force seams reached in bridge admission");
    }
    static void Verify(JsonElement receipt)
    {
        Need(receipt.GetProperty("schema").GetString()=="continuum-live-rigid-owner/v1"&&receipt.GetProperty("status").GetString()=="passed"&&B(receipt,"passed"),"completed live receipt");Need(!string.IsNullOrEmpty(receipt.GetProperty("runId").GetString()),"run identity");
        Need(I(receipt,"sourceParts")==512&&I(receipt,"sourceBodies")>0&&I(receipt,"sourceBodies")<=2048,"frozen real craft domain");int bodies=I(receipt,"sourceBodies"),owner=I(receipt,"aggregateId");Need(owner!=0,"aggregate identity");
        foreach(string flag in new[]{"pulseQueued","originalBodiesRestored","aggregateRetired","bridgeReleased"})Need(B(receipt,flag),"closure "+flag);
        Need(receipt.GetProperty("cleanupStatus").GetString()=="restored-original-ownership","ownership cleanup");foreach(string field in new[]{"velocityReads","angularReads","centerReads","pointReads"})Need(I(receipt,field)>0,"read coverage "+field);
        if(receipt.TryGetProperty("statsReads",out var statsReads))Need(statsReads.GetInt32()>0,"coherent aggregate statistics read coverage");
        Need(I(receipt,"duplicateAccelerationReceivers")==0&&D(receipt,"maxProxyPositionError")>=0&&D(receipt,"maxProxyPositionError")<=1e-4&&D(receipt,"maxProxyRotationError")>=0&&D(receipt,"maxProxyRotationError")<=.002,"proxy/common ownership errors");
        Bridge(receipt);State(receipt.GetProperty("initial"),owner);State(receipt.GetProperty("restoredAggregate"),owner);
        var steps=Rows(receipt,"steps",1024);Need(steps.Length>10&&I(receipt,"physicsSteps")==steps.Length,"meaningful completed native step coverage");var sourceIds=new HashSet<int>();int offcenter=0,commands=0,common=0;bool pulseSeen=false;double priorTime=-1;int priorFrame=-1;double maxLinear=0,maxAngular=0,maxAngularVelocity=0,maxReduction=0;
        for(int index=0;index<steps.Length;index++)
        {
            var step=steps[index];double time=F(step,"fixedTime"),dt=F(step,"dt");Need(I(step,"index")==index&&I(step,"frame")>=priorFrame&&time>priorTime&&dt>0&&dt<=1&&I(step,"dynamicProxies")==0,"matched ordered native steps");priorTime=time;priorFrame=I(step,"frame");
            Need(I(step,"commonReceivers")==bodies,"uniform acceleration reaches distinct source bodies once");common+=I(step,"commonReceivers");var before=step.GetProperty("before");var after=step.GetProperty("after");State(before,owner);State(after,owner);Need(F(before,"mass")==F(after,"mass"),"within-step mass stable");
            Wrench(step,out var force,out var torque,out var impulse,out var angularImpulse,out int reached,sourceIds);offcenter+=reached;commands+=I(step,"commands");
            // The fixed diagnostic queues .25 m/s worth of force perpendicular to its lever.
            double pulseMagnitude=F(before,"mass")*.25/dt;
            foreach(var command in step.GetProperty("nonzeroCommands").EnumerateArray())if(I(command,"mode")==0&&!B(command,"torque")&&B(command,"atPoint"))
            {
                var value=A(command,"value");var lever=Sub(A(command,"point"),A(before,"center"));double length=Norm(value),arm=Norm(lever);
                if(Math.Abs(length-pulseMagnitude)<=1e-4+1e-5*pulseMagnitude&&arm>0&&length>0&&Math.Abs(Dot(lever,value))/(arm*length)<=1e-4&&Norm(Cross(lever,value))>0)pulseSeen=true;
            }
            foreach(var item in new[]{(Key:"force",Value:force),(Key:"torque",Value:torque),(Key:"impulse",Value:impulse),(Key:"angularImpulse",Value:angularImpulse)})maxReduction=Math.Max(maxReduction,Error(A(step,item.Key,3,false),item.Value,1e-10,1e-8,"independent raw reduction "+item.Key));
            double mass=F(before,"mass");var acceleration=A(step,"commonAcceleration");var expectedVelocityDelta=Add(Scale(Add(Scale(force,1/mass),acceleration),dt),Scale(impulse,1/mass));
            maxLinear=Math.Max(maxLinear,Error(Sub(A(after,"velocity"),A(before,"velocity")),expectedVelocityDelta,1e-4,1e-4,"native linear velocity delta"));
            var oldL=Mul(Tensor(before),A(before,"angularVelocity"));var newL=Mul(Tensor(after),A(after,"angularVelocity"));var expectedDelta=Add(Scale(torque,dt),angularImpulse);
            double angularResidual=Norm(Sub(Sub(newL,oldL),expectedDelta));double angularScale=Math.Max(Math.Max(Norm(oldL),Norm(newL)),Norm(expectedDelta));
            Need(double.IsFinite(angularResidual)&&angularResidual<=1e-4+.02*angularScale,"native angular momentum residual error="+angularResidual.ToString("R",System.Globalization.CultureInfo.InvariantCulture));maxAngular=Math.Max(maxAngular,angularResidual);
            var expectedAngularVelocityDelta=Mul(InverseTensor(before),expectedDelta);
            maxAngularVelocity=Math.Max(maxAngularVelocity,Error(Sub(A(after,"angularVelocity"),A(before,"angularVelocity")),expectedAngularVelocityDelta,1e-4,1e-5,"native angular velocity torque response"));
            Need(D(step,"proxyPositionError")>=0&&D(step,"proxyPositionError")<=1e-4&&D(step,"proxyRotationError")>=0&&D(step,"proxyRotationError")<=.002,"per-step proxy pose coverage");
        }
        Need(sourceIds.Count>0&&sourceIds.Count<=bodies&&offcenter>0&&pulseSeen,"actual frozen queued off-center pulse moment");Need(I(receipt,"routedCommands")>=commands&&I(receipt,"accelerationCalls")==common&&I(receipt,"omittedZeroCommands")>=0,"raw/global command coverage");
        Need(I(receipt,"postRestorationNativeSteps")>=2,"original ownership resumes native steps");
        RestoreRows(receipt.GetProperty("restoredRows"),bodies,owner);
        var restoredIds=new HashSet<int>(receipt.GetProperty("restoredRows").EnumerateArray().Select(r=>I(r,"originalId")));Need(sourceIds.IsSubsetOf(restoredIds),"raw commands bound to restored original recipients");
        Console.WriteLine("Independent live rigid receipt passed: "+steps.Length+" native steps, "+bodies+" source bodies, "+commands+" nonzero commands, "+offcenter+" off-center moments. Max raw reduction / linear / angular momentum residual / angular velocity response errors: "+maxReduction.ToString("R")+" / "+maxLinear.ToString("R")+" / "+maxAngular.ToString("R")+" / "+maxAngularVelocity.ToString("R")+". Angular momentum residual uses the maximum before/after/applied momentum scale; torque response uses the angular velocity delta scale. Fixed-topology no-contact vacuum scope; no stock trajectory, exact free-rotation conservation, complete gameplay, contact or FPS qualification.");
    }
    static void RestoreRows(JsonElement element,int count,int aggregate)
    {
        var rows=element.EnumerateArray().ToArray();Need(rows.Length==count&&rows.Select(r=>I(r,"originalId")).All(id=>id!=0&&id!=aggregate)&&rows.Select(r=>I(r,"originalId")).Distinct().Count()==count,"restored constituent identity coverage");
        foreach(var row in rows)
        {
            Need(I(row,"originalId")==I(row,"readbackId")&&!B(row,"originalKinematic")&&B(row,"originalKinematic")==B(row,"readbackKinematic")&&B(row,"originalDetectCollisions")==B(row,"readbackDetectCollisions"),"original constituent flags restored");
            Error(A(row,"readbackVelocity"),A(row,"expectedVelocity"),0,1e-4,"restored native velocity assignment");Error(A(row,"readbackAngularVelocity"),A(row,"expectedAngularVelocity"),0,1e-4,"restored native angular assignment");
        }
    }
    public static int Main(string[] args)
    {
        try{Need(args.Length==1,"usage: receipt.json");byte[] bytes=File.ReadAllBytes(args[0]);using var doc=JsonDocument.Parse(bytes);Verify(doc.RootElement);Need(bytes.SequenceEqual(File.ReadAllBytes(args[0])),"receipt changed during verification");Console.WriteLine("receiptSha256="+Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());return 0;}
        catch(Exception e){Console.Error.WriteLine(e.ToString());return 1;}
    }
}
