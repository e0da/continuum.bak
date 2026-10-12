using System;
using KspContinuum;
static class Program
{
    static void Near(double a,double b) { if(double.IsNaN(a)||double.IsInfinity(a)||Math.Abs(a-b)>1e-9) throw new Exception($"{a} != {b}"); }
    static PersistentTrailOwner Owner(int capacity=100,double life=30) => new(capacity,new double[]{1000000000,0,0},new double[]{1,0,0},life,2,.5,.1,1);
    static void Main()
    {
        MeasuredEmitterChecks.Run();
        var a=Owner(); a.SetWind(2,0,0); a.SetEmission(true); a.Advance(1); a.SetEmission(false); a.Advance(3);
        var p=a.At(0); Near(p.x,1000000005); Near(p.width,Math.Sqrt(6)); Near(p.opacity,Math.Exp(-.2));
        a.SetWind(-1,0,0); Near(a.At(0).x,p.x); a.Advance(4); Near(a.At(0).x,p.x-1);
        var relative=a.RelativeAt(0,new double[]{1000000000,0,0}); Near(relative.x,4);
        var rebased=a.RelativeAt(0,new double[]{999999900,10,0}); Near(rebased.x-relative.x,100); Near(rebased.y-relative.y,-10); Near(a.At(0).x,1000000004);
        var b=Owner(3,2); var c=Owner(3,2); b.SetEmission(true); c.SetEmission(true); b.Advance(5);
        for(int i=1;i<=5;i++) c.Advance(i);
        if(b.Count!=c.Count||b.Expired!=c.Expired||b.CapacityEvictions!=c.CapacityEvictions) throw new Exception("Split-step lifecycle differs");
        for(int i=0;i<b.Count;i++) { Near(b.At(i).x,c.At(i).x); Near(b.At(i).width,c.At(i).width); }
        var decimalWhole=new PersistentTrailOwner(100,new double[3],new double[]{1,0,0}); var decimalSplit=new PersistentTrailOwner(100,new double[3],new double[]{1,0,0});
        decimalWhole.SetEmission(true); decimalSplit.SetEmission(true); decimalWhole.SetWind(2,0,0); decimalSplit.SetWind(2,0,0); decimalWhole.Advance(2);
        for(int i=1;i<=20;i++) decimalSplit.Advance(i*.1);
        if(decimalWhole.Count!=decimalSplit.Count) throw new Exception("Decimal event cadence differs"); for(int i=0;i<decimalWhole.Count;i++) { Near(decimalWhole.At(i).x,decimalSplit.At(i).x); Near(decimalWhole.At(i).age,decimalSplit.At(i).age); }
        var bounded=Owner(2); bounded.SetEmission(true); bounded.Advance(3); if(bounded.CapacityEvictions!=1||bounded.Count!=2) throw new Exception("Capacity contract differs");
        bounded.SetEmission(false); bounded.Advance(4); if(bounded.Count!=2) throw new Exception("Emission-off erased history");
        long generation=bounded.Generation; double time=bounded.Time;
        foreach(double invalid in new[]{double.NaN,double.PositiveInfinity,-1,4000}) { try { bounded.Advance(invalid); throw new Exception("Invalid step accepted"); } catch(ArgumentException) {} }
        try { bounded.SetWind(1,double.NaN,0); throw new Exception("Invalid wind accepted"); } catch(ArgumentException) {}
        if(bounded.Generation!=generation||bounded.Time!=time||bounded.Count!=2) throw new Exception("Rejected request mutated history");
        var work=Owner(1); work.SetEmission(true); long before=work.Generation; try { work.Advance(5); throw new Exception("Oversized work accepted"); } catch(ArgumentException) {} if(work.Generation!=before||work.Count!=0||work.Time!=0) throw new Exception("Work bound mutated history");
        bounded.Advance(34); if(bounded.Count!=0) throw new Exception("Expiry failed");
        Console.WriteLine("Persistent trail analytical and transaction checks passed.");
    }
}
