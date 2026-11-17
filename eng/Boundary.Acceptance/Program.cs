using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FileMapper.Collect.FileSystem;
using FileMapper.Domain;
using FileMapper.Engine;
using FileMapper.Storage;

if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Drive-root/UNC regression requires Windows path semantics.");
if(args.Length!=2)throw new ArgumentException("Usage: new-fixture-directory report.json");
var fixture=Path.GetFullPath(args[0]);
if(Directory.Exists(fixture)&&Directory.EnumerateFileSystemEntries(fixture).Any())throw new ArgumentException("Fixture must be new or empty.");
Directory.CreateDirectory(fixture);
var within=typeof(FileSystemCollector).GetMethod("Within",BindingFlags.Static|BindingFlags.NonPublic)!;
var cases=new (string Name,string Root,string Target,bool Expected)[]
{
 ("drive-root-self",@"D:\",@"D:\",true),
 ("drive-root-child",@"D:\",@"D:\folder",true),
 ("drive-root-grandchild",@"D:\",@"D:\folder\child\file.txt",true),
 ("drive-case-insensitive",@"D:\",@"d:\Folder",true),
 ("different-drive",@"D:\",@"E:\folder",false),
 ("drive-relative-is-not-child",@"D:\",@"D:folder",false),
 ("directory-self-trailing",@"C:\scope\",@"C:\scope",true),
 ("directory-child",@"C:\scope",@"C:\scope\child",true),
 ("sibling-prefix",@"C:\scope",@"C:\scope-other\child",false),
 ("unc-root-self",@"\\server\share\",@"\\server\share\",true),
 ("unc-root-child",@"\\server\share\",@"\\server\share\folder\child",true),
 ("unc-root-no-trailing",@"\\server\share",@"\\server\share\folder",true),
 ("unc-share-prefix",@"\\server\share",@"\\server\share-other\child",false),
 ("unc-other-host",@"\\server\share",@"\\other\share\child",false),
 ("unc-directory-prefix",@"\\server\share\scope",@"\\server\share\scope2\child",false),
 ("extended-drive-child",@"\\?\D:\",@"\\?\D:\folder\child",true),
 ("extended-unc-child",@"\\?\UNC\server\share\",@"\\?\UNC\server\share\folder",true),
 ("extended-unc-prefix",@"\\?\UNC\server\share",@"\\?\UNC\server\share-other\folder",false)
};
var rows=cases.Select(c=>new{c.Name,c.Root,c.Target,c.Expected,Actual=(bool)within.Invoke(null,[c.Root,c.Target])!}).ToArray();
if(rows.Any(r=>r.Expected!=r.Actual))throw new Exception("Boundary regression: "+JsonSerializer.Serialize(rows.Where(r=>r.Expected!=r.Actual)));

// A real drive-root scan over an owned local fixture reproduces the failing ancestor path.
// The only drive mapping created is an unused letter R: through Z:, never D:.
var data=Path.Combine(fixture,"drive-data");Directory.CreateDirectory(Path.Combine(data,"one","two"));
File.WriteAllBytes(Path.Combine(data,"root.bin"),[1,2,3]);File.WriteAllBytes(Path.Combine(data,"one","middle.bin"),[4,5,6,7]);File.WriteAllBytes(Path.Combine(data,"one","two","deep.bin"),[8,9,10,11,12]);
var letter=Enumerable.Range('R',9).Select(x=>((char)x)+":").FirstOrDefault(d=>QueryDosDeviceW(d,new StringBuilder(32768),32768)==0&&Marshal.GetLastWin32Error()==2)??throw new IOException("No unused fixture drive letter.");
void Subst(params string[] values)
{
 var info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"subst.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};foreach(var value in values)info.ArgumentList.Add(value);
 using var process=Process.Start(info)!;var stdout=process.StandardOutput.ReadToEnd();var stderr=process.StandardError.ReadToEnd();process.WaitForExit();if(process.ExitCode!=0)throw new IOException("Fixture drive mapping failed: "+stdout+stderr);
}
Subst(letter,data);
var scanResults=new List<object>();
try
{
 var root=letter+"\\";var store=new MapStore(Path.Combine(fixture,"boundary.db"));store.Initialize();await using var coordinator=new ScanCoordinator(store);
 foreach(var policy in new[]{"skip","reference","within","all"})
 {
  var job=coordinator.StartScan(new(root,policy,BatchSize:1,HashFiles:true));var completed=await coordinator.WaitAsync(job.Id);var snapshot=store.GetSnapshot(job.SnapshotId)!;
  var entries=store.StreamEntries(snapshot.Id).ToArray();
  if(completed.Status!="completed"||snapshot.EntryCount!=6||snapshot.FileCount!=3||snapshot.DirectoryCount!=3||snapshot.LogicalBytes!=12||snapshot.ErrorCount!=0||snapshot.Completeness!="complete"||entries.Where(e=>e.Kind=="File").Any(e=>e.Hash is null))throw new Exception("Durable drive-root scan failed: "+JsonSerializer.Serialize(snapshot));
  scanResults.Add(new{policy,snapshot,hashedFiles=entries.Count(e=>e.Kind=="File"&&e.Hash is not null)});
 }
 var directEntries=new List<FileEntry>();var errors=new List<Evidence>();
 await foreach(var batch in new FileSystemCollector().CollectAsync("direct-boundary",new(root,"within",BatchSize:1,HashFiles:true),default)){directEntries.AddRange(batch.Entries);errors.AddRange(batch.Observations.Where(e=>e.ErrorCode is not null));}
 if(directEntries.Count!=6||directEntries.Count(e=>e.Kind=="File")!=3||errors.Count!=0)throw new Exception("Direct collector drive-root regression failed.");
 var report=new{boundaryCases=rows,driveLetter=letter,driveFixture=data,durableScans=scanResults,directCollector=new{entryCount=directEntries.Count,fileCount=3,errorCount=errors.Count},dDriveAccess="No D: access; D: appears only in lexical boundary cases.",limitations="UNC cases validate path predicates only; no SMB server/share was mounted. Real traversal uses a temporary SUBST drive over an owned local fixture."};
 File.WriteAllText(args[1],JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"PASS: {rows.Length} boundary cases; four durable drive-root policies; direct collector; all expected hashes, counts and completeness.");
}
finally
{
 var target=new StringBuilder(32768);if(QueryDosDeviceW(letter,target,32768)>0&&target.ToString().Equals(@"\??\"+data,StringComparison.OrdinalIgnoreCase))Subst(letter,"/D");
 else throw new IOException("Fixture drive mapping changed; refusing to remove another mapping.");
}
[DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern uint QueryDosDeviceW(string device,StringBuilder target,int size);
