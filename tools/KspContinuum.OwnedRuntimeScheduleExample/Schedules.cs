using System;
using KspContinuum;

namespace KspContinuum.OwnedRuntimeScheduleExample
{
    public sealed class ForwardSchedule : IOwnedRuntimeSchedule
    {
        public string Schema {get{return "continuum-owned-runtime-schedule/v1";}}
        public string StateSchema {get{return "world-bank-state-only/v1";}}
        public string ImplementationId {get{return "example-forward/v1";}}
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,OwnedRuntimeEnvironment environment,OwnedRuntimeControls controls)
        {systems.Step(step,environment,controls);}
    }

    public sealed class GravityProbeSchedule : IOwnedRuntimeSchedule
    {
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
    }

    public sealed class RejectedSchedule : IOwnedRuntimeSchedule
    {
        public string Schema {get{return "deliberately-incompatible-schedule/v1";}}
        public string StateSchema {get{return "world-bank-state-only/v1";}}
        public string ImplementationId {get{return "example-rejected-abi/v1";}}
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,OwnedRuntimeEnvironment environment,OwnedRuntimeControls controls)
        {throw new InvalidOperationException("An ABI-rejected schedule must never execute.");}
    }

    public sealed class FailedSchedule : IOwnedRuntimeSchedule
    {
        public string Schema {get{return "continuum-owned-runtime-schedule/v1";}}
        public string StateSchema {get{return "world-bank-state-only/v1";}}
        public string ImplementationId {get{return "example-failed-after-compute/v1";}}
        public void Step(OwnedRuntimeSystems systems,OwnedRuntimeStep step,OwnedRuntimeEnvironment environment,OwnedRuntimeControls controls)
        {
            systems.Step(step,environment,controls);
            throw new InvalidOperationException("Deliberate failure after provisional compute, before host publication.");
        }
    }
}
