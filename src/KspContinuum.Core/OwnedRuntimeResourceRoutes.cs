using System;
using System.Collections.Generic;

namespace KspContinuum
{
    internal sealed class OwnedRuntimeResourceRoutes
    {
        readonly OwnedRuntimeDefinition definition;
        readonly OwnedRuntimeTopology topology;
        readonly bool[] flows;
        readonly Dictionary<int[],Dictionary<int,int[]>> pools=new Dictionary<int[],Dictionary<int,int[]>>();
        internal OwnedRuntimeResourceRoutes(OwnedRuntimeBank bank,bool[] flows)
        {definition=bank.Definition;topology=bank.Topology;this.flows=flows;}
        internal int[] Get(int[] source,int region)
        {
            Dictionary<int,int[]> regions;
            if(!pools.TryGetValue(source,out regions))
            {
                var rows=new Dictionary<int,List<int>>();
                for(int j=0;j<source.Length;j++)if(flows[source[j]])
                {
                    int resource=source[j],owner=topology.BodyRegions[definition.ResourceBodies[resource]];List<int> list;
                    if(!rows.TryGetValue(owner,out list)){list=new List<int>();rows.Add(owner,list);}list.Add(resource);
                }
                regions=new Dictionary<int,int[]>();foreach(var row in rows)regions.Add(row.Key,row.Value.ToArray());
                pools.Add(source,regions);
            }
            int[] route;return regions.TryGetValue(region,out route)?route:Array.Empty<int>();
        }
    }
}
