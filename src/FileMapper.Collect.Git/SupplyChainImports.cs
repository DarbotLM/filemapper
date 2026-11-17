using System.Text;
using System.Text.Json;
using FileMapper.Domain;

namespace FileMapper.Collect.Git;

internal sealed class SupplyChainImports(ProvenanceGraph graph, RepositoryHistory history, PackageProvenance packages)
{
    private int componentCount;
    private readonly Dictionary<string, (string Hash, long Length, string Evidence)> hashes = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public async Task CollectAsync(string[] documents, CancellationToken ct)
    {
        var jsonFiles = documents.Where(p => Path.GetExtension(p).Equals(".json", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(p) is not ("project.assets.json" or "packages.lock.json"))
            .OrderByDescending(p => p.EndsWith("provenance.json", StringComparison.OrdinalIgnoreCase));
        foreach (var path in jsonFiles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var bytes = graph.Read(path, ct); using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 }); var json = document.RootElement;
                if (ProvenanceGraph.Text(json, "kind") == "filemapper-build-provenance") await Manifest(path, bytes, json, ct);
                else if (ProvenanceGraph.Text(json, "bomFormat") == "CycloneDX") CycloneDx(path, bytes, json, ct);
                else if (ProvenanceGraph.Text(json, "spdxVersion").StartsWith("SPDX-2.", StringComparison.Ordinal)) Spdx(path, bytes, json, ct);
                else graph.Diagnostic(path, "unsupported_provenance_document", "Candidate JSON is not a supported CycloneDX, SPDX 2.x or FileMapper build provenance document.");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or FormatException)
            { graph.Diagnostic(path, "provenance_import", ex.Message); }
        }
    }
    private (string Hash, long Length, string Evidence) Content(string path, CancellationToken ct)
    {
        if (hashes.TryGetValue(path, out var cached)) return cached;
        if (hashes.Count >= 128) throw new InvalidDataException("Provenance content verification is limited to 128 files per analysis.");
        var (hash, length) = graph.HashFile(path, ct); var source = graph.Document(path);
        graph.Node(source, "SourceDocument", Path.GetFileName(path), path, new() { ["sha256"] = hash });
        var ev = graph.Observe(source, "observed_content_hash", new { sha256 = hash, length, atomicFilesystemSnapshot = false }, path, hash: hash);
        return hashes[path] = (hash, length, ev);
    }
    private string Artifact(string path, (string Hash, long Length, string Evidence) content)
    {
        var id = Identity.For("artifact", content.Hash + ":" + content.Length);
        graph.Node(id, "Artifact", Path.GetFileName(path), path, new() { ["sha256"] = content.Hash, ["length"] = content.Length, ["executionVerified"] = false });
        graph.Edge(id, graph.Document(path), "LOCATED_AT", content.Evidence); return id;
    }
    private async Task Manifest(string path, byte[] bytes, JsonElement json, CancellationToken ct)
    {
        if (!json.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out var schema) || schema != 1) throw new InvalidDataException("Unsupported build provenance schemaVersion.");
        var document = graph.Document(path, bytes); var manifestHash = ProvenanceGraph.Hash(bytes);
        var manifestEvidence = graph.Observe(document, "build_manifest", new { schemaVersion = 1, authority = "untrusted declarative manifest", executionVerified = false }, path, completeness: "partial", hash: manifestHash);
        var inputSpecs = ProvenanceGraph.Items(json, "inputs").ToArray(); var outputSpecs = ProvenanceGraph.Items(json, "outputs").ToArray();
        if (inputSpecs.Length is < 1 or > 64 || outputSpecs.Length is < 1 or > 64) throw new InvalidDataException("Build manifest requires 1–64 inputs and 1–64 outputs.");
        var verifiedInputs = Verify(inputSpecs); var verifiedOutputs = Verify(outputSpecs);
        if (!json.TryGetProperty("invocation", out var invocation) || invocation.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Build manifest requires invocation metadata.");
        var fingerprint = ProvenanceGraph.Hash(Encoding.UTF8.GetBytes(invocation.GetRawText()));
        var invocationId = Identity.For("build-invocation", manifestHash + ":" + fingerprint);
        var toolName = json.TryGetProperty("tool", out var tool) ? ProvenanceGraph.Text(tool, "name", "unknown") : "unknown";
        var toolVersion = tool.ValueKind == JsonValueKind.Object ? ProvenanceGraph.Text(tool, "version", "unknown") : "unknown";
        graph.Node(invocationId, "BuildInvocation", toolName, path, new()
        {
            ["tool"] = toolName, ["toolVersion"] = toolVersion, ["invocationFingerprint"] = fingerprint,
            ["environmentFingerprint"] = invocation.TryGetProperty("environment", out var environment) ? ProvenanceGraph.Hash(Encoding.UTF8.GetBytes(environment.GetRawText())) : null,
            ["declaredTimestamp"] = ProvenanceGraph.Text(json, "timestamp"), ["executionVerified"] = false, ["authority"] = "manifest assertion with observed content hashes"
        });
        var ev = graph.Observe(invocationId, "verified_manifest_content", new { manifestHash, invocationFingerprint = fingerprint, inputs = verifiedInputs.Count, outputs = verifiedOutputs.Count, executionVerified = false, meaning = "all declared hashes matched observed bytes; this does not prove execution or hermeticity" }, path, 0.8, "partial", manifestHash,
            new[] { manifestEvidence }.Concat(verifiedInputs.Select(i => i.Content.Evidence)).Concat(verifiedOutputs.Select(i => i.Content.Evidence)));
        graph.Edge(document, invocationId, "DECLARES", ev, 0.8);
        foreach (var input in verifiedInputs)
        {
            graph.Edge(invocationId, graph.Document(input.Path), "USES_INPUT", ev, 0.8);
            await history.LinkVerifiedSourceAsync(input.Path, input.Content.Hash, ct);
        }
        var declaredCommit = ProvenanceGraph.Text(json, "commit");
        if (declaredCommit.Length > 0 && history.RepositoryId is not null)
        {
            if (declaredCommit.Length is not (40 or 64) || !declaredCommit.All(Uri.IsHexDigit)) throw new InvalidDataException("Manifest commit must be a full Git object ID.");
            var commit = Identity.For("commit", history.RepositoryId + ":" + declaredCommit);
            graph.Node(commit, "Commit", declaredCommit[..Math.Min(12, declaredCommit.Length)], history.Repository, new() { ["sha"] = declaredCommit, ["declaredByManifest"] = true });
            graph.Edge(invocationId, commit, "DECLARES_SOURCE_REVISION", ev, 0.5, new() { ["equalsObservedHead"] = declaredCommit == history.Head, ["executionVerified"] = false });
        }
        var projectPath = ProvenanceGraph.Text(json, "project");
        if (projectPath.Length > 0)
        {
            var project = packages.Project(graph.Resolve(Path.GetDirectoryName(path)!, projectPath));
            var framework = ProvenanceGraph.Text(json, "targetFramework", "unknown"); var rid = ProvenanceGraph.Text(json, "runtimeIdentifier");
            var target = packages.Target(project, framework + (rid.Length == 0 ? "" : "/" + rid));
            graph.Edge(project, target, "CONTAINS", ev); graph.Edge(invocationId, target, "DECLARES_TARGET", ev, 0.8);
            foreach (var input in verifiedInputs) graph.Edge(graph.Document(input.Path), project, "DECLARED_BUILD_INPUT", ev, 0.8);
        }
        foreach (var output in verifiedOutputs)
        {
            var artifact = Artifact(output.Path, output.Content);
            graph.Edge(artifact, invocationId, "GENERATED_BY", ev, 0.8, new() { ["executionVerified"] = false });
            foreach (var input in verifiedInputs)
                graph.Edge(artifact, graph.Document(input.Path), "GENERATED_FROM", ev, 0.8,
                    new() { ["basis"] = "explicit manifest plus matching observed input/output SHA256", ["executionVerified"] = false, ["declaredDerivation"] = true });
        }
        List<(string Path, (string Hash, long Length, string Evidence) Content)> Verify(JsonElement[] specs)
        {
            var result = new List<(string, (string, long, string))>();
            foreach (var spec in specs)
            {
                ct.ThrowIfCancellationRequested(); var sha = ProvenanceGraph.Text(spec, "sha256");
                if (!ProvenanceGraph.IsHash(sha)) throw new InvalidDataException("Build input/output requires a full SHA256.");
                var location = graph.Resolve(Path.GetDirectoryName(path)!, ProvenanceGraph.Text(spec, "path")); var actual = Content(location, ct);
                if (!sha.Equals(actual.Hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Build manifest content mismatch: " + Path.GetFileName(location) + "; no GENERATED_FROM edges accepted.");
                if (spec.TryGetProperty("length", out var length) && (!length.TryGetInt64(out var expected) || expected != actual.Length)) throw new InvalidDataException("Build manifest length mismatch.");
                result.Add((location, actual));
            }
            return result;
        }
    }
    private void CycloneDx(string path, byte[] bytes, JsonElement json, CancellationToken ct)
    {
        var version = ProvenanceGraph.Text(json, "specVersion");
        if (version is not ("1.4" or "1.5" or "1.6" or "1.7")) throw new InvalidDataException("CycloneDX JSON version is not supported.");
        var source = graph.Document(path, bytes); var docHash = ProvenanceGraph.Hash(bytes);
        var ev = graph.Observe(source, "sbom_import", new { format = "CycloneDX", version, serialNumber = ProvenanceGraph.Text(json, "serialNumber"), trust = "document assertion; no signature or completeness verification" }, path, completeness: "partial", hash: docHash);
        var references = new Dictionary<string, string>(StringComparer.Ordinal);
        var queue = new Queue<JsonElement>(ProvenanceGraph.Items(json, "components"));
        if (json.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("component", out var main)) queue.Enqueue(main);
        while (queue.TryDequeue(out var component))
        {
            ct.ThrowIfCancellationRequested(); if (++componentCount > graph.Limits.MaxComponents) throw new InvalidDataException("SBOM component budget reached.");
            var reference = ProvenanceGraph.Text(component, "bom-ref", "anonymous:" + componentCount);
            if (references.ContainsKey(reference)) throw new InvalidDataException("Duplicate CycloneDX bom-ref.");
            var id = Identity.For("sbom-component", docHash + ":" + reference); references[reference] = id;
            var name = ProvenanceGraph.Text(component, "name", reference); var purl = ProvenanceGraph.Text(component, "purl");
            graph.Node(id, "SBOMComponent", name, path, new() { ["format"] = "CycloneDX", ["documentHash"] = docHash, ["bomRef"] = reference, ["purl"] = purl, ["version"] = ProvenanceGraph.Text(component, "version"), ["componentType"] = ProvenanceGraph.Text(component, "type"), ["hashes"] = ProvenanceGraph.Items(component, "hashes").Select(h => h.GetRawText()).ToArray(), ["declaredLicenses"] = ProvenanceGraph.Items(component, "licenses").Select(l => l.GetRawText()).ToArray() });
            graph.Edge(source, id, "CONTAINS", ev); LinkPackage(id, purl, ev);
            if (ProvenanceGraph.Text(component, "type") == "file")
            {
                var hash = ProvenanceGraph.Items(component, "hashes").FirstOrDefault(h => ProvenanceGraph.Text(h, "alg") == "SHA-256");
                LinkFile(path, name, ProvenanceGraph.Text(hash, "content"), id, ev, ct);
            }
            foreach (var child in ProvenanceGraph.Items(component, "components")) queue.Enqueue(child);
        }
        foreach (var dependency in ProvenanceGraph.Items(json, "dependencies"))
        {
            if (!references.TryGetValue(ProvenanceGraph.Text(dependency, "ref"), out var from)) { graph.Diagnostic(path, "sbom_unknown_reference", "CycloneDX dependency source reference is absent."); continue; }
            foreach (var target in ProvenanceGraph.Items(dependency, "dependsOn"))
                if (target.ValueKind == JsonValueKind.String && references.TryGetValue(target.GetString()!, out var to)) graph.Edge(from, to, "DEPENDS_ON", ev, 0.9, new() { ["basis"] = "SBOM declaration" });
                else graph.Diagnostic(path, "sbom_unknown_reference", "CycloneDX dependency target reference is absent.");
        }
    }
    private void Spdx(string path, byte[] bytes, JsonElement json, CancellationToken ct)
    {
        var version = ProvenanceGraph.Text(json, "spdxVersion");
        if (version is not ("SPDX-2.2" or "SPDX-2.3")) throw new InvalidDataException("Only SPDX JSON 2.2 and 2.3 are supported.");
        var source = graph.Document(path, bytes); var hash = ProvenanceGraph.Hash(bytes); var documentNamespace = ProvenanceGraph.Text(json, "documentNamespace");
        var ev = graph.Observe(source, "sbom_import", new { format = "SPDX", version, documentNamespace, trust = "document assertion; no signature or completeness verification" }, path, completeness: "partial", hash: hash);
        var refs = new Dictionary<string, string>(StringComparer.Ordinal) { [ProvenanceGraph.Text(json, "SPDXID", "SPDXRef-DOCUMENT")] = source };
        foreach (var (component, file) in ProvenanceGraph.Items(json, "packages").Select(p => (p, false)).Concat(ProvenanceGraph.Items(json, "files").Select(p => (p, true))))
        {
            ct.ThrowIfCancellationRequested(); if (++componentCount > graph.Limits.MaxComponents) throw new InvalidDataException("SBOM component budget reached.");
            var reference = ProvenanceGraph.Text(component, "SPDXID"); if (reference.Length == 0 || refs.ContainsKey(reference)) throw new InvalidDataException("Missing or duplicate SPDXID.");
            var id = Identity.For("sbom-component", hash + ":" + reference); refs[reference] = id;
            var name = ProvenanceGraph.Text(component, file ? "fileName" : "name", reference);
            var purl = ProvenanceGraph.Items(component, "externalRefs").Where(r => ProvenanceGraph.Text(r, "referenceType") == "purl").Select(r => ProvenanceGraph.Text(r, "referenceLocator")).FirstOrDefault() ?? "";
            graph.Node(id, "SBOMComponent", name, path, new() { ["format"] = "SPDX", ["documentHash"] = hash, ["documentNamespace"] = documentNamespace, ["spdxId"] = reference, ["version"] = ProvenanceGraph.Text(component, "versionInfo"), ["purl"] = purl, ["declaredLicense"] = ProvenanceGraph.Text(component, "licenseDeclared"), ["concludedLicense"] = ProvenanceGraph.Text(component, "licenseConcluded"), ["checksums"] = ProvenanceGraph.Items(component, "checksums").Select(c => c.GetRawText()).ToArray() });
            graph.Edge(source, id, "CONTAINS", ev); LinkPackage(id, purl, ev);
            if (file)
            {
                var checksum = ProvenanceGraph.Items(component, "checksums").FirstOrDefault(h => ProvenanceGraph.Text(h, "algorithm") == "SHA256");
                LinkFile(path, name, ProvenanceGraph.Text(checksum, "checksumValue"), id, ev, ct);
            }
        }
        foreach (var relationship in ProvenanceGraph.Items(json, "relationships"))
        {
            if (!refs.TryGetValue(ProvenanceGraph.Text(relationship, "spdxElementId"), out var from) || !refs.TryGetValue(ProvenanceGraph.Text(relationship, "relatedSpdxElement"), out var to))
            { graph.Diagnostic(path, "spdx_external_reference", "SPDX relationship points outside imported elements; no inferred node merge."); continue; }
            var type = ProvenanceGraph.Text(relationship, "relationshipType");
            graph.Edge(from, to, type is "DEPENDS_ON" or "CONTAINS" ? type : "DECLARES_" + type, ev, 0.8, new() { ["basis"] = "SPDX assertion", ["executionVerified"] = false });
        }
    }
    private void LinkPackage(string component, string purl, string ev)
    {
        if (!purl.StartsWith("pkg:nuget/", StringComparison.OrdinalIgnoreCase)) return;
        var coordinate = purl[10..].Split('?', '#')[0]; var at = coordinate.LastIndexOf('@'); if (at < 1 || at == coordinate.Length - 1) return;
        var package = packages.Package(Uri.UnescapeDataString(coordinate[..at]), Uri.UnescapeDataString(coordinate[(at + 1)..]), ev);
        graph.Edge(component, package, "IDENTIFIES", ev, properties: new() { ["basis"] = "declared package URL", ["bytesVerified"] = false });
    }
    private void LinkFile(string document, string relative, string expected, string component, string ev, CancellationToken ct)
    {
        if (!ProvenanceGraph.IsHash(expected)) return;
        try
        {
            var path = graph.Resolve(Path.GetDirectoryName(document)!, relative); var content = Content(path, ct);
            if (!content.Hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) { graph.Diagnostic(document, "sbom_hash_mismatch", "SBOM file hash does not match observed content: " + relative); return; }
            var verified = graph.Observe(component, "sbom_file_content_match", new { sha256 = content.Hash, content.Length, path }, document, hash: content.Hash, contributes: [ev, content.Evidence]);
            graph.Edge(component, Artifact(path, content), "IDENTIFIES", verified);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { graph.Diagnostic(document, "sbom_file_unverified", relative + ": " + ex.Message); }
    }
}
