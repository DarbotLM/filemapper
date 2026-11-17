/* Load spatial-renderer.js first. Live API adapter; no polling or inferred trajectories. */
(function (global) {
  'use strict';
  const core=global.FileMapperSpatial;
  if(!core?.mount)throw new Error('Load spatial-renderer.js before spatial-live.js.');
  const hosts=new WeakMap(), LIMIT=12;
  const kinds=['Function','SourceDocument','Symbol','Package','PackageVersion','Repository','Commit','Owner','Artifact','BuildInvocation','Project','TaintSink'];
  const categories=[['I','File / stream / artifact','#6b9faf'],['O','Directory / repository / project','#526d90'],['T','Method / function / symbol','#aa8270'],['L','Package / build target','#91c7df'],['J','Contributor / owner','#285e96'],['S','Branch / reference / reparse point','#dfbf7f'],['Z','Finding / diagnostic','#88b99e'],['stair','Commit / change / build invocation','#000000'],['pillar','Other kinds / snapshot','#64deff']];
  const make=(tag,cls,text)=>{const e=document.createElement(tag);if(cls)e.className=cls;if(text!=null)e.textContent=String(text);return e;};
  const stamp=v=>v==null||v===''?'Unknown':String(v);
  const metric=v=>typeof v==='number'&&Number.isFinite(v)?v:null;
  const shape=kind=>{const k=String(kind).toLowerCase();return ['file','stream','artifact','repositoryfile'].includes(k)?'I':['directory','repository','solution','project'].includes(k)?'O':['method','function','symbol','command'].includes(k)?'T':['package','packageversion','sbomcomponent','buildtarget'].includes(k)?'L':['contributor','owner','user'].includes(k)?'J':['branch','tag','reference','externalreference','reparsepoint'].includes(k)?'S':['finding','diagnostic','error'].includes(k)?'Z':['commit','change','buildinvocation'].includes(k)?'stair':'pillar';};
  const color=key=>categories.find(x=>x[0]===key)[2];
  const pathKeyCanonical=value=>String(value||'').replace(/\//g,'\\').replace(/\\+$/,'').toLowerCase();
  const rootKey=s=>pathKeyCanonical(s.root);
  function pathKey(value){let hash=14695981039346656037n;for(const character of String(value)){hash^=BigInt(character.codePointAt(0));hash=BigInt.asUintN(64,hash*1099511628211n);}return hash.toString(16).padStart(16,'0');}
  function destroy(host){hosts.get(host)?.destroy();}
  async function render(host,options={}){
    if(!(host instanceof Element))throw new TypeError('Spatial host must be an element.');
    destroy(host);host.replaceChildren();host.classList.add('fml-host');
    let disposed=false,sequence=0,view=null,abort=null,selectedIds=new Set(),lastObjects=[];
    const snapshot=options.snapshot, snapshots=Array.isArray(options.snapshots)?options.snapshots:[];
    const selectedId=String(snapshot?.id||'');
    const controller={destroy(){if(disposed)return;disposed=true;sequence++;abort?.abort();view?.destroy();host.replaceChildren();if(hosts.get(host)===controller)hosts.delete(host);},refresh:()=>load()};
    hosts.set(host,controller);
    if(!snapshot?.id){host.append(make('div','fml-empty','No snapshot selected. Select an observed snapshot to load its filesystem and semantic objects.'));return controller;}
    if(typeof options.api!=='function')throw new TypeError('Spatial adapter needs the authenticated api(url, options) function.');
    const sameRoot=[snapshot,...snapshots.filter(s=>s?.id&&s.id!==snapshot.id&&rootKey(s)===rootKey(snapshot))];
    const distinct=[...new Map(sameRoot.map(s=>[s.id,s])).values()];
    const recent=distinct.sort((a,b)=>(Date.parse(b.startedAt)||0)-(Date.parse(a.startedAt)||0)).slice(0,6);
    if(!recent.some(s=>s.id===snapshot.id))recent.push(snapshot);
    selectedIds.add(selectedId);
    const heading=make('details','fml-about');heading.append(make('summary','','How to read this scene'),make('p','','Select a numbered shape or its glass card to inspect the same source. Shape and color follow the native category catalog; distance and size are presentation, not measurements. Vertical layers are recorded snapshots, spaced by order rather than elapsed time.'));
    const controls=make('div','fml-toolbar'),layers=make('fieldset','fml-layers');layers.append(make('legend','','Observed snapshot layers · up to three'));
    const checks=[];
    recent.forEach(s=>{const label=make('label','fml-layer'),input=make('input');input.type='checkbox';input.value=s.id;input.checked=s.id===snapshot.id;input.disabled=s.id===snapshot.id;const text=make('span','',`${s.id===snapshot.id?'Selected · ':''}${stamp(s.startedAt)} · ${s.id.slice(0,8)}`);label.append(input,text);layers.append(label);checks.push(input);input.addEventListener('change',()=>{if(input.checked&&selectedIds.size>=3){input.checked=false;status.textContent='At most three snapshot layers can be loaded at once.';return;}if(input.checked)selectedIds.add(s.id);else selectedIds.delete(s.id);load();});});
    const kindLabel=make('label','fml-kind','Nodes '),kind=make('select');kind.setAttribute('aria-label','Spatial semantic category');kinds.forEach(k=>{const option=make('option','',k);option.value=k;kind.append(option);});kindLabel.append(kind);kind.addEventListener('change',()=>load());
    const refresh=make('button','fml-button','↻ Refresh');refresh.type='button';refresh.addEventListener('click',()=>load());
    const layerMenu=make('details','fml-layer-menu'),layerSummary=make('summary','','Layers · 1');layerMenu.append(layerSummary,layers);controls.append(kindLabel,layerMenu,refresh);
    const status=make('p','fml-status');status.setAttribute('role','status');status.setAttribute('aria-live','polite');
    const times=make('div','fml-times'),legend=make('details','fml-legend');legend.append(make('summary','','Nine canonical shapes · category legend'));
    const legendBody=make('div','fml-legend-grid');categories.forEach(([key,label,tint])=>{const item=make('span');item.style.setProperty('--shape-color',tint);item.append(make('b','',key),document.createTextNode(' '+label));legendBody.append(item);});legend.append(legendBody);
    const stage=make('div','fml-stage'),selection=make('section','fml-selection');selection.setAttribute('aria-label','Selected spatial object');
    const note=make('p','fml-note','Bounded view: at most 12 hierarchy locations and 12 native semantic objects per layer, plus snapshot metadata (75 objects across three layers). Renderer view-instance IDs distinguish displayed locations and layers; they are not native SOM identities. Native SOM IDs and observation IDs are shown separately when supplied by the backend. No inferred movement or session context. Snapshot timestamps are observation windows, not exact per-object observation times. Separate requests during an active scan are not an atomic transaction.');
    const layerPanel=make('div','fml-layer-panel');layerPanel.append(layers,times);
    heading.append(note);layerMenu.append(layerPanel);host.append(controls,status,stage,selection,legend,heading);
    function record(s,layerIndex,kindName,id,label,value,field,url,raw,sourceType){
      const key=shape(kindName),ref=`snapshot:${s.id}:${sourceType}`;
      return {somId:`${s.id}:${id}`,viewInstanceId:`${s.id}:${id}`,identityScope:'renderer view instance; not native SOM identity',somshape:key,shape_type:key,shape_color:color(key),some_type:kindName,label:label||id,arm:`Layer ${layerIndex+1} · ${stamp(s.startedAt)} · ${s.id.slice(0,8)}`,status:s.completeness||s.status||'Unknown',value,valueSource:value==null?undefined:{evidenceRef:ref,field,value},evidenceRefs:[{id:ref,label:`Snapshot ${s.id} · ${field}`,path:url}],observedAt:`Started ${stamp(s.startedAt)}; finished ${stamp(s.finishedAt)}; exact object observation time unknown`,layer:`${layerIndex+1} · ${s.id}`,layerIndex,schemaPath:field,snapshot:s,raw,sourceType};
    }
    async function load(){
      if(disposed)return;const request=++sequence;abort?.abort();abort=new AbortController();const signal=abort.signal;
      const timeout=setTimeout(()=>abort?.signal===signal&&abort.abort(),45000);
      refresh.disabled=true;status.classList.remove('fml-error');status.textContent='Loading observed shapes…';
      const selected=recent.filter(s=>selectedIds.has(s.id)).sort((a,b)=>(Date.parse(a.startedAt)||0)-(Date.parse(b.startedAt)||0));
      try{
        const loaded=await Promise.all(selected.map(async s=>{
          // Pin every request to the captured ID; a host endpoint closure may change with workspace selection.
          const base=`/api/snapshots/${encodeURIComponent(s.id)}/`;
          const hierarchyUrl=base+`hierarchy?limit=${LIMIT}&offset=0`,nodesUrl=base+`semantic-objects?kind=${encodeURIComponent(kind.value)}&limit=${LIMIT}&offset=0`;
          const responses=await Promise.allSettled([options.api(hierarchyUrl,{signal}),options.api(nodesUrl,{signal}),options.api(base.slice(0,-1),{signal})]);
          const metadata=responses[2].status==='fulfilled'&&responses[2].value?.id===s.id?responses[2].value:null;
          const fresh=metadata||{id:s.id,root:s.root,logicalBytes:null,allocatedBytes:null,startedAt:null,finishedAt:null,completeness:'Unknown',status:'Metadata unavailable'};
          const errors=responses.flatMap((r,i)=>r.status==='rejected'?[`${['Hierarchy','Semantic','Snapshot metadata'][i]} request unavailable: ${r.reason?.name==='AbortError'?'cancelled or timed out':String(r.reason?.message||'request failed')}`]:[]);if(!metadata)errors.push('Snapshot metadata unavailable or ID mismatched; stale measures are not displayed.');
          return {s:fresh,hierarchyUrl,nodesUrl,hierarchy:responses[0].status==='fulfilled'?responses[0].value:null,nodes:responses[1].status==='fulfilled'?responses[1].value:null,errors};
        }));
        if(disposed||request!==sequence)return;
        const objects=[],edges=[],warnings=[];times.replaceChildren();
        loaded.forEach((data,layerIndex)=>{
          const s=data.s,rows=(data.hierarchy?.page?.items||[]).slice(0,LIMIT),nodes=(data.nodes?.items||[]).slice(0,LIMIT);
          const observed=make('article','fml-time');observed.append(make('strong','',`Layer ${layerIndex+1} · ${s.id.slice(0,8)} · ${s.completeness||'Unknown completeness'}`),make('span','',`Started: ${stamp(s.startedAt)}`),make('span','',`Finished: ${stamp(s.finishedAt)}`),make('span','',`${rows.length} of ${data.hierarchy?.page?.total??'unknown'} root locations · ${nodes.length} of ${data.nodes?.total??'unknown'} ${kind.value} nodes`));times.append(observed);
          data.errors.forEach(e=>warnings.push(`${s.id.slice(0,8)}: ${e}`));
          const amount=metric(s.logicalBytes),snapshotObject=record(s,layerIndex,'Snapshot','snapshot',s.root||s.id,amount==null?null:{amount,unit:'logical bytes'},'snapshot.logicalBytes',`/api/snapshots/${encodeURIComponent(s.id)}`,s,'snapshot');objects.push(snapshotObject);
          rows.forEach((row,index)=>{const item=row.item||row,metrics=row.metrics||item.directoryMetrics,isReparse=/reparse/i.test(item.kind)||/reparse/i.test(String(item.attributes||'')),isDirectory=/directory/i.test(item.kind)||(isReparse&&(/directory/i.test(String(item.attributes||''))||metric(metrics?.directoryCount)>0||metric(metrics?.entryCount)>1)),n=metric(isDirectory?metrics?.recursiveLogicalBytes:isReparse?null:item.logicalBytes),field=`hierarchy.page.items[${index}].${isDirectory?'metrics.recursiveLogicalBytes':isReparse?'item (link target size unknown)':'item.logicalBytes'}`;
            const id=`location:${encodeURIComponent(item.path||item.nodeId||String(index))}`;
            // Full path identity is retained separately when an unusually long path exceeds the renderer ID bound.
            const stableId=id.length<=110?id:`node:${item.nodeId||'unknown'}:${pathKey(item.path||id)}`;
            const displayKind=isReparse?(isDirectory?'Directory link / reparse scope':'File link / reparse scope'):item.kind||'File';
            const object=record(s,layerIndex,displayKind,stableId,(item.name||item.path)+(isReparse?' · link scope':''),n==null?null:{amount:n,unit:isReparse?'observed subtree bytes':'logical bytes'},field,data.hierarchyUrl,{...item,logicalBytes:isReparse?null:item.logicalBytes,allocatedBytes:isReparse?null:item.allocatedBytes,directoryMetrics:metrics},'entry');object.schemaPath=item.path||field;
            if(isReparse){object.somshape=object.shape_type='S';object.shape_color=color('S');object.measurementNote='Reparse/link scope: any value counts observed descendants only. Link target size is unknown; collector placeholder zero is not a target measurement.';}objects.push(object);
            if(pathKeyCanonical(item.parentPath)===pathKeyCanonical(s.root))edges.push({from:snapshotObject.somId,to:object.somId,label:'Observed root contains location',evidenceRefs:object.evidenceRefs});
          });
          nodes.forEach(native=>{const node=native.rawSourceNode;if(!node||!native.somId||!native.observationId){warnings.push('Native semantic object missing its source or identity; skipped.');return;}const object=record(s,layerIndex,native.some_type,`native:${native.somId}`,native.label,null,'semantic-objects.items (no scalar measurement)',data.nodesUrl,node,'node');object.somshape=native.somshape;object.shape_type=native.shape_type;object.shape_color=native.shape_color;object.nativeSomId=native.somId;object.nativeObservationId=native.observationId;object.nativeSemanticObject=native;if(native.observedWindow?.from||native.observedWindow?.to)object.observedAt=`Native observation from ${stamp(native.observedWindow.from)} to ${stamp(native.observedWindow.to)}; basis ${stamp(native.observedWindow.basis)}`;object.evidenceRefs.push(...(native.evidenceRefs||[]).map(ref=>({id:ref.id,label:ref.sourceField,path:data.nodesUrl})));object.schemaPath=native.sourceReference?.path||native.sourceReference?.nodeId;objects.push(object);});
          if(!rows.length&&!nodes.length)warnings.push(`${s.id.slice(0,8)}: metadata-only view; no hierarchy locations or ${kind.value} nodes returned. Empty categories do not prove an empty filesystem or completed analysis.`);
          else if(!nodes.length)warnings.push(`${s.id.slice(0,8)}: no ${kind.value} nodes in this category. Semantic analysis may be absent or this category may have no observations.`);
        });
        lastObjects=objects;view?.destroy();stage.replaceChildren();selection.replaceChildren();
        view=core.mount(stage,{objects,edges,sampled:true,layout:'snapshot-layers',onSelect(object){
          selection.replaceChildren(make('strong','',object.label),make('p','fml-path',object.raw.path||object.raw.root||object.raw.id||object.somId),make('p','',`Snapshot ${object.snapshot.id} · ${object.observedAt}`));
          selection.append(make('p','fml-path','View instance ID: '+object.viewInstanceId));
          const nativeButton=make('button','fml-button','Inspect native identity'),nativePanel=make('div','fml-native');nativeButton.type='button';
          nativeButton.addEventListener('click',async()=>{nativeButton.disabled=true;try{let native=object.nativeSemanticObject;if(!native&&object.sourceType!=='snapshot'){const sourceNodeId=object.raw.nodeId||object.raw.id,query=String(object.raw.path||object.raw.name||'').slice(0,512);const page=await options.api('/api/snapshots/'+encodeURIComponent(object.snapshot.id)+'/semantic-objects?limit=50&search='+encodeURIComponent(query));native=page.items?.find(item=>item.sourceReference?.nodeId===sourceNodeId);}if(disposed)return;nativePanel.replaceChildren();if(!native){nativePanel.append(make('p','','No matching native SOM object was returned in the bounded lookup. This view-instance ID is not a native identity.'));return;}nativePanel.append(make('p','fml-path','Native SOM ID: '+native.somId),make('p','fml-path','Observation ID: '+native.observationId));const details=make('details'),summary=make('summary','','Complete native object, word shapes, dimensions and hashes');details.append(summary,make('pre','',JSON.stringify(native,null,2)));nativePanel.append(details);}catch(error){nativePanel.textContent='Native identity unavailable: '+String(error?.message||error);}finally{nativeButton.disabled=false;}});selection.append(nativeButton,nativePanel);
          if(object.measurementNote)selection.append(make('p','',object.measurementNote));
          if(object.sourceType!=='snapshot'&&object.snapshot.id===selectedId){const callback=object.sourceType==='entry'?options.inspectEntry:options.inspectNode;if(typeof callback==='function'){const button=make('button','fml-button','Inspect supporting evidence');button.type='button';button.addEventListener('click',()=>Promise.resolve(callback(object.raw)).catch(error=>{status.textContent='Inspector unavailable: '+String(error?.message||error);}));selection.append(button);}}
          else if(object.snapshot.id!==selectedId)selection.append(make('p','','Historical layer: choose this snapshot in the workspace selector to open its full inspector.'));
        }});
        stage.querySelector('.fms-heading h3').textContent='Observed objects · layered map';stage.querySelector('.fms-snapshot').textContent='API snapshot data · manual refresh';
        stage.querySelector('.fms-stage-caption span:last-child').textContent='≤12 root + 12 native / layer · Y: snapshot order';
        const layerSelect=stage.querySelector('.fms-filterbar select'),armLabel=layerSelect?.closest('label')?.querySelector('span');if(armLabel)armLabel.textContent='Snapshot layer';if(layerSelect?.options[0])layerSelect.options[0].textContent='All layers';
        const layerOrder=stage.querySelector('.fms-filterbar select:last-child option[value="arm"]')||stage.querySelector('.fms-filterbar option[value="arm"]');if(layerOrder)layerOrder.textContent='Layer';
        layerSummary.textContent='Layers · '+loaded.length;status.textContent=warnings.length?warnings.join(' '):'';status.classList.toggle('fml-error',warnings.length>0);status.title=`Loaded ${objects.length} observed objects at ${new Date().toISOString()}`;
      }catch(error){if(!disposed&&request===sequence){status.classList.add('fml-error');status.textContent='Spatial data unavailable: '+String(error?.message||error);}}
      finally{clearTimeout(timeout);if(!disposed&&request===sequence)refresh.disabled=false;}
    }
    controller.getObjects=()=>lastObjects.slice();controller.getView=()=>view;
    await load();return controller;
  }
  global.FileMapperSpatial=Object.freeze({...core,render,destroy,liveVersion:'filemapper-spatial-live/2'});
})(window);
