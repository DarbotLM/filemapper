using FileMapper.Storage;
using Microsoft.Data.Sqlite;
var file=Path.Combine(Path.GetTempPath(),"filemapper-finalization-"+Guid.NewGuid().ToString("N")+".db"); var store=new MapStore(file); store.Initialize();
foreach(var status in new[]{"completed","failed","cancelled","paused"}) foreach(var fixture in new[]{"empty","mixed","known","unknown","error"}){
 var snap=store.CreateSnapshot("fixture");
 using(var db=new SqliteConnection("Data Source="+file+";Pooling=False")){db.Open();
 void Add(string path,string id,string kind,long logical,long? alloc){using var c=db.CreateCommand();c.CommandText="INSERT INTO entries(snapshot_id,path,node_id,name,kind,logical_bytes,allocated_bytes,modified_at,attributes,link_count) VALUES($s,$p,$n,$p,$k,$l,$a,'t','',1)";c.Parameters.AddWithValue("$s",snap.Id);c.Parameters.AddWithValue("$p",path);c.Parameters.AddWithValue("$n",id);c.Parameters.AddWithValue("$k",kind);c.Parameters.AddWithValue("$l",logical);c.Parameters.AddWithValue("$a",alloc.HasValue?(object)alloc.Value:DBNull.Value);c.ExecuteNonQuery();}
 if(fixture=="mixed"){Add("1","f","File",10,5);Add("2","f","Stream",12,null);Add("3","f","File",9,7);Add("4","f","Directory",0,null);Add("5","f","Directory",0,null);Add("6","z","File",0,0);Add("7","s","Stream",4,4);Add("8","x","Symlink",99,99);}
 if(fixture=="known"||fixture=="error"){Add("1","a","File",2,3);Add("2","a","Stream",2,3);Add("3","d","Directory",0,null);}
 if(fixture=="unknown"){Add("1","a","File",2,null);Add("2","a","File",2,3);}
 if(fixture=="error"){using var c=db.CreateCommand();c.CommandText="INSERT INTO observations VALUES($s,'err','x','p','E','incomplete','t','{}')";c.Parameters.AddWithValue("$s",snap.Id);c.ExecuteNonQuery();}
 }
 store.FinishSnapshot(snap.Id,status);var got=store.GetSnapshot(snap.Id)!;
 var expected=fixture switch {"empty"=>(0L,0L,0L,0L,(long?)0,0L,false),"mixed"=>(8L,2L,1L,16L,(long?)null,0L,true),"known"=>(3L,1L,1L,2L,(long?)3,0L,false),"unknown"=>(2L,1L,0L,2L,(long?)null,0L,false),_=>(3L,1L,1L,2L,(long?)3,1L,true)};
 var actual=(got.EntryCount,got.FileCount,got.DirectoryCount,got.LogicalBytes,got.AllocatedBytes,got.ErrorCount,got.Completeness=="incomplete");
 expected.Item7=expected.Item7 || status!="completed";
 if(actual!=expected || got.Status!=status)throw new Exception($"{status}/{fixture} expected {expected}, got {actual}");
 var finished=got.FinishedAt;store.FinishSnapshot(snap.Id,status);if(store.GetSnapshot(snap.Id)!.FinishedAt!=finished)throw new Exception("retry timestamp changed");
 Console.WriteLine($"PASS {status}/{fixture}: {actual}");
}
