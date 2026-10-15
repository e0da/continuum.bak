using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal static class Program
{
    const int MaximumCommandBytes=8192,MaximumResponseBytes=16*1024*1024;
    static readonly UTF8Encoding Utf8=new(false,true);

    static async Task<int> Main(string[] arguments)
    {
        try
        {
            var options=new Dictionary<string,string>(StringComparer.Ordinal);
            if(arguments.Length!=6)throw new ArgumentException("Usage: --port INT --command TEXT --output NEW_ABSOLUTE_JSON");
            for(int i=0;i<arguments.Length;i+=2)
                if(arguments[i]!="--port"&&arguments[i]!="--command"&&arguments[i]!="--output" || !options.TryAdd(arguments[i],arguments[i+1]))
                    throw new ArgumentException("Unknown or duplicate option.");
            if(!int.TryParse(options["--port"],NumberStyles.None,CultureInfo.InvariantCulture,out int port)||port<1||port>65535)
                throw new ArgumentException("Loopback port must be 1..65535.");
            string command=options["--command"],path=options["--output"];
            if(string.IsNullOrWhiteSpace(command)||command.Any(char.IsControl))throw new ArgumentException("Command must be a nonempty single line without control characters.");
            byte[] request=Utf8.GetBytes(command);
            if(request.Length>MaximumCommandBytes)throw new ArgumentException("Command exceeds 8KiB UTF-8.");
            if(!Path.IsPathFullyQualified(path)||!string.Equals(Path.GetExtension(path),".json",StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Output must be an absolute JSON path.");
            path=Path.GetFullPath(path);
            using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            try
            {
                string operation=command.Split(' ',StringSplitOptions.RemoveEmptyEntries)[0];
                bool clockAdministration=operation is "clock-enable" or "clock-disable" or "clock-save" or "clock-restore";
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(clockAdministration?65:15));
                using var client=new TcpClient(AddressFamily.InterNetwork){NoDelay=true};
                await client.ConnectAsync(IPAddress.Loopback,port,deadline.Token);
                using NetworkStream stream=client.GetStream();
                await stream.WriteAsync(request,deadline.Token);
                await stream.WriteAsync(new byte[]{10},deadline.Token);
                using var response=new MemoryStream();
                byte[] buffer=new byte[4096];bool complete=false;
                while(!complete)
                {
                    int count=await stream.ReadAsync(buffer,deadline.Token);
                    if(count==0)throw new IOException("Connection closed before the newline-delimited response.");
                    int newline=Array.IndexOf(buffer,(byte)10,0,count);
                    int used=newline<0?count:newline;
                    if(response.Length+used>MaximumResponseBytes)throw new InvalidDataException("Response exceeds 16MiB.");
                    response.Write(buffer,0,used);
                    complete=newline>=0;
                    if(complete&&newline+1!=count)throw new InvalidDataException("Server returned more than one response line.");
                }
                byte[] bytes=response.ToArray();
                Utf8.GetString(bytes);
                using JsonDocument json=JsonDocument.Parse(bytes);
                if(json.RootElement.ValueKind!=JsonValueKind.Object||!json.RootElement.TryGetProperty("status",out var status)
                    ||status.ValueKind!=JsonValueKind.String)throw new InvalidDataException("Response must be a JSON object with a string status.");
                await output.WriteAsync(bytes);
                await output.WriteAsync(new byte[]{10});
                await output.FlushAsync();
                string result=status.GetString()!;Console.WriteLine(result+": "+path);
                if(operation=="clock-lighting"&&result=="ok")
                {
                    if(!json.RootElement.TryGetProperty("clock",out var clock)||!clock.TryGetProperty("lightingPath",out var lighting)||lighting.ValueKind!=JsonValueKind.String||
                        !Path.IsPathFullyQualified(lighting.GetString()!))throw new InvalidDataException("Successful clock-lighting reply omitted its absolute saved path.");
                    Console.WriteLine("lighting-path: "+lighting.GetString());
                }
                return result is "rejected" or "timeout" or "started" or "in-progress" or "cancelled-before-execution" or "failed" or "error"?2:0;
            }
            catch(Exception error)
            {
                if(output.Length==0)
                {
                    byte[] failure=JsonSerializer.SerializeToUtf8Bytes(new {schema="continuum-owned-runtime-control/v1",status="failed",port,command,error=error.Message});
                    await output.WriteAsync(failure);await output.WriteAsync(new byte[]{10});await output.FlushAsync();
                }
                throw;
            }
        }
        catch(Exception error){Console.Error.WriteLine(error.Message);return 2;}
    }
}
