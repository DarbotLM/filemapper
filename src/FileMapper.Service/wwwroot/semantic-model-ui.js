(function (global) {
  'use strict';
  const instances = new WeakMap(), PAGE = 50;
  const pieceColors={I:'#64deff',O:'#dfbf7f',T:'#aa9bcf',L:'#aa8270',J:'#6b9faf',S:'#88b99e',Z:'#cf8b9c',pillar:'#91c7df',stair:'#78a9f3'};
  const gameShapeCells={I:[[0,0],[1,0],[2,0],[3,0]],O:[[0,0],[1,0],[0,1],[1,1]],T:[[1,1],[0,0],[1,0],[2,0]],L:[[2,1],[0,0],[1,0],[2,0]],J:[[0,1],[0,0],[1,0],[2,0]],S:[[1,1],[2,1],[0,0],[1,0]],Z:[[0,1],[1,1],[1,0],[2,0]],pillar:[[0,0],[0,1],[0,2]],stair:[[0,0],[1,0],[2,0],[1,1],[2,1],[2,2]]};
  const make = (tag, cls, text) => { const n = document.createElement(tag); if (cls) n.className = cls; if (text !== undefined) n.textContent = String(text); return n; };
  const json = value => JSON.stringify(value, null, 2);
  const shown = value => value === null || value === undefined ? 'Not observed' : typeof value === 'object' ? json(value) : String(value);
  const encoded = value => encodeURIComponent(String(value));
  const color = value => typeof value === 'string' && /^#[0-9a-f]{6}$/i.test(value) ? value : '#91c7df';
  const short = value => String(value || '').length > 34 ? String(value).slice(0, 16) + '…' + String(value).slice(-12) : String(value || 'Not observed');
  function download(value, name) { const blob = new Blob([json(value)], {type:'application/json'}), url = URL.createObjectURL(blob), a = make('a'); a.href = url; a.download = name; document.body.append(a); a.click(); a.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000); }
  function rawDetails(title, value) { const d = make('details', 'fmm-json'); d.append(make('summary', '', title), make('pre', '', json(value))); return d; }
  function empty(host, title, detail) { host.replaceChildren(); const n = make('div', 'fmm-empty'); n.append(make('strong', '', title), make('p', '', detail)); host.append(n); }
  function destroy(host) { instances.get(host)?.destroy(); }
  async function render(host, options = {}) {
    if (!(host instanceof Element)) throw new TypeError('Semantic model needs a DOM host.');
    if (typeof options.api !== 'function') throw new TypeError('Semantic model needs the authenticated api function.');
    const snapshot = options.snapshot, api = options.api, key = String(snapshot?.id || '') + '|' + [...(options.capabilities || [])].sort().join(',');
    const current = instances.get(host); if (current?.key === key) { current.updateSnapshots(options.snapshots || []); return current; }
    destroy(host); host.replaceChildren();
    let disposed = false, revision = 0, patternRevision = 0, objectOffset = 0, activeTab = 'objects', snapshots = options.snapshots || [];
    let challenge = null, game = null, moves = [], gameBusy = false, gameRound = 1, gameRevision = 0, camera = {yaw:-24,tilt:14,zoom:1}, boardFrame = 0, drag = null;
    const cleanup = [], aborters = new Set();
    function on(target, event, handler, opts) { target.addEventListener(event, handler, opts); cleanup.push(() => target.removeEventListener(event, handler, opts)); }
    async function request(url, opts = {}) { const c = new AbortController(); aborters.add(c); try { return await api(url, {...opts, signal:c.signal}); } finally { aborters.delete(c); } }
    const root = make('section', 'fmm-root'), disclosure = make('details', 'fmm-disclosure');
    disclosure.append(make('summary', '', 'How shapes represent evidence'), make('p', '', 'The large shape identifies the source object category. The smaller shapes encode actual retained words from that object. Geometry and color follow the native model; shape size does not measure bytes or importance. Nine-bit signatures are collision-prone lexical features. Stable IDs and full hashes preserve distinctions.'));
    host.append(root);
    const tabs = make('div', 'fmm-tabs'); tabs.setAttribute('role','tablist'); tabs.setAttribute('aria-label','Semantic model views'); const panes = {}, tabButtons = {};
    const status = make('div','fmm-status'); status.setAttribute('role','status'); status.setAttribute('aria-live','polite');
    const tabStatus={objects:{text:'',error:false},patterns:{text:'Choose an earlier and later snapshot, then compare their observed patterns.',error:false},game:{text:'Load a recorded challenge to start a verified replay.',error:false}};
    function showStatus(){const value=tabStatus[activeTab];status.textContent=value.text;status.classList.toggle('error',value.error);}
    function message(text,error=false,tab=activeTab){if(disposed)return;tabStatus[tab]={text,error};if(tab===activeTab)showStatus();}
    const objectMessage=(text,error=false)=>message(text,error,'objects'),patternMessage=(text,error=false)=>message(text,error,'patterns'),gameMessage=(text,error=false)=>message(text,error,'game');
    for (const [id, title] of [['objects','Semantic objects'],['patterns','Across snapshots'],['game','9bit replay']]) { const b = make('button','fmm-button',title); b.type='button'; b.setAttribute('role','tab'); b.setAttribute('aria-selected',String(id==='objects')); b.tabIndex=id==='objects'?0:-1; const pane=make('section','fmm-pane'); pane.hidden=id!=='objects'; pane.setAttribute('role','tabpanel'); pane.setAttribute('aria-label',title); panes[id]=pane; tabButtons[id]=b; on(b,'click',()=>switchTab(id)); on(b,'keydown',e=>{ const ids=Object.keys(tabButtons), i=ids.indexOf(id); if(['ArrowLeft','ArrowRight','Home','End'].includes(e.key)){e.preventDefault();const next=e.key==='Home'?ids[0]:e.key==='End'?ids.at(-1):ids[(i+(e.key==='ArrowRight'?1:-1)+ids.length)%ids.length];tabButtons[next].focus();switchTab(next);} }); tabs.append(b); }
    const topbar=make('div','fmm-topbar');topbar.append(tabs,disclosure);root.append(topbar,status,...Object.values(panes));
    function button(title, action, cls='fmm-button') { const b=make('button',cls,title);b.type='button';b.addEventListener('click',action);return b; }
    function field(title, control, wide=false) { const label=make('label','fmm-field'+(wide?' wide':''));label.append(make('span','',title),control);control.setAttribute('aria-label',title);return label; }
    const objectForm=make('form','fmm-toolbar'), search=make('input'), kind=make('input');search.type='search';search.placeholder='Label, path, or lexical token';kind.placeholder='All kinds · e.g. Function';
    const apply=make('button','fmm-button primary','Apply filters');apply.type='submit';objectForm.append(field('Find semantic objects',search,true),field('Source node kind',kind),apply);
    const objectScope=make('div','fmm-scope'), layout=make('div','fmm-layout'), cards=make('div','fmm-cards'), inspector=make('aside','fmm-inspector');inspector.setAttribute('aria-label','Semantic object details');layout.append(cards,inspector);
    const previous=button('← Previous',()=>{objectOffset=Math.max(0,objectOffset-PAGE);loadObjects();}), next=button('Next →',()=>{objectOffset+=PAGE;loadObjects();}), pageInfo=make('span','fmm-small'), pager=make('div','fmm-pager'), pageActions=make('div');pageActions.append(previous,next);pager.append(pageInfo,pageActions);
    const limits=make('div');panes.objects.append(objectForm,objectScope,layout,pager,limits);
    on(objectForm,'submit',e=>{e.preventDefault();objectOffset=0;loadObjects();});
    const compareForm=make('form','fmm-toolbar'), before=make('select'), after=make('select'), compare=make('button','fmm-button primary','Compare observed patterns');compare.type='submit';compareForm.append(field('Earlier snapshot',before),field('Later snapshot',after),compare);
    const patternScope=make('div','fmm-scope'), patternResults=make('div','fmm-patterns');panes.patterns.append(make('p','fmm-note','Identity continuity and shared lexical patterns are different matches. A matching word or nine-bit feature does not establish the same file, causal influence, ownership, or movement.'),compareForm,patternScope,patternResults);on(compareForm,'submit',e=>{e.preventDefault();loadPatterns();});
    let comparisonEdited=false;
    const snapshotTime=s=>Date.parse(s?.startedAt);
    function comparisonSnapshots(){return [...new Map([snapshot,...snapshots].filter(s=>s?.id).map(s=>[s.id,s])).values()];}
    on(before,'change',()=>{comparisonEdited=true;});on(after,'change',()=>{comparisonEdited=true;});
    function fillSnapshots(){
      const list=comparisonSnapshots().sort((a,b)=>(snapshotTime(b)||0)-(snapshotTime(a)||0)),a=before.value,b=after.value;
      const oldBefore=list.find(s=>s.id===a),oldAfter=list.find(s=>s.id===b);
      const preserve=comparisonEdited&&a!==b&&oldBefore&&oldAfter&&Number.isFinite(snapshotTime(oldBefore))&&Number.isFinite(snapshotTime(oldAfter))&&snapshotTime(oldBefore)<=snapshotTime(oldAfter);
      before.replaceChildren();after.replaceChildren();
      for(const s of list){const label=(Number.isFinite(snapshotTime(s))?new Date(s.startedAt).toLocaleString():'Unknown time')+' · '+s.id.slice(0,8)+' · '+s.root;before.append(new Option(label,s.id));after.append(new Option(label,s.id));}
      if(preserve){before.value=a;after.value=b;}
      else{
        const selected=list.find(s=>s.id===snapshot?.id),selectedTime=snapshotTime(selected);
        const older=list.filter(s=>s.id!==selected?.id&&snapshotTime(s)<selectedTime).sort((x,y)=>snapshotTime(y)-snapshotTime(x))[0];
        const newer=list.filter(s=>s.id!==selected?.id&&snapshotTime(s)>selectedTime).sort((x,y)=>snapshotTime(x)-snapshotTime(y))[0];
        const simultaneous=list.find(s=>s.id!==selected?.id&&snapshotTime(s)===selectedTime);
        before.value=older?.id||selected?.id||list.at(-1)?.id||'';
        after.value=older?selected.id:newer?.id||simultaneous?.id||selected?.id||list[0]?.id||'';
        comparisonEdited=false;
      }
      compare.disabled=list.length<2;
    }
    const gameToolbar=make('div','fmm-toolbar'), round=make('select');for(let i=1;i<=3;i++)round.append(new Option('Round '+i,String(i)));const loadGameButton=button('Load challenge',()=>loadGame(),'fmm-button primary');gameToolbar.append(field('Recorded challenge round',round),loadGameButton);
    const gameScope=make('div','fmm-scope'), gameLayout=make('div','fmm-game-layout'), stage=make('div','fmm-board-stage'), canvas=make('canvas','fmm-board');canvas.tabIndex=0;canvas.setAttribute('role','img');canvas.setAttribute('aria-label','Orbitable replay board. Arrow keys orbit; plus and minus zoom; Home resets camera.');
    const stageCaption=make('div','fmm-stage-footer','No challenge loaded. Occupied cells will come from the replay API.');stage.append(canvas,stageCaption);
    const controls=make('div','fmm-controls'), piece=make('div','fmm-piece'), rotation=make('select'), column=make('select');for(let i=0;i<4;i++)rotation.append(new Option(i*90+'°',String(i)));const drop=button('Drop next piece',()=>dropPiece(),'fmm-button primary'), reset=button('Reset this replay',()=>resetReplay()), transcript=button('Download transcript',()=>download({schema:'filemapper-semantic-replay-ui/1',challenge,moves,state:game},'filemapper-semantic-replay-round-'+gameRound+'.json'));
    const scores=make('dl','fmm-score-grid');controls.append(make('h3','','Replay controls'),piece,field('Quarter-turn rotation',rotation),field('Leftmost board column',column),drop,reset,transcript,scores,make('p','fmm-note','Moves are replayed and scored by the native engine. This board is a puzzle projection of recorded semantic data; clearing a line never changes files.'));
    gameLayout.append(stage,controls); const cameras=make('div','fmm-camera');for(const [name,value] of [['Front',{yaw:0,tilt:0,zoom:1}],['Oblique',{yaw:-24,tilt:14,zoom:1}],['Side',{yaw:55,tilt:12,zoom:1}],['Reset camera',{yaw:-24,tilt:14,zoom:1}]])cameras.append(button(name,()=>{camera={...value};drawBoard();}));
    const gameDetails=make('div','fmm-breakdown'), gameEvidence=make('div'),gameHelp=make('details','fmm-disclosure fmm-game-help');
    gameHelp.append(make('summary','','Board controls and evidence limits'),make('p','','Drag or use arrow keys to orbit; + / − zoom; Home resets. Row zero is the bottom. One cube is one occupied board cell. Color identifies a piece category; depth and perspective carry no semantic score. Column choices fit the selected rotation within the board; the native engine still validates collisions, top-out, and scoring.'));
    panes.game.append(gameToolbar,gameHelp,gameScope,gameLayout,cameras,gameDetails,gameEvidence);
    on(rotation,'change',()=>renderPiece());
    const controller={key,updateSnapshots(value){snapshots=value;fillSnapshots();},refresh:()=>loadObjects(),destroy(){if(disposed)return;disposed=true;revision++;gameRevision++;for(const c of aborters)c.abort();aborters.clear();if(boardFrame)cancelAnimationFrame(boardFrame);cleanup.forEach(f=>f());root.remove();if(instances.get(host)===controller)instances.delete(host);}};
    instances.set(host,controller);fillSnapshots();updateGameButtons();
    function switchTab(id){activeTab=id;for(const key of Object.keys(panes)){panes[key].hidden=key!==id;tabButtons[key].setAttribute('aria-selected',String(key===id));tabButtons[key].tabIndex=key===id?0:-1;}showStatus();if(id==='game')drawBoard();}
    async function loadObjects(){if(!snapshot?.id){empty(cards,'No selected snapshot','Select an observed snapshot to inspect its native semantic objects.');empty(inspector,'No object selected','Stable identity and the complete observation envelope appear here.');return;}const generation=++revision;objectMessage('Loading native semantic objects…');apply.disabled=true;previous.disabled=next.disabled=true;const q=new URLSearchParams({offset:String(objectOffset),limit:String(PAGE)});if(search.value.trim())q.set('search',search.value.trim());if(kind.value.trim())q.set('kind',kind.value.trim());try{const result=await request('/api/snapshots/'+encoded(snapshot.id)+'/semantic-objects?'+q);if(disposed||generation!==revision)return;if(!Array.isArray(result.items))throw new Error('Semantic object response did not contain an items array.');objectScope.replaceChildren(make('span','','Snapshot '+snapshot.id.slice(0,8)),make('span','','Model '+shown(result.modelVersion)),make('span','','Completeness '+shown(result.snapshot?.completeness||snapshot.completeness)));cards.replaceChildren();empty(inspector,'Select an object','Flip a card to inspect stable identity, its observation ID, lexical features, hashes, and observed dimensions.');if(!result.items.length)empty(cards,'No matching semantic objects','Change the kind or text filter, or choose another observed snapshot.');for(const item of result.items)renderCard(item);pageInfo.textContent=result.total?`${objectOffset+1}–${objectOffset+result.items.length} of ${result.total} objects`:'0 objects';previous.disabled=objectOffset===0;next.disabled=objectOffset+result.items.length>=result.total;limits.replaceChildren(rawDetails('Projection limits and model scope',result.limits??{message:'No limits metadata returned'}));objectMessage(result.items.length+' objects loaded · inspect a card for full identity and evidence.');}catch(e){if(!disposed&&generation===revision){objectMessage(e.message,true);empty(cards,'Objects unavailable',e.message);}}finally{if(!disposed&&generation===revision)apply.disabled=false;}}
    function renderCard(item){
      const card=make('article','fmm-card'),inner=make('div','fmm-card-inner'),front=make('div','fmm-card-face'),back=make('div','fmm-card-face fmm-card-back'),preview=make('canvas','fmm-object-preview');
      preview.width=320;preview.height=160;preview.setAttribute('aria-hidden','true');
      const label=make('span','fmm-chip'),swatch=make('span','fmm-card-color');swatch.style.background=color(item.shape_color);label.append(swatch,document.createTextNode(shown(item.some_type)+' · '+shown(item.shape_type)));
      front.append(label,preview,make('h3','',item.label||item.somId));
      const allTokens=Array.isArray(item.some_shapes)?item.some_shapes:[],tokens=[...allTokens.filter(t=>t.sources?.includes('/name')),...allTokens.filter(t=>!t.sources?.includes('/name'))].slice(0,3),wordStrip=make('div','fmm-word-strip'),wordCanvases=[];
      wordStrip.setAttribute('aria-label',tokens.length+' recorded word shapes, name words first');
      for(const token of tokens){
        const word=make('div','fmm-word-preview'),picture=make('canvas');picture.setAttribute('aria-hidden','true');
        const name=make('strong','',token.token);name.title=String(token.token);
        word.append(picture,name,make('small','',shown(token.shape_type)+' · '+shown(token.count)+'×'),make('small','fmm-word-bits','9bit '+shown(token.signature9)));
        word.title=String(token.token)+' · '+shown(token.shape_type)+' · '+shown(token.count)+' occurrence(s) · signature9 '+shown(token.signature9)+' (collisions allowed)';
        wordStrip.append(word);wordCanvases.push([picture,token]);
      }
      front.append(make('p','fmm-word-caption',tokens.length?tokens.length+' of '+item.some_shapes.length+' word shapes · name first':'No retained lexical features'),wordStrip,make('p','fmm-card-code',short(item.somId)));
      back.append(make('strong','',item.label||item.somId),make('p','','Stable object: '+short(item.somId)),make('p','','Observation: '+short(item.observationId)),make('p','',String(item.some_shapes?.length||0)+' retained word shapes'),make('p','','Object geometry identifies its category. Word geometry and nine-bit flags encode lexical features; matching flags may collide.'));
      back.setAttribute('aria-hidden','true');inner.append(front,back);
      const flip=button('Inspect and flip ↗',()=>{const flipped=card.classList.toggle('is-flipped');front.setAttribute('aria-hidden',String(flipped));back.setAttribute('aria-hidden',String(!flipped));flip.setAttribute('aria-expanded',String(flipped));flip.textContent=flipped?'Return to shape ↶':'Inspect and flip ↗';selectObject(item);});
      flip.setAttribute('aria-expanded','false');card.append(inner,flip);card.dataset.somId=item.somId;cards.append(card);
      drawShape(preview,item.somshape?.shape||item.shape_type,item.shape_color);
      for(const [picture,token]of wordCanvases)drawShape(picture,token.somshape?.shape||token.shape_type,token.shape_color);
    }
    function selectObject(item){for(const c of cards.children)c.classList.toggle('selected',c.dataset.somId===item.somId);inspector.replaceChildren(make('h3','',item.label||item.somId));const dl=make('dl');function pair(k,v){dl.append(make('dt','',k),make('dd','',shown(v)));}pair('STABLE SOM ID · object identity',item.somId);pair('OBSERVATION ID · snapshot-specific',item.observationId);pair('Object / shape type',item.some_type+' / '+item.shape_type);pair('Shape color · category encoding',item.shape_color);pair('Source reference',item.sourceReference);pair('Snapshot window',item.snapshotWindow);pair('Observed window and basis',item.observedWindow);pair('Lexical retention bounds',item.bounds);inspector.append(dl,make('p','fmm-note','These SHA-256 hashes describe the semantic model and source metadata. contentHash is not a cryptographic hash of file contents.'));inspector.append(rawDetails('Full SHA-256 hashes',item.hashes),rawDetails('Observed dimensions · missing stays unknown',item.dimensions),rawDetails('Source evidence references',item.evidenceRefs));const words=make('details');words.open=true;words.append(make('summary','',`Word shapes · ${item.some_shapes?.length||0} retained`),make('p','fmm-note','A nine-bit signature has only 512 possible values. Equal signatures may collide and do not establish equal words or meaning. Word IDs and full object hashes remain distinct.'));const tokenList=make('div','fmm-token-list');for(const token of item.some_shapes||[]){const t=make('article','fmm-token');t.append(make('strong','',token.token),make('p','','Occurrences '+shown(token.count)+' · '+shown(token.shape_type)+' · '+shown(token.shape_color)));const bits=Number(token.signature9);if(Number.isInteger(bits)&&bits>=0&&bits<512){const bar=make('div','fmm-bitbar');bar.setAttribute('role','img');bar.setAttribute('aria-label','Nine-bit lexical signature '+bits.toString(2).padStart(9,'0')+'; collisions allowed');for(const bit of bits.toString(2).padStart(9,'0'))bar.append(make('span','fmm-bit'+(bit==='1'?' on':'')));t.append(bar);}t.append(make('p','','wordId '+shown(token.wordId)),make('p','','signature9 '+shown(token.signature9)+' · bits '+shown(token.signatureBits)),rawDetails('Features and source fields',{features:token.features,sources:token.sources,somshape:token.somshape}));tokenList.append(t);}words.append(tokenList);inspector.append(words,rawDetails('Raw source node',item.rawSourceNode),button('Download complete semantic object',()=>download(item,'filemapper-semantic-object.json')));}
    async function loadPatterns(){
      if(!before.value||!after.value||before.value===after.value){patternMessage('Choose two different observed snapshots.',true);return;}
      const selectedSnapshots=comparisonSnapshots(),beforeTime=snapshotTime(selectedSnapshots.find(s=>s.id===before.value)),afterTime=snapshotTime(selectedSnapshots.find(s=>s.id===after.value));
      if(!Number.isFinite(beforeTime)||!Number.isFinite(afterTime)){patternMessage('Both snapshots need recorded start times before chronological comparison.',true);return;}
      if(beforeTime>afterTime){patternMessage('Earlier snapshot is newer than Later snapshot. Choose the older observation first, or swap the selections.',true);return;}
      const generation=++patternRevision;compare.disabled=true;patternMessage('Comparing identity and lexical patterns…');
      try{
        const result=await request('/api/semantic-patterns?'+new URLSearchParams({before:before.value,after:after.value,limit:'50'}));
        if(disposed||generation!==patternRevision)return;
        patternScope.replaceChildren(make('span','','Before '+shown(result.before?.id||before.value)),make('span','','After '+shown(result.after?.id||after.value)),make('span','','Matching basis: '+shown(result.matchedBy)));
        patternResults.replaceChildren();
        if(!result.items?.length)empty(patternResults,'No matches in the returned projection','No continuity or lexical relationship is inferred beyond the result scope.');
        for(const item of result.items||[]){
          const panel=make('article','fmm-pattern');
          panel.append(make('span','fmm-chip',item.matchType==='stable-identity'?'Stable identity match':item.matchType==='schema-path-pattern'?'Lexical path pattern · distinct from identity':shown(item.matchType)),make('h3','',item.after?.label||item.before?.label||'Observed comparison'),make('p','fmm-note',shown(item.explanation)));
          const sides=make('div','fmm-pattern-sides');
          for(const [label,object]of [['BEFORE',item.before],['AFTER',item.after]]){const side=make('div','fmm-pattern-side');side.append(make('strong','',label),make('p','',shown(object?.sourceReference?.path)),make('p','','somId '+shown(object?.somId)),make('p','','Observation '+shown(object?.observationId)));sides.append(side);}
          panel.append(sides,make('p','fmm-note','Identity equal: '+shown(item.identityEqual)+' · Pattern key: '+shown(item.patternKey)),rawDetails('Full pattern hash and comparison evidence',item));patternResults.append(panel);
        }
        patternResults.append(rawDetails('Comparison scope and limits',result.limits));
        patternMessage('Returned '+(result.items?.length||0)+' comparison records. '+(result.limits?.candidatesTruncated?'Candidate scope was truncated; this is not an exhaustive comparison.':'Inspect each match basis before interpreting continuity.'));
      }catch(e){if(!disposed&&generation===patternRevision)patternMessage(e.message,true);}finally{if(!disposed&&generation===patternRevision)compare.disabled=false;}
    }
    // Canvas drawing uses projected 3D vertices. The server owns game placement and scores.
    function drawShape(target,shape,shapeColor){let cells;try{cells=global.FileMapperSpatial?.shapeCells(shape,0,0);}catch{}if(!cells?.length){const ctx=target.getContext('2d');if(ctx){ctx.fillStyle='#adcddd';ctx.font='13px Consolas';ctx.fillText(shown(shape),12,45);}return;}paintCells(target,cells.map(([x,y,z])=>({x,y,z,color:color(shapeColor)})),{yaw:-28,tilt:22,zoom:1},{width:Math.max(...cells.map(c=>c[0]))+1,height:Math.max(...cells.map(c=>c[1]))+1,depth:Math.max(...cells.map(c=>c[2]))+1},false);}
    function paintCells(target,cells,view,bounds,grid){const ctx=target.getContext('2d');if(!ctx)return;const ratio=Math.min(2,global.devicePixelRatio||1),width=Math.max(32,target.clientWidth||320),height=Math.max(32,target.clientHeight||180);target.width=Math.round(width*ratio);target.height=Math.round(height*ratio);ctx.setTransform(ratio,0,0,ratio,0,0);ctx.clearRect(0,0,width,height);const yaw=view.yaw*Math.PI/180,tilt=view.tilt*Math.PI/180,scale=Math.min(width/(bounds.width+5),height/(bounds.height+4))*view.zoom,cx=bounds.width/2,cy=bounds.height/2,cz=(bounds.depth||1)/2;function project(p){const x=p[0]-cx,y=p[1]-cy,z=p[2]-cz;const rx=x*Math.cos(yaw)+z*Math.sin(yaw),rz=-x*Math.sin(yaw)+z*Math.cos(yaw),ry=y*Math.cos(tilt)-rz*Math.sin(tilt),depth=y*Math.sin(tilt)+rz*Math.cos(tilt),perspective=1/(1+depth/Math.max(40,bounds.height*4));return{x:width/2+rx*scale*perspective,y:height/2-ry*scale*perspective,z:depth};}function polygon(points,fill,stroke){ctx.beginPath();points.forEach((p,i)=>i?ctx.lineTo(p.x,p.y):ctx.moveTo(p.x,p.y));ctx.closePath();ctx.fillStyle=fill;ctx.fill();ctx.strokeStyle=stroke;ctx.lineWidth=.8;ctx.stroke();}if(grid){ctx.strokeStyle='#90bbd435';ctx.lineWidth=.8;for(let x=0;x<=bounds.width;x++){const a=project([x,0,-.06]),b=project([x,bounds.height,-.06]);ctx.beginPath();ctx.moveTo(a.x,a.y);ctx.lineTo(b.x,b.y);ctx.stroke();}for(let y=0;y<=bounds.height;y++){const a=project([0,y,-.06]),b=project([bounds.width,y,-.06]);ctx.beginPath();ctx.moveTo(a.x,a.y);ctx.lineTo(b.x,b.y);ctx.stroke();}}const faces=[];const definitions=[[[0,0,0],[1,0,0],[1,1,0],[0,1,0]],[[0,0,1],[0,1,1],[1,1,1],[1,0,1]],[[0,1,0],[1,1,0],[1,1,1],[0,1,1]],[[1,0,0],[1,0,1],[1,1,1],[1,1,0]],[[0,0,0],[0,1,0],[0,1,1],[0,0,1]],[[0,0,0],[0,0,1],[1,0,1],[1,0,0]]];for(const c of cells){for(let i=0;i<definitions.length;i++){const points=definitions[i].map(v=>project([c.x+v[0],c.y+v[1],c.z+v[2]*.8]));faces.push({points,z:points.reduce((a,p)=>a+p.z,0)/4,color:c.color,shade:i});}}faces.sort((a,b)=>b.z-a.z);for(const face of faces){polygon(face.points,face.color+'ce','#c8ecff83');if(face.shade>1)polygon(face.points,'#06101b'+(face.shade===2?'1c':'4d'),'#c8ecff20');}}
    function updateGameButtons(){loadGameButton.disabled=gameBusy||!snapshot?.id;round.disabled=gameBusy;rotation.disabled=column.disabled=drop.disabled=gameBusy||!game||game.isOver||game.nextPieceIndex>=challenge?.deck?.length;drop.disabled=drop.disabled||column.options.length===0;reset.disabled=gameBusy||!challenge;transcript.disabled=!game;}
    async function loadGame(){
      if(!snapshot?.id||gameBusy)return;
      const generation=++gameRevision,prior={challenge,game,moves,gameRound};gameBusy=true;updateGameButtons();gameMessage('Loading a challenge from the selected snapshot…');
      try{const value=await request('/api/snapshots/'+encoded(snapshot.id)+'/semantic-game/challenge?round='+encoded(round.value));if(disposed||generation!==gameRevision)return;challenge=value.challenge||value;gameRound=Number(round.value);await replay([],generation);}
      catch(e){if(!disposed&&generation===gameRevision){({challenge,game,moves,gameRound}=prior);if(game)renderGame();gameMessage(e.message,true);}}
      finally{if(!disposed&&generation===gameRevision){gameBusy=false;updateGameButtons();}}
    }
    async function replay(candidateMoves,generation){const result=await request('/api/semantic-game/replay',{method:'POST',body:{snapshotId:snapshot.id,round:gameRound,expectedChallengeHash:challenge.challengeHash,moves:candidateMoves}});if(disposed||generation!==gameRevision)return;const value=result.state||result;if(!Array.isArray(value.board)||!value.stateHash)throw new Error('Replay response is missing its authoritative board or state hash.');game=value;challenge=value.challenge||challenge;moves=candidateMoves;renderGame();gameMessage(game.isOver?'Replay finished: '+shown(game.endReason):'Replay verified by the native engine. '+game.nextPieceIndex+' piece(s) placed.');}
    async function dropPiece(){if(!game||gameBusy||game.isOver)return;const generation=++gameRevision;gameBusy=true;updateGameButtons();try{await replay([...moves,{rotation:Number(rotation.value),column:Number(column.value),expectedPriorStateHash:game.stateHash}],generation);}catch(e){if(!disposed&&generation===gameRevision)gameMessage(e.message,true);}finally{if(!disposed&&generation===gameRevision){gameBusy=false;updateGameButtons();}}}
    async function resetReplay(){if(!challenge||gameBusy)return;const generation=++gameRevision;gameBusy=true;updateGameButtons();try{await replay([],generation);}catch(e){if(!disposed&&generation===gameRevision)gameMessage(e.message,true);}finally{if(!disposed&&generation===gameRevision){gameBusy=false;updateGameButtons();}}}
    function renderPiece(){
      piece.replaceChildren();const next=challenge?.deck?.[game?.nextPieceIndex||0];
      if(!next){column.replaceChildren();piece.append(make('strong','','No next piece'),make('p','',game?.endReason||'Load a challenge to inspect its recorded deck.'));return;}
      const identity=make('p','','somId '+short(next.somId));identity.title=shown(next.somId);
      piece.append(make('strong','',next.label||next.id),identity,make('p','','Shape '+shown(next.shape)+' · rotation '+Number(rotation.value)*90+'°'));
      const p=make('canvas');p.setAttribute('aria-hidden','true');piece.append(p);
      let cells=gameShapeCells[next.shape]?.map(c=>[...c]);
      const priorColumn=Number(column.value)||0;column.replaceChildren();
      if(cells){
        for(let i=0;i<Number(rotation.value);i++)cells=cells.map(([x,y])=>[-y,x]);
        const minX=Math.min(...cells.map(c=>c[0])),minY=Math.min(...cells.map(c=>c[1]));cells=cells.map(([x,y])=>[x-minX,y-minY]);
        const width=Math.max(...cells.map(c=>c[0]))+1,height=Math.max(...cells.map(c=>c[1]))+1,lastColumn=challenge.width-width;
        for(let i=0;i<=lastColumn;i++)column.append(new Option(String(i),String(i)));
        if(lastColumn>=0)column.value=String(Math.min(priorColumn,lastColumn));
        paintCells(p,cells.map(([x,y])=>({x,y,z:0,color:pieceColors[next.shape]||'#91c7df'})),{yaw:-24,tilt:14,zoom:1},{width,height,depth:1},false);
        piece.append(make('p','',lastColumn>=0?`${width} × ${height} cells · legal left columns 0–${lastColumn}`:'This orientation does not fit the board.'));
      }
      const tokenDetail=make('details','fmm-piece-evidence');tokenDetail.append(make('summary','','Source tokens and identity · '+(next.tokens?.length||0)+' words'),make('p','',(next.tokens||[]).join(' · ')),make('p','','somId '+shown(next.somId)));piece.append(tokenDetail);updateGameButtons();
    }
    function renderGame(){
      gameScope.replaceChildren(make('span','','Challenge '+shown(challenge.id)),make('span','','Round '+gameRound),make('span','','Dataset '+shown(challenge.datasetVersion)));
      scores.replaceChildren();for(const [label,value]of [['Engine score',game.score],['Cleared lines',game.clearedLines],['Next piece index',game.nextPieceIndex],['Occupied cells',game.board.length]]){const d=make('div','fmm-score');d.append(make('dt','',label),make('dd','',shown(value)));scores.append(d);}
      renderPiece();drawBoard();
      gameDetails.replaceChildren(make('h3','','Score, moves, and learned associations'),make('p','fmm-note','Every score component below is returned by the native replay engine. Grounded points refer to supplied evidence. Inferred points describe replay-local patterns; they do not establish causal or general language understanding.'));
      const wrap=make('div','fmm-table-wrap fmm-transcript'),table=make('table','fmm-table'),head=make('thead'),hr=make('tr');
      for(const label of ['Move / piece','Landing row','Lines','Line points','Grounded pattern','Inferred pattern','Grounded relation','Inferred relation','Total']){const th=make('th','',label);th.scope='col';hr.append(th);}head.append(hr);table.append(head);const body=make('tbody');
      for(const step of game.history||[]){const score=step.scoreBreakdown||{},tr=make('tr');for(const value of [shown(step.index)+' · '+shown(step.pieceId),step.topOut?'Top out':shown(step.landingRow),score.lines,score.linePoints,score.groundedPatternPoints,score.inferredPatternPoints,score.groundedRelationshipPoints,score.inferredRelationshipPoints,score.total])tr.append(make('td','',shown(value)));body.append(tr);}table.append(body);wrap.append(table);gameDetails.append(wrap);
      if(!game.history?.length)gameDetails.append(make('p','fmm-note','No moves have been scored yet.'));
      const learning=make('div','fmm-learning');learning.append(make('h3','','Learned co-clear associations'));const list=make('ul');for(const item of game.learning||[])list.append(make('li','',shown(item.firstSomId)+' ↔ '+shown(item.secondSomId)+' · co-clear count '+shown(item.coClearCount)+' · '+shown(item.interpretation)));if(!game.learning?.length)learning.append(make('p','fmm-note','No co-clear associations have been recorded in this replay.'));else learning.append(list);gameDetails.append(learning,rawDetails('Complete move history · cleared words and evidence',game.history||[]),rawDetails('Learned association grounding',game.learning||[]),rawDetails('Board cells · accessible exact coordinates',game.board));
      const legend=make('div','fmm-legend');for(const shape of [...new Set((challenge.deck||[]).map(p=>p.shape))]){const n=make('span'),swatch=make('i','fmm-card-color');swatch.style.background=pieceColors[shape]||'#91c7df';n.append(swatch,document.createTextNode(shape));legend.append(n);}gameDetails.append(legend);
      gameEvidence.replaceChildren(rawDetails('Dataset, challenge, and replay integrity',{datasetHash:challenge.datasetHash,datasetSchema:challenge.datasetSchema,datasetVersion:challenge.datasetVersion,challengeHash:challenge.challengeHash,stateHash:game.stateHash,associations:challenge.associations,patterns:challenge.patterns}),rawDetails('Complete authoritative replay state',game));
      stageCaption.textContent=`${challenge.width} columns × ${challenge.height} rows · ${game.board.length} occupied cells · row 0 is bottom · ${game.isOver?'ended':'active'}`;updateGameButtons();
    }
    function drawBoard(){if(disposed)return;if(boardFrame)cancelAnimationFrame(boardFrame);boardFrame=requestAnimationFrame(()=>{boardFrame=0;const ids=new Map((challenge?.deck||[]).map(p=>[p.id,p.shape]));const cells=(game?.board||[]).map(c=>({x:c.column,y:c.row,z:0,color:pieceColors[ids.get(c.pieceId)]||'#91c7df'}));paintCells(canvas,cells,camera,{width:challenge?.width||12,height:challenge?.height||20,depth:1},true);});}
    on(canvas,'pointerdown',e=>{if(e.button!==0)return;drag={x:e.clientX,y:e.clientY};canvas.setPointerCapture?.(e.pointerId);canvas.classList.add('dragging');});on(canvas,'pointermove',e=>{if(!drag)return;camera.yaw=Math.max(-80,Math.min(80,camera.yaw+(e.clientX-drag.x)*.4));camera.tilt=Math.max(-60,Math.min(60,camera.tilt+(e.clientY-drag.y)*.3));drag={x:e.clientX,y:e.clientY};drawBoard();});on(canvas,'pointerup',()=>{drag=null;canvas.classList.remove('dragging');});on(canvas,'pointercancel',()=>{drag=null;canvas.classList.remove('dragging');});on(canvas,'keydown',e=>{if(!['ArrowLeft','ArrowRight','ArrowUp','ArrowDown','+','=','-','Home'].includes(e.key))return;e.preventDefault();if(e.key==='Home')camera={yaw:-24,tilt:14,zoom:1};if(e.key==='ArrowLeft')camera.yaw=Math.max(-80,camera.yaw-5);if(e.key==='ArrowRight')camera.yaw=Math.min(80,camera.yaw+5);if(e.key==='ArrowUp')camera.tilt=Math.min(60,camera.tilt+5);if(e.key==='ArrowDown')camera.tilt=Math.max(-60,camera.tilt-5);if(e.key==='+'||e.key==='=')camera.zoom=Math.min(2,camera.zoom+.1);if(e.key==='-')camera.zoom=Math.max(.6,camera.zoom-.1);drawBoard();});
    const observer=global.ResizeObserver?new ResizeObserver(()=>{if(activeTab==='game')drawBoard();}):null;observer?.observe(canvas);cleanup.push(()=>observer?.disconnect());renderPiece();await loadObjects();return controller;
  }
  global.FileMapperSemanticModel=Object.freeze({render,destroy,version:'filemapper-semantic-model-ui/1'});
})(window);
