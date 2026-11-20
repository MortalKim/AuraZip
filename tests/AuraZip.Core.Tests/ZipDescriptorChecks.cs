using AuraZip.Core;
using System.Text;

internal static class ZipDescriptorChecks
{
    public static async Task Run(string root, Action<bool,string> check, Func<Func<Task>,string,Task> reject)
    {
        // Synthetic encrypted ZIP with zero local sizes and a trailing data descriptor.
        // Do not use personal documents as regression fixtures.
        var name = Encoding.UTF8.GetBytes("测试.txt");
        var data = Encoding.UTF8.GetBytes("encrypted descriptor regression");
        uint Crc(uint crc, byte value) { crc ^= value; for(int i=0;i<8;i++) crc=(crc&1)!=0 ? (crc>>1)^0xedb88320u : crc>>1; return crc; }
        uint checksum=uint.MaxValue; foreach(var b in data) checksum=Crc(checksum,b); checksum=~checksum;
        uint key0=0x12345678,key1=0x23456789,key2=0x34567890;
        void Update(byte b) { key0=Crc(key0,b);key1=unchecked((key1+(byte)key0)*134775813+1);key2=Crc(key2,(byte)(key1>>24)); }
        foreach(var b in Encoding.UTF8.GetBytes("fixture-password")) Update(b);
        var plain=new byte[12+data.Length];data.CopyTo(plain,12); // DOS time is zero, including the password check byte.
        var encrypted=new byte[plain.Length];
        for(int i=0;i<plain.Length;i++){uint temp=key2|2;encrypted[i]=(byte)(plain[i]^((temp*(temp^1))>>8));Update(plain[i]);}
        var path=Path.Combine(root,"encrypted-descriptor.zip");
        using(var stream=File.Create(path))
        using(var w=new BinaryWriter(stream))
        {
            w.Write(0x04034b50u);w.Write((ushort)20);w.Write((ushort)0x809);w.Write((ushort)0);w.Write(0u);w.Write(0u);w.Write(0u);w.Write(0u);w.Write((ushort)name.Length);w.Write((ushort)0);w.Write(name);w.Write(encrypted);
            w.Write(0x08074b50u);w.Write(checksum);w.Write((uint)encrypted.Length);w.Write((uint)data.Length);
            uint offset=(uint)stream.Position;
            w.Write(0x02014b50u);w.Write((ushort)20);w.Write((ushort)20);w.Write((ushort)0x809);w.Write((ushort)0);w.Write(0u);w.Write(checksum);w.Write((uint)encrypted.Length);w.Write((uint)data.Length);w.Write((ushort)name.Length);w.Write((ushort)0);w.Write((ushort)0);w.Write((ushort)0);w.Write((ushort)0);w.Write(0u);w.Write(0u);w.Write(name);
            uint length=(uint)stream.Position-offset;
            w.Write(0x06054b50u);w.Write((ushort)0);w.Write((ushort)0);w.Write((ushort)1);w.Write((ushort)1);w.Write(length);w.Write(offset);w.Write((ushort)0);
        }
        var service=new ArchiveService(Path.GetFullPath("AuraZip/Engine/x64/7z.exe"));
        var index=await service.OpenAsync(path);
        check(index.Items.Count==1 && index.Items[0].IsEncrypted,"encrypted descriptor ZIP lists without a password");
        check((await service.ReadPreviewAsync(path,"测试.txt","fixture-password")).SequenceEqual(data),"encrypted descriptor ZIP reads with correct password");
        await reject(async()=>{await service.ReadPreviewAsync(path,"测试.txt","wrong");},"encrypted descriptor ZIP rejects wrong password");
        using var control=new OperationControl();var sink=new DirectProgress();
        await service.TestAsync(index,"fixture-password",control,sink,default);
        check(true,"encrypted descriptor ZIP integrity check succeeds");
        var output=Path.Combine(root,"descriptor-output");
        await service.ExtractAsync(index,output,null,ConflictPolicy.Fail,"fixture-password",control,sink,default);
        check(File.ReadAllBytes(Path.Combine(output,"测试.txt")).SequenceEqual(data),"encrypted descriptor ZIP extracts original bytes");
    }
}
