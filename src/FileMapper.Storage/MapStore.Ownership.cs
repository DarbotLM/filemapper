using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileMapper.Storage;

public sealed partial class MapStore
{
    /// <summary>Normal writers hold admission for their lifetime, so maintenance cannot race backup and rollback.</summary>
    public IDisposable AcquireWriter(bool allowInstallerSession=false)
    {
        RequireWritable();
        FileStream? admission=null;
        var installerBypass=false;
        try
        {
            try {admission=AcquireMaintenanceAdmission();}
            catch(InvalidOperationException) when(allowInstallerSession && ValidateInstallerSession()) {installerBypass=true;}
            var writer=new FileStream(DatabasePath+".writer.lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            if(installerBypass && !ValidateInstallerSession())
            {
                writer.Dispose();throw new InvalidOperationException("Installer ownership ended before the database writer was acquired.");
            }
            return new WriterLease(writer,admission);
        }
        catch(IOException ex)
        {
            admission?.Dispose();throw new InvalidOperationException("Another FileMapper process owns the database writer. Pause or wait for its job.",ex);
        }
        catch {admission?.Dispose();throw;}
    }

    private FileStream AcquireMaintenanceAdmission()
    {
        RequireWritable();
        try {return new FileStream(DatabasePath+".maintenance.lock",FileMode.OpenOrCreate,FileAccess.Read,FileShare.Read);}
        catch(IOException ex) {throw new InvalidOperationException("FileMapper installation maintenance is active. Retry after activation or rollback finishes.",ex);}
    }

    // Constructor token is supplied only by an explicit installer-session startup. It is never consulted by ordinary jobs or controls.
    private bool ValidateInstallerSession()
    {
        if(string.IsNullOrEmpty(installerToken) || installerToken.Length<32 || installerToken.Length>256)return false;
        try
        {
            using var file=new FileStream(DatabasePath+".maintenance.json",FileMode.Open,FileAccess.Read,FileShare.Read);
            if(file.Length>8192)return false;
            using var json=JsonDocument.Parse(file,new JsonDocumentOptions{MaxDepth=8});var root=json.RootElement;
            var recordedDatabase=root.GetProperty("databasePath").GetString();
            if(recordedDatabase is null || !string.Equals(Path.GetFullPath(recordedDatabase),DatabasePath,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))return false;
            var expected=Convert.FromHexString(root.GetProperty("tokenHash").GetString()??"");
            var actual=SHA256.HashData(Encoding.UTF8.GetBytes(installerToken));
            if(!CryptographicOperations.FixedTimeEquals(expected,actual))return false;
            using var owner=Process.GetProcessById(root.GetProperty("ownerProcessId").GetInt32());
            if(owner.HasExited||owner.StartTime.ToUniversalTime().Ticks!=root.GetProperty("ownerStartTimeUtcTicks").GetInt64())return false;
            // A stale marker or token cannot bypass anything after the installer released its exclusive barrier.
            try {using var unlocked=AcquireMaintenanceAdmission();return false;}
            catch(InvalidOperationException) {return true;}
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException or System.ComponentModel.Win32Exception)
        {return false;}
    }

    private sealed class WriterLease(FileStream writer,FileStream? admission):IDisposable
    {
        public void Dispose() {try{writer.Dispose();}finally{admission?.Dispose();}}
    }
}
