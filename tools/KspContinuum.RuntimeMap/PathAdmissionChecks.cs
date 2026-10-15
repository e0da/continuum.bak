using System.Text.Json;

namespace KspContinuum.RuntimeMap;

static class PathAdmissionChecks
{
    public static void Run(string directory)
    {
        if(Directory.Exists(directory)||File.Exists(directory))throw new InvalidDataException("Path-check fixture must be new.");
        Directory.CreateDirectory(directory);
        string input=Path.Combine(directory,"input"),alias=Path.Combine(directory,"alias");Directory.CreateDirectory(input);
        Directory.CreateSymbolicLink(alias,input);
        int rejected=0;
        void Reject(string root,string output)
        {
            bool denied=false;
            try {Program.ValidatePaths(root,output);}catch(InvalidDataException){denied=true;}
            if(!denied||Directory.Exists(output)||File.Exists(output))throw new InvalidDataException("Unsafe path admitted/created: "+output);
            rejected++;
        }
        Reject(input,Path.Combine(alias,"would-write-input"));
        Reject(alias,Path.Combine(directory,"would-read-through-alias"));
        Reject(input,Path.Combine(input,"lexical-input-child"));
        Program.ValidatePaths(input,Path.Combine(directory,"safe-new-sibling"));
        using(var file=new FileStream(Path.Combine(directory,"receipt.json"),FileMode.CreateNew,FileAccess.Write))
            JsonSerializer.Serialize(file,new {schema="continuum-runtime-map-path-check/v1",status="passed",rejected,acceptedSafeSibling=true,inputTreeRemainsEmpty=!Directory.EnumerateFileSystemEntries(input).Any()});
        Console.WriteLine("PASS runtime-map path admission: three unsafe boundaries rejected before output creation; safe sibling accepted.");
    }
}
