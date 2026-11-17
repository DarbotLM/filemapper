using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace FileMapper.Service;

/// <summary>Role-specific, per-user local credentials. No executor credential is created.</summary>
public sealed class LocalCredentials
{
    private readonly List<(byte[] Secret, string Role, string[] Capabilities)> credentials = [];
    public LocalCredentials(string directory)
    {
        EnsurePrivateDirectory(directory);
        Add("service.token", "operator", ["read", "scan", "plan", "learn"]);
        Add("reader.token", "reader", ["read"]);
        Add("scanner.token", "scanner", ["read", "scan"]);
        Add("planner.token", "planner", ["read", "plan"]);
        void Add(string name, string role, string[] capabilities)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Credential files must not be reparse points.");
            if (File.Exists(path))
            {
                if (OperatingSystem.IsWindows())
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    EnsurePrivateWindowsAccess(new FileInfo(path).GetAccessControl(AccessControlSections.Access), identity.User!,
                        "Existing credential is accessible to another identity. Replace this credential with a new private token before starting the service: " + name);
                }
                else if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
                    throw new InvalidOperationException("Existing credential is accessible to another identity. Replace it with a new private token: " + name);
            }
            if (!File.Exists(path))
            {
                try
                {
                    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    using var file = new FileStream(path, options);
                    file.Write(Encoding.ASCII.GetBytes(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))));
                    file.Flush(true);
                }
                catch (IOException) when (File.Exists(path)) { /* Another local process created the credential. */ }
            }
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var security = new FileSecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(security);
            }
            else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var token = File.ReadAllText(path).Trim();
            if (token.Length != 64 || !token.All(Uri.IsHexDigit)) throw new InvalidOperationException("Credential must be a 32-byte hexadecimal token: " + name);
            var secret = Encoding.ASCII.GetBytes("Bearer " + token);
            if (credentials.Any(c => CryptographicOperations.FixedTimeEquals(secret, c.Secret))) throw new InvalidOperationException("Credentials for separate roles must be distinct.");
            credentials.Add((secret, role, capabilities));
        }
    }
    internal static void EnsurePrivateDirectory(string directory)
    {
        for (string? current = Path.GetFullPath(directory); current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The service data directory must not traverse a reparse point.");
        }
        if (OperatingSystem.IsWindows())
        {
            using var current = WindowsIdentity.GetCurrent();
            var user = current.User!;
            var info = new DirectoryInfo(directory);
            if (!info.Exists)
            {
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                info.Create(security);
            }
            EnsurePrivateWindowsAccess(info.GetAccessControl(AccessControlSections.Access), user,
                "Service data directory is accessible to another identity. Choose a new private directory or use the per-user installer.");
        }
        else
        {
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if ((File.GetUnixFileMode(directory) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new InvalidOperationException("Service data directory must be accessible only to its owner (0700).");
        }
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void EnsurePrivateWindowsAccess(FileSystemSecurity security, SecurityIdentifier user, string error)
    {
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (rule.AccessControlType == AccessControlType.Allow && sid != user &&
                !sid.IsWellKnown(WellKnownSidType.LocalSystemSid) && !sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
                throw new InvalidOperationException(error);
        }
    }
    public AccessSession? Authenticate(string authorization)
    {
        var candidate = Encoding.UTF8.GetBytes(authorization);
        AccessSession? session = null;
        foreach (var credential in credentials)
            if (CryptographicOperations.FixedTimeEquals(candidate, credential.Secret)) session = new(credential.Role, credential.Capabilities);
        return session;
    }
}
public sealed record AccessSession(string Role, IReadOnlyList<string> Capabilities);
