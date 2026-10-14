using System;
using KspContinuum;

namespace KspContinuum.OwnedRuntimeScheduleExample
{
    public sealed class ForwardSchedule : IOwnedRuntimePassiveKeplerSchedule, IOwnedRuntimeTrialSafeSchedule, IOwnedRuntimeWorkerSchedule, IOwnedRuntimeScalarActuatorSchedule
    {
        public string Schema {get{return "continuum-owned-runtime-schedule/v1";}}
        public string StateSchema {get{return "world-bank-state-only/v1";}}
        public string ImplementationId {get{return "example-forward/v1";}}
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,OwnedRuntimeEnvironment environment,OwnedRuntimeControls controls)
        {systems.Step(step,environment,controls);}
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,IOwnedRuntimeEnvironmentField field,OwnedRuntimeControls controls)
        {systems.Step(step,field,controls);}
    }

    public sealed class GravityProbeSchedule : IOwnedRuntimeTrialSafeSchedule, IOwnedRuntimeWorkerSchedule, IOwnedRuntimeScalarActuatorSchedule
    {
        sealed class ProbeField : IOwnedRuntimeEnvironmentField
        {
            public IOwnedRuntimeEnvironmentField Source;
            public OwnedRuntimeEnvironmentSample Sample(OwnedRuntimeStamp stamp,int region,OwnedRuntimeMotion motion,RigidAssemblyProperties properties)
            {
                var value=Source.Sample(stamp,region,motion,properties);
                return new OwnedRuntimeEnvironmentSample(value.SourceId,value.Revision,value.Epoch,value.Density,value.Temperature,
                    value.WindVelocity,value.Gravity+new Vec(0,-.25,0),value.ConvectiveCoefficient,value.RadiationTemperature,value.FlowAngularVelocity);
            }
        }
        readonly ProbeField field=new ProbeField();
        public string Schema {get{return "continuum-owned-runtime-schedule/v1";}}
        public string StateSchema {get{return "world-bank-state-only/v1";}}
        public string ImplementationId {get{return "example-gravity-negative-world-y-0.25/v1";}}
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,OwnedRuntimeEnvironment environment,OwnedRuntimeControls controls)
        {
            if(environment==null)throw new ArgumentNullException("environment");
            var changed=new OwnedRuntimeEnvironment(environment.SourceId,environment.Revision,environment.Epoch,
                environment.Density,environment.Temperature,environment.WindVelocity,
                environment.Gravity+new Vec(0,-.25,0),environment.ConvectiveCoefficient,environment.RadiationTemperature);
            systems.Step(step,changed,controls);
        }
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,IOwnedRuntimeEnvironmentField source,OwnedRuntimeControls controls)
        {field.Source=source;try{systems.Step(step,field,controls);}finally{field.Source=null;}}
    }

    public sealed class RejectedSchedule : IOwnedRuntimeSchedule, IOwnedRuntimeWorkerSchedule
    {
        public string Schema {get{return "deliberately-incompatible-schedule/v1";}}
        public string StateSchema {get{return "world-bank-state-only/v1";}}
        public string ImplementationId {get{return "example-rejected-abi/v1";}}
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,OwnedRuntimeEnvironment environment,OwnedRuntimeControls controls)
        {throw new InvalidOperationException("An ABI-rejected schedule must never execute.");}
    }

    public sealed class FailedSchedule : IOwnedRuntimeTrialSafeSchedule, IOwnedRuntimeWorkerSchedule, IOwnedRuntimeScalarActuatorSchedule
    {
        public string Schema {get{return "continuum-owned-runtime-schedule/v1";}}
        public string StateSchema {get{return "world-bank-state-only/v1";}}
        public string ImplementationId {get{return "example-failed-after-compute/v1";}}
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,OwnedRuntimeEnvironment environment,OwnedRuntimeControls controls)
        {
            systems.Step(step,environment,controls);
            throw new InvalidOperationException("Deliberate failure after provisional compute, before host publication.");
        }
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,IOwnedRuntimeEnvironmentField field,OwnedRuntimeControls controls)
        {systems.Step(step,field,controls);throw new InvalidOperationException("Deliberate field failure after provisional compute, before host publication.");}
    }
}
