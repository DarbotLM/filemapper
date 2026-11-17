using System.ComponentModel;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FileMapper.Domain;

namespace FileMapper.Collect.FileSystem;

/// <summary>Depth-first streaming walker with consumer-controlled backpressure and a bounded batch.</summary>
public sealed class FileSystemCollector : IMapCollector
{
    public string Name=>"filesystem/0.1.0";
    private static readonly StringComparison PathComparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;

    /// <summary>Resumes a durable frontier. Only an unfinished directory listing is replayed after interruption.</summary>
    public async IAsyncEnumerable<ScanCheckpointBatch> CollectCheckpointedAsync(string snapshotId,ScanOptions options,IScanWorkQueue queue,[EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if(options.BatchSize is <1 or >10000)throw new ArgumentException("Batch size must be 1–10000.");
        if(options.ReparsePolicy is not("skip" or "within" or "all" or "reference"))throw new ArgumentException("Invalid reparse policy.");
        var root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Root));
        while(true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending=queue.Pending(options.BatchSize); if(pending.Count==0)yield break;
            var nodes=new List<MapNode>();var edges=new List<MapEdge>();var observations=new List<Evidence>();var entries=new List<FileEntry>();var done=new List<string>();
            Evidence Observe(string id,string predicate,string value,string path,string complete="complete",string? error=null,double confidence=1)
            {
                var fact=new Evidence(Identity.For("observation",snapshotId+"|"+id+"|"+predicate+"|"+path),snapshotId,id,predicate,value,Name,path,DateTimeOffset.UtcNow,confidence,complete,error,error is null?null:value);observations.Add(fact);return fact;
            }
            void Error(string path,Exception ex,string? id=null)=>Observe(id??Identity.For("unresolved-path",path),"collection.error",ex.Message,path,"incomplete",ex is Win32Exception native?native.NativeErrorCode.ToString():ex.HResult.ToString());
            foreach(var work in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path=work.Path;
                var relative=Path.GetRelativePath(root,path).Replace('\\','/');
                if(work.ParentPath is not null && options.Excludes?.Any(p=>FileSystemName.MatchesSimpleExpression(p.Replace('\\','/'),relative,OperatingSystem.IsWindows())||FileSystemName.MatchesSimpleExpression(p,Path.GetFileName(path),OperatingSystem.IsWindows()))==true) {done.Add(path);continue;}
                try
                {
                    if(work.ParentPath is not null)
                    {
                        var ancestor=work.ParentPath;
                        while(true)
                        {
                            if(!Within(root,ancestor))throw new IOException("Queued ancestor is outside the scan boundary.");
                            var expectedParent=queue.DirectoryIdentity(ancestor);
                            if(expectedParent is null)throw new IOException("Queued ancestor has no durable traversal evidence.");
                            var parentAttributes=File.GetAttributes(ancestor);
                            var actualParent=FileIdentityReader.Read(ancestor).StableId;
                            if(actualParent is not null && expectedParent!=actualParent)throw new IOException("Ancestor directory identity changed since discovery; refusing queued path.");
                            if(parentAttributes.HasFlag(FileAttributes.ReparsePoint))
                            {
                                if(options.ReparsePolicy is "skip" or "reference")throw new IOException("Queued ancestor became a reparse point; refusing traversal.");
                                var target=new DirectoryInfo(ancestor).ResolveLinkTarget(true);
                                if(options.ReparsePolicy=="within" && (target is null||!Within(root,target.FullName)))throw new IOException("Queued ancestor resolves outside the scan boundary.");
                            }
                            if(ancestor.Equals(root,PathComparison))break;
                            ancestor=Path.GetDirectoryName(ancestor)??throw new IOException("Queued ancestor chain does not reach the scan root.");
                        }
                    }
                    var attributes=File.GetAttributes(path);var directory=attributes.HasFlag(FileAttributes.Directory);var reparse=attributes.HasFlag(FileAttributes.ReparsePoint);
                    var metadata=FileIdentityReader.Read(path,reparse);var id=metadata.StableId??Identity.For("path-fallback",OperatingSystem.IsWindows()?path.ToUpperInvariant():path);
                    var kind=reparse?"ReparsePoint":directory?"Directory":"File";var confidence=metadata.StableId is null?.5:1;
                    FileSystemInfo info=directory?new DirectoryInfo(path):new FileInfo(path);
                    long logical=directory||reparse?0:((FileInfo)info).Length;var modified=new DateTimeOffset(info.LastWriteTimeUtc);
                    var fact=Observe(id,"filesystem.metadata",kind,path,confidence:confidence);
                    if(metadata.ErrorCode is not null)Observe(id,"native.error",metadata.ErrorDetail??"Native metadata unavailable",path,"incomplete",metadata.ErrorCode.ToString());
                    if(metadata.StableId is null)Observe(id,"identity.fallback","Physical identity unavailable",path,"incomplete",confidence:.5);
                    string? hash=null;
                    if(options.HashFiles&&!directory&&!reparse)
                    {
                        try
                        {
                            await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,131072,FileOptions.Asynchronous|FileOptions.SequentialScan);
                            hash=Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,cancellationToken));
                            var after=new FileInfo(path);var afterIdentity=FileIdentityReader.Read(path);
                            if(after.Length!=logical||after.LastWriteTimeUtc!=modified.UtcDateTime||afterIdentity.StableId!=metadata.StableId) {hash=null;Observe(id,"hash.invalidated","File changed while hashing",path,"incomplete","CHANGED_DURING_READ");}
                            else Observe(id,"content.sha256.unnamed-stream",hash,path);
                        }
                        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {Error(path,ex,id);}
                    }
                    entries.Add(new(snapshotId,path,work.ParentPath,id,info.Name,kind,logical,directory||reparse?0:metadata.AllocatedBytes,modified,attributes.ToString(),metadata.LinkCount,metadata.Owner,hash));
                    nodes.Add(new(id,kind,info.Name,path,new(){["logicalBytes"]=logical,["allocatedBytes"]=metadata.AllocatedBytes,["identitySource"]=metadata.StableId is null?"path-fallback":"windows-file-id",["linkCount"]=metadata.LinkCount,["owner"]=metadata.Owner,["securityDescriptor"]=metadata.SecurityDescriptor,["createdAt"]=info.CreationTimeUtc,["accessedAt"]=info.LastAccessTimeUtc,["modifiedAt"]=info.LastWriteTimeUtc,["attributes"]=attributes.ToString(),["hashScope"]="unnamed-stream",["evidenceId"]=fact.Id}));
                    if(metadata.SecurityDescriptor is not null)Observe(id,"filesystem.security-descriptor",metadata.SecurityDescriptor,path);
                    var location=Identity.For("location",OperatingSystem.IsWindows()?path.ToUpperInvariant():path);
                    nodes.Add(new(location,"Location",info.Name,path,new(){["evidenceId"]=fact.Id}));edges.Add(new(Identity.For("edge",id+"|LOCATED_AT|"+location),id,location,"LOCATED_AT",fact.Id,confidence));
                    if(work.ParentId is not null)edges.Add(new(Identity.For("edge",work.ParentId+"|CONTAINS|"+path),work.ParentId,id,"CONTAINS",fact.Id,confidence));
                    if(!directory&&!reparse&&OperatingSystem.IsWindows())
                    {
                        try
                        {
                            foreach(var (streamName,size) in FileIdentityReader.GetStreams(path))
                            {
                                var streamPath=path+streamName;var streamId=Identity.For("stream",id+streamName);var native=FileIdentityReader.Read(streamPath);var evidence=Observe(streamId,"filesystem.stream",streamName,streamPath);
                                long? streamAllocation=metadata.StableId is null?null:native.AllocatedBytes;
                                if(metadata.StableId is null)Observe(streamId,"stream.identity.fallback","Parent physical identity is unavailable. Logical size is per observed path and may include aliases; physical allocation cannot be safely attributed.",streamPath,"incomplete","IDENTITY_UNAVAILABLE",.5);
                                entries.Add(new(snapshotId,streamPath,path,streamId,streamName,"Stream",size,streamAllocation,modified,attributes.ToString(),metadata.LinkCount,metadata.Owner));
                                nodes.Add(new(streamId,"Stream",streamName,streamPath,new(){["logicalBytes"]=size,["allocatedBytes"]=streamAllocation,["identitySource"]=metadata.StableId is null?"path-fallback":"parent-native-file-id",["evidenceId"]=evidence.Id}));edges.Add(new(Identity.For("edge",id+"|CONTAINS|"+streamId),id,streamId,"CONTAINS",evidence.Id));
                            }
                        }
                        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or Win32Exception) {Error(path,ex,id);}
                    }
                    var traverse=directory&&!reparse;var traversalIdentity=id;
                    if(reparse)
                    {
                        var target=info.ResolveLinkTarget(true);
                        if(target is not null)
                        {
                            var targetNative=FileIdentityReader.Read(target.FullName);var targetId=targetNative.StableId??Identity.For("external-path",target.FullName);
                            Observe(id,"reparse.target",target.FullName,path);nodes.Add(new(targetId,"ExternalReference",target.Name,target.FullName,new(){["withinBoundary"]=Within(root,target.FullName)}));edges.Add(new(Identity.For("edge",id+"|REFERENCES|"+targetId),id,targetId,"REFERENCES",fact.Id));
                            if(directory&&options.ReparsePolicy is "all" or "within")
                            {
                                if(options.ReparsePolicy=="within"&&!Within(root,target.FullName))Observe(id,"reparse.boundary","Target excluded by within policy",path);
                                else if(targetNative.StableId is null)Observe(id,"reparse.skipped","Cannot follow without stable identity",path,"incomplete","IDENTITY_UNAVAILABLE");
                                else {traverse=true;traversalIdentity=targetNative.StableId;}
                            }
                        }
                    }
                    if(traverse)
                    {
                        if(queue.EnterDirectory(traversalIdentity,path))
                        {
                            var children=new List<ScanWork>(options.BatchSize);
                            foreach(var child in Directory.EnumerateFileSystemEntries(path))
                            {
                                cancellationToken.ThrowIfCancellationRequested();children.Add(new(child,path,id));
                                if(children.Count>=options.BatchSize){queue.Discover(children);children.Clear();}
                            }
                            if(children.Count>0)queue.Discover(children);
                        }
                        else Observe(id,"directory.already-visited","Stable directory identity already traversed at another location",path);
                    }
                }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or Win32Exception) {Error(path,ex);}
                done.Add(path);
            }
            yield return new(done,new(nodes,edges,observations,entries));
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<CollectionBatch> CollectAsync(string snapshotId,ScanOptions options,[EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if(options.BatchSize<1 || options.BatchSize>10000) throw new ArgumentOutOfRangeException(nameof(options),"Batch size must be 1–10000.");
        if(options.ReparsePolicy is not ("skip" or "within" or "all" or "reference")) throw new ArgumentException("Reparse policy must be skip, within, all or reference.",nameof(options));
        var root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Root));
        var nodes=new List<MapNode>();var edges=new List<MapEdge>();var evidence=new List<Evidence>();var entries=new List<FileEntry>();
        var visited=new HashSet<string>(StringComparer.Ordinal);
        var stack=new Stack<(IEnumerator<string> Iterator,string Path,string NodeId)>();

        Evidence Observe(string subject,string predicate,string value,string path,string completeness="complete",string? error=null,string? detail=null,double confidence=1)
        {
            var fact=new Evidence(Identity.For("observation",snapshotId+"|"+subject+"|"+predicate+"|"+path),snapshotId,subject,predicate,value,Name,path,DateTimeOffset.UtcNow,confidence,completeness,error,detail);
            evidence.Add(fact);return fact;
        }
        void Error(string path,Exception error,string? id=null)=>Observe(id??Identity.For("unresolved-path",path),"collection.error",error.GetType().Name,path,"incomplete",error is Win32Exception native?native.NativeErrorCode.ToString():error.HResult.ToString(),error.Message);
        CollectionBatch Flush()
        {
            var batch=new CollectionBatch(nodes.ToArray(),edges.ToArray(),evidence.ToArray(),entries.ToArray());
            nodes.Clear();edges.Clear();evidence.Clear();entries.Clear();return batch;
        }
        Observe(Identity.For("scan-scope",snapshotId),"scan.options",System.Text.Json.JsonSerializer.Serialize(options with { Root=root }),root);
        bool Excluded(string path)
        {
            var relative=Path.GetRelativePath(root,path).Replace('\\','/');
            return options.Excludes?.Any(pattern=>FileSystemName.MatchesSimpleExpression(pattern.Replace('\\','/'),relative,OperatingSystem.IsWindows()) || FileSystemName.MatchesSimpleExpression(pattern,Path.GetFileName(path),OperatingSystem.IsWindows()))==true;
        }
        async Task Visit(string path,string? parentPath,string? parentId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(parentPath is not null && Excluded(path)) return;
            try
            {
                var attributes=File.GetAttributes(path);
                var isDirectory=(attributes & FileAttributes.Directory)!=0;
                var reparse=(attributes & FileAttributes.ReparsePoint)!=0;
                var metadata=FileIdentityReader.Read(path,reparse);
                var id=metadata.StableId??Identity.For("path-fallback",OperatingSystem.IsWindows()?path.ToUpperInvariant():path);
                var confidence=metadata.StableId is null?.5:1;
                var kind=reparse?"ReparsePoint":isDirectory?"Directory":"File";
                var info=isDirectory?(FileSystemInfo)new DirectoryInfo(path):new FileInfo(path);
                var logical=isDirectory||reparse?0:((FileInfo)info).Length;
                var modified=new DateTimeOffset(info.LastWriteTimeUtc);
                var fact=Observe(id,"filesystem.metadata",kind,path,confidence:confidence);
                if(metadata.ErrorCode is not null) Observe(id,"native.error",metadata.ErrorDetail??"Native metadata unavailable",path,"incomplete",metadata.ErrorCode.Value.ToString(),metadata.ErrorDetail);
                if(metadata.StableId is null) Observe(id,"identity.fallback","Path-based identity; hardlink and rename identity unavailable",path,"incomplete",confidence:.5);
                string? hash=null;
                if(options.HashFiles && !isDirectory && !reparse)
                {
                    try
                    {
                        await using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,131072,FileOptions.Asynchronous|FileOptions.SequentialScan);
                        hash=Convert.ToHexStringLower(await SHA256.HashDataAsync(file,cancellationToken));
                        var after=new FileInfo(path);
                        var afterIdentity=FileIdentityReader.Read(path);
                        if(after.Length!=logical || after.LastWriteTimeUtc!=info.LastWriteTimeUtc || afterIdentity.StableId!=metadata.StableId)
                        {
                            hash=null;Observe(id,"hash.invalidated","File changed during hash",path,"incomplete","CHANGED_DURING_READ");
                        }
                        else Observe(id,"content.sha256.unnamed-stream",hash,path);
                    }
                    catch(Exception exception) when(exception is IOException or UnauthorizedAccessException) { Error(path,exception,id); }
                }
                entries.Add(new(snapshotId,path,parentPath,id,info.Name,kind,logical,isDirectory||reparse?0:metadata.AllocatedBytes,modified,attributes.ToString(),metadata.LinkCount,metadata.Owner,hash));
                nodes.Add(new(id,kind,info.Name,path,new() { ["logicalBytes"]=logical,["allocatedBytes"]=metadata.AllocatedBytes,["identitySource"]=metadata.StableId is null?"path-fallback":"windows-file-id",["linkCount"]=metadata.LinkCount,["owner"]=metadata.Owner,["securityDescriptor"]=metadata.SecurityDescriptor,["createdAt"]=info.CreationTimeUtc,["accessedAt"]=info.LastAccessTimeUtc,["modifiedAt"]=info.LastWriteTimeUtc,["attributes"]=attributes.ToString(),["hashScope"]="unnamed-stream",["evidenceId"]=fact.Id }));
                if(metadata.SecurityDescriptor is not null) Observe(id,"filesystem.security-descriptor",metadata.SecurityDescriptor,path);
                var locationId=Identity.For("location",OperatingSystem.IsWindows()?path.ToUpperInvariant():path);
                nodes.Add(new(locationId,"Location",info.Name,path,new() {["evidenceId"]=fact.Id}));
                edges.Add(new(Identity.For("edge",id+"|LOCATED_AT|"+locationId),id,locationId,"LOCATED_AT",fact.Id,confidence));
                if(parentId is not null) edges.Add(new(Identity.For("edge",parentId+"|CONTAINS|"+path),parentId,id,"CONTAINS",fact.Id,confidence));
                if(!isDirectory && !reparse && OperatingSystem.IsWindows())
                {
                    try
                    {
                        foreach(var (streamName,size) in FileIdentityReader.GetStreams(path))
                        {
                            var streamPath=path+streamName;var streamId=Identity.For("stream",id+streamName);
                            var streamMetadata=FileIdentityReader.Read(streamPath);
                            var streamFact=Observe(streamId,"filesystem.stream",streamName,streamPath);
                            long? streamAllocation=metadata.StableId is null?null:streamMetadata.AllocatedBytes;
                            if(metadata.StableId is null)Observe(streamId,"stream.identity.fallback","Parent physical identity is unavailable. Logical size is per observed path and may include aliases; physical allocation cannot be safely attributed.",streamPath,"incomplete","IDENTITY_UNAVAILABLE",confidence:.5);
                            entries.Add(new(snapshotId,streamPath,path,streamId,streamName,"Stream",size,streamAllocation,modified,attributes.ToString(),metadata.LinkCount,metadata.Owner));
                            nodes.Add(new(streamId,"Stream",streamName,streamPath,new() {["logicalBytes"]=size,["allocatedBytes"]=streamAllocation,["identitySource"]=metadata.StableId is null?"path-fallback":"parent-native-file-id",["evidenceId"]=streamFact.Id}));
                            edges.Add(new(Identity.For("edge",id+"|CONTAINS|"+streamId),id,streamId,"CONTAINS",streamFact.Id));
                        }
                    }
                    catch(Exception exception) when(exception is Win32Exception or IOException or UnauthorizedAccessException) {Error(path,exception,id);}
                }
                if(reparse)
                {
                    var target=info.ResolveLinkTarget(true);
                    if(target is not null)
                    {
                        Observe(id,"reparse.target",target.FullName,path);
                        var targetMetadata=FileIdentityReader.Read(target.FullName);
                        var targetId=targetMetadata.StableId??Identity.For("external-path",target.FullName);
                        nodes.Add(new(targetId,"ExternalReference",target.Name,target.FullName,new() {["withinBoundary"]=Within(root,target.FullName)}));
                        edges.Add(new(Identity.For("edge",id+"|REFERENCES|"+targetId),id,targetId,"REFERENCES",fact.Id));
                        if(options.ReparsePolicy is "skip" or "reference") return;
                        if(options.ReparsePolicy=="within" && !Within(root,target.FullName)) { Observe(id,"reparse.boundary","Target excluded by within policy",path);return; }
                        if(targetMetadata.StableId is null) { Observe(id,"reparse.skipped","Cannot safely follow without stable identity",path,"incomplete","IDENTITY_UNAVAILABLE");return; }
                        if(!visited.Add(targetMetadata.StableId)) { Observe(id,"reparse.cycle","Already visited target identity",path);return; }
                        if(isDirectory) stack.Push((Directory.EnumerateFileSystemEntries(path).GetEnumerator(),path,id));
                    }
                    return;
                }
                if(isDirectory && visited.Add(id)) stack.Push((Directory.EnumerateFileSystemEntries(path).GetEnumerator(),path,id));
            }
            catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or Win32Exception) {Error(path,exception);}
        }
        try
        {
            await Visit(root,null,null);
            while(stack.Count>0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame=stack.Peek();string? next=null;
                try
                {
                    if(frame.Iterator.MoveNext()) next=frame.Iterator.Current;
                    else { frame.Iterator.Dispose();stack.Pop(); }
                }
                catch(Exception exception) when(exception is IOException or UnauthorizedAccessException) { Error(frame.Path,exception,frame.NodeId);frame.Iterator.Dispose();stack.Pop(); }
                if(next is not null) await Visit(next,frame.Path,frame.NodeId);
                if(entries.Count>=options.BatchSize || evidence.Count>=options.BatchSize*4) {yield return Flush();await Task.Yield();}
            }
            if(entries.Count>0 || evidence.Count>0) yield return Flush();
        }
        finally { foreach(var frame in stack) frame.Iterator.Dispose(); }
    }
    private static bool Within(string root,string target)
    {
        root=Path.TrimEndingDirectorySeparator(root);
        target=Path.TrimEndingDirectorySeparator(target);
        // Drive/UNC filesystem roots keep their trailing separator when trimmed.
        // Appending another would reject every descendant of a drive such as D:\.
        var prefix=Path.EndsInDirectorySeparator(root)?root:root+Path.DirectorySeparatorChar;
        return target.Equals(root,PathComparison)||target.StartsWith(prefix,PathComparison);
    }
}
