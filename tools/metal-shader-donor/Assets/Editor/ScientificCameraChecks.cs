using System;
using System.IO;
using KspContinuum;
using UnityEngine;

public static class ScientificCameraChecks
{
    static void Need(bool value,string message) {if(!value)throw new InvalidOperationException(message);}
    public static void Run()
    {
        var camera=new ScientificCameraControls();
        var origin=new ScientificVector3(10,20,30);var com=new ScientificVector3(15,23,35);
        camera.SetAnchors(new[] {
            new ScientificCameraControls.Anchor("vessel-origin","origin","Fixture origin",origin,Quaternion.identity),
            new ScientificCameraControls.Anchor("center-of-mass","com","Fixture CoM",com,Quaternion.identity,false)
        },"origin");
        camera.Handle(new[] {"camera","focus","center-of-mass"});
        camera.Handle(new[] {"camera","snap","+y"});
        Need(camera.Focus.position.x==com.x&&camera.GridReference.position.x==origin.x,"Focus and grid parent were coupled.");
        Need(Vector3.Distance(camera.Rotation*Vector3.forward,Vector3.down)<1e-5f,"Top-down snap did not use independent reference axes.");
        Need(Vector3.Distance(camera.Rotation*Vector3.up,Vector3.forward)<1e-5f,"Top-down snap lost its defined up axis.");
        Quaternion before=camera.Rotation;
        camera.Handle(new[] {"camera","drag","0","0","90"});
        Need(Vector3.Distance(before*Vector3.forward,camera.Rotation*Vector3.forward)<1e-5f,"Roll changed viewing direction.");
        Need(Math.Abs(Vector3.Dot(before*Vector3.up,camera.Rotation*Vector3.up))<1e-5f,"Roll was discarded.");
        camera.Handle(new[] {"camera","drag","0","0","-90"});
        Need(Math.Abs(Quaternion.Dot(before,camera.Rotation))>1-1e-6f,"Inverse roll did not restore orientation.");
        Quaternion rejected;
        Need(!ScientificCameraControls.TryBasis(Vector3.left,Vector3.up,Vector3.forward,out rejected),"Reflected reference basis was admitted.");
        Need(!ScientificCameraControls.TryBasis(Vector3.right,new Vector3(.2f,1,0),Vector3.forward,out rejected),"Sheared reference basis was admitted.");
        bool missing=false;try {camera.Handle(new[] {"camera","focus","root-part"});}catch(InvalidDataException){missing=true;}
        Need(missing&&camera.Focus.id=="com"&&Math.Abs(Quaternion.Dot(before,camera.Rotation))>1-1e-6f,"Unavailable focus changed state.");
        bool removed=false;try {camera.ValidateReplacement(new[] {new ScientificCameraControls.Anchor("vessel-origin","origin","Fixture origin",origin,Quaternion.identity)});}catch(InvalidDataException){removed=true;}
        Need(removed&&camera.Focus.id=="com","Reload removed an active anchor silently.");
        Debug.Log("Scientific camera checks passed: independent CoM focus/grid axes, pole snap, reversible roll, reflected/sheared rejection, missing selection and reload preservation.");
    }
}
