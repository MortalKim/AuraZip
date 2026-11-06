using AuraZip.Core;
using System.Security.Cryptography;
using System.Text;

internal static class StreamCreationChecks
{
    public static async Task Run(ArchiveService service, string parent, Action<bool,string> check, Func<Func<Task>,string,Task> reject)
    {
        var root=Path.Combine(parent,"stream-creation");Directory.CreateDirectory(root);
        var source=Path.Combine(root,"资料.txt");var bytes=Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Stream creation 中文\n",100)));File.WriteAllBytes(source,bytes);
        using var control=new OperationControl();var progress=new Sink();
        foreach(var format in new[]{"gz","bz2","xz"})
        {
            foreach(var level in new[]{0,1,5,9})
            {
                var destination=Path.Combine(root,$"level-{level}.txt.{format}");
                var created=await service.CreateArchiveAsync(destination,new[]{source},new(format,Level:level),control,progress,default);
                var index=await service.OpenAsync(destination);var item=index.Items.Single(i=>!i.IsDirectory);
                await service.TestAsync(index,null,control,progress,default);
                check(created.Files.Count==1 && created.OriginalBytes==bytes.Length && created.PackedBytes==new FileInfo(destination).Length,"standalone creation result: "+format+" level="+level);
                check((await service.ReadPreviewAsync(destination,item.Key,null)).SequenceEqual(bytes),"standalone creation preview roundtrip: "+format+" level="+level);
                var output=Path.Combine(root,$"{format}-{level}-out");
                await service.ExtractAsync(index,output,null,ConflictPolicy.Fail,null,control,progress,default);
                check(File.ReadAllBytes(Path.Combine(output,item.Key)).SequenceEqual(bytes),"standalone creation extraction roundtrip: "+format+" level="+level);
            }
            await reject(()=>service.CreateArchiveAsync(Path.Combine(root,"folder."+format),new[]{root},new(format),control,progress,default),"standalone format rejects folder input: "+format);
            await reject(()=>service.CreateArchiveAsync(Path.Combine(root,"split."+format),new[]{source},new(format,VolumeBytes:1024),control,progress,default),"standalone format rejects unsupported splitting: "+format);
            await reject(()=>service.CreateArchiveAsync(source,new[]{source},new(format),control,progress,default,overwrite:true),"standalone output cannot overwrite its input: "+format);
            check(File.ReadAllBytes(source).SequenceEqual(bytes),"rejected standalone overwrite preserves the source: "+format);
        }
        var custom=Path.Combine(root,"inputs.txt");
        await service.CreateArchiveAsync(custom,new[]{source},new("gz"),control,progress,default);
        await service.TestAsync(await service.OpenAsync(custom),null,control,progress,default);
        check(new FileInfo(custom).Length>0,"custom output name cannot collide with staging metadata");
        var random=Path.Combine(root,"random.bin");File.WriteAllBytes(random,RandomNumberGenerator.GetBytes(100000));
        var split=Path.Combine(root,"protected.zip");var orphan=split+".002";byte[] marker=[1,2,3,4];File.WriteAllBytes(orphan,marker);
        await reject(()=>service.CreateArchiveAsync(split,new[]{random},new("zip",VolumeBytes:8192),control,progress,default),"an orphan output volume blocks publishing the new volume set");
        check(!File.Exists(split+".001") && File.ReadAllBytes(orphan).SequenceEqual(marker),"volume conflict leaves existing data and no partial output");
        await reject(()=>service.CreateArchiveAsync(split,new[]{random},new("zip",VolumeBytes:8192),control,progress,default,overwrite:true),"split overwrite is rejected before replacing any volume");
        File.WriteAllBytes(split+".001",marker);
        await reject(()=>service.CreateArchiveAsync(split,new[]{random},new("zip"),control,progress,default,overwrite:true),"single output cannot overwrite an existing split series");
        check(File.ReadAllBytes(split+".001").SequenceEqual(marker) && File.ReadAllBytes(orphan).SequenceEqual(marker) && !File.Exists(split),"split overwrite guards preserve all existing volumes");
        check(!Directory.GetDirectories(root,".aurazip-*.tmp").Any(),"stream and failed split creation remove their temporary directories");
        foreach(var format in new[]{"zip","7z","tar","tar.gz"})
        {
            await reject(()=>service.CreateArchiveAsync(source,new[]{source},new(format),control,progress,default,overwrite:true),"archive output cannot overwrite an explicitly selected source: "+format);
            check(File.ReadAllBytes(source).SequenceEqual(bytes),"self-overwrite guard preserves original input bytes: "+format);
        }
    }
    private sealed class Sink:IProgress<ArchiveProgress>{public void Report(ArchiveProgress p){}}
}
