using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileMapper.Collect.FileSystem;

public sealed record NativeMetadata(string? StableId, long? AllocatedBytes, long LinkCount, string? Owner,
    int? ErrorCode, string? ErrorDetail, string? SecurityDescriptor = null);

/// <summary>Documented user-mode Windows APIs; unknown allocation stays null.</summary>
public static class FileIdentityReader
{
    public static NativeMetadata Read(string path,bool reparsePoint=false)
    {
        if(!OperatingSystem.IsWindows()) return new(null,null,1,null,null,"Native identity and allocation are unavailable on this platform.");
        var nativePath=NativePath(path);
        var flags=0x02000000u | (reparsePoint ? 0x00200000u : 0u);
        using var handle=CreateFileW(nativePath,0x00020000,7,IntPtr.Zero,3,flags,IntPtr.Zero);
        if(handle.IsInvalid)
        {
            using var fallback=CreateFileW(nativePath,0,7,IntPtr.Zero,3,flags,IntPtr.Zero);
            if(fallback.IsInvalid) { var code=Marshal.GetLastWin32Error();return new(null,null,1,null,code,new Win32Exception(code).Message); }
            return ReadHandle(fallback);
        }
        return ReadHandle(handle);
    }

    private static NativeMetadata ReadHandle(SafeFileHandle handle)
    {
        if(!GetFileInformationByHandle(handle,out var info)) { var code=Marshal.GetLastWin32Error();return new(null,null,1,null,code,new Win32Exception(code).Message); }
        var stable=$"ntfs:{info.VolumeSerialNumber:x8}:{info.FileIndexHigh:x8}{info.FileIndexLow:x8}";
        long? allocation=null;
        int? error=null;
        string? detail=null;
        // Standard allocation can describe the uncompressed reservation. Sparse/compressed
        // streams require FileCompressionInfo on this same handle, preserving identity binding.
        if((info.Attributes & (0x200u | 0x800u)) != 0 && (info.Attributes & 0x10u) == 0)
        {
            if(GetFileInformationByHandleEx(handle,8,out FileCompressionInfo compressed,(uint)Marshal.SizeOf<FileCompressionInfo>())) allocation=compressed.CompressedFileSize;
            else { error=Marshal.GetLastWin32Error();detail=new Win32Exception(error.Value).Message; }
        }
        else if(GetFileInformationByHandleEx(handle,1,out FileStandardInfo standard,(uint)Marshal.SizeOf<FileStandardInfo>())) allocation=standard.AllocationSize;
        else { error=Marshal.GetLastWin32Error();detail=new Win32Exception(error.Value).Message; }
        string? owner=null;
        string? securityDescriptor=null;
        var result=GetSecurityInfo(handle,1,7,out var sid,out _,out _,out _,out var descriptor);
        if(result==0)
        {
            try
            {
                if(ConvertSidToStringSidW(sid,out var sidString))
                {
                    try {owner=Marshal.PtrToStringUni(sidString);} finally { LocalFree(sidString); }
                }
                if(ConvertSecurityDescriptorToStringSecurityDescriptorW(descriptor,1,7,out var descriptorString,out _))
                {
                    try {securityDescriptor=Marshal.PtrToStringUni(descriptorString);} finally { LocalFree(descriptorString); }
                }
            }
            finally { LocalFree(descriptor); }
        }
        return new(stable,allocation,info.NumberOfLinks,owner,error,detail,securityDescriptor);
    }

    public static IReadOnlyList<(string Name,long Size)> GetStreams(string path)
    {
        if(!OperatingSystem.IsWindows()) return [];
        var result=new List<(string,long)>();
        var handle=FindFirstStreamW(NativePath(path),0,out var data,0);
        if(handle==new IntPtr(-1))
        {
            var error=Marshal.GetLastWin32Error();
            if(error is 38 or 87 or 1 or 50) return result;
            throw new Win32Exception(error);
        }
        try
        {
            do
            {
                if(data.StreamName!="::$DATA") result.Add((data.StreamName,data.StreamSize));
            } while(FindNextStreamW(handle,out data));
            var error=Marshal.GetLastWin32Error();
            if(error!=38) throw new Win32Exception(error);
        }
        finally { FindClose(handle); }
        return result;
    }

    // Managed enumeration supports long paths even when the Win32 process policy
    // does not. Bind metadata and stream enumeration to the same extended path.
    private static string NativePath(string path)
    {
        if(path.StartsWith(@"\\?\",StringComparison.Ordinal) || path.StartsWith(@"\\.\",StringComparison.Ordinal)) return path;
        var full=Path.GetFullPath(path);
        return full.StartsWith(@"\\",StringComparison.Ordinal) ? @"\\?\UNC\"+full[2..] : @"\\?\"+full;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime,LastAccessTime,LastWriteTime;
        public uint VolumeSerialNumber,FileSizeHigh,FileSizeLow,NumberOfLinks,FileIndexHigh,FileIndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        public long AllocationSize,EndOfFile;public uint NumberOfLinks;
        [MarshalAs(UnmanagedType.U1)] public bool DeletePending;
        [MarshalAs(UnmanagedType.U1)] public bool Directory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileCompressionInfo
    {
        public long CompressedFileSize;
        public ushort CompressionFormat;
        public byte CompressionUnitShift,ChunkShift,ClusterShift,Reserved0,Reserved1,Reserved2;
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct StreamData
    {
        public long StreamSize;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=296)] public string StreamName;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInformation info);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int kind,out FileStandardInfo info,uint size);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int kind,out FileCompressionInfo info,uint size);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafeFileHandle handle,int objectType,uint securityInfo,out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertSidToStringSidW(IntPtr sid,out IntPtr value);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(IntPtr descriptor,uint revision,uint information,out IntPtr value,out uint length);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr FindFirstStreamW(string path,int infoLevel,out StreamData data,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool FindNextStreamW(IntPtr handle,out StreamData data);
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool FindClose(IntPtr handle);
}
