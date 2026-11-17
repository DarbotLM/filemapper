using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using FileMapper.Domain;

namespace FileMapper.Collect.Git;

internal sealed class PackageProvenance(ProvenanceGraph graph)
{
    private readonly Dictionary<string, string> projects = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly HashSet<string> components = new(StringComparer.Ordinal);
    public string Project(string path)
    {
        var id = Identity.For("project", path); projects[path] = id;
        graph.Node(id, "Project", Path.GetFileNameWithoutExtension(path), path);
        return id;
    }
    public string Target(string project, string framework)
    {
        var id = Identity.For("build-target", project + ":" + framework);
        var split = framework.Split('/', 2);
        graph.Node(id, "BuildTarget", framework, null, new() { ["projectId"] = project, ["targetFramework"] = split[0], ["runtimeIdentifier"] = split.Length == 2 ? split[1] : null, ["buildExecuted"] = false });
        return id;
    }
    public string Package(string name, string version, string evidence, string? contentHash = null)
    {
        var key = name.ToLowerInvariant() + "@" + version; var id = Identity.For("package-version", "nuget:" + key);
        if (components.Add(id) && components.Count > graph.Limits.MaxComponents) throw new InvalidDataException("Resolved package component budget reached.");
        graph.Node(id, "PackageVersion", name, null, new() { ["ecosystem"] = "nuget", ["version"] = version, ["purl"] = "pkg:nuget/" + name + "@" + version, ["declaredContentHash"] = contentHash, ["packageBytesVerified"] = false });
        var family = Identity.For("package", "nuget:" + name.ToLowerInvariant());
        graph.Node(family, "Package", name, null, new() { ["ecosystem"] = "nuget" });
        graph.Edge(family, id, "HAS_VERSION", evidence); return id;
    }
    public void Collect(string[] documents, CancellationToken ct)
    {
        foreach (var path in documents.Where(p => Path.GetExtension(p).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj" or ".slnx" or ".sln"))
            Try(path, () => CollectProject(path, ct));
        foreach (var path in documents.Where(p => Path.GetFileName(p) is "project.assets.json" or "packages.lock.json"))
            Try(path, () => CollectResolution(path, ct));
    }
    private void Try(string path, Action work)
    {
        try { work(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or XmlException or JsonException or ArgumentException or InvalidOperationException)
        { graph.Diagnostic(path, "package_metadata", ex.Message); }
    }
    private void CollectProject(string path, CancellationToken ct)
    {
        var bytes = graph.Read(path, ct); var source = graph.Document(path, bytes); var hash = ProvenanceGraph.Hash(bytes);
        var solution = Path.GetExtension(path).ToLowerInvariant() is ".sln" or ".slnx";
        var id = solution ? Identity.For("solution", path) : Project(path);
        var ev = graph.Observe(id, "project_declaration", "Declarative source only: MSBuild conditions, imports and property expansion are not evaluated.", path, completeness: "partial", hash: hash);
        graph.Node(id, solution ? "Solution" : "Project", Path.GetFileNameWithoutExtension(path), path, new() { ["evaluation"] = "declarative-only", ["buildExecuted"] = false });
        graph.Edge(source, id, "DEFINES", ev);
        if (Path.GetExtension(path).Equals(".sln", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match match in Regex.Matches(Encoding.UTF8.GetString(bytes), "(?m)^Project\\([^\\r\\n]+\\)\\s*=\\s*\"[^\"]*\",\\s*\"([^\"]+)\"", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)))
                if (Path.GetExtension(match.Groups[1].Value).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj") Reference(id, path, match.Groups[1].Value, "CONTAINS", ev);
            return;
        }
        using var input = new MemoryStream(bytes);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = graph.Limits.MaxDocumentBytes });
        var xml = XDocument.Load(reader);
        var frameworks = xml.Descendants().Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks").SelectMany(e => e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct(StringComparer.Ordinal).ToArray();
        graph.Node(id, solution ? "Solution" : "Project", Path.GetFileNameWithoutExtension(path), path, new() { ["declaredTargetFrameworks"] = frameworks, ["sdk"] = xml.Root?.Attribute("Sdk")?.Value });
        foreach (var framework in frameworks.Where(f => !f.Contains('$')))
            graph.Edge(id, Target(id, framework), "CONTAINS", ev, properties: new() { ["context"] = "declared; conditions unevaluated" });
        foreach (var item in xml.Descendants())
        {
            ct.ThrowIfCancellationRequested();
            var include = item.Attribute("Include")?.Value ?? item.Attribute("Path")?.Value;
            if (string.IsNullOrWhiteSpace(include)) continue;
            var conditions = item.AncestorsAndSelf().Select(e => e.Attribute("Condition")?.Value).OfType<string>().ToArray();
            if (item.Name.LocalName == "PackageReference")
            {
                var version = item.Attribute("Version")?.Value ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;
                var declaration = Identity.For("package-declaration", path + ":" + include + ":" + version + ":" + string.Join('|', conditions));
                graph.Node(declaration, "Package", include, path, new() { ["ecosystem"] = "nuget", ["versionConstraint"] = version, ["resolved"] = false, ["conditions"] = conditions });
                graph.Edge(id, declaration, "DECLARES_DEPENDENCY", ev, 0.8);
            }
            else if (item.Name.LocalName == "ProjectReference" || solution && item.Name.LocalName == "Project")
                Reference(id, path, include, solution ? "CONTAINS" : "DECLARES_DEPENDENCY", ev, conditions);
        }
    }
    private void Reference(string owner, string sourcePath, string relative, string kind, string ev, string[]? conditions = null)
    {
        try
        {
            var path = graph.Resolve(Path.GetDirectoryName(sourcePath)!, relative);
            graph.Edge(owner, Project(path), kind, ev, 0.8, new() { ["conditions"] = conditions ?? [], ["evaluation"] = "declarative-only" });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { graph.Diagnostic(sourcePath, "unresolved_project_reference", relative + ": " + ex.Message); }
    }
    private void CollectResolution(string path, CancellationToken ct)
    {
        var bytes = graph.Read(path, ct); var source = graph.Document(path, bytes);
        using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 }); var json = parsed.RootElement;
        var lockId = Identity.For("lockfile", path);
        graph.Node(lockId, "Lockfile", Path.GetFileName(path), path, new() { ["sha256"] = ProvenanceGraph.Hash(bytes), ["restoreExecuted"] = false });
        var ev = graph.Observe(lockId, "resolved_package_graph", "Dependency resolution is imported from this document; its freshness and package bytes are not independently verified.", path, completeness: "partial", hash: ProvenanceGraph.Hash(bytes));
        graph.Edge(source, lockId, "DEFINES", ev);
        var project = ResolveProject(path, json);
        if (project is null) { graph.Diagnostic(path, "project_association_unknown", "No unambiguous in-scope project association. Resolution remains document-scoped."); project = lockId; }
        else graph.Edge(project, lockId, "USES_RESOLUTION", ev);
        if (Path.GetFileName(path) == "project.assets.json") Assets(json, project, lockId, ev, ct);
        else Lock(json, project, lockId, ev, ct);
    }
    private string? ResolveProject(string path, JsonElement json)
    {
        if (json.TryGetProperty("project", out var project) && project.TryGetProperty("restore", out var restore))
        {
            var declared = ProvenanceGraph.Text(restore, "projectPath");
            if (declared.Length > 0) return Project(graph.Resolve(Path.GetDirectoryName(path)!, declared));
        }
        var directory = Path.GetDirectoryName(path)!;
        var candidates = projects.Where(p => string.Equals(Path.GetDirectoryName(p.Key), directory, ProvenanceGraph.PathComparison)).ToArray();
        return candidates.Length == 1 ? candidates[0].Value : null;
    }
    private void Assets(JsonElement json, string project, string lockId, string ev, CancellationToken ct)
    {
        if (!json.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object) throw new InvalidDataException("NuGet assets lacks targets.");
        foreach (var target in targets.EnumerateObject())
        {
            ct.ThrowIfCancellationRequested(); var context = Target(project, target.Name);
            graph.Edge(project, context, "CONTAINS", ev); graph.Edge(lockId, context, "DESCRIBES", ev);
            var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in target.Value.EnumerateObject())
            {
                var split = library.Name.LastIndexOf('/'); if (split < 1) continue;
                if (ProvenanceGraph.Text(library.Value, "type") == "project")
                {
                    if (json.TryGetProperty("libraries", out var projectLibraries) && projectLibraries.TryGetProperty(library.Name, out var projectLibrary))
                    {
                        var relative = ProvenanceGraph.Text(projectLibrary, "msbuildProject", ProvenanceGraph.Text(projectLibrary, "path"));
                        var ownerPath = projects.FirstOrDefault(p => p.Value == project).Key;
                        if (ownerPath is not null && relative.Length > 0)
                        {
                            var resolved = Project(graph.Resolve(Path.GetDirectoryName(ownerPath)!, relative)); versions[library.Name[..split]] = resolved;
                            graph.Edge(context, resolved, "DEPENDS_ON", ev, properties: new() { ["targetContext"] = target.Name, ["basis"] = "resolved NuGet project reference" });
                        }
                    }
                    continue;
                }
                if (ProvenanceGraph.Text(library.Value, "type") != "package") continue;
                var name = library.Name[..split]; var version = library.Name[(split + 1)..]; string? contentHash = null;
                if (json.TryGetProperty("libraries", out var libraries) && libraries.TryGetProperty(library.Name, out var metadata)) contentHash = ProvenanceGraph.Text(metadata, "sha512");
                var package = Package(name, version, ev, contentHash); versions[name] = package;
                graph.Edge(context, package, "RESOLVES", ev, properties: new() { ["targetContext"] = target.Name });
            }
            foreach (var library in target.Value.EnumerateObject())
            {
                var split = library.Name.LastIndexOf('/'); if (split < 1 || !versions.TryGetValue(library.Name[..split], out var from)) continue;
                if (library.Value.TryGetProperty("dependencies", out var dependencies))
                    foreach (var dependency in dependencies.EnumerateObject())
                        if (versions.TryGetValue(dependency.Name, out var to)) graph.Edge(from, to, "DEPENDS_ON", ev, properties: new() { ["targetContext"] = target.Name, ["requestedVersion"] = dependency.Value.ToString() });
            }
            if (json.TryGetProperty("project", out var projectData) && projectData.TryGetProperty("frameworks", out var frameworks))
                foreach (var framework in frameworks.EnumerateObject())
                    if (target.Name.Split('/')[0] == framework.Name && framework.Value.TryGetProperty("dependencies", out var direct))
                        foreach (var dependency in direct.EnumerateObject())
                            if (versions.TryGetValue(dependency.Name, out var to)) graph.Edge(context, to, "DEPENDS_ON", ev, properties: new() { ["targetContext"] = target.Name, ["direct"] = true });
        }
    }
    private void Lock(JsonElement json, string project, string lockId, string ev, CancellationToken ct)
    {
        if (!json.TryGetProperty("dependencies", out var frameworks) || frameworks.ValueKind != JsonValueKind.Object) throw new InvalidDataException("NuGet lockfile lacks dependencies.");
        foreach (var framework in frameworks.EnumerateObject())
        {
            ct.ThrowIfCancellationRequested(); var context = Target(project, framework.Name);
            graph.Edge(project, context, "CONTAINS", ev); graph.Edge(lockId, context, "DESCRIBES", ev);
            var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dependency in framework.Value.EnumerateObject())
            {
                var version = ProvenanceGraph.Text(dependency.Value, "resolved"); if (version.Length == 0) continue;
                var package = Package(dependency.Name, version, ev, ProvenanceGraph.Text(dependency.Value, "contentHash")); versions[dependency.Name] = package;
                graph.Edge(context, package, ProvenanceGraph.Text(dependency.Value, "type") == "Direct" ? "DEPENDS_ON" : "RESOLVES", ev, properties: new() { ["targetContext"] = framework.Name, ["resolutionSource"] = "packages.lock.json" });
            }
            foreach (var dependency in framework.Value.EnumerateObject())
                if (versions.TryGetValue(dependency.Name, out var from) && dependency.Value.TryGetProperty("dependencies", out var transitive))
                    foreach (var child in transitive.EnumerateObject())
                        if (versions.TryGetValue(child.Name, out var to)) graph.Edge(from, to, "DEPENDS_ON", ev, properties: new() { ["targetContext"] = framework.Name, ["requestedVersion"] = child.Value.ToString() });
        }
    }
}
