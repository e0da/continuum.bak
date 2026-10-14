using System;

namespace KspContinuum
{
    public sealed class OwnedRuntimeThermalPolicy
    {
        public readonly bool Independent;
        public readonly double MaximumLagSeconds, MaximumDeferredKelvin, AbsoluteKelvinError, RelativeError;
        public readonly int MaximumRefinements;
        public static readonly OwnedRuntimeThermalPolicy Reference = new OwnedRuntimeThermalPolicy(false, .1, .25, 1e-4, 1e-6, 8);
        public OwnedRuntimeThermalPolicy(bool independent, double maximumLagSeconds=.1, double maximumDeferredKelvin=.25,
            double absoluteKelvinError=1e-4, double relativeError=1e-6, int maximumRefinements=8)
        {
            AssemblyModel.Positive(maximumLagSeconds); AssemblyModel.Positive(maximumDeferredKelvin);
            AssemblyModel.Positive(absoluteKelvinError); AssemblyModel.Positive(relativeError);
            if(maximumLagSeconds>10 || absoluteKelvinError<1e-8 || relativeError>1e-2 || maximumRefinements<1 || maximumRefinements>12)
                throw new ArgumentException("Thermal error/deadline policy is outside its bounded domain.");
            Independent=independent;MaximumLagSeconds=maximumLagSeconds;MaximumDeferredKelvin=maximumDeferredKelvin;
            AbsoluteKelvinError=absoluteKelvinError;RelativeError=relativeError;MaximumRefinements=maximumRefinements;
        }
    }
    [Serializable] public sealed class OwnedRuntimeThermalState
    {
        public string schema="continuum-owned-thermal-state/v1", planId="";
        public bool independent;
        public double integratedTime,pendingDuration,maximumLagSeconds=.1,maximumDeferredKelvin=.25,absoluteKelvinError=1e-4,relativeError=1e-6;
        public double passiveFrozenSeconds,lastErrorKelvin,lastDeferredBoundKelvin,sourceJoules,exchangeJoules;
        public int maximumRefinements=8,lastSubsteps;
        public long causalRevision,integratedCausalRevision,topologyRevision,advances,deferredSteps;
        public double[] heatJoules,convectionExposure,convectionSource,radiationExposure,radiationSource;
        public const int SegmentCapacity=8;
        public int segmentCount;
        public double[] segmentDuration,segmentHeat,segmentConvection,segmentConvectionSource,segmentRadiation,segmentRadiationSource;
        public OwnedRuntimeThermalPolicy Policy {get{return new OwnedRuntimeThermalPolicy(independent,maximumLagSeconds,maximumDeferredKelvin,absoluteKelvinError,relativeError,maximumRefinements);}}
        public OwnedRuntimeThermalState(){}
        internal OwnedRuntimeThermalState(int parts,double time=0)
        {integratedTime=time;heatJoules=new double[parts];convectionExposure=new double[parts];convectionSource=new double[parts];radiationExposure=new double[parts];radiationSource=new double[parts];segmentDuration=new double[SegmentCapacity];int count=checked(parts*SegmentCapacity);segmentHeat=new double[count];segmentConvection=new double[count];segmentConvectionSource=new double[count];segmentRadiation=new double[count];segmentRadiationSource=new double[count];}
        internal void Apply(OwnedRuntimeThermalPolicy policy)
        {independent=policy.Independent;maximumLagSeconds=policy.MaximumLagSeconds;maximumDeferredKelvin=policy.MaximumDeferredKelvin;absoluteKelvinError=policy.AbsoluteKelvinError;relativeError=policy.RelativeError;maximumRefinements=policy.MaximumRefinements;}
        internal void CopyFrom(OwnedRuntimeThermalState s)
        {
            int previousSegments=segmentCount;
            schema=s.schema;planId=s.planId;independent=s.independent;integratedTime=s.integratedTime;pendingDuration=s.pendingDuration;
            maximumLagSeconds=s.maximumLagSeconds;maximumDeferredKelvin=s.maximumDeferredKelvin;absoluteKelvinError=s.absoluteKelvinError;relativeError=s.relativeError;
            maximumRefinements=s.maximumRefinements;passiveFrozenSeconds=s.passiveFrozenSeconds;lastErrorKelvin=s.lastErrorKelvin;
            lastDeferredBoundKelvin=s.lastDeferredBoundKelvin;sourceJoules=s.sourceJoules;exchangeJoules=s.exchangeJoules;lastSubsteps=s.lastSubsteps;
            causalRevision=s.causalRevision;integratedCausalRevision=s.integratedCausalRevision;topologyRevision=s.topologyRevision;advances=s.advances;deferredSteps=s.deferredSteps;
            if(s.segmentCount!=0||previousSegments!=0)
            {Array.Copy(s.heatJoules,heatJoules,heatJoules.Length);Array.Copy(s.convectionExposure,convectionExposure,heatJoules.Length);Array.Copy(s.convectionSource,convectionSource,heatJoules.Length);Array.Copy(s.radiationExposure,radiationExposure,heatJoules.Length);Array.Copy(s.radiationSource,radiationSource,heatJoules.Length);}
            segmentCount=s.segmentCount;Array.Copy(s.segmentDuration,segmentDuration,SegmentCapacity);
            CopySegments(s.segmentHeat,segmentHeat,previousSegments);CopySegments(s.segmentConvection,segmentConvection,previousSegments);CopySegments(s.segmentConvectionSource,segmentConvectionSource,previousSegments);CopySegments(s.segmentRadiation,segmentRadiation,previousSegments);CopySegments(s.segmentRadiationSource,segmentRadiationSource,previousSegments);
        }
        void CopySegments(double[] source,double[] target,int previousSegments)
        {int used=segmentCount*heatJoules.Length;Array.Copy(source,target,used);if(previousSegments>segmentCount)Array.Clear(target,used,(previousSegments-segmentCount)*heatJoules.Length);}
        public OwnedRuntimeThermalState Clone()
        {if(heatJoules==null)throw new ArgumentException("Thermal ledger absent.");Validate(heatJoules.Length);var result=new OwnedRuntimeThermalState(heatJoules.Length);result.CopyFrom(this);return result;}
        internal void Validate(int parts)
        {
            if(schema!="continuum-owned-thermal-state/v1" || planId==null || planId.Length>256 || causalRevision<0 || integratedCausalRevision<0 || integratedCausalRevision>causalRevision || topologyRevision<0)
                throw new ArgumentException("Thermal causal metadata differs.");
            AssemblyModel.Positive(maximumLagSeconds);AssemblyModel.Positive(maximumDeferredKelvin);AssemblyModel.Positive(absoluteKelvinError);AssemblyModel.Positive(relativeError);
            if(maximumLagSeconds>10||absoluteKelvinError<1e-8||relativeError>1e-2||maximumRefinements<1||maximumRefinements>12)throw new ArgumentException("Thermal policy domain differs.");
            AssemblyModel.Finite(integratedTime);Nonnegative(pendingDuration);Nonnegative(passiveFrozenSeconds);
            Nonnegative(lastErrorKelvin);Nonnegative(lastDeferredBoundKelvin);Nonnegative(sourceJoules);AssemblyModel.Finite(exchangeJoules);
            if(advances<0 || deferredSteps<0 || lastSubsteps<0 || lastSubsteps>(1<<(maximumRefinements+1)))throw new ArgumentException("Thermal counters differ.");
            ValidateArray(heatJoules,parts);ValidateArray(convectionExposure,parts);ValidateArray(convectionSource,parts);ValidateArray(radiationExposure,parts);ValidateArray(radiationSource,parts);
            if(pendingDuration==0)for(int i=0;i<parts;i++)if(heatJoules[i]!=0 || convectionExposure[i]!=0 || convectionSource[i]!=0 || radiationExposure[i]!=0 || radiationSource[i]!=0)
                throw new ArgumentException("Thermal zero-duration ledger has pending energy/exposure.");
            if(pendingDuration>0 && (!independent || planId.Length==0))throw new ArgumentException("Pending thermal interval lacks its admitted independent model.");
            if(segmentCount<0||segmentCount>SegmentCapacity||segmentDuration==null||segmentDuration.Length!=SegmentCapacity || (pendingDuration==0)!=(segmentCount==0))throw new ArgumentException("Thermal forcing history bounds differ.");
            ValidateArray(segmentHeat,parts*SegmentCapacity);ValidateArray(segmentConvection,parts*SegmentCapacity);ValidateArray(segmentConvectionSource,parts*SegmentCapacity);ValidateArray(segmentRadiation,parts*SegmentCapacity);ValidateArray(segmentRadiationSource,parts*SegmentCapacity);
            double duration=0;for(int i=0;i<SegmentCapacity;i++){Nonnegative(segmentDuration[i]);if(i<segmentCount){AssemblyModel.Positive(segmentDuration[i]);duration+=segmentDuration[i];}else if(segmentDuration[i]!=0)throw new ArgumentException("Unused thermal history duration differs.");}
            if(Math.Abs(duration-pendingDuration)>16*2.2204460492503131e-16*Math.Max(1,Math.Abs(integratedTime+pendingDuration)))throw new ArgumentException("Thermal forcing history clock differs.");
            if(causalRevision-integratedCausalRevision!=segmentCount)throw new ArgumentException("Thermal forcing causal revision differs.");
            ValidateLedger(heatJoules,segmentHeat,parts);ValidateLedger(convectionExposure,segmentConvection,parts);ValidateLedger(convectionSource,segmentConvectionSource,parts);ValidateLedger(radiationExposure,segmentRadiation,parts);ValidateLedger(radiationSource,segmentRadiationSource,parts);
        }
        void ValidateLedger(double[] total,double[] history,int parts)
        {for(int p=0;p<parts;p++){double sum=0;for(int i=0;i<SegmentCapacity;i++){double value=history[i*parts+p];if(i<segmentCount)sum+=value;else if(value!=0)throw new ArgumentException("Unused thermal forcing history differs.");}if(sum!=total[p])throw new ArgumentException("Thermal energy/exposure ledger differs from exact ordered history.");}}
        static void ValidateArray(double[] array,int parts)
        {if(array==null||array.Length!=parts)throw new ArgumentException("Thermal array shape differs.");foreach(double value in array)Nonnegative(value);}
        internal void ValidateClock(double time)
        {double end=integratedTime+pendingDuration;AssemblyModel.Finite(end);if(Math.Abs(end-time)>16*2.2204460492503131e-16*Math.Max(1,Math.Abs(time)))throw new ArgumentException("Thermal causal frontier differs from physical time.");}
        internal static void Nonnegative(double value){AssemblyModel.Finite(value);if(value<0)throw new ArgumentException("Thermal quantity must be nonnegative.");}
        internal void Clear()
        {Array.Clear(heatJoules,0,heatJoules.Length);Array.Clear(convectionExposure,0,heatJoules.Length);Array.Clear(convectionSource,0,heatJoules.Length);Array.Clear(radiationExposure,0,heatJoules.Length);Array.Clear(radiationSource,0,heatJoules.Length);Array.Clear(segmentDuration,0,SegmentCapacity);Array.Clear(segmentHeat,0,segmentHeat.Length);Array.Clear(segmentConvection,0,segmentHeat.Length);Array.Clear(segmentConvectionSource,0,segmentHeat.Length);Array.Clear(segmentRadiation,0,segmentHeat.Length);Array.Clear(segmentRadiationSource,0,segmentHeat.Length);segmentCount=0;pendingDuration=0;}
    }
    internal sealed class OwnedRuntimeThermalSolver
    {
        readonly OwnedRuntimeDefinition definition;
        readonly OwnedRuntimeThermalLink[] links;
        readonly double[] coarse,fine;
        internal OwnedRuntimeThermalSolver(OwnedRuntimeDefinition definition,OwnedRuntimeThermalLink[] links)
        {this.definition=definition;this.links=links;coarse=new double[definition.PartCount];fine=new double[definition.PartCount];}
        internal double DeferredBound(OwnedRuntimeBank bank)
        {
            var s=bank.Thermal;double upper=0,bound=0;
            for(int p=0;p<fine.Length;p++)
            {
                double t=bank.Temperature[p];
                for(int segment=0;segment<s.segmentCount;segment++)
                {
                    int offset=segment*fine.Length+p;
                    if(s.segmentConvection[offset]>0)t=Math.Max(t,s.segmentConvectionSource[offset]/s.segmentConvection[offset]);
                    if(s.segmentRadiation[offset]>0)t=Math.Max(t,Math.Sqrt(Math.Sqrt(s.segmentRadiationSource[offset]/s.segmentRadiation[offset])));
                }
                upper=Math.Max(upper,t+s.heatJoules[p]/definition.GetPart(p).HeatCapacity);
            }
            double fourth=upper*upper;fourth*=fourth;AssemblyModel.Finite(fourth);
            for(int p=0;p<fine.Length;p++)
            {
                fine[p]=(s.heatJoules[p]+s.convectionSource[p]+s.convectionExposure[p]*upper+s.radiationSource[p]+s.radiationExposure[p]*fourth)/definition.GetPart(p).HeatCapacity;
                AssemblyModel.Finite(fine[p]);
            }
            foreach(var link in links)if(bank.Topology.PartRegions[link.A]==bank.Topology.PartRegions[link.B])
            {double q=link.ConductanceWattsPerKelvin*s.pendingDuration*upper;fine[link.A]+=q/definition.GetPart(link.A).HeatCapacity;fine[link.B]+=q/definition.GetPart(link.B).HeatCapacity;}
            for(int p=0;p<fine.Length;p++){AssemblyModel.Finite(fine[p]);bound=Math.Max(bound,fine[p]);}return bound;
        }
        internal void Flush(OwnedRuntimeBank bank)
        {
            var state=bank.Thermal;if(state.pendingDuration==0)return;
            if(state.topologyRevision!=bank.Topology.Revision)throw new InvalidOperationException("Pending thermal topology changed.");
            int subdivisions=1;bool admitted=false;double error=0;
            for(int refinement=0;refinement<state.maximumRefinements;refinement++,subdivisions*=2)
            {
                Integrate(bank,coarse,subdivisions);Integrate(bank,fine,subdivisions*2);admitted=true;error=0;
                for(int p=0;p<fine.Length;p++)
                {double difference=2*Math.Abs(fine[p]-coarse[p]);error=Math.Max(error,difference);if(difference>state.absoluteKelvinError+state.relativeError*Math.Abs(fine[p]))admitted=false;}
                if(admitted)break;
            }
            if(!admitted)throw new InvalidOperationException("Thermal adaptive interval exceeds its frozen refinement/error budget.");
            double before=0,after=0,source=0;
            for(int p=0;p<fine.Length;p++){double c=definition.GetPart(p).HeatCapacity;before+=c*bank.Temperature[p];after+=c*fine[p];source+=state.heatJoules[p];bank.Temperature[p]=fine[p];}
            AssemblyModel.Finite(before);AssemblyModel.Finite(after);AssemblyModel.Finite(source);
            state.exchangeJoules+=after-before-source;state.sourceJoules+=source;state.integratedTime+=state.pendingDuration;
            state.integratedCausalRevision=state.causalRevision;state.lastErrorKelvin=error;state.lastSubsteps=subdivisions*2;state.advances++;state.Clear();
        }
        void Integrate(OwnedRuntimeBank bank,double[] target,int count)
        {
            var state=bank.Thermal;Array.Copy(bank.Temperature,target,target.Length);double fraction=1.0/count;
            for(int segment=0;segment<state.segmentCount;segment++)for(int step=0;step<count;step++)
            {
                int offset=segment*target.Length;double dt=state.segmentDuration[segment]*fraction;
                for(int p=0;p<target.Length;p++)
                {
                    double c=definition.GetPart(p).HeatCapacity,q=state.segmentHeat[offset+p]*fraction,a=state.segmentConvection[offset+p]*fraction,b=state.segmentConvectionSource[offset+p]*fraction,k=state.segmentRadiation[offset+p]*fraction,d=state.segmentRadiationSource[offset+p]*fraction,t=target[p];
                    double low=0,high=t+q/c;if(a>0)high=Math.Max(high,b/a);if(k>0)high=Math.Max(high,Math.Sqrt(Math.Sqrt(d/k)));
                    double value=high;
                    for(int iteration=0;iteration<80;iteration++)
                    {
                        double square=value*value,residual=c*(value-t)-q+a*value-b+k*square*square-d;
                        AssemblyModel.Finite(residual);
                        if(Math.Abs(residual)/c<=1e-10)break;
                        if(residual>0)high=value;else low=value;
                        double derivative=c+a+4*k*square*value,next=value-residual/derivative;
                        value=next>low&&next<high?next:(low+high)*.5;
                        if(iteration==79)throw new InvalidOperationException("Thermal implicit root did not converge.");
                    }
                    AssemblyModel.Positive(value);target[p]=value;
                }
                foreach(var link in links)if(bank.Topology.PartRegions[link.A]==bank.Topology.PartRegions[link.B])
                {
                    double ca=definition.GetPart(link.A).HeatCapacity,cb=definition.GetPart(link.B).HeatCapacity;
                    double energy=(target[link.A]-target[link.B])*link.ConductanceWattsPerKelvin*dt/(1+link.ConductanceWattsPerKelvin*dt*(1/ca+1/cb));
                    target[link.A]-=energy/ca;target[link.B]+=energy/cb;
                }
                foreach(double value in target)AssemblyModel.Positive(value);
            }
        }
    }
}
