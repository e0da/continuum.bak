using System;
namespace KspContinuum
{
    public enum ScientificProjectionKind { Perspective, Orthographic }
    public struct ScientificVector3
    {
        public readonly double x,y,z;
        public ScientificVector3(double x,double y,double z) { this.x=x;this.y=y;this.z=z; }
        public static double Dot(ScientificVector3 a,ScientificVector3 b) {return a.x*b.x+a.y*b.y+a.z*b.z;}
        public static ScientificVector3 Subtract(ScientificVector3 a,ScientificVector3 b) {return new ScientificVector3(a.x-b.x,a.y-b.y,a.z-b.z);}
        public static double Distance(ScientificVector3 a,ScientificVector3 b) {var d=Subtract(a,b);return Math.Sqrt(Dot(d,d));}
    }
    public sealed class ScientificView
    {
        public readonly ScientificVector3 origin,right,up,forward;
        public readonly int width,height;
        public readonly ScientificProjectionKind projection;
        public readonly double fovDegrees,verticalSpanMeters,near,far;
        readonly double focal;
        public ScientificView(ScientificVector3 origin,ScientificVector3 right,ScientificVector3 up,ScientificVector3 forward,int width,int height,ScientificProjectionKind projection,double fovDegrees,double verticalSpanMeters,double near,double far)
        {
            CheckVector(origin);CheckAxes(right,up);CheckAxes(right,forward);CheckAxes(up,forward);
            var cross=new ScientificVector3(right.y*up.z-right.z*up.y,right.z*up.x-right.x*up.z,right.x*up.y-right.y*up.x);
            Need(ScientificVector3.Dot(cross,forward)>1-1e-6,"Right/up/forward must be right handed.");
            Need(width>=1&&height>=1&&width<=16384&&height<=16384&&(long)width*height<=268435456,"Viewport bound.");
            Need(Finite(near)&&Finite(far)&&near>=1e-12&&far>near&&far<=1e15,"Ordered eye-depth clip interval 1e-12..1e15 metres required.");
            Need(Finite(fovDegrees)&&Finite(verticalSpanMeters),"Projection scalars must be finite.");
            Need(projection==ScientificProjectionKind.Perspective||projection==ScientificProjectionKind.Orthographic,"Unknown projection.");
            Need(projection==ScientificProjectionKind.Perspective?fovDegrees>0&&fovDegrees<180:verticalSpanMeters>=1e-9&&verticalSpanMeters<=1e15,"Projection scale invalid.");
            this.origin=origin;this.right=right;this.up=up;this.forward=forward;this.width=width;this.height=height;this.projection=projection;this.fovDegrees=fovDegrees;this.verticalSpanMeters=verticalSpanMeters;this.near=near;this.far=far;
            focal=projection==ScientificProjectionKind.Perspective?height/(2*Math.Tan(fovDegrees*Math.PI/360)):height/verticalSpanMeters;
            Need(Finite(focal)&&focal>0,"Unrepresentable pixel scale.");
        }
        internal static bool Finite(double value) {return !double.IsNaN(value)&&!double.IsInfinity(value);}
        internal static void Need(bool condition,string message) {if(!condition)throw new ArgumentException(message);}
        internal static void CheckVector(ScientificVector3 v) {Need(Finite(v.x)&&Finite(v.y)&&Finite(v.z)&&Math.Abs(v.x)<=1e15&&Math.Abs(v.y)<=1e15&&Math.Abs(v.z)<=1e15,"Finite bounded world vector required.");}
        internal static void CheckAxes(ScientificVector3 a,ScientificVector3 b)
        {CheckVector(a);CheckVector(b);Need(Math.Abs(ScientificVector3.Dot(a,a)-1)<=1e-6&&Math.Abs(ScientificVector3.Dot(b,b)-1)<=1e-6&&Math.Abs(ScientificVector3.Dot(a,b))<=1e-6,"Orthonormal axes required.");}
        public bool ProjectUnclipped(ScientificVector3 world,out double pixelX,out double pixelY,out double eyeDepth)
        {
            pixelX=pixelY=eyeDepth=0;
            if(!Finite(world.x)||!Finite(world.y)||!Finite(world.z)||Math.Abs(world.x)>1e15||Math.Abs(world.y)>1e15||Math.Abs(world.z)>1e15)return false;
            var relative=ScientificVector3.Subtract(world,origin);eyeDepth=ScientificVector3.Dot(relative,forward);
            if(!Finite(eyeDepth)||eyeDepth<=0)return false;
            double scale=projection==ScientificProjectionKind.Perspective?focal/eyeDepth:focal;
            pixelX=width*.5+ScientificVector3.Dot(relative,right)*scale;pixelY=height*.5-ScientificVector3.Dot(relative,up)*scale;
            return Finite(pixelX)&&Finite(pixelY);
        }
        public bool Project(ScientificVector3 world,out double pixelX,out double pixelY,out double eyeDepth)
        {return ProjectUnclipped(world,out pixelX,out pixelY,out eyeDepth)&&eyeDepth>=near&&eyeDepth<=far&&pixelX>=0&&pixelX<=width&&pixelY>=0&&pixelY<=height;}
        public bool Unproject(double pixelX,double pixelY,double eyeDepth,out ScientificVector3 world)
        {
            world=new ScientificVector3();
            if(!Finite(pixelX)||!Finite(pixelY)||!Finite(eyeDepth)||pixelX<0||pixelX>width||pixelY<0||pixelY>height||eyeDepth<near||eyeDepth>far)return false;
            double scale=projection==ScientificProjectionKind.Perspective?eyeDepth/focal:1/focal;
            double x=(pixelX-width*.5)*scale,y=(height*.5-pixelY)*scale;
            var result=new ScientificVector3(origin.x+right.x*x+up.x*y+forward.x*eyeDepth,origin.y+right.y*x+up.y*y+forward.y*eyeDepth,origin.z+right.z*x+up.z*y+forward.z*eyeDepth);
            if(!Finite(result.x)||!Finite(result.y)||!Finite(result.z)||Math.Abs(result.x)>1e15||Math.Abs(result.y)>1e15||Math.Abs(result.z)>1e15)return false;
            world=result;return true;
        }
        public double PixelsPerMeter(double eyeDepth)
        {Need(Finite(eyeDepth)&&eyeDepth>0,"Positive finite depth required.");double result=projection==ScientificProjectionKind.Perspective?focal/eyeDepth:focal;Need(Finite(result),"Pixel scale overflow.");return result;}
        public double ChordDepth(double u,double aDepth,double bDepth)
        {
            Need(Finite(u)&&u>=0&&u<=1&&Finite(aDepth)&&aDepth>0&&Finite(bDepth)&&bDepth>0,"Chord inputs invalid.");
            double result=projection==ScientificProjectionKind.Perspective?1/((1-u)/aDepth+u/bDepth):(1-u)*aDepth+u*bDepth;Need(Finite(result)&&result>0,"Chord depth overflow.");return result;
        }
        public double DepthFromDevice(double raw01,bool reversedZ)
        {
            Need(Finite(raw01)&&raw01>=0&&raw01<=1,"Normalized device depth required.");double z=reversedZ?1-raw01:raw01;
            return projection==ScientificProjectionKind.Orthographic?near+(far-near)*z:1/((1-z)/near+z/far);
        }
    }
}
