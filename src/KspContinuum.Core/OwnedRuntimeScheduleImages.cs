using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace KspContinuum
{
    public sealed class OwnedRuntimeScheduleImage
    {
        public Assembly Assembly { get; private set; }
        public string Sha256 { get; private set; }
        public string AssemblyIdentity { get; private set; }
        public Guid ModuleVersionId { get; private set; }
        internal OwnedRuntimeScheduleImage(Assembly assembly,string digest,string identity)
        {Assembly=assembly;Sha256=digest;AssemblyIdentity=identity;ModuleVersionId=assembly.ManifestModule.ModuleVersionId;}
    }

    public static class OwnedRuntimeScheduleImages
    {
        const int MaximumImages=32,MaximumImageBytes=16*1024*1024;
        sealed class Entry { internal byte[] Bytes;internal OwnedRuntimeScheduleImage Image; }
        static readonly object Gate=new object();
        static readonly Dictionary<string,Entry> Images=new Dictionary<string,Entry>(StringComparer.OrdinalIgnoreCase);

        public static OwnedRuntimeScheduleImage Load(byte[] image)
        {
            if(image==null||image.Length==0||image.Length>MaximumImageBytes)throw new ArgumentException("Schedule image exceeds its cold byte bound.");
            var bytes=(byte[])image.Clone();string digest;
            using(var sha=SHA256.Create())digest=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
            string identity=ReadIdentity(bytes);
            lock(Gate)
            {
                Entry known;
                if(Images.TryGetValue(identity,out known))
                {
                    if(known.Image.Sha256!=digest||!Equal(bytes,known.Bytes))throw new InvalidOperationException("Changed schedule bytes reuse an already loaded assembly identity; build a fresh generation identity.");
                    RequireUniqueLoaded(identity,known.Image.Assembly);return known.Image;
                }
                RequireUniqueLoaded(identity,null);
                if(Images.Count==MaximumImages)throw new InvalidOperationException("Process schedule image registry is full; restart explicitly to reclaim loaded images.");
                var loaded=System.Reflection.Assembly.Load(bytes);
                if(!String.Equals(loaded.GetName().FullName,identity,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Runtime loaded a different schedule assembly identity.");
                RequireUniqueLoaded(identity,loaded);
                var admitted=new OwnedRuntimeScheduleImage(loaded,digest,identity);
                Images.Add(identity,new Entry {Bytes=bytes,Image=admitted});return admitted;
            }
        }

        static string ReadIdentity(byte[] bytes)
        {
            string path=Path.Combine(Path.GetTempPath(),"continuum-schedule-"+Guid.NewGuid().ToString("N")+".dll");
            Exception primary=null;
            try
            {
                using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))file.Write(bytes,0,bytes.Length);
                var name=AssemblyName.GetAssemblyName(path);
                if(String.IsNullOrEmpty(name.Name)||String.IsNullOrEmpty(name.FullName))throw new ArgumentException("Schedule has no full managed assembly identity.");
                return name.FullName;
            }
            catch(Exception error){primary=error;throw;}
            finally
            {
                try{if(File.Exists(path))File.Delete(path);}
                catch(Exception cleanup)
                {if(primary!=null)primary.Data["ScheduleMetadataSnapshotCleanupFailure"]=cleanup.ToString();else throw new IOException("Schedule metadata snapshot cleanup failed; executable image was not admitted.",cleanup);}
            }
        }
        static void RequireUniqueLoaded(string identity,Assembly expected)
        {
            int matches=0;
            foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if(String.Equals(assembly.GetName().FullName,identity,StringComparison.OrdinalIgnoreCase))
                {matches++;if(!ReferenceEquals(assembly,expected))throw new InvalidOperationException("Schedule identity is already loaded outside the attested image registry.");}
            if(expected!=null&&matches!=1)throw new InvalidOperationException("Attested schedule assembly is no longer uniquely loaded.");
        }
        static bool Equal(byte[] a,byte[] b)
        {if(a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
    }
}
