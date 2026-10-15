using System;
using System.IO;
using KspContinuum;
static class Program
{
    static int checks;
    static void Need(bool b,string n) {checks++;if(!b)throw new Exception(n);}
    static void Reject(Action a,string n) {bool rejected=false;try{a();}catch(InvalidDataException){rejected=true;}Need(rejected,n);}
    static ScientificAnchorData Row(string id,string kind,uint part,double x) {return new ScientificAnchorData {id=id,kind=kind,label=id,source="supplied-owner/epoch",partFlightId=part,offsetMeters=new[] {x,0.0,0.0},axesAvailable=false};}
    static ScientificAnchorSet Data()
    {return new ScientificAnchorSet {runId="run",processId=42,vesselId="vessel",bodyId="body",epochSeconds=100,preferredId="control/2",anchors=new[] {Row("origin","vessel-origin",0,0),Row("root/1","root-part",1,2),Row("control/2","control",2,3),Row("com","center-of-mass",0,.25)}};}
    static ScientificAnchorCatalog Admit(ScientificAnchorSet s) {return ScientificAnchors.Admit(s,"run",42,"vessel","body","body-centred-world-axes");}
    static void Main()
    {
        var dto=Data();var c=Admit(dto);Need(c.Count==4&&c.Preferred.kind=="control"&&c.epochSeconds==100,"Bound immutable catalog and control priority");
        var p=ScientificAnchors.Resolve(c.Preferred,new ScientificVector3(1e12,0,0));Need(p.x==1e12+3&&p.y==0,"Offsets translate in world axes without vessel rotation");
        dto.anchors[2].offsetMeters[0]=99;dto.anchors[2].label="changed";Need(c.Preferred.offsetMeters.x==3&&c.Preferred.label=="control/2","Mutable DTO cannot modify admitted owner");
        Need(!c.Find("com").axesAvailable,"CoM position has no fabricated orientation");
        var root=Data();root.anchors=new[] {root.anchors[0],root.anchors[1]};root.preferredId="root/1";Need(Admit(root).Preferred.kind=="root-part","Actual root fallback");
        root.anchors=new[] {root.anchors[0]};root.preferredId="origin";Need(Admit(root).Preferred.kind=="vessel-origin","Captured vessel-origin fallback");
        var wrong=Data();wrong.processId=43;Reject(()=>Admit(wrong),"Foreign PID");wrong=Data();wrong.vesselId="other";Reject(()=>Admit(wrong),"Foreign vessel");wrong=Data();wrong.runId="other";Reject(()=>Admit(wrong),"Foreign run");wrong=Data();wrong.bodyId="other";Reject(()=>Admit(wrong),"Foreign body");
        wrong=Data();wrong.preferredId="root/1";Reject(()=>Admit(wrong),"Cannot demote available actual control");wrong=Data();wrong.epochSeconds=double.NaN;Reject(()=>Admit(wrong),"Invalid epoch");
        wrong=Data();wrong.anchors[1].offsetMeters[0]=double.PositiveInfinity;Reject(()=>Admit(wrong),"Invalid offset");wrong=Data();wrong.anchors[0].offsetMeters[0]=1;Reject(()=>Admit(wrong),"Origin cannot claim root or CoM position");
        wrong=Data();wrong.anchors[3].right=new[] {1.0,0.0,0.0};Reject(()=>Admit(wrong),"Unavailable axes cannot contain guessed data");
        var basis=Data();var control=basis.anchors[2];control.axesAvailable=true;control.right=new[] {0.0,1.0,0.0};control.up=new[] {-1.0,0.0,0.0};control.forward=new[] {0.0,0.0,1.0};Need(Admit(basis).Preferred.right.y==1,"Captured rotated right-handed basis");
        control.forward[2]=-1;Reject(()=>Admit(basis),"Reflected basis rejected");
        var node=Row("node/2/top","attachment-node",2,4);node.nodeId="top";var nodes=Data();nodes.anchors=new[] {nodes.anchors[0],nodes.anchors[1],nodes.anchors[2],node};Need(Admit(nodes).Find(node.id).nodeId=="top","Explicit selected attachment identity");node.nodeId="";Reject(()=>Admit(nodes),"Attachment requires actual node ID");
        Need(new MultiscaleSceneManifest().scientificAnchors==null,"Legacy optional anchors absent");
        Console.WriteLine("Scientific anchor checks passed: "+checks);
    }
}
