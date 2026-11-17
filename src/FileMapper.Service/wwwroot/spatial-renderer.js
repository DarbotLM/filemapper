(function (global) {
  'use strict';
  const CELLS = Object.freeze({ I:[[0,0,0],[1,0,0],[2,0,0],[3,0,0]], O:[[0,0,0],[1,0,0],[0,1,0],[1,1,0]], T:[[1,1,0],[0,0,0],[1,0,0],[2,0,0]], L:[[2,1,0],[0,0,0],[1,0,0],[2,0,0]], J:[[0,1,0],[0,0,0],[1,0,0],[2,0,0]], S:[[1,1,0],[2,1,0],[0,0,0],[1,0,0]], Z:[[0,1,0],[1,1,0],[1,0,0],[2,0,0]], pillar:[[0,0,0],[0,1,0],[0,2,0]], stair:[[0,0,0],[1,0,0],[2,0,0],[1,1,0],[2,1,0],[2,2,0]] });
  const PALETTE = ['#000000','#6b9faf','#526d90','#aa8270','#91c7df','#285e96','#dfbf7f','#88b99e','#64deff'];
  // TileSlide convention x = tilt, y = yaw. Names/values are copied from the supplied convention file.
  const CAMERAS = Object.freeze({front:{tilt:0,yaw:0,zoom:1},oblique:{tilt:18,yaw:-28,zoom:1},isometric:{tilt:35,yaw:-40,zoom:1},side:{tilt:8,yaw:78,zoom:1},time:{tilt:10,yaw:-62,zoom:1},low:{tilt:-14,yaw:28,zoom:1},overhead:{tilt:58,yaw:12,zoom:1},reverse:{tilt:22,yaw:38,zoom:1}});
  Object.values(CAMERAS).forEach(Object.freeze);
  const MAX_SHAPES = 90, MAX_EDGES = 180;
  let serial = 0;
  const text = value => value == null ? '' : String(value);
  const clamp = (value,min,max) => Math.min(max,Math.max(min,value));
  const refId = ref => typeof ref === 'string' ? ref : ref && text(ref.id);
  function sameValue(a,b) { try { return JSON.stringify(a) === JSON.stringify(b); } catch (_) { return false; } }
  function valueText(value) {
    if (value === null || value === undefined) return 'Not measured';
    // Preserve every meaningful supplied digit; tiny nonzero metrics must never become zero.
    if (typeof value === 'number') return Math.abs(value)!==0 && Math.abs(value)<.001 ? String(value) : new Intl.NumberFormat(undefined,{maximumSignificantDigits:21}).format(value);
    if (typeof value === 'object') return valueText(value.amount) + (value.unit ? ' ' + text(value.unit) : '');
    return text(value);
  }
  function validateObjects(objects) {
    const errors=[], warnings=[], normalized=[], ids=new Set();
    if (!Array.isArray(objects)) return {valid:false,errors:['objects must be an array.'],warnings,objects:[]};
    if (objects.length > 100000) return {valid:false,errors:['Adapter limit: at most 100,000 semantic objects. Group records explicitly before mounting.'],warnings,objects:[]};
    objects.forEach((original,index) => {
      if (!original || typeof original !== 'object') { errors.push('Object '+index+' is not a record.'); return; }
      const id=original.somId;
      if (typeof id !== 'string' || !id.trim() || id.length>160 || ids.has(id)) { errors.push('Object '+index+' needs a unique, nonempty somId of at most 160 characters.'); return; }
      ids.add(id);
      const pose=typeof original.somshape === 'object' && original.somshape ? original.somshape : {shape:original.somshape || original.shape_type};
      const shape=pose.shape;
      if (!Object.prototype.hasOwnProperty.call(CELLS,shape)) { errors.push(id+': unknown somshape; use a supplied Oasis shape key.'); return; }
      const axis=pose.axis == null ? 0 : pose.axis, rotation=pose.rotation == null ? 0 : pose.rotation;
      if (!Number.isInteger(axis) || axis<0 || axis>2 || !Number.isInteger(rotation) || rotation<0 || rotation>3) { errors.push(id+': axis must be 0–2 and rotation 0–3.'); return; }
      let color=original.shape_color;
      if (Number.isInteger(color) && color>=0 && color<=8) color=PALETTE[color];
      if (typeof color!=='string' || !/^#[0-9a-f]{6}$/i.test(color)) { errors.push(id+': shape_color must be #RRGGBB or a palette index 0–8.'); return; }
      const refs=Array.isArray(original.evidenceRefs)?original.evidenceRefs:[];
      if (refs.some(ref=>!refId(ref))) { errors.push(id+': evidenceRefs need nonempty strings or records with id.'); return; }
      const value=original.value;
      const supported=value==null || typeof value==='string' || (typeof value==='number'&&Number.isFinite(value)) || (typeof value==='object' && Number.isFinite(value.amount) && typeof value.unit==='string');
      if (!supported) { errors.push(id+': value must be null, a finite number, text, or {amount,unit}.'); return; }
      const source=original.valueSource;
      const verified=value==null || !!(source && typeof source.field==='string' && source.field.trim() && refs.some(ref=>refId(ref)===source.evidenceRef) && sameValue(value,source.value));
      if (!verified) warnings.push(id+': displayed value has missing or mismatched source-value provenance.');
      normalized.push({somId:id,label:text(original.label||id).slice(0,300),arm:text(original.arm||'Unspecified arm').slice(0,150),status:text(original.status||'Unspecified status').slice(0,150),some_type:text(original.some_type||'object').slice(0,150),shape_type:text(original.shape_type||shape).slice(0,150),shape_color:color,shape,axis,rotation,value,evidenceRefs:refs,valueSource:source,valueVerified:verified,original});
    });
    return {valid:errors.length===0,errors,warnings,objects:normalized};
  }
  function shapeCells(shape,axis=0,rotation=0) {
    if (!Object.prototype.hasOwnProperty.call(CELLS,shape)) throw new Error('Unknown shape.');
    if(!Number.isInteger(axis)||axis<0||axis>2||!Number.isInteger(rotation)||rotation<0||rotation>3)throw new TypeError('Invalid shape orientation.');
    const cells=CELLS[shape].map(c=>{let [x,y,z]=c;if(axis===1)[y,z]=[z,y];if(axis===2)[x,z]=[z,x];for(let i=0;i<rotation;i++)[x,z]=[-z,x];return[x,y,z];});
    const minimum=[0,1,2].map(i=>Math.min(...cells.map(c=>c[i])));
    return cells.map(c=>c.map((v,i)=>v-minimum[i]));
  }
  function make(tag,className,content) { const element=document.createElement(tag);if(className)element.className=className;if(content!==undefined)element.textContent=content;return element; }
  function mount(container,options={}) {
    if (!(container instanceof Element)) throw new TypeError('mount requires a DOM element.');
    const instance='fms-'+(++serial), root=make('section','fms-root');root.setAttribute('aria-label','Spatial evidence explorer');container.appendChild(root);
    let objects=[], allEdges=[], visible=[], viewEdges=[], selected=null, camera={...CAMERAS.isometric}, filter={query:'',arm:'',status:'',sort:'label'}, destroyed=false, frame=0, geometry=[], hitFaces=[], cardMap=new Map(), flipped=new Set(), validation={warnings:[]};
    const cleanups=[]; const on=(target,event,handler,opts)=>{target.addEventListener(event,handler,opts);cleanups.push(()=>target.removeEventListener(event,handler,opts));};
    const motion=global.matchMedia ? global.matchMedia('(prefers-reduced-motion: reduce)') : {matches:false};
    let reduced=motion.matches;
    root.innerHTML='<div class="fms-heading"><div><p class="fms-kicker">9BIT / SPATIAL EVIDENCE</p><h3>Evidence, with another dimension.</h3></div><span class="fms-snapshot">Snapshot · not a live feed</span></div><p class="fms-intro">Each polycube is a semantic object. Geometry identifies a category; size does not encode bytes or performance. Select a shape or its card to inspect the same evidence.</p><div class="fms-notice" role="status"></div><div class="fms-filterbar"></div><div class="fms-layout"><div class="fms-stage"><canvas class="fms-canvas" tabindex="0" role="img"></canvas><p class="fms-canvas-fallback" hidden>Spatial drawing is unavailable. Every visible object remains available in the accessible cards below.</p><div class="fms-stage-caption"><span class="fms-view-count"></span><span>Y ↑ · one cell = one voxel</span></div></div><div class="fms-inspector"><div class="fms-inspector-head"><strong>Flipdeck</strong><span class="fms-selected-name">Choose an object</span></div><div class="fms-deck" role="list"></div></div></div><div class="fms-controls"></div><p class="fms-help"></p><div class="fms-live fms-visually-hidden" aria-live="polite" aria-atomic="true"></div>';
    const $=selector=>root.querySelector(selector), canvas=$('.fms-canvas'), deck=$('.fms-deck'), live=$('.fms-live'), help=$('.fms-help');
    const relationships=make('details','fms-relationships'),relationSummary=make('summary','','Relationships'),relationBody=make('div','fms-table-wrap');relationships.append(relationSummary,relationBody);root.insertBefore(relationships,$('.fms-controls'));
    help.id=instance+'-help';help.textContent='Drag to orbit. On the canvas: arrows orbit, + / − zoom, Home resets, [ / ] select objects, Enter opens the selected card. The sliders and all evidence cards work with a keyboard.';
    canvas.setAttribute('aria-label','Rotatable polycube evidence view');canvas.setAttribute('aria-describedby',help.id);
    let ctx=null;try { ctx=canvas.getContext('2d',{alpha:false}); } catch (_) {}
    if (!ctx) {canvas.hidden=true;$('.fms-canvas-fallback').hidden=false;root.classList.add('fms-no-canvas');}
    const filters=$('.fms-filterbar');
    function labelled(label,element,className) {const wrapper=make('label',className||'fms-field');wrapper.append(make('span','',label),element);return wrapper;}
    const search=make('input');search.type='search';search.placeholder='Find label, ID or category';search.setAttribute('aria-label','Filter spatial objects');filters.append(labelled('Find an object',search,'fms-field fms-search'));
    const arm=make('select'),status=make('select'),sort=make('select');
    filters.append(labelled('Arm',arm),labelled('Status',status),labelled('Order',sort));
    [['label','Label'],['arm','Arm'],['value-desc','Numeric value ↓ (by unit)'],['status','Status']].forEach(([value,label])=>{const option=make('option','',label);option.value=value;sort.append(option);});
    const controls=$('.fms-controls'),presets=make('div','fms-presets fms-camera-dock');presets.setAttribute('role','group');presets.setAttribute('aria-label','Camera presets');$('.fms-stage').prepend(presets);
    ['front','isometric','side','time'].forEach(key=>{const button=make('button','fms-button',key[0].toUpperCase()+key.slice(1));button.type='button';button.dataset.preset=key;button.title=key==='time'?'View recorded snapshot layers from the side; spacing is ordinal, not elapsed time.':key[0].toUpperCase()+key.slice(1)+' camera';on(button,'click',()=>setCamera(key));presets.append(button);});
    const fit=make('button','fms-button fms-fit','Fit');fit.type='button';fit.title='Fit the complete current scene without changing its orientation';on(fit,'click',()=>setCamera({zoom:1}));presets.append(fit);
    const sliders=make('div','fms-sliders');controls.append(sliders);
    const ranges={};[['yaw','Yaw',-180,180,1],['tilt','Tilt',-85,85,1],['zoom','Zoom',.45,2.8,.05]].forEach(([key,label,min,max,step])=>{
      const range=make('input');range.type='range';range.min=min;range.max=max;range.step=step;const output=make('output');range.id=instance+'-'+key;output.setAttribute('for',range.id);const wrapper=labelled(label,range,'fms-range');wrapper.append(output);sliders.append(wrapper);ranges[key]={range,output};on(range,'input',()=>setCamera({[key]:Number(range.value)}));
    });
    const extra=make('div','fms-extra'),reset=make('button','fms-button','Reset camera'),motionInput=make('input');reset.type='button';motionInput.type='checkbox';motionInput.checked=reduced;extra.append(reset,labelled('Reduce motion',motionInput,'fms-motion'));controls.append(extra);on(reset,'click',()=>setCamera('isometric'));on(motionInput,'change',()=>{reduced=motionInput.checked;root.classList.toggle('fms-reduced',reduced);});root.classList.toggle('fms-reduced',reduced);
    const allPresets=make('select');allPresets.setAttribute('aria-label','All camera presets');allPresets.append(new Option('Choose a preset',''));Object.keys(CAMERAS).forEach(key=>allPresets.append(new Option(key[0].toUpperCase()+key.slice(1),key)));on(allPresets,'change',()=>{if(allPresets.value)setCamera(allPresets.value);});controls.append(labelled('All camera presets',allPresets));
    const settings=make('details','fms-settings');settings.append(make('summary','','Filter objects & fine-tune camera'),filters,controls,help,$('.fms-intro'));root.append(settings);
    function syncOptions(select,key,emptyLabel) {
      const current=filter[key];select.replaceChildren();const first=make('option','',emptyLabel);first.value='';select.append(first);
      const values=[...new Set(objects.map(o=>o[key]))].sort((a,b)=>a.localeCompare(b));
      values.slice(0,100).forEach(value=>{const option=make('option','',value);option.value=value;select.append(option);});
      if(values.length>100)validation.warnings.push('Only the first 100 '+key+' options are listed; text search and setView can match the rest.');
      select.value=current;if(select.value!==current){filter[key]='';select.value='';}
    }
    function emitView() {if(typeof options.onViewChange==='function')options.onViewChange({...filter,visibleIds:visible.map(o=>o.somId),matchingCount:matchingCount()});}
    function matches(o) {const query=filter.query.trim().toLocaleLowerCase();return (!filter.arm||o.arm===filter.arm)&&(!filter.status||o.status===filter.status)&&(!query||[o.label,o.somId,o.some_type,o.arm,o.status].join(' ').toLocaleLowerCase().includes(query));}
    function matchingCount() {return objects.reduce((sum,o)=>sum+(matches(o)?1:0),0);}
    function numeric(o) {return o.valueVerified ? typeof o.value==='number'?o.value:o.value&&typeof o.value==='object'?o.value.amount:-Infinity : -Infinity;}
    function unit(o) {return o.value&&typeof o.value==='object'?o.value.unit:text(o.original.valueUnit||'unitless');}
    function updateView(notify=true) {
      const matching=objects.filter(matches);
      matching.sort((a,b)=>{let result=filter.sort==='value-desc'?(unit(a).localeCompare(unit(b))||numeric(b)-numeric(a)):filter.sort==='arm'?a.arm.localeCompare(b.arm):filter.sort==='status'?a.status.localeCompare(b.status):a.label.localeCompare(b.label);return (Number.isNaN(result)?0:result)||a.label.localeCompare(b.label)||a.somId.localeCompare(b.somId);});
      visible=matching.slice(0,MAX_SHAPES);
      if(selected&&!visible.some(o=>o.somId===selected))selected=null;
      const count=options.sampled?'Bounded sample · '+visible.length+'/'+objects.length+' loaded · '+matching.length+' matches':visible.length+' of '+matching.length+' matching objects';$('.fms-view-count').textContent=count;
      const messages=[];if(matching.length>MAX_SHAPES)messages.push('Bounded overview: showing the first '+MAX_SHAPES+' of '+matching.length+' matching objects. No totals are recomputed from this subset. Full metadata stays with the host report.');
      if(validation.warnings.length)messages.push(validation.warnings.length+' provenance or adapter warning(s). Unverified values are marked on their cards.');
      if(!visible.length)messages.push('No matching objects. Clear a filter to restore the view.');
      $('.fms-notice').textContent=messages.join(' ');$('.fms-notice').hidden=messages.length===0;
      const ids=new Set(visible.map(o=>o.somId)),matchingEdges=allEdges.filter(e=>ids.has(e.from)&&ids.has(e.to));viewEdges=matchingEdges.slice(0,MAX_EDGES);
      renderCards();renderRelationships(matchingEdges.length);layout();schedule();if(notify)emitView();
    }
    function renderRelationships(count) {
      relationSummary.textContent=(options.undirectedEdges?'Observation links · ':'Directed relationships · ')+viewEdges.length+' of '+count+' in this view';relationBody.replaceChildren();
      relationBody.append(make('p','fms-relation-note',options.undirectedEdges?'Host-supplied equality links with both endpoints in the current object view. Endpoint order has no directional meaning; dashed connectors are an overview. At most 180 links are displayed.':'Host-supplied relationships with both endpoints in the current object view. The table preserves direction and evidence; dashed spatial connectors are only an overview. At most 180 relationships are displayed.'));
      if(!viewEdges.length){relationBody.append(make('p','','No relationships in the current view.'));return;}
      const table=make('table'),caption=make('caption','',options.undirectedEdges?'Observation link endpoints and evidence':'Relationship direction and evidence'),head=make('thead'),header=make('tr');(options.undirectedEdges?['Endpoint A','Relation','Endpoint B','Evidence']:['From','Relation','To','Evidence']).forEach(label=>{const th=make('th','',label);th.scope='col';header.append(th);});head.append(header);table.append(caption,head);const body=make('tbody'),byId=new Map(visible.map(o=>[o.somId,o]));
      viewEdges.forEach(edge=>{
        const row=make('tr');function endpoint(id){const cell=make('td'),button=make('button','fms-edge-select',byId.get(id).label);button.type='button';button.addEventListener('click',()=>selectObject(id,'relationship',false));cell.append(button,make('small','fms-id',id));return cell;}
        row.append(endpoint(edge.from),make('td','',text(edge.label||'Unspecified relationship').slice(0,600)),endpoint(edge.to));
        const refs=make('td'),list=make('ul');const evidence=Array.isArray(edge.evidenceRefs)?edge.evidenceRefs.filter(ref=>refId(ref)):[];
        evidence.slice(0,6).forEach(ref=>{const item=make('li','',(typeof ref==='string'?ref:text(ref.label||ref.id)).slice(0,600));if(ref&&typeof ref==='object'&&ref.path)item.append(make('small','',text(ref.path).slice(0,600)));list.append(item);});
        if(evidence.length){refs.append(list);if(evidence.length>6)refs.append(make('small','',evidence.length-6+' more references in full metadata.'));}else refs.append(make('span','fms-unverified','No evidence supplied'));
        row.append(refs);body.append(row);
      });table.append(body);relationBody.append(table);
    }
    function renderCards() {
      deck.replaceChildren();cardMap.clear();
      visible.forEach((o,index)=>{
        const card=make('article','fms-card');card.setAttribute('role','listitem');card.dataset.somId=o.somId;card.dataset.someType=o.some_type;card.style.setProperty('--fms-object-color',o.shape_color);
        const head=make('div','fms-card-head'),select=make('button','fms-select',o.label);select.type='button';select.setAttribute('aria-label','Select '+o.label+'; '+o.arm);select.setAttribute('aria-pressed',String(o.somId===selected));
        const swatch=make('span','fms-swatch',o.shape);swatch.setAttribute('aria-hidden','true');head.append(make('span','fms-ordinal',String(index+1).padStart(2,'0')),swatch,select);card.append(head);
        const inner=make('div','fms-card-inner'),front=make('div','fms-face fms-front'),back=make('div','fms-face fms-back');back.id=instance+'-evidence-'+cardMap.size;
        const badges=make('p','fms-badges');badges.append(make('span','',o.arm),make('span','',o.status));
        const displayValue=make('p','fms-value',valueText(o.value).slice(0,400));displayValue.title='Exact supplied value: '+JSON.stringify(o.value);
        front.append(badges,displayValue,make('p',o.valueVerified?'fms-provenance':'fms-provenance fms-unverified',o.value==null?'No measured value supplied':o.valueVerified?'Source-linked value':'Unverified value · provenance missing or mismatched'),make('p','fms-kind',o.some_type+' / '+o.shape_type));
        back.append(make('strong','','Evidence'),make('p','fms-id',(o.original.viewInstanceId?'View instance ID: ':'SOM ID: ')+o.somId));
        if(o.original.viewInstanceId)back.append(make('p','fms-source-field','Renderer instance only; not a native SOM identity.'));
        if(o.original.nativeSomId)back.append(make('p','fms-id','Native SOM ID: '+o.original.nativeSomId),make('p','fms-id','Observation ID: '+text(o.original.nativeObservationId)));
        back.append(make('p','fms-source-field','Exact supplied value: '+JSON.stringify(o.value==null?null:o.value).slice(0,600)));
        if(o.valueSource)back.append(make('p','fms-source-field','Source field: '+text(o.valueSource.field)));
        ['observedAt','sessionId','layer','schemaPath'].forEach(key=>{if(o.original[key]!=null)back.append(make('p','fms-source-field',key+': '+text(o.original[key]).slice(0,600)));});
        const refs=make('ul','fms-evidence');o.evidenceRefs.slice(0,6).forEach(ref=>{const item=make('li');item.append(make('span','',(typeof ref==='string'?ref:text(ref.label||ref.id)).slice(0,300)));if(typeof ref==='object'&&ref.path)item.append(make('small','',text(ref.path).slice(0,600)));refs.append(item);});
        if(!o.evidenceRefs.length)refs.append(make('li','','No evidence reference supplied.'));
        back.append(refs);if(o.evidenceRefs.length>6)back.append(make('p','',o.evidenceRefs.length-6+' more references in the full metadata.'));
        inner.append(front,back);card.append(inner);
        const flip=make('button','fms-button fms-flip','View evidence');flip.type='button';flip.setAttribute('aria-controls',back.id);card.append(flip);
        function syncFlip() {const isBack=flipped.has(o.somId);card.classList.toggle('fms-flipped',isBack);front.setAttribute('aria-hidden',String(isBack));back.setAttribute('aria-hidden',String(!isBack));flip.textContent=isBack?'View result':'View evidence';flip.setAttribute('aria-expanded',String(isBack));flip.setAttribute('aria-label',(isBack?'View result for ':'View evidence for ')+o.label);}
        // Removed cards must be collectible when filters change; do not retain them in global cleanup closures.
        select.addEventListener('click',()=>selectObject(o.somId,'card',false));flip.addEventListener('click',()=>{if(flipped.has(o.somId))flipped.delete(o.somId);else flipped.add(o.somId);syncFlip();selectObject(o.somId,'card',false);});
        syncFlip();deck.append(card);cardMap.set(o.somId,{card,select,flip});
      });
      paintSelection();
    }
    function selectObject(id,source='api',focus=false) {
      const object=visible.find(o=>o.somId===id);if(!object)return false;selected=id;paintSelection();schedule();
      live.textContent='Selected '+object.label+', '+object.arm+', '+object.status+'. '+(object.valueVerified?'':'Unverified value. ')+valueText(object.value)+'.';
      const card=cardMap.get(id);if(card&&(source==='canvas'||source==='keyboard')){const d=deck.getBoundingClientRect(),r=card.card.getBoundingClientRect();if(deck.scrollWidth>deck.clientWidth)deck.scrollLeft+=r.left-d.left-6;else if(r.top<d.top||r.bottom>d.bottom)deck.scrollTop+=r.top-d.top-6;}if(focus&&card)card.select.focus({preventScroll:true});
      if(typeof options.onSelect==='function')options.onSelect(object.original,{somId:id,evidenceRefs:object.evidenceRefs,source});return true;
    }
    function paintSelection() {cardMap.forEach((parts,id)=>{parts.card.classList.toggle('fms-selected',id===selected);parts.select.setAttribute('aria-pressed',String(id===selected));});const object=visible.find(o=>o.somId===selected);$('.fms-selected-name').textContent=object?object.label:'Choose an object';}
    function layout() {
      geometry=[];const groups=new Map();
      visible.forEach(o=>{const n=(options.layout==='snapshot-layers'||options.layout==='capture-layers')&&Number.isInteger(o.original.layerIndex)?Math.max(0,Math.min(options.layout==='capture-layers'?30:2,o.original.layerIndex)):0;if(!groups.has(n))groups.set(n,[]);groups.get(n).push(o);});
      groups.forEach((group,layer)=>{const cols=Math.max(1,Math.ceil(Math.sqrt(group.length*1.25))),rows=Math.ceil(group.length/cols);
        group.forEach((o,index)=>{const cells=shapeCells(o.shape,o.axis,o.rotation),extent=[0,1,2].map(axis=>Math.max(...cells.map(c=>c[axis]))+1);const position=[(index%cols-(cols-1)/2)*4.8-extent[0]/2,layer*6,(Math.floor(index/cols)-(rows-1)/2)*4.8-extent[2]/2];geometry.push({object:o,cells,position,extent});});});
    }
    const FACE_DEFS=[{n:[0,0,1],v:[[0,0,1],[1,0,1],[1,1,1],[0,1,1]]},{n:[0,0,-1],v:[[1,0,0],[0,0,0],[0,1,0],[1,1,0]]},{n:[1,0,0],v:[[1,0,1],[1,0,0],[1,1,0],[1,1,1]]},{n:[-1,0,0],v:[[0,0,0],[0,0,1],[0,1,1],[0,1,0]]},{n:[0,1,0],v:[[0,1,1],[1,1,1],[1,1,0],[0,1,0]]},{n:[0,-1,0],v:[[0,0,0],[1,0,0],[1,0,1],[0,0,1]]}];
    function rotate(p) {const y=camera.yaw*Math.PI/180,t=camera.tilt*Math.PI/180,x=p[0]*Math.cos(y)+p[2]*Math.sin(y),z=-p[0]*Math.sin(y)+p[2]*Math.cos(y);return [x,p[1]*Math.cos(t)-z*Math.sin(t),p[1]*Math.sin(t)+z*Math.cos(t)];}
    function shade(hex,normal) {const factor=.64+.34*Math.max(0,normal[0]*.35+normal[1]*.8+normal[2]*.4);const rgb=[1,3,5].map(i=>Math.round(parseInt(hex.slice(i,i+2),16)*factor));return 'rgba('+rgb.join(',')+',0.94)';}
    function draw() {
      frame=0;if(destroyed||!ctx)return;
      const box=canvas.getBoundingClientRect(),w=Math.max(240,box.width),h=Math.max(260,box.height),dpr=Math.min(global.devicePixelRatio||1,2);
      if(canvas.width!==Math.round(w*dpr)||canvas.height!==Math.round(h*dpr)){canvas.width=Math.round(w*dpr);canvas.height=Math.round(h*dpr);}ctx.setTransform(dpr,0,0,dpr,0,0);ctx.fillStyle='#05090f';ctx.fillRect(0,0,w,h);hitFaces=[];
      if(!geometry.length){ctx.fillStyle='#a7bacb';ctx.font='14px system-ui';ctx.textAlign='center';ctx.fillText('No matching objects',w/2,h/2);return;}
      const points=geometry.flatMap(g=>[0,g.extent[0]].flatMap(x=>[0,g.extent[1]].flatMap(y=>[0,g.extent[2]].map(z=>rotate([g.position[0]+x,g.position[1]+y-1.5,g.position[2]+z])))));
      // Fit the perspective-projected object bounds, reserving room for the camera dock.
      // Perspective is a presentation lens; no physical distance or similarity is implied.
      const radius=Math.max(8,...points.map(p=>Math.hypot(...p))),distance=radius*3.6;
      const perspective=r=>{const factor=distance/Math.max(distance*.2,distance-r[2]);return [r[0]*factor,r[1]*factor,r[2]];};
      const projected=points.map(perspective),minX=Math.min(...projected.map(p=>p[0])),maxX=Math.max(...projected.map(p=>p[0])),minY=Math.min(...projected.map(p=>p[1])),maxY=Math.max(...projected.map(p=>p[1]));
      const paddingX=w<500?23:34,top=55,bottom=25,scale=Math.min((w-paddingX*2)/Math.max(4,maxX-minX),(h-top-bottom)/Math.max(4,maxY-minY))*camera.zoom,cx=(minX+maxX)/2,cy=(minY+maxY)/2,centreY=top+(h-top-bottom)/2;
      const project=p=>{const r=perspective(rotate([p[0],p[1]-1.5,p[2]]));return [w/2+(r[0]-cx)*scale,centreY-(r[1]-cy)*scale,r[2]];};
      const size=Math.max(8,Math.ceil(Math.sqrt(visible.length))*6),step=visible.length>30?2:1;
      ctx.lineWidth=.6;ctx.strokeStyle='rgba(80,132,158,.19)';
      for(const layerY of new Set(geometry.map(g=>g.position[1])))for(let i=-size;i<=size;i+=step){[[[i,layerY-.08,-size],[i,layerY-.08,size]],[[-size,layerY-.08,i],[size,layerY-.08,i]]].forEach(line=>{const a=project(line[0]),b=project(line[1]);ctx.beginPath();ctx.moveTo(a[0],a[1]);ctx.lineTo(b[0],b[1]);ctx.stroke();});}
      const centres=new Map(geometry.map(g=>[g.object.somId,project([g.position[0]+g.extent[0]/2,g.position[1]+g.extent[1]/2,g.position[2]+g.extent[2]/2])]));
      ctx.strokeStyle='rgba(133,182,207,.35)';ctx.setLineDash([3,5]);ctx.lineWidth=1;
      viewEdges.forEach(edge=>{const a=centres.get(edge.from),b=centres.get(edge.to);if(a&&b){ctx.beginPath();ctx.moveTo(a[0],a[1]);ctx.lineTo(b[0],b[1]);ctx.stroke();}});ctx.setLineDash([]);
      const faces=[];
      geometry.forEach(g=>{const occupied=new Set(g.cells.map(c=>c.join(',')));g.cells.forEach(cell=>FACE_DEFS.forEach(face=>{
        if(occupied.has(cell.map((v,i)=>v+face.n[i]).join(','))||rotate(face.n)[2]<=.001)return;
        const polygon=face.v.map(v=>project(v.map((n,i)=>n+cell[i]+g.position[i])));faces.push({polygon,depth:polygon.reduce((s,p)=>s+p[2],0)/4,id:g.object.somId,color:shade(g.object.shape_color,face.n)});
      }));});faces.sort((a,b)=>a.depth-b.depth);
      faces.forEach(face=>{ctx.beginPath();face.polygon.forEach((p,i)=>i?ctx.lineTo(p[0],p[1]):ctx.moveTo(p[0],p[1]));ctx.closePath();ctx.fillStyle=face.color;ctx.fill();ctx.lineWidth=face.id===selected?1.8:.7;ctx.strokeStyle=face.id===selected?'#d5f7ff':'rgba(200,228,240,.38)';ctx.stroke();hitFaces.push(face);});
      if(visible.length<=40){ctx.font='600 10px Consolas,monospace';ctx.textAlign='center';visible.forEach((object,index)=>{if(object.somId===selected)return;const p=centres.get(object.somId);if(!p)return;const n=String(index+1).padStart(2,'0');ctx.fillStyle='rgba(4,12,21,.86)';ctx.fillRect(p[0]-10,p[1]-7,20,14);ctx.fillStyle='#d6edfa';ctx.fillText(n,p[0],p[1]+3);});}
      if(selected&&centres.has(selected)){const p=centres.get(selected),object=visible.find(o=>o.somId===selected);const label=object.label.length>35?object.label.slice(0,32)+'…':object.label;ctx.font='600 12px system-ui';ctx.textAlign='center';const width=ctx.measureText(label).width+20;ctx.fillStyle='rgba(3,9,16,.94)';ctx.fillRect(clamp(p[0]-width/2,4,w-width-4),clamp(p[1]-39,4,h-27),width,25);ctx.fillStyle='#e7f8ff';ctx.fillText(label,clamp(p[0],width/2+4,w-width/2-4),clamp(p[1]-22,21,h-10));}
    }
    function schedule() {if(!destroyed&&!frame)frame=requestAnimationFrame(draw);}
    function setCamera(next) {
      if(destroyed)return;const value=typeof next==='string'?CAMERAS[next]:next;if(!value)throw new Error('Unknown camera preset.');
      ['yaw','tilt','zoom'].forEach(key=>{if(value[key]!==undefined&&!Number.isFinite(value[key]))throw new TypeError('Camera values must be finite.');});
      camera={yaw:clamp(value.yaw??camera.yaw,-180,180),tilt:clamp(value.tilt??camera.tilt,-85,85),zoom:clamp(value.zoom??camera.zoom,.45,2.8)};
      Object.keys(ranges).forEach(key=>{ranges[key].range.value=camera[key];ranges[key].output.textContent=key==='zoom'?camera[key].toFixed(2)+'×':Math.round(camera[key])+'°';});
      presets.querySelectorAll('[data-preset]').forEach(button=>{const preset=CAMERAS[button.dataset.preset];button.setAttribute('aria-pressed',String(Object.keys(preset).every(k=>Math.abs(preset[k]-camera[k])<.01)));});schedule();
    }
    function setView(next={}) {if(destroyed)return;const previous={...filter};filter={...filter,...next};filter.query=text(filter.query);filter.arm=text(filter.arm);filter.status=text(filter.status);if(!['label','arm','status','value-desc'].includes(filter.sort))filter.sort='label';if(['query','arm','status','sort'].every(key=>previous[key]===filter[key]))return;search.value=filter.query;arm.value=filter.arm;status.value=filter.status;sort.value=filter.sort;updateView();}
    function setObjects(next,edges=allEdges) {
      if(destroyed)return;const result=validateObjects(next);if(!result.valid){$('.fms-notice').hidden=false;$('.fms-notice').textContent='Spatial data rejected: '+result.errors.slice(0,4).join(' ');throw new TypeError(result.errors.join('\n'));}
      validation=result;objects=result.objects;const ids=new Set(objects.map(o=>o.somId));allEdges=Array.isArray(edges)?edges.filter(e=>e&&ids.has(e.from)&&ids.has(e.to)).map(e=>({...e})):[];
      if(Array.isArray(edges)&&allEdges.length!==edges.length)validation.warnings.push('Some edge endpoints do not match semantic object IDs.');
      if(allEdges.length>MAX_EDGES)validation.warnings.push('Only the first 180 edges can appear in this bounded overview.');
      flipped=new Set([...flipped].filter(id=>ids.has(id)));syncOptions(arm,'arm','All arms');syncOptions(status,'status','All statuses');updateView(false);return {valid:true,errors:[],warnings:validation.warnings.slice()};
    }
    on(search,'input',()=>setView({query:search.value}));on(arm,'change',()=>setView({arm:arm.value}));on(status,'change',()=>setView({status:status.value}));on(sort,'change',()=>setView({sort:sort.value}));
    let drag=null;
    on(canvas,'pointerdown',event=>{if(event.button!==0)return;drag={x:event.clientX,y:event.clientY,lastX:event.clientX,lastY:event.clientY,moved:false};canvas.setPointerCapture?.(event.pointerId);canvas.classList.add('fms-dragging');});
    on(canvas,'pointermove',event=>{if(!drag)return;const dx=event.clientX-drag.lastX,dy=event.clientY-drag.lastY;if(Math.hypot(event.clientX-drag.x,event.clientY-drag.y)>5)drag.moved=true;if(drag.moved)setCamera({yaw:camera.yaw+dx*.45,tilt:camera.tilt+dy*.35});drag.lastX=event.clientX;drag.lastY=event.clientY;});
    on(canvas,'pointerup',event=>{if(!drag)return;const wasDrag=drag.moved;drag=null;canvas.classList.remove('fms-dragging');if(wasDrag)return;const rect=canvas.getBoundingClientRect(),x=event.clientX-rect.left,y=event.clientY-rect.top;const hit=[...hitFaces].reverse().find(face=>inside(x,y,face.polygon));if(hit)selectObject(hit.id,'canvas',false);});
    on(canvas,'pointercancel',()=>{drag=null;canvas.classList.remove('fms-dragging');});
    on(canvas,'keydown',event=>{
      const key=event.key;if(['ArrowLeft','ArrowRight','ArrowUp','ArrowDown','+','=','-','Home','[',']','Enter'].includes(key))event.preventDefault();
      if(key==='ArrowLeft')setCamera({yaw:camera.yaw-5});if(key==='ArrowRight')setCamera({yaw:camera.yaw+5});if(key==='ArrowUp')setCamera({tilt:camera.tilt+5});if(key==='ArrowDown')setCamera({tilt:camera.tilt-5});if(key==='+'||key==='=')setCamera({zoom:camera.zoom+.1});if(key==='-')setCamera({zoom:camera.zoom-.1});if(key==='Home')setCamera('isometric');
      if((key==='['||key===']')&&visible.length){const index=visible.findIndex(o=>o.somId===selected),next=index<0?(key===']'?0:visible.length-1):(index+(key===']'?1:-1)+visible.length)%visible.length;selectObject(visible[next].somId,'keyboard',false);}if(key==='Enter'&&selected)selectObject(selected,'keyboard',true);
    });
    const observer=global.ResizeObserver?new ResizeObserver(schedule):null;if(observer)observer.observe(canvas);else on(global,'resize',schedule);
    setCamera('isometric');
    try {setObjects(options.objects||[],options.edges||[]);} catch(error) {root.classList.add('fms-invalid');}
    function inside(x,y,polygon) {let hit=false;for(let i=0,j=polygon.length-1;i<polygon.length;j=i++){const a=polygon[i],b=polygon[j];if(((a[1]>y)!==(b[1]>y))&&(x<(b[0]-a[0])*(y-a[1])/(b[1]-a[1])+a[0]))hit=!hit;}return hit;}
    return {setObjects,setCamera,setView,select:id=>selectObject(id,'api'),getState:()=>({camera:{...camera},view:{...filter},selectedId:selected,visibleIds:visible.map(o=>o.somId),matchingCount:matchingCount(),warnings:validation.warnings.slice()}),getObjects:()=>objects.map(o=>o.original),getEdges:()=>allEdges.slice(),destroy(){if(destroyed)return;destroyed=true;if(frame)cancelAnimationFrame(frame);observer?.disconnect();cleanups.forEach(fn=>fn());cleanups.length=0;root.remove();objects=[];visible=[];viewEdges=[];allEdges=[];geometry=[];hitFaces=[];cardMap.clear();}};
  }
  global.FileMapperSpatial=Object.freeze({mount,validateObjects,shapeCells,version:'filemapper-spatial-adapter/1',limits:{shapes:MAX_SHAPES,edges:MAX_EDGES},cameras:CAMERAS});
})(window);
