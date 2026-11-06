using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using AuraZip.Core;

var repo = Path.GetFullPath(".");
var root = Path.Combine(repo,"artifacts","Performance-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(root);
var service = new ArchiveService(Path.Combine(repo,"AuraZip/Engine/x64/7z.exe"));
var results=new List<object>();
async Task Measure(string name,Func<Task> work)
{
    GC.Collect();GC.WaitForPendingFinalizers();long allocated=GC.GetTotalAllocatedBytes(true);var clock=Stopwatch.StartNew();
    await work();clock.Stop();
    var result=new {Name=name,Seconds=clock.Elapsed.TotalSeconds,AllocatedBytes=GC.GetTotalAllocatedBytes(true)-allocated};
    results.Add(result);Console.WriteLine(JsonSerializer.Serialize(result));
}
var items=Enumerable.Range(0,2000).SelectMany(d=>Enumerable.Range(0,20).Select(f=>new ArchiveItem($"dir-{d}/file-{f}.txt",false,1024))).ToArray();
var index=new ArchiveIndex(Path.Combine(root,"synthetic.zip"),items);
await Measure("40000 entries: browse 2000 folders",()=>{int sum=0;for(int d=0;d<2000;d++)sum+=index.Children("dir-"+d).Count();if(sum!=40000)throw new Exception("Bad folder index.");return Task.CompletedTask;});
await Measure("40000 entries: 100 folder selections",()=>{for(int n=0;n<100;n++)if(index.Scope(new[]{"dir-"+(n%2000)}).Count!=20)throw new Exception("Bad selection.");return Task.CompletedTask;});
var archive=Path.Combine(root,"large-first.zip");
var block=new byte[1024*1024];new Random(12).NextBytes(block);
using(var zip=ZipFile.Open(archive,ZipArchiveMode.Create))
{
    using(var stream=zip.CreateEntry("first.bin",CompressionLevel.Optimal).Open())for(int i=0;i<128;i++)stream.Write(block);
    using(var writer=new StreamWriter(zip.CreateEntry("last.txt").Open()))writer.Write("tail preview");
}
var opened=await service.OpenAsync(archive);
await Measure("128 MiB ZIP: last entry preview x5",async()=>{for(int i=0;i<5;i++){var bytes=await service.ReadPreviewAsync(archive,"last.txt",null);if(bytes.Length!=12)throw new Exception("Bad preview.");}});
using(var control=new OperationControl())
    await Measure("128 MiB ZIP: integrity check",()=>service.TestAsync(opened,null,control,new Sink(),default));
var many=Path.Combine(root,"many-files.zip");
using(var zip=ZipFile.Open(many,ZipArchiveMode.Create))for(int i=0;i<5000;i++){using var stream=zip.CreateEntry($"folder-{i%100}/file-{i}.txt").Open();stream.Write(block,0,64);}
await Measure("5000 files: archive open",async()=>{var idx=await service.OpenAsync(many);if(idx.Items.Count(i=>!i.IsDirectory)!=5000)throw new Exception("Bad archive count.");});
var manyIndex=await service.OpenAsync(many);
using(var control=new OperationControl())
    await Measure("5000 files: extraction",()=>service.ExtractAsync(manyIndex,Path.Combine(root,"many-output"),null,ConflictPolicy.Fail,null,control,new Sink(),default));
using(var control=new OperationControl())
    await Measure("5000 files: ZIP rename",()=>service.EditZipAsync(manyIndex,null,null,"folder-0/file-0.txt","renamed.txt",control,new Sink(),default));
File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
// Remove only generated large input archives. Keep small fixtures and measurements.
File.Delete(archive);
Console.WriteLine("Results="+Path.Combine(root,"results.json"));
sealed class Sink:IProgress<ArchiveProgress>{public void Report(ArchiveProgress p){}}
