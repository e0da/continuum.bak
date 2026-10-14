using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using KspContinuum;

static class Program
{
    static void Need(bool condition,string message){if(!condition)throw new Exception(message);}
    static void Reject(Action action,string message)
    {try{action();}catch(ArgumentException){return;}catch(InvalidOperationException){return;}catch(BadImageFormatException){return;}throw new Exception(message);}
    static string Hash(byte[] bytes){using(var sha=SHA256.Create())return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();}
    static int Main()
    {
        Reject(()=>OwnedRuntimeScheduleImages.Load(File.ReadAllBytes(Assembly.GetExecutingAssembly().Location)),"Unknown preloaded identity was admitted.");
        string folder=Path.Combine(AppContext.BaseDirectory,"images");
        byte[] original=File.ReadAllBytes(Path.Combine(folder,"Continuum.ScheduleImageFixture.dll"));
        byte[] caller=(byte[])original.Clone();var first=OwnedRuntimeScheduleImages.Load(caller);
        Need(first.Sha256==Hash(original)&&first.AssemblyIdentity==first.Assembly.GetName().FullName,"Image provenance differs.");
        caller[caller.Length-1]^=1;
        var again=OwnedRuntimeScheduleImages.Load(original);
        Need(Object.ReferenceEquals(first,again),"Same exact image did not retain its admitted assembly.");
        Activator.CreateInstance(first.Assembly.GetType("ScheduleImageFixture.First",true));
        Activator.CreateInstance(again.Assembly.GetType("ScheduleImageFixture.Second",true));
        var witness=first.Assembly.GetType("ScheduleImageFixture.Witness",true).GetField("Constructors");
        Need((int)witness.GetValue(null)==2,"Same image alternate type selection failed.");
        var changed=new byte[original.Length+1];Array.Copy(original,changed,original.Length);changed[changed.Length-1]=123;
        Reject(()=>OwnedRuntimeScheduleImages.Load(changed),"Changed bytes under known identity were admitted.");
        Need((int)witness.GetValue(null)==2,"Rejected image ran a constructor.");
        var fresh=OwnedRuntimeScheduleImages.Load(File.ReadAllBytes(Path.Combine(folder,"Continuum.ScheduleImageFresh.dll")));
        Need(fresh.AssemblyIdentity!=first.AssemblyIdentity&&!Object.ReferenceEquals(fresh.Assembly,first.Assembly),"Fresh generation identity did not load separately.");
        Reject(()=>OwnedRuntimeScheduleImages.Load(new byte[]{1,2,3}),"Malformed metadata accepted.");
        Reject(()=>OwnedRuntimeScheduleImages.Load(new byte[16*1024*1024+1]),"Oversized image accepted.");
        Need(Object.ReferenceEquals(first,OwnedRuntimeScheduleImages.Load(original)),"Rejection damaged prior admission.");
        Console.WriteLine("Schedule images: preloaded rejection, exact retention/alternate types, changed-byte rejection, fresh identity, malformed/bounds pass.");return 0;
    }
}
