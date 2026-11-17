(function(global){
  'use strict';
  const instances=new WeakMap(), PAGE=24;
  const make=(tag,cls,text)=>{const e=document.createElement(tag);if(cls)e.className=cls;if(text!==undefined)e.textContent=String(text);return e;};
  const defaults={E:true,Z:true,COPY:'DAT',DCOPY:'DAT',XJ:true,R:2,W:2};
  const numericTypes=new Set(['integer','integerBytes','byteQuantity','nonnegativeInteger','positiveIntegerMinutes']);
  function typedOption(option,value){
    const name=option.id,type=option.parameter?.type,text=String(value).trim();
    if(type==='booleanSwitch')return {name,enabled:!!value};
    if(option.parameter?.optional&&text==='')return {name};
    if(numericTypes.has(type)||(type==='ageOrDate'&&/^\d{1,4}$/.test(text))){
      const number=Number(text);
      if(!/^\d+$/.test(text)||!Number.isSafeInteger(number))throw new Error(`/${name}: enter a whole number from 0 through 9,007,199,254,740,991. Byte quantities use bytes; larger exact values require the native CLI or PowerShell.`);
      return {name,number};
    }
    if(type==='argumentList')return {name,items:String(value).split(/\r?\n/).map(s=>s.trim()).filter(Boolean)};
    return {name,text};
  }
  function destroy(host){instances.get(host)?.destroy();}
  async function render(host,{api,capabilities=[]}){
    const capabilityKey=[...capabilities].sort().join(',');
    const existing=instances.get(host);if(existing?.capabilityKey===capabilityKey)return existing;
    destroy(host);host.replaceChildren();
    let closed=false,timer=null,catalog=null,page=0,plan=null,lastJob=null,revision=0,busy=false;
    const options=new Map(Object.entries(defaults));
    const canPlan=capabilities.includes('plan');
    const controller={capabilityKey,destroy(){closed=true;clearTimeout(timer);host.replaceChildren();instances.delete(host);}};
    instances.set(host,controller);
    const header=make('section','rc-intro');header.append(make('p','eyebrow','NATIVE WINDOWS / ROBOCOPY WORKBENCH'),make('h2','','Plan the transfer. Inspect every effect.'),make('p','','Build a transfer with typed option tiles, save an immutable plan, then inspect a native list-only preview. Source and destination are independent of the active scan snapshot.'));
    const form=make('form','rc-plan panel'),fields=make('div','rc-fields');
    function field(label,type='text',value='',placeholder=''){
      const wrap=make('label','rc-field'),input=make('input');input.type=type;input.value=value;input.placeholder=placeholder;input.setAttribute('aria-label',label);wrap.append(make('span','',label),input);fields.append(wrap);return input;
    }
    const source=field('Source directory','text','','C:\\Data\\source'),destination=field('Destination directory','text','','C:\\Data\\destination');
    const patterns=field('File patterns · separate with semicolons','text','*.*');patterns.placeholder='*.txt;*.md';
    const timeout=field('Maximum preview seconds','number','300');timeout.min='1';timeout.max='3600';
    const rollback=field('Rollback statement','text','No backup is managed by FileMapper; overwritten content cannot be restored by this action.');
    rollback.closest('label').classList.add('rc-wide');
    const note=make('p','rc-note',canPlan?'A preview lists intended work. It does not copy, move, delete, or grant execution permission.':'Connected with a read-only role for actions. Use a planner or operator credential to validate and preview a transfer.');
    const actions=make('div','rc-actions');
    function button(label,handler,cls='button secondary'){const b=make('button',cls,label);b.type='button';b.addEventListener('click',handler);return b;}
    const validate=button('Validate options',()=>submit('validate'));
    const save=button('Save plan',()=>submit('plans'),'button primary');
    const preview=button('Run list-only preview',()=>runPreview());
    const download=button('Download plan JSON',()=>downloadPlan());
    const cancel=button('Cancel preview',async()=>{if(!lastJob||lastJob.finishedAt)return;try{await api('/api/robocopy/jobs/'+encodeURIComponent(lastJob.id)+'/cancel',{method:'POST'});status.textContent='Cancellation requested. Waiting for the recorded terminal state…';}catch(error){status.textContent=error.message;}});
    actions.append(validate,save,preview,cancel,download);form.append(fields,note,actions);
    const status=make('p','rc-status');status.setAttribute('role','status');status.setAttribute('aria-live','polite');
    const summary=make('section','rc-result panel');summary.setAttribute('aria-label','Transfer plan evidence');
    const nativeJob=make('details','rc-json'),nativeText=make('textarea','rc-job-text');nativeText.setAttribute('aria-label','Native Robocopy job text');nativeText.placeholder='Paste a .rcj job for validation and import';
    nativeJob.append(make('summary','','Import or export a native Robocopy job'),make('p','rc-note','Job text is parsed as data. Importing does not run the job or read another job file.'),nativeText);
    const importButton=button('Import job text',async()=>{if(!canPlan||busy)return;const version=revision;busy=true;updateButtons();try{const result=await api('/api/robocopy/import-job',{method:'POST',body:{text:nativeText.value}});if(closed||version!==revision)return;const value=result.draft;source.value=value.source;destination.value=value.destination;patterns.value=(value.files||[]).join(';');timeout.value=value.timeoutSeconds||300;rollback.value=value.rollbackStatement||rollback.value;options.clear();for(const o of value.options||[])options.set(o.name,o.enabled??o.number??o.text??o.items?.join('\n')??'');plan=null;lastJob=null;clearTimeout(timer);summary.replaceChildren();draw();status.textContent='Job imported as a draft. '+(result.notes||[]).join(' ');}catch(error){if(!closed&&version===revision)status.textContent=error.message;}finally{if(!closed&&version===revision){busy=false;updateButtons();}}});
    const exportButton=button('Export native job',async()=>{if(!canPlan||busy)return;try{const response=await api('/api/robocopy/export-job',{method:'POST',body:draft(),raw:true});saveBlob(await response.text(),'filemapper-transfer.rcj','text/plain');status.textContent='Native job downloaded. Exporting does not run the job.';}catch(error){status.textContent=error.message;}});
    const jobActions=make('div','rc-actions');jobActions.append(importButton,exportButton);nativeJob.append(jobActions);
    const filters=make('div','rc-toolbar');const search=make('input');search.type='search';search.placeholder='Find a switch, effect, or behavior';search.setAttribute('aria-label','Search Robocopy options');
    const category=make('select');category.setAttribute('aria-label','Robocopy category');category.append(new Option('All categories',''));
    const selected=make('input');selected.type='checkbox';const selectedLabel=make('label','rc-selected');selectedLabel.append(selected,document.createTextNode('Selected only'));
    const count=make('span','rc-count');filters.append(search,category,selectedLabel,count);
    const grid=make('div','rc-grid');grid.setAttribute('aria-label','Robocopy option tiles');
    const pager=make('div','rc-pager');const previous=button('← Previous options',()=>{page--;draw();}),next=button('Next options →',()=>{page++;draw();});pager.append(previous,next);
    const history=make('section','rc-history panel'),jobPicker=make('select'),historyStatus=make('p','rc-note');jobPicker.setAttribute('aria-label','Recent transfer jobs');
    const refreshJobs=button('Refresh transfer jobs',()=>loadJobs()),openJob=button('Open recorded job',()=>recoverJob(jobPicker.value));
    history.append(make('h3','','Resume a recorded transfer'),make('p','rc-note','The latest 20 jobs remain available after reconnecting. Open a running preview to restore progress and cancellation controls.'),jobPicker,refreshJobs,openJob,historyStatus);
    host.append(header,history,form,status,summary,nativeJob,filters,grid,pager);
    function updateButtons(){validate.disabled=save.disabled=importButton.disabled=exportButton.disabled=!canPlan||busy;preview.disabled=!canPlan||busy||!plan;download.disabled=!plan;cancel.disabled=!canPlan||!lastJob||lastJob.kind!=='preview'||!!lastJob.finishedAt;fields.querySelectorAll('input').forEach(n=>n.disabled=busy);grid.querySelectorAll('.rc-tile').forEach(tile=>{const box=tile.querySelector('input[type=checkbox]');box.disabled=busy;tile.querySelectorAll('.rc-field input,.rc-field textarea').forEach(n=>n.disabled=busy||!box.checked);});}
    async function loadJobs(){
      refreshJobs.disabled=true;historyStatus.textContent='Loading recorded jobs…';
      try{const jobs=await api('/api/robocopy/jobs?limit=20');if(closed)return;const selectedId=jobPicker.value;jobPicker.replaceChildren();for(const job of jobs)jobPicker.append(new Option(`${job.kind} · ${job.state} · ${new Date(job.startedAt).toLocaleString()} · ${job.id.slice(0,8)}`,job.id));if(jobs.some(j=>j.id===selectedId))jobPicker.value=selectedId;openJob.disabled=!jobs.length;historyStatus.textContent=jobs.length?`${jobs.length} recent jobs. Opening a record replaces the current draft.`:'No transfer jobs have been recorded.';}
      catch(error){if(!closed)historyStatus.textContent=error.message;}finally{refreshJobs.disabled=false;}
    }
    async function recoverJob(id){
      if(!id)return;const version=++revision;clearTimeout(timer);busy=true;updateButtons();status.textContent='Opening recorded job…';
      try{const job=await api('/api/robocopy/jobs/'+encodeURIComponent(id));const saved=await api('/api/robocopy/plans/'+encodeURIComponent(job.planId));if(closed||version!==revision)return;
        plan=saved;const value=saved.draft;source.value=value.source;destination.value=value.destination;patterns.value=(value.files||[]).join(';');timeout.value=value.timeoutSeconds;rollback.value=value.rollbackStatement;options.clear();for(const o of value.options||[])options.set(o.name,o.enabled??o.number??o.text??o.items?.join('\n')??'');
        busy=!job.finishedAt;draw();showJob(job);if(busy)timer=setTimeout(()=>poll(id,version),1000);
      }catch(error){if(!closed&&version===revision){busy=false;status.textContent='Could not reopen job: '+error.message;updateButtons();}}
    }
    function dirty(){revision++;plan=null;lastJob=null;clearTimeout(timer);summary.replaceChildren();status.textContent='Draft changed. Validate and save a new plan before previewing.';updateButtons();}
    [source,destination,patterns,timeout,rollback].forEach(input=>input.addEventListener('input',dirty));
    [search,category,selected].forEach(input=>input.addEventListener(input===search?'input':'change',()=>{page=0;draw();}));
    form.addEventListener('submit',e=>{e.preventDefault();submit('validate');});
    function draft(){
      const typed=[];
      for(const [name,value] of options){
        const option=catalog.options.find(o=>o.id===name);if(!option)throw new Error(`Unknown catalog option: ${name}`);typed.push(typedOption(option,value));
      }
      return {source:source.value.trim(),destination:destination.value.trim(),files:patterns.value.split(';').map(s=>s.trim()).filter(Boolean),options:typed,timeoutSeconds:Number(timeout.value),maximumOutputBytes:33554432,maximumTreeEntries:100000,rollbackStatement:rollback.value};
    }
    function showJson(label,value){const section=make('details','rc-json');section.append(make('summary','',label),make('pre','',JSON.stringify(value,null,2)));summary.append(section);}
    async function submit(route){
      if(!canPlan||busy||!catalog)return;const version=revision;busy=true;updateButtons();status.textContent=route==='plans'?'Validating and saving the transfer plan…':'Checking option types, conflicts, and endpoint boundaries…';
      try{const result=await api('/api/robocopy/'+route,{method:'POST',body:draft()});if(closed||version!==revision)return;summary.replaceChildren();
        if(route==='validate'){
          summary.append(make('h3','',result.valid?'Draft validation passed':'Draft needs changes'));
          for(const issue of result.issues||[])summary.append(make('p','rc-issue',`${issue.severity}: ${issue.message}`));
          for(const block of result.executionBlocks||[])summary.append(make('p','rc-note','Execution limit: '+block));
          showJson('Validation details',result);status.textContent=result.valid?'Options are valid. Save the plan to bind its endpoints and native binary.':'Fix the reported issues before saving.';
        }else{plan=result;summary.append(make('h3','','Immutable plan saved'),make('p','rc-mono',result.id),make('p','rc-note','Plan SHA-256: '+result.planHash));
          for(const block of result.executionBlocks||[])summary.append(make('p','rc-issue','Execution limit: '+block));showJson('Review the complete plan',result);status.textContent='Plan saved. Run a list-only preview to collect native evidence.';}
      }catch(error){if(!closed&&version===revision)status.textContent=error.message;}finally{if(!closed&&version===revision){busy=false;updateButtons();}}
    }
    function showJob(job){
      lastJob=job;summary.replaceChildren(make('h3','',`${job.kind==='preview'?'Preview':'Execution'} · ${job.state}`),make('p','rc-mono',job.id));
      if(job.command){summary.append(make('p','rc-note',job.command.listOnly?'Native list-only mode. No transfer was performed.':'Inspect the execution mode before interpreting this result.'));showJson('Exact native argument vector',job.command.arguments);for(const change of job.command.presentationChanges||[])summary.append(make('p','rc-note',change));}
      if(job.nativeExit)summary.append(make('p','',`Native exit ${job.nativeExit.rawCode} · ${job.nativeExit.meaning}`));
      if(job.error)summary.append(make('p','rc-issue',job.error));
      for(const limit of job.limitations||[])summary.append(make('p','rc-note',limit));
      for(const block of job.executionBlocks||[])summary.append(make('p','rc-issue','Execution limit: '+block));
      if(job.previewHash)summary.append(make('p','rc-mono','Preview SHA-256: '+job.previewHash));
      if(job.output?.length)showJson('Captured output hashes',job.output);
      showJson('Job and verification record',job);
      if(job.finishedAt){const output=make('details','rc-json'),log=make('pre','','Loading captured native output…');output.append(make('summary','','Native output · first 65,536 characters'),log);summary.append(output);api('/api/robocopy/jobs/'+encodeURIComponent(job.id)+'/output',{raw:true}).then(r=>r.text()).then(text=>{if(!closed&&lastJob?.id===job.id)log.textContent=text||'No native output was captured.';}).catch(error=>{log.textContent=error.message;});}
      status.textContent=`${job.kind==='preview'?'Preview':'Execution'} ${job.state}. ${job.enumerationComplete?'Enumeration recorded as complete.':'Enumeration completeness is not yet established.'}`;
      updateButtons();
    }
    async function poll(id,version){
      if(closed||version!==revision)return;
      try{const job=await api('/api/robocopy/jobs/'+encodeURIComponent(id));if(closed||version!==revision)return;showJob(job);
        if(!job.finishedAt){timer=setTimeout(()=>poll(id,version),1200);}else{busy=false;updateButtons();loadJobs();}
      }catch(error){if(!closed&&version===revision){busy=false;updateButtons();status.textContent='Could not refresh preview: '+error.message;}}
    }
    async function runPreview(){if(!plan||busy||!canPlan)return;busy=true;updateButtons();const version=revision;status.textContent='Starting native list-only preview…';
      try{const job=await api('/api/robocopy/plans/'+encodeURIComponent(plan.id)+'/preview',{method:'POST'});if(closed||version!==revision)return;showJob(job);if(!job.finishedAt)timer=setTimeout(()=>poll(job.id,version),1000);else{busy=false;updateButtons();}}
      catch(error){if(!closed&&version===revision){busy=false;updateButtons();status.textContent=error.message;}}
    }
    function saveBlob(text,name,type){const blob=new Blob([text],{type}),url=URL.createObjectURL(blob),a=make('a');a.href=url;a.download=name;a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);}
    function downloadPlan(){if(!plan)return;saveBlob(JSON.stringify({schemaVersion:1,plan,job:lastJob},null,2),'filemapper-transfer-'+plan.id+'.json','application/json');}
    function draw(){if(!catalog||closed)return;const term=search.value.trim().toLowerCase();const rows=catalog.options.filter(o=>(!category.value||o.tileCategory===category.value)&&(!selected.checked||options.has(o.id))&&(!term||JSON.stringify([o.id,o.switch,o.semantics,o.effects]).toLowerCase().includes(term)));
      page=Math.max(0,Math.min(page,Math.max(0,Math.ceil(rows.length/PAGE)-1)));grid.replaceChildren();count.textContent=`${rows.length} options · ${options.size} selected`;previous.disabled=page===0;next.disabled=(page+1)*PAGE>=rows.length;
      for(const option of rows.slice(page*PAGE,(page+1)*PAGE)){
        const tile=make('article','rc-tile'+(options.has(option.id)?' rc-active':'')),head=make('label','rc-tile-head'),check=make('input');check.type='checkbox';check.checked=options.has(option.id);check.setAttribute('aria-label','Select '+option.switch);head.append(check,make('strong','',option.switch),make('span','rc-category',option.tileCategory));
        const face=make('div','rc-face'),back=make('div','rc-back');face.append(make('p','',option.semantics));back.hidden=true;back.append(make('p','rc-note','Native syntax: '+(option.installedHelp||[]).map(x=>x.syntax).join(' / ')),make('p','rc-note','Effects: '+(option.effects||[]).join(', ')),make('p','rc-note','Preview policy: '+option.previewHandling));
        if(option.destructiveApprovalRequired)face.append(make('span','rc-risk','Changes or removes existing data'));
        if(option.backendHandling)back.append(make('p','rc-note','Backend support: '+option.backendHandling));
        check.disabled=busy;
        const type=option.parameter?.type||'booleanSwitch';let input=null;
        if(type!=='booleanSwitch'){
          const wrap=make('label','rc-field');wrap.append(make('span','',type==='byteQuantity'||type==='integerBytes'?'Whole bytes':type==='ageOrDate'?'Days (0–1899) or date (YYYYMMDD)':type));input=make(type==='argumentList'?'textarea':'input');if(input.tagName==='INPUT')input.type=numericTypes.has(type)?'number':'text';if(numericTypes.has(type)){input.step='1';input.min=String(option.parameter.minimum??0);input.max=String(Math.min(option.parameter.maximum??Number.MAX_SAFE_INTEGER,Number.MAX_SAFE_INTEGER));}input.value=String(options.get(option.id)??'');input.setAttribute('aria-label',option.switch+' value');input.placeholder=option.parameter?.optional?'Blank uses native default':option.parameter?.default??'';input.disabled=!check.checked;wrap.append(input);face.append(wrap);
          input.disabled=busy||!check.checked;input.addEventListener('input',()=>{options.set(option.id,input.value);dirty();});
        }
        check.addEventListener('change',()=>{if(check.checked)options.set(option.id,input?input.value:true);else options.delete(option.id);if(input)input.disabled=!check.checked;tile.classList.toggle('rc-active',check.checked);dirty();count.textContent=`${rows.length} options · ${options.size} selected`;});
        const inner=make('div','rc-inner');back.hidden=false;back.inert=true;back.setAttribute('aria-hidden','true');inner.append(face,back);
        const flip=button('Flip to option details',()=>{const flipped=tile.classList.toggle('rc-flipped');back.inert=!flipped;face.inert=flipped;back.setAttribute('aria-hidden',String(!flipped));face.setAttribute('aria-hidden',String(flipped));flip.textContent=flipped?'Back to controls':'Flip to option details';flip.setAttribute('aria-pressed',String(flipped));});flip.className='rc-flip';flip.setAttribute('aria-pressed','false');tile.append(head,inner,flip);grid.append(tile);
      }
      if(!rows.length)grid.append(make('p','rc-note','No matching switches. Adjust the search or category.'));
    }
    updateButtons();status.textContent='Loading the native Robocopy option catalog…';
    try{catalog=await api('/api/robocopy/catalog');if(closed)return controller;for(const name of catalog.tileCategories||[])category.append(new Option(name,name));draw();status.textContent=`${catalog.options.length} cataloged options. Native binary and endpoint identities are bound when you save a plan.`;await loadJobs();}
    catch(error){if(!closed)status.textContent='Robocopy catalog unavailable: '+error.message;}
    return controller;
  }
  global.FileMapperRobocopy=Object.freeze({render,destroy});
})(window);
