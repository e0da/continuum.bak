using System;

namespace KspContinuum
{
    public struct OwnedRuntimeEnvironmentSample
    {
        public readonly string SourceId;
        public readonly long Revision;
        public readonly double Epoch,Density,Temperature,ConvectiveCoefficient,RadiationTemperature;
        public readonly Vec WindVelocity,Gravity,FlowAngularVelocity;
        public OwnedRuntimeEnvironmentSample(string sourceId,long revision,double epoch,double density,double temperature,
            Vec windVelocity,Vec gravity,double convectiveCoefficient=0,double radiationTemperature=3,Vec flowAngularVelocity=default(Vec))
        {
            SourceId=sourceId;Revision=revision;Epoch=epoch;Density=density;Temperature=temperature;WindVelocity=windVelocity;Gravity=gravity;
            ConvectiveCoefficient=convectiveCoefficient;RadiationTemperature=radiationTemperature;FlowAngularVelocity=flowAngularVelocity;Validate();
        }
        public OwnedRuntimeEnvironmentSample(OwnedRuntimeEnvironment source)
            :this(source.SourceId,source.Revision,source.Epoch,source.Density,source.Temperature,source.WindVelocity,source.Gravity,source.ConvectiveCoefficient,source.RadiationTemperature){}
        public void Validate()
        {
            if(string.IsNullOrWhiteSpace(SourceId)||Revision<0)throw new ArgumentException("Environment field identity is required.");
            AssemblyModel.Finite(Epoch);OwnedRuntimeEnvironment.Nonnegative(Density);AssemblyModel.Positive(Temperature);
            OwnedRuntimeEnvironment.Nonnegative(ConvectiveCoefficient);AssemblyModel.Positive(RadiationTemperature);
            OwnedRuntimeDynamics.Require(WindVelocity);OwnedRuntimeDynamics.Require(Gravity);OwnedRuntimeDynamics.Require(FlowAngularVelocity);
        }
        public OwnedRuntimeEnvironment ToEnvironment()
        {return new OwnedRuntimeEnvironment(SourceId,Revision,Epoch,Density,Temperature,WindVelocity,Gravity,ConvectiveCoefficient,RadiationTemperature);}
    }
    public interface IOwnedRuntimeEnvironmentField
    {
        OwnedRuntimeEnvironmentSample Sample(OwnedRuntimeStamp stamp,int region,OwnedRuntimeMotion motion,RigidAssemblyProperties properties);
    }
    public interface IOwnedRuntimeStepGuard
    {
        void ValidateStep(OwnedRuntimeStep step);
    }
    public interface IOwnedRuntimeFieldSchedule : IOwnedRuntimeSchedule
    {
        void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,IOwnedRuntimeEnvironmentField field,OwnedRuntimeControls controls);
    }
    public interface IOwnedRuntimePassiveKeplerSchedule : IOwnedRuntimeFieldSchedule { }
}
