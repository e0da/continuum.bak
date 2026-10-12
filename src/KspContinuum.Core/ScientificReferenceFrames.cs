using System;
namespace KspContinuum
{
    public enum ScientificFrameSide { PositiveNormal, NegativeNormal }
    public sealed class ScientificFrameObservation
    {
        public readonly string sourceId,coordinateSpace;
        public readonly double epochSeconds,observedAtSeconds,validUntilSeconds;
        public readonly bool valid;
        public ScientificFrameObservation(string sourceId,string coordinateSpace,double epochSeconds,double validUntilSeconds,bool valid)
            :this(sourceId,coordinateSpace,epochSeconds,epochSeconds,validUntilSeconds,valid) { }
        public ScientificFrameObservation(string sourceId,string coordinateSpace,double epochSeconds,double observedAtSeconds,double validUntilSeconds,bool valid)
        {
            Text(sourceId);Text(coordinateSpace);
            ScientificView.Need(ScientificView.Finite(epochSeconds)&&ScientificView.Finite(observedAtSeconds)&&ScientificView.Finite(validUntilSeconds)&&validUntilSeconds>=observedAtSeconds,"Finite sample epoch and ordered observation lifetime required.");
            this.sourceId=sourceId;this.coordinateSpace=coordinateSpace;this.epochSeconds=epochSeconds;this.observedAtSeconds=observedAtSeconds;this.validUntilSeconds=validUntilSeconds;this.valid=valid;
        }
        internal static void Text(string s)
        {ScientificView.Need(!string.IsNullOrEmpty(s)&&s.Length<=256,"Nonempty bounded frame identity required.");foreach(char c in s)ScientificView.Need(!char.IsControl(c),"Frame identity contains control characters.");}
        public void RequireCurrent(double now)
        {ScientificView.Need(valid&&ScientificView.Finite(now)&&now>=observedAtSeconds&&now<=validUntilSeconds,"Frame observation is invalid, not yet issued or stale.");}
    }
    public sealed class ScientificReferenceFrame
    {
        public readonly string id;
        public readonly ScientificVector3 origin,right,up,normal;
        public readonly ScientificFrameObservation observation;
        internal ScientificReferenceFrame(string id,ScientificVector3 origin,ScientificVector3 right,ScientificVector3 up,ScientificVector3 normal,ScientificFrameObservation observation)
        {
            ScientificFrameObservation.Text(id);if(observation==null)throw new ArgumentNullException("observation");
            ScientificView.CheckVector(origin);ScientificView.CheckAxes(right,up);ScientificView.CheckAxes(right,normal);ScientificView.CheckAxes(up,normal);
            ScientificView.Need(ScientificVector3.Dot(ScientificReferenceFrames.Cross(right,up),normal)>1-1e-10,"Frame axes must be right handed.");
            this.id=id;this.origin=origin;this.right=right;this.up=up;this.normal=normal;this.observation=observation;
        }
    }
    public static class ScientificReferenceFrames
    {
        const double MinimumAngularSeparation=1e-10;
        internal static ScientificVector3 Cross(ScientificVector3 a,ScientificVector3 b)
        {return new ScientificVector3(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);}
        static ScientificVector3 Scale(ScientificVector3 a,double s) {return new ScientificVector3(a.x*s,a.y*s,a.z*s);}
        static ScientificVector3 Add(ScientificVector3 a,ScientificVector3 b) {return new ScientificVector3(a.x+b.x,a.y+b.y,a.z+b.z);}
        static ScientificVector3 Unit(ScientificVector3 a)
        {
            ScientificView.CheckVector(a);double scale=Math.Max(Math.Abs(a.x),Math.Max(Math.Abs(a.y),Math.Abs(a.z)));
            ScientificView.Need(scale>0,"Zero direction has no reference frame.");
            var scaled=new ScientificVector3(a.x/scale,a.y/scale,a.z/scale);return Scale(scaled,1/Math.Sqrt(ScientificVector3.Dot(scaled,scaled)));
        }
        static ScientificVector3 IndependentCross(ScientificVector3 a,ScientificVector3 b)
        {
            var c=Cross(a,b);ScientificView.Need(ScientificVector3.Dot(c,c)>=MinimumAngularSeparation*MinimumAngularSeparation,"Parallel or ill-conditioned reference directions.");return Unit(c);
        }
        public static ScientificReferenceFrame Plane(string id,ScientificVector3 origin,ScientificVector3 normal,ScientificVector3 inPlaneReference,ScientificFrameObservation observation)
        {
            var n=Unit(normal);var reference=Unit(inPlaneReference);var up=IndependentCross(n,reference);var right=Unit(Cross(up,n));
            return new ScientificReferenceFrame(id,origin,right,up,n,observation);
        }
        public static ScientificReferenceFrame Equatorial(string id,ScientificVector3 bodyOrigin,ScientificVector3 rotationAxis,ScientificVector3 primeMeridianReference,ScientificFrameObservation observation)
        {return Plane(id,bodyOrigin,rotationAxis,primeMeridianReference,observation);}
        public static ScientificReferenceFrame Orbital(string id,ScientificVector3 referenceOrigin,ScientificVector3 relativePosition,ScientificVector3 relativeVelocity,ScientificFrameObservation observation)
        {
            var radial=Unit(relativePosition);var velocity=Unit(relativeVelocity);var normal=IndependentCross(radial,velocity);
            return new ScientificReferenceFrame(id,referenceOrigin,radial,Unit(Cross(normal,radial)),normal,observation);
        }
        public static ScientificReferenceFrame RadialIn(string id,ScientificVector3 selectedOrigin,ScientificVector3 relativePosition,ScientificVector3 angularMomentum,ScientificFrameObservation observation)
        {
            var radial=Unit(relativePosition);var h=Unit(angularMomentum);
            ScientificView.Need(Math.Abs(ScientificVector3.Dot(radial,h))<=1e-8,"Radial vector and supplied orbital normal disagree.");
            var right=IndependentCross(h,radial);return new ScientificReferenceFrame(id,selectedOrigin,right,Unit(Cross(radial,right)),radial,observation);
        }
        // Plane identities describe supplied observations, never a guessed body or
        // trajectory. epochSeconds can be a future predicted node; freshness is
        // checked against observedAtSeconds and validUntilSeconds instead.
        public static ScientificView Snap(ScientificReferenceFrame frame,ScientificView previous,double distance,ScientificFrameSide side,double currentEpochSeconds)
        {
            if(frame==null)throw new ArgumentNullException("frame");if(previous==null)throw new ArgumentNullException("previous");frame.observation.RequireCurrent(currentEpochSeconds);
            ScientificView.Need(ScientificView.Finite(distance)&&distance>=previous.near&&distance<=previous.far,"Frame origin must lie inside preserved camera clip interval.");
            ScientificView.Need(side==ScientificFrameSide.PositiveNormal||side==ScientificFrameSide.NegativeNormal,"Unknown plane view side.");
            double sign=side==ScientificFrameSide.PositiveNormal?1:-1;
            var cameraOrigin=Add(frame.origin,Scale(frame.normal,sign*distance));
            // Preserve frame-up; opposite viewing sides reverse camera-right so
            // right x up remains the actual forward direction into the origin.
            return new ScientificView(cameraOrigin,Scale(frame.right,-sign),frame.up,Scale(frame.normal,-sign),previous.width,previous.height,previous.projection,previous.fovDegrees,previous.verticalSpanMeters,previous.near,previous.far);
        }
    }
}
