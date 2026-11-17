using Microsoft.Data.Sqlite;

namespace FileMapper.Storage;

public sealed partial class MapStore
{
    /// <summary>SQLite online backup includes committed WAL data and provides one consistent database image.</summary>
    public void BackupTo(string destination)
    {
        destination = Path.GetFullPath(destination);
        if (string.Equals(DatabasePath, destination, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Backup destination must differ from the live database.");
        if (File.Exists(destination)) throw new IOException("Backup destination already exists; backups are never overwritten.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var source = OpenExisting(DatabasePath);
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        target.Open(); source.BackupDatabase(target);
        ValidateDatabase(target);
    }
    /// <summary>Restore requires exclusive FileMapper writer ownership and uses SQLite's atomic backup transaction.</summary>
    public void RestoreFrom(string backup)
    {
        backup = Path.GetFullPath(backup);
        if (string.Equals(DatabasePath, backup, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Restore source must differ from the live database.");
        using var ownership = AcquireWriter(allowInstallerSession:true);
        using var source = OpenExisting(backup); ValidateDatabase(source);
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false, DefaultTimeout = 30 }.ToString());
        target.Open(); source.BackupDatabase(target); ValidateDatabase(target);
    }
    private static SqliteConnection OpenExisting(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Database file not found.", path);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 30 }.ToString());
        connection.Open(); return connection;
    }
    private static void ValidateDatabase(SqliteConnection connection)
    {
        using var check = connection.CreateCommand(); check.CommandText = "PRAGMA quick_check;";
        if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.Ordinal)) throw new InvalidDataException("SQLite integrity check failed.");
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('snapshots','nodes','entries','observations','schema_versions')";
        if (Convert.ToInt64(check.ExecuteScalar()) != 5) throw new InvalidDataException("Backup is not a FileMapper database.");
    }
}
