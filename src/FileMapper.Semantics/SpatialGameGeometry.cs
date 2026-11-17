using System.Collections.Immutable;

namespace FileMapper.Semantics;

/// <summary>Declared alternate geometry encodings, independent of native SOM identity and the unchanged v1 catalog.</summary>
public static class SpatialGameGeometry
{
    public static ImmutableArray<string> Variants { get; } = ["canonical/v1", "corner4/v1", "screw4/v1", "cube8/v1", "native-lift/v1"];
    private static readonly IReadOnlyDictionary<string, SpatialGeometry> Geometries = BuildGeometries();
    private static readonly IReadOnlyDictionary<string, ImmutableArray<SpatialOrientation>> Poses =
        Geometries.ToDictionary(p => p.Key, p => BuildOrientations(p.Value.Cells), StringComparer.Ordinal);

    public static SpatialGeometry Get(string baseShape, string geometryVariant) =>
        Geometries.TryGetValue(baseShape + "/" + geometryVariant, out var geometry) ? geometry
            : throw new SpatialGameException("geometry", "Unknown base shape or geometry variant.");
    public static ImmutableArray<SpatialOrientation> Orientations(string baseShape, string geometryVariant) =>
        Poses.TryGetValue(baseShape + "/" + geometryVariant, out var poses) ? poses
            : throw new SpatialGameException("geometry", "Unknown base shape or geometry variant.");
    private static IReadOnlyDictionary<string, SpatialGeometry> BuildGeometries()
    {
        var result = new Dictionary<string, SpatialGeometry>(StringComparer.Ordinal);
        foreach (var shape in ShapeCatalog.Shapes)
        foreach (var variant in Variants)
        {
            Voxel[] cells = variant switch
            {
                "canonical/v1" => shape.Value,
                "native-lift/v1" => LiftNative(shape.Value),
                "corner4/v1" => [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)],
                "screw4/v1" => [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(1, 1, 1)],
                "cube8/v1" => [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0), new(0, 0, 1), new(1, 0, 1), new(0, 1, 1), new(1, 1, 1)],
                _ => throw new InvalidOperationException()
            };
            var frozen = Normalize(cells);
            result.Add(shape.Key + "/" + variant, new(shape.Key, variant,
                CanonicalJson.Sha256(new { Model = "spatial-geometry/v1", BaseShape = shape.Key, GeometryVariant = variant, Cells = frozen }),
                frozen, variant != "canonical/v1", variant == "canonical/v1"
                    ? "unchanged native canonical geometry" : "explicit alternate encoding; not a rigid rotation of the native geometry; SOM identity unchanged"));
        }
        return result;
    }
    private static Voxel[] LiftNative(Voxel[] source)
    {
        // The native footprint remains intact. Lift its first occupied cell into Z and, for a one-axis
        // native line, add the missing transverse support so the result spans true XYZ.
        var anchor = source.OrderBy(cell => cell.X).ThenBy(cell => cell.Y).ThenBy(cell => cell.Z).First();
        var cells = new List<Voxel>(source) { new(anchor.X, anchor.Y, anchor.Z + 1) };
        if (source.All(cell => cell.X == 0)) cells.Add(new(1, 0, 0));
        if (source.All(cell => cell.Y == 0)) cells.Add(new(0, 1, 0));
        return cells.Distinct().ToArray();
    }
    private static ImmutableArray<Voxel> Normalize(IEnumerable<Voxel> source)
    {
        var cells = source.ToArray(); var x = cells.Min(c => c.X); var y = cells.Min(c => c.Y); var z = cells.Min(c => c.Z);
        return cells.Select(c => new Voxel(c.X - x, c.Y - y, c.Z - z)).OrderBy(c => c.X).ThenBy(c => c.Y).ThenBy(c => c.Z).ToImmutableArray();
    }
    private static ImmutableArray<SpatialOrientation> BuildOrientations(ImmutableArray<Voxel> cells)
    {
        var result = new SortedDictionary<string, ImmutableArray<Voxel>>(StringComparer.Ordinal);
        // Six axis permutations times eight sign choices; determinant +1 retains exactly the 24 proper rotations.
        foreach (var permutation in new[] { new[] { 0, 1, 2 }, [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0] })
        {
            var inversions = 0;
            for (var a = 0; a < 3; a++) for (var b = a + 1; b < 3; b++) if (permutation[a] > permutation[b]) inversions++;
            var parity = inversions % 2 == 0 ? 1 : -1;
            foreach (var sx in new[] { -1, 1 }) foreach (var sy in new[] { -1, 1 }) foreach (var sz in new[] { -1, 1 })
            {
                if (parity * sx * sy * sz != 1) continue;
                var posed = Normalize(cells.Select(c => { int[] values = [c.X, c.Y, c.Z]; return new Voxel(sx * values[permutation[0]], sy * values[permutation[1]], sz * values[permutation[2]]); }));
                result.TryAdd(CanonicalJson.Serialize(posed), posed);
            }
        }
        return result.Values.Select(c => new SpatialOrientation("orientation:" + CanonicalJson.Sha256(new { Model = "proper-cube-rotation/v1", Cells = c }),
            c, c.Max(p => p.X) + 1, c.Max(p => p.Y) + 1, c.Max(p => p.Z) + 1)).OrderBy(p => p.Id, StringComparer.Ordinal).ToImmutableArray();
    }
}
