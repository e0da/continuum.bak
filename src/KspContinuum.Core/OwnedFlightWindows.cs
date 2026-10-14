using System;

namespace KspContinuum
{
    [Serializable] public struct OwnedFlightCoastWindow
    {
        public OwnedRuntimeStamp SourceStamp;
        public string ContextSha256,Kind,Model;
        public double RequestedUniversalTime,ReachedUniversalTime,RuntimeTime,RadiusMeters,LowerCrossingUniversalTime,UpperCrossingUniversalTime;
        public int Region;
        public bool Limited;
    }
    [Serializable] public struct OwnedFlightLocalWindow
    {
        public OwnedRuntimeStamp SourceStamp;
        public double MaximumIntervalSeconds,SelectedIntervalSeconds;
        public string Kind,ContextSha256;
        public long ForecastRegion,ConfigurationRevision,CodeGeneration;
        public bool Advisory;
    }
    public static class OwnedFlightWindows
    {
        public static double ThermalLagInterval(OwnedRuntimeReadLease read,double maximumInterval)
        {
            if(read==null)throw new ArgumentNullException("read");AssemblyModel.Positive(maximumInterval);
            double time=read.Stamp.Time,integrated=read.ThermalIntegratedTime,lag=read.ThermalMaximumLagSeconds;
            if(!read.ThermalIndependent||(time+maximumInterval)-integrated<lag)return maximumInterval;
            if(time-integrated>=lag)throw new InvalidOperationException("Thermal lag is already due before scheduling.");
            long low=0,high=BitConverter.DoubleToInt64Bits(maximumInterval);
            while(high-low>1)
            {
                long middle=low+(high-low)/2;double delta=BitConverter.Int64BitsToDouble(middle);
                if((time+delta)-integrated>=lag)high=middle;else low=middle;
            }
            double result=BitConverter.Int64BitsToDouble(high),end=time+result;
            if(!(end>time)||double.IsInfinity(end))throw new InvalidOperationException("Thermal deadline does not advance representable time.");
            return result;
        }
    }
}
