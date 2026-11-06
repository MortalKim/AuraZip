using System.Globalization;

namespace AuraZip.Core;

public sealed partial class ArchiveService
{
    private static bool IsCompressedStream(Stream source)
    {
        Span<byte> bytes = stackalloc byte[6];long position=source.Position;
        int read=source.Read(bytes);source.Position=position;
        return (read>=2 && bytes[0]==0x1f && bytes[1]==0x8b)
            || (read>=3 && bytes[0]=='B' && bytes[1]=='Z' && bytes[2]=='h')
            || (read==6 && bytes.SequenceEqual(new byte[]{0xfd,0x37,0x7a,0x58,0x5a,0x00}));
    }

    private ArchiveIndex ReadSingleStreamIndex(string path, CancellationToken token, int? codePage)
    {
        using var process=StartReadProcess(new[]{"l","-slt","-sccUTF-8","-p-","--",path});
        using var cancellation=token.Register(()=>KillReadProcess(process));
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        process.WaitForExit();var text=stdout.GetAwaiter().GetResult();var error=stderr.GetAwaiter().GetResult();token.ThrowIfCancellationRequested();
        if(process.ExitCode!=0)ThrowReadError(error,process.ExitCode);
        var fields=new Dictionary<string,string>(StringComparer.Ordinal);bool entry=false;
        foreach(var line in text.Split('\n'))
        {
            if(line.Trim()=="----------"){entry=true;continue;}
            if(!entry)continue;
            int equals=line.IndexOf(" = ",StringComparison.Ordinal);
            if(equals>=0)fields[line[..equals]]=line[(equals+3)..].TrimEnd('\r');
        }
        var key=fields.GetValueOrDefault("Path");
        if(string.IsNullOrEmpty(key))key=Path.GetFileNameWithoutExtension(path);
        key=Normalize(key);
        bool known=long.TryParse(fields.GetValueOrDefault("Size"),out long size);
        uint? crc=uint.TryParse(fields.GetValueOrDefault("CRC"),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out uint crcValue)?crcValue:null;
        var item=new ArchiveItem(key,false,size,new FileInfo(path).Length,expectedCrc:crc,isSizeKnown:known);
        return new(Path.GetFullPath(path),new[]{item},codePage,IsSingleStream:true);
    }
}
