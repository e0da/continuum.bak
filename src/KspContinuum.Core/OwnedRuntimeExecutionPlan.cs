using System;

namespace KspContinuum
{
    public enum OwnedRuntimeExecutionStrategy { Reference, Compiled }

    public sealed class OwnedRuntimeExecutionPlan
    {
        internal struct ActuatorRow {internal int Body,Region;internal Vec Point,Axis;}
        internal struct SurfaceRow {internal int Body,Region;internal Vec Point,Normal;}
        internal readonly ActuatorRow[] Actuators;
        internal readonly SurfaceRow[] Surfaces;
        internal readonly int[] SurfaceCounts;
        internal readonly int[][][] FuelRoutes;
        internal readonly double[] ResourceMassPerUnit;
        readonly OwnedRuntimeDefinition definition;
        readonly OwnedRuntimeTopology topology;
        readonly long configuration;
        public int ActuatorCount {get{return Actuators.Length;}}
        public int SurfaceCount {get{return Surfaces.Length;}}
        public int RegionCount {get{return SurfaceCounts.Length;}}
        public long ConfigurationRevision {get{return configuration;}}
        public long TopologyRevision {get{return topology.Revision;}}
        internal OwnedRuntimeExecutionPlan(OwnedRuntimeBank bank,OwnedRuntimeActuatorSpec[] actuators,OwnedRuntimeSurfaceSpec[] surfaces,int[][][] fuelPools,bool[] flows,long configuration)
        {
            definition=bank.Definition;topology=bank.Topology;this.configuration=configuration;
            Actuators=new ActuatorRow[actuators.Length];Surfaces=new SurfaceRow[surfaces.Length];SurfaceCounts=new int[topology.RegionCount];
            FuelRoutes=new int[actuators.Length][][];ResourceMassPerUnit=new double[definition.ResourceCount];
            for(int r=0;r<ResourceMassPerUnit.Length;r++)ResourceMassPerUnit[r]=definition.GetResource(r).MassPerUnit;
            var routedPools=new OwnedRuntimeResourceRoutes(bank,flows);
            for(int i=0;i<Actuators.Length;i++)
            {
                var spec=actuators[i];var part=definition.GetPart(spec.Part);int body=definition.BodySlot(part.BodyId);var member=bank.Members[body];
                Rotation local=member.LocalRotation*part.LocalRotation;
                Actuators[i]=new ActuatorRow{Body=body,Region=topology.BodyRegions[body],Point=Point(part,member,spec.LocalPoint),Axis=local.Rotate(spec.LocalAxis)};
                FuelRoutes[i]=new int[fuelPools[i].Length][];
                for(int f=0;f<fuelPools[i].Length;f++)
                {
                    FuelRoutes[i][f]=routedPools.Get(fuelPools[i][f],Actuators[i].Region);
                }
            }
            for(int i=0;i<Surfaces.Length;i++)
            {
                var spec=surfaces[i];var part=definition.GetPart(spec.Part);int body=definition.BodySlot(part.BodyId);var member=bank.Members[body];
                Rotation local=member.LocalRotation*part.LocalRotation;int region=topology.BodyRegions[body];
                Surfaces[i]=new SurfaceRow{Body=body,Region=region,Point=Point(part,member,spec.LocalPoint),Normal=local.Rotate(spec.LocalNormal)};SurfaceCounts[region]++;
            }
        }
        static Vec Point(OwnedRuntimePartDefinition part,RigidAssemblyMember member,Vec point)
        {Vec bodyPoint=part.LocalPosition+part.LocalRotation.Rotate(point);return member.LocalPosition+member.LocalRotation.Rotate(bodyPoint);}
        internal bool Matches(OwnedRuntimeBank bank,long configuration)
        {return ReferenceEquals(definition,bank.Definition)&&ReferenceEquals(topology,bank.Topology)&&this.configuration==configuration;}
    }
}
