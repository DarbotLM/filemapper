using System;
using System.IO;
namespace FileMapper.Release;
public static class ByteComparison
{
    public static bool Equal(string left,string right)
    {
        using var a=File.OpenRead(left);using var b=File.OpenRead(right);
        if(a.Length!=b.Length)return false;
        var first=new byte[65536];var second=new byte[65536];int count;
        while((count=a.Read(first,0,first.Length))!=0)
        {
            b.ReadExactly(second,0,count);
            if(!first.AsSpan(0,count).SequenceEqual(second.AsSpan(0,count)))return false;
        }
        return b.ReadByte()==-1;
    }
}
