using System.Collections.ObjectModel;

namespace FileMapper.Semantics;

public readonly record struct Voxel(int X, int Y, int Z);

/// <summary>The nine supplied Oasis geometries; poses and colors are presentation, not identity.</summary>
public static class ShapeCatalog
{
    private static readonly IReadOnlyDictionary<string, Voxel[]> Canonical = new ReadOnlyDictionary<string, Voxel[]>(
        new Dictionary<string, Voxel[]>(StringComparer.Ordinal)
        {
            ["I"] = [new(0,0,0), new(1,0,0), new(2,0,0), new(3,0,0)],
            ["O"] = [new(0,0,0), new(1,0,0), new(0,1,0), new(1,1,0)],
            ["T"] = [new(1,1,0), new(0,0,0), new(1,0,0), new(2,0,0)],
            ["L"] = [new(2,1,0), new(0,0,0), new(1,0,0), new(2,0,0)],
            ["J"] = [new(0,1,0), new(0,0,0), new(1,0,0), new(2,0,0)],
            ["S"] = [new(1,1,0), new(2,1,0), new(0,0,0), new(1,0,0)],
            ["Z"] = [new(0,1,0), new(1,1,0), new(1,0,0), new(2,0,0)],
            ["pillar"] = [new(0,0,0), new(0,1,0), new(0,2,0)],
            ["stair"] = [new(0,0,0), new(1,0,0), new(2,0,0), new(1,1,0), new(2,1,0), new(2,2,0)]
        });

    // Arrays in a read-only dictionary remain mutable. Return defensive copies so callers cannot
    // corrupt the canonical geometry used by later objects or board validation.
    public static IReadOnlyDictionary<string, Voxel[]> Shapes => new ReadOnlyDictionary<string, Voxel[]>(
        Canonical.ToDictionary(pair => pair.Key, pair => (Voxel[])pair.Value.Clone(), StringComparer.Ordinal));

    public static string ForKind(string kind) => kind.ToLowerInvariant() switch
    {
        "file" or "stream" or "artifact" or "repositoryfile" => "I",
        "directory" or "repository" or "solution" or "project" => "O",
        "method" or "function" or "symbol" or "command" => "T",
        "package" or "packageversion" or "sbomcomponent" or "buildtarget" => "L",
        "contributor" or "owner" or "user" => "J",
        "branch" or "tag" or "reference" or "externalreference" or "reparsepoint" => "S",
        "finding" or "diagnostic" or "error" => "Z",
        "commit" or "change" or "buildinvocation" => "stair",
        _ => "pillar"
    };

    public static string ColorFor(string shape) => shape switch
    {
        "I" => "#6b9faf", "O" => "#526d90", "T" => "#aa8270", "L" => "#91c7df",
        "J" => "#285e96", "S" => "#dfbf7f", "Z" => "#88b99e", "pillar" => "#64deff",
        "stair" => "#000000", _ => throw new ArgumentException("Unknown canonical shape.", nameof(shape))
    };

    /// <summary>Counterclockwise XY quarter turns, normalized to nonnegative minimum coordinates.</summary>
    public static Voxel[] Rotate(string shape, int turns)
    {
        if (!Canonical.TryGetValue(shape, out var source))
            throw new ArgumentException("Unknown canonical shape.", nameof(shape));
        var count = ((turns % 4) + 4) % 4;
        var cells = source.Select(cell =>
        {
            var (x, y, z) = cell;
            for (var turn = 0; turn < count; turn++) (x, y) = (-y, x);
            return new Voxel(x, y, z);
        }).ToArray();
        var minX = cells.Min(cell => cell.X); var minY = cells.Min(cell => cell.Y); var minZ = cells.Min(cell => cell.Z);
        return cells.Select(cell => new Voxel(cell.X - minX, cell.Y - minY, cell.Z - minZ)).ToArray();
    }
}
