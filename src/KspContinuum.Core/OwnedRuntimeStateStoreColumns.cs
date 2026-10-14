using System;
using System.IO;
using System.Collections.Generic;

namespace KspContinuum
{
    public static partial class OwnedRuntimeStateStore
    {
        static List<Column> Columns(Counts c, OwnedRuntimeBank b, OwnedRuntimeSystems systems, Loaded a, Metadata m, OwnedRuntimeStateLimits limits)
        {
            var columns = new List<Column>(); OwnedRuntimeDefinition d = b == null ? null : b.Definition;
            Action<int, long, int, Action<long, BinaryWriter>, Action<long, BinaryReader>> add = (id, count, width, write, read) => columns.Add(new Column(id, count, width, write, read));
            add(1, c.Parts, 8, (i,w) => w.Write(d.GetPart((int)i).Id), (i,r) => a.PartIds[i] = r.ReadInt64());
            add(2, c.Parts, 8, (i,w) => w.Write(d.GetPart((int)i).BodyId), (i,r) => a.PartBodies[i] = r.ReadInt64());
            add(3, c.Parts, 0, (i,w) => Text(w,d.GetPart((int)i).Name,limits), (i,r) => a.Names[i] = Text(r,a.Budget));
            add(4, c.Parts, 24, (i,w) => Vector(w,d.GetPart((int)i).LocalPosition), (i,r) => a.Positions[i] = Vector(r));
            add(5, c.Parts, 32, (i,w) => Quaternion(w,d.GetPart((int)i).LocalRotation), (i,r) => a.Rotations[i] = Quaternion(r));
            add(6, c.Parts, 8, (i,w) => w.Write(d.GetPart((int)i).HeatCapacity), (i,r) => a.HeatCapacity[i] = r.ReadDouble());
            add(7, c.Parts, 8, (i,w) => w.Write(d.GetPart((int)i).InitialTemperature), (i,r) => a.InitialTemperature[i] = r.ReadDouble());
            add(10, c.Bodies, 8, (i,w) => w.Write(d.GetBody((int)i).Id), (i,r) => a.BodyIds[i] = r.ReadInt64());
            add(11, c.Bodies, 148, (i,w) => Member(w,d.GetBody((int)i).Member), (i,r) => a.Dry[i] = Member(r));
            add(12, c.Bodies, 148, (i,w) => Member(w,b.Members[i]), (i,r) => a.Wet[i] = Member(r));
            add(20, c.Resources, 41, (i,w) => { var v=d.GetResource((int)i);w.Write(v.Id);w.Write(v.PartId);w.Write(v.Capacity);w.Write(v.MassPerUnit);w.Write(v.InitialQuantity);w.Write(v.AcquiredSurplus); },
                (i,r) => { a.ResourceIds[i]=r.ReadInt64();a.ResourceParts[i]=r.ReadInt64();a.Capacity[i]=r.ReadDouble();a.MassPerUnit[i]=r.ReadDouble();a.InitialQuantity[i]=r.ReadDouble();a.Surplus[i]=Boolean(r); });
            add(21, c.Resources, 0, (i,w) => Text(w,d.GetResource((int)i).Kind,limits), (i,r) => a.Kinds[i]=Text(r,a.Budget));
            add(22, c.Resources, 8, (i,w) => w.Write(b.ResourceQuantity[i]), (i,r) => a.Quantity[i]=r.ReadDouble());
            add(23, c.Resources, 1, (i,w) => w.Write(systems.IsResourceFlowEnabled((int)i)), (i,r) => a.Flows[i]=Boolean(r));
            add(30, c.Providers, 8, (i,w) => w.Write(d.GetProviderKey((int)i)), (i,r) => a.ProviderKeys[i]=r.ReadInt64());
            add(31, c.Providers, 8, (i,w) => w.Write(b.ProviderState[i]), (i,r) => a.Providers[i]=r.ReadDouble());
            add(40, c.Connections, 25, (i,w) => { var v=b.Topology.GetConnection((int)i);w.Write(v.Id);w.Write(v.PartA);w.Write(v.PartB);w.Write(v.Locked); }, (i,r) => a.Connections[i]=new OwnedRuntimeConnection(r.ReadInt64(),r.ReadInt64(),r.ReadInt64(),Boolean(r)));
            add(50, c.Regions, 8, (i,w) => w.Write(b.Topology.GetRegionId((int)i)), (i,r) => a.RegionIds[i]=r.ReadInt64());
            add(51, c.Regions, 104, (i,w) => { var v=b.RegionMotion[i];Vector(w,v.RootPosition);Quaternion(w,v.Orientation);Vector(w,v.Velocity);Vector(w,v.AngularMomentum); }, (i,r) => a.Motions[i]=new OwnedRuntimeMotion { RootPosition=Vector(r),Orientation=Quaternion(r),Velocity=Vector(r),AngularMomentum=Vector(r) });
            add(60, c.Parts, 8, (i,w) => w.Write(b.Temperature[i]), (i,r) => a.Temperature[i]=r.ReadDouble());
            add(61, c.History, 80, (i,w) => { var v=b.History[(b.HistoryHead-b.HistoryCount+(int)i+b.History.Length)%b.History.Length];w.Write(v.Sequence);w.Write(v.StartedAt);w.Write(v.DeltaTime);Input(w,v.Input); },
                (i,r) => a.History[i]=new OwnedRuntimeInputRecord { Sequence=r.ReadInt64(),StartedAt=r.ReadDouble(),DeltaTime=r.ReadDouble(),Input=Input(r) });
            add(70, c.Actuators, 109, (i,w) => { var v=systems.GetActuator((int)i);w.Write(v.Part);w.Write((int)v.Kind);Vector(w,v.LocalPoint);Vector(w,v.LocalAxis);w.Write(v.MaximumForceOrTorque);w.Write(v.FuelResource);w.Write(v.SpecificImpulseSeconds);w.Write(v.ElectricResource);w.Write(v.ElectricUnitsPerSecond);w.Write(v.HeatWatts);w.Write(v.WheelMomentumCapacity);w.Write(systems.WheelProviderSlot((int)i));w.Write(systems.IsEnabled((int)i)); },
                (i,r) => { a.ActuatorValues[i]=new ActuatorValue { Part=r.ReadInt32(),Kind=r.ReadInt32(),Point=Vector(r),Axis=Vector(r),Maximum=r.ReadDouble(),Fuel=r.ReadInt32(),Isp=r.ReadDouble(),Electric=r.ReadInt32(),ElectricRate=r.ReadDouble(),Heat=r.ReadDouble(),WheelCapacity=r.ReadDouble() };a.WheelSlots[i]=r.ReadInt32();a.Enabled[i]=Boolean(r); });
            add(71, c.Actuators, 0, (i,w) => { var v=systems.GetActuator((int)i);int n=v.FuelResource<0?v.FuelCount:0;w.Write(n);for(int f=0;f<n;f++){Text(w,v.FuelKind(f),limits);w.Write(v.FuelMassFraction(f));} },
                (i,r) => { int n=r.ReadInt32();Need(n>=0&&n<=32,"State mixture width differs.");a.Budget.Reserve(32L+24L*n,n);a.FuelKinds[i]=new string[n];a.FuelFractions[i]=new double[n];for(int f=0;f<n;f++){a.FuelKinds[i][f]=Text(r,a.Budget);a.FuelFractions[i][f]=r.ReadDouble();} });
            add(80, c.Surfaces, 92, (i,w) => { var v=systems.GetSurface((int)i);w.Write(v.Part);Vector(w,v.LocalPoint);Vector(w,v.LocalNormal);w.Write(v.DragArea);w.Write(v.LiftArea);w.Write(v.ThermalArea);w.Write(v.Emissivity);w.Write(v.HeatingFraction); },
                (i,r) => a.Surfaces[i]=OwnedRuntimeSurfaceSpec.RestoreUnitNormal(r.ReadInt32(),Vector(r),Vector(r),r.ReadDouble(),r.ReadDouble(),r.ReadDouble(),r.ReadDouble(),r.ReadDouble()));
            add(81, c.Links, 16, (i,w) => { var v=systems.GetThermalLink((int)i);w.Write(v.A);w.Write(v.B);w.Write(v.ConductanceWattsPerKelvin); }, (i,r) => a.Links[i]=new OwnedRuntimeThermalLink(r.ReadInt32(),r.ReadInt32(),r.ReadDouble()));
            for(int column=100;column<=110;column++)
            {
                int id=column;long count=id==105?8:id>=106?checked((long)c.Parts*8):c.Parts;
                add(id,count,8,(i,w) => { double[] values=ThermalArray(m.Thermal,id);w.Write(values.Length==0?0:values[i]); },
                    (i,r) => { double value=r.ReadDouble();double[] values=ThermalArray(m.Thermal,id);if(BitConverter.DoubleToInt64Bits(value)!=0&&(values.Length==0||id<=104&&m.Thermal.segmentCount==0)){HydrateThermal(m.Thermal);values=ThermalArray(m.Thermal,id);}if(values.Length!=0)values[i]=value; });
            }
            return columns;
        }
        static double[] ThermalArray(OwnedRuntimeThermalState t,int id)
        {
            switch(id){case 100:return t.heatJoules;case 101:return t.convectionExposure;case 102:return t.convectionSource;case 103:return t.radiationExposure;case 104:return t.radiationSource;case 105:return t.segmentDuration;
                case 106:return t.segmentHeat;case 107:return t.segmentConvection;case 108:return t.segmentConvectionSource;case 109:return t.segmentRadiation;case 110:return t.segmentRadiationSource;default:throw new InvalidDataException("Unknown thermal column.");}
        }
    }
}
