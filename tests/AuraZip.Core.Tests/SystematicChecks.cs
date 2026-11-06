using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AuraZip.Core;

internal static class SystematicChecks
{
    public static async Task Run(ArchiveService service, string root, Action<bool,string> check, Func<Func<Task>,string,Task> reject)
    {
        var clock = Stopwatch.StartNew();
        using var control = new OperationControl();var progress = new Sink();
        var random = new Random(94137);
        var expected = new Dictionary<string, byte[]> { ["empty.bin"] = [], ["folder/中文 spaced.txt"] = Encoding.UTF8.GetBytes("archive roundtrip 中文"), ["folder/nested/random.bin"] = new byte[32769] };
        random.NextBytes(expected["folder/nested/random.bin"]);
        string Zip(string name, IEnumerable<KeyValuePair<string,byte[]>> entries, CompressionLevel level = CompressionLevel.Optimal)
        {
            var path=Path.Combine(root,name);
            using var zip=ZipFile.Open(path, ZipArchiveMode.Create);
            foreach(var (key, data) in entries){using var stream=zip.CreateEntry(key,level).Open();stream.Write(data);}
            return path;
        }
        for(int seed=0;seed<12;seed++)
        {
            var entries = expected.Concat(Enumerable.Range(0,seed+1).Select(n => new KeyValuePair<string,byte[]>($"seed-{seed}/file-{n}.bin", RandomNumberGenerator.GetBytes(seed*17+n)))).ToDictionary();
            var path=Zip($"seed-{seed}.zip",entries);
            var index=await service.OpenAsync(path);var target=Path.Combine(root,$"seed-{seed}-out");
            await service.ExtractAsync(index,target,null,ConflictPolicy.Fail,null,control,progress,default);
            await service.TestAsync(index,null,control,progress,default);
            check(entries.All(e => File.ReadAllBytes(Path.Combine(target,e.Key)).SequenceEqual(e.Value)), "random ZIP roundtrip and checksum seed="+seed);
            var selected=entries.Keys.Last();var selectedTarget=Path.Combine(root,$"seed-{seed}-selected");
            await service.ExtractAsync(index,selectedTarget,new[]{selected},ConflictPolicy.Fail,null,control,progress,default);
            check(Directory.GetFiles(selectedTarget,"*",SearchOption.AllDirectories).Length==1, "random selected extraction seed="+seed);
            check((await service.ReadPreviewAsync(path,selected,null)).SequenceEqual(entries[selected]), "random preview seed="+seed);
        }
        var zeroPath=Zip("zero.zip",new Dictionary<string,byte[]>{["empty.txt"]=[]});
        var zero=await service.OpenAsync(zeroPath);
        var zeroProgress=new Sink();await service.ExtractAsync(zero,Path.Combine(root,"zero-output"),null,ConflictPolicy.Fail,null,control,zeroProgress,default);
        check(zeroProgress.Last is {FilesDone:1,TotalFiles:1,BytesDone:0},"zero-byte extraction finishes its file count");
        var damaged=Zip("damaged-preview.zip",new Dictionary<string,byte[]>{["preview.txt"]=Encoding.ASCII.GetBytes("distinct preview bytes")},CompressionLevel.NoCompression);
        var damage=File.ReadAllBytes(damaged);damage[damage.AsSpan().IndexOf("distinct preview bytes"u8)]^=1;File.WriteAllBytes(damaged,damage);
        await reject(()=>service.ReadPreviewAsync(damaged,"preview.txt",null),"plain ZIP preview rejects CRC corruption");
        using(var cancel=new CancellationTokenSource())
        {
            var path=Zip("cancel-multi.zip",expected);var index=await service.OpenAsync(path);
            cancel.Cancel();var target=Path.Combine(root,"cancel-before-output");
            await reject(()=>service.ExtractAsync(index,target,null,ConflictPolicy.Fail,null,control,progress,cancel.Token),"cancel before extraction stops immediately");
            check(!Directory.Exists(target),"cancelled extraction creates no output directory");
        }
        var indexItems=new[] { new ArchiveItem("folder",true),new ArchiveItem("folder/nested",true),new ArchiveItem("folder/nested/a.txt",false),new ArchiveItem("folder/b.txt",false),new ArchiveItem("other.txt",false) };
        var synthetic=new ArchiveIndex(Path.Combine(root,"scope.zip"),indexItems);
        check(synthetic.Scope(new[]{"folder","folder/nested","folder/b.txt"}).SequenceEqual(indexItems.Take(4)),"overlapping selections include descendants once in archive order");
        check(synthetic.Scope(new[]{"folder/nested/a.txt"}).SequenceEqual(indexItems.Skip(2).Take(1)),"single-file scope excludes siblings");
        check(synthetic.Scope(new[]{"missing"}).Count==0 && synthetic.Scope(Array.Empty<string>()).Count==0,"unknown and empty selections stay empty");
        check(synthetic.Children("folder").Select(i=>i.Key).SequenceEqual(new[]{"folder/nested","folder/b.txt"}),"indexed folder lookup returns direct children only");
        var parents=Enumerable.Range(0,32).Select(async _=>{await Task.Yield();return synthetic.Children("folder").Count();});
        check((await Task.WhenAll(parents)).All(n=>n==2),"directory lookup can be shared by concurrent readers");
        var replaced=synthetic with {Items=new[]{new ArchiveItem("fresh.txt",false)}};
        check(replaced.Children("").Single().Key=="fresh.txt" && replaced.Scope(new[]{"folder"}).Count==0,"record copies with replacement entries do not reuse a stale index");
        var selectedParent=new[] {new ArchiveItem("hidden/child.txt",false)};
        check(new ArchiveIndex("synthetic.zip",selectedParent).Scope(new[]{"hidden"}).Count==1,"selection includes children of implicit directory keys");
        var largePath=Zip("large-preview.zip",new Dictionary<string,byte[]>{["large.bin"]=new byte[20000]});
        await reject(()=>service.ReadPreviewAsync(largePath,"large.bin",null,limit:1024),"preview byte limits reject oversized entries");
        var zeroCrc=Zip("zero-crc-preview.zip",new Dictionary<string,byte[]>{["data.txt"]=Encoding.ASCII.GetBytes("CRC header tampering")});
        var crcData=File.ReadAllBytes(zeroCrc);
        // Both ZIP records claim CRC zero. A nonempty corrupt record must still be checked.
        Array.Clear(crcData,14,4);
        for(int n=0;n<crcData.Length-46;n++)if(crcData[n]=='P'&&crcData[n+1]=='K'&&crcData[n+2]==1&&crcData[n+3]==2)Array.Clear(crcData,n+16,4);
        File.WriteAllBytes(zeroCrc,crcData);
        await reject(()=>service.ReadPreviewAsync(zeroCrc,"data.txt",null),"ZIP previews validate a zero stored CRC instead of treating it as absent");
        var rawPayload=Encoding.UTF8.GetBytes("Raw compressed stream 中文\n");
        var rawGzip=Path.Combine(root,"raw.txt.gz");
        using(var stream=new GZipStream(File.Create(rawGzip),CompressionLevel.Optimal))stream.Write(rawPayload);
        var rawIndex=await service.OpenAsync(rawGzip);
        check(rawIndex.Items.Count(i=>!i.IsDirectory)==1,"GZip without an embedded filename exposes its payload");
        var rawKey=rawIndex.Items.Single(i=>!i.IsDirectory).Key;
        var rawOutput=Path.Combine(root,"raw-gzip-out");
        await service.ExtractAsync(rawIndex,rawOutput,null,ConflictPolicy.Fail,null,control,progress,default);
        check(File.ReadAllBytes(Path.Combine(rawOutput,rawKey)).SequenceEqual(rawPayload),"standalone GZip payload roundtrip");
        check((await service.ReadPreviewAsync(rawGzip,rawKey,null)).SequenceEqual(rawPayload),"standalone GZip preview uses a synthesized filename");
        await service.TestAsync(rawIndex,null,control,progress,default);
        var rawSource=Path.Combine(root,"single.txt");File.WriteAllBytes(rawSource,rawPayload);
        foreach(var codec in new[]{"bzip2","xz"})
        {
            var suffix=codec=="bzip2"?"bz2":"xz";
            var rawPath=Path.Combine(root,"single.txt."+suffix);
            var start=new ProcessStartInfo(Path.GetFullPath("AuraZip/Engine/x64/7z.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true};
            foreach(var arg in new[]{"a","-t"+codec,rawPath,rawSource})start.ArgumentList.Add(arg);
            using(var process=Process.Start(start)!){await process.StandardOutput.ReadToEndAsync();await process.WaitForExitAsync();check(process.ExitCode==0,"standalone fixture creation: "+codec);}
            var raw=await service.OpenAsync(rawPath);var item=raw.Items.Single(i=>!i.IsDirectory);
            var output=Path.Combine(root,"raw-"+suffix+"-out");
            var streamProgress=new Sink();
            await service.ExtractAsync(raw,output,null,ConflictPolicy.Fail,null,control,streamProgress,default);
            await service.TestAsync(raw,null,control,progress,default);
            check(File.ReadAllBytes(Path.Combine(output,item.Key)).SequenceEqual(rawPayload),"standalone compressed stream extraction: "+codec);
            check(streamProgress.Last?.BytesDone==rawPayload.Length,"standalone completion reports actual extracted bytes: "+codec);
            check((await service.ReadPreviewAsync(rawPath,item.Key,null)).SequenceEqual(rawPayload),"standalone compressed stream preview: "+codec);
            await reject(()=>service.ReadPreviewAsync(rawPath,item.Key,null,limit:4),"standalone preview enforces byte limit: "+codec);
        }
        var brokenGzip=Path.Combine(root,"broken.txt.gz");var gzData=File.ReadAllBytes(rawGzip);gzData[^8]^=1;File.WriteAllBytes(brokenGzip,gzData);
        var brokenIndex=await service.OpenAsync(brokenGzip);
        await reject(()=>service.TestAsync(brokenIndex,null,control,progress,default),"standalone GZip validates its footer CRC");
        await reject(()=>service.ReadPreviewAsync(brokenGzip,brokenIndex.Items[0].Key,null),"standalone GZip preview rejects CRC corruption");
        Console.WriteLine($"Systematic boundary checks: {clock.Elapsed.TotalSeconds:F3}s");
    }
    private sealed class Sink : IProgress<ArchiveProgress>{public ArchiveProgress? Last;public void Report(ArchiveProgress p)=>Last=p;}
}
