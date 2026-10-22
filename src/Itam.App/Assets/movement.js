// One movement (plan v1.9 step 4): a gate pass (branch transfer or other exit) or an IT warehouse transfer, with what the
// signed-in person may do now; and the form for a new one (?new=gate or ?new=warehouse).
import {$,el,api,shell,fail,message,date,when,card,table,edit,form,option,svg,dl,people,adCard,withSide} from '/itam.js';
const q=new URLSearchParams(location.search);let id=Number(q.get('id'));let m,d;
const open=href=>window.open(href,'_blank');
const btn=(text,fn,cls,icon)=>{const b=el('button',null,cls||'');b.type='button';if(icon)b.append(svg(icon));b.append(text);b.onclick=fn;return b;};
const today=()=>new Date(Date.now()+3*3600e3).toISOString().slice(0,10);
const addDays=(s,n)=>{const t=new Date(s+'T00:00:00Z');t.setUTCDate(t.getUTCDate()+n);return t.toISOString().slice(0,10);};
const STATE={Pending:['Waiting dispatch',''],Dispatched:['Dispatched','warn'],Received:['Received','ok'],Missing:['Missing','bad'],Damaged:['Damaged','bad'],Incorrect:['Incorrect','bad'],
  Returned:['Returned','ok'],Lost:['Lost','off'],NeverDispatched:['Never dispatched','off'],Cancelled:['Cancelled','off']};
const RES={ArrivedLater:'Arrived later',AcceptedAtDestination:'Accepted at the destination',ReturnedToSource:'Returned to the source',NeverDispatched:'Never dispatched',ConfirmedLost:'Confirmed lost'};
const moveTag=x=>{const s=el('span');s.append(el('span',x.statusLabel,'tag '+(x.status==='Completed'?'ok':['Rejected','Cancelled','Expired'].includes(x.status)?'off':['Submitted','ReceiptDisputed','AwaitingCustody','ClosedWithExceptions'].includes(x.status)?'warn':'')));if(x.overdue)s.append(' ',el('span','Overdue','tag bad'));return s;};

async function load(note){
  d=await api('/movements/'+id);const x=d.movement,can=d.can;
  document.title='IT Assets · '+x.no;$('title').textContent=x.no;$('eyebrow').textContent=x.kindLabel+(x.kind==='Warehouse'?' · pass '+x.passNo:'');
  $('subtitle').replaceChildren(moveTag(x),` · ${x.purpose} · ${x.from} → ${x.to||x.destination||''}`);
  const acts=[];
  if(can.decide)acts.push(btn('Approve',()=>decide(true),'primary','check'),btn('Reject',()=>decide(false)));
  if(can.pass)acts.push(btn('Print the gate pass',()=>open(`/api/movements/${id}/pass.pdf`),can.decide||can.dispatch?'':'primary','doc'));
  if(can.dispatch)acts.push(btn(x.kind==='Exit'?'The assets left':'Record the dispatch',dispatch,'primary','movements'));
  if(can.receive)acts.push(btn('Confirm the receipt',receive,'primary','check'));
  if(can.returned)acts.push(btn('The assets are back',returned,'primary','check'));
  if(can.cancel)acts.push(btn('Cancel',cancel));
  $('head-actions').replaceChildren(...acts);
  const info=dl([['Requested',`${when(x.requestedAt)} by ${x.requestedBy||''}`],['Employee responsible',x.employee],['Purpose',x.purpose+(x.purposeDetails?' · '+x.purposeDetails:'')],
    ['Pass type',x.kind==='Warehouse'?`Warehouse transfer, pass ${x.passNo} version ${x.passVersion}`:x.passType==='Returnable'?'Returnable':'Non-returnable'],['From',x.from],['To',x.to||x.destination],
    ['Exit (dispatch) date',date(x.exitDate)],['Valid until',date(x.validUntil)+' (dispatch only)'],[x.kind==='Exit'?'Expected return':'Expected receipt',x.expectedDate?date(x.expectedDate):null],
    ['Named sender',x.sender],['Named receiver',x.receiver],['Carried by',x.carriedBy==='Other'?`${x.carrierName} (${x.carrierMobile})`:x.carrierName||'The employee'],
    ['Maintenance order',x.maintenanceOrder],[x.status==='Rejected'?'Rejected':'Approved',x.decidedBy?`${x.decidedBy} · ${when(x.decidedAt)}`+(x.rejectReason?' · '+x.rejectReason:''):null],
    ['Dispatched',x.dispatchedAt?when(x.dispatchedAt):null],['Closed',x.completedAt?when(x.completedAt):null],['Cancelled',x.cancelledReason]]);
  const lines=table(['Asset No.','Type','Model','Serial / service tag','Had it','State','Received','Resolution'],d.lines,
    l=>[(()=>{const a=el('a',l.assetNo,'mono');a.href='/asset.html?id='+l.assetId;return a;})(),l.type,l.model||'',l.serial||'',l.holder||'In stock',
      (()=>{const s=el('span');const [t,c]=STATE[l.state]||[l.state,''];s.append(el('span',t,'tag '+c));if(l.note)s.append(el('small',l.note,'x-sub'));return s;})(),
      l.receivedAt?`${when(l.receivedAt)}${l.receivedBy?' by '+l.receivedBy:''}${l.newDeliveryNo?' · new delivery note '+l.newDeliveryNo:''}`:'',
      l.resolution?`${RES[l.resolution]||l.resolution} · ${l.resolutionNote||''} (${l.resolvedBy||''})`:''],
    l=>can.resolve&&['Missing','Damaged','Incorrect','Dispatched'].includes(l.state)?[['Resolve',()=>resolve(l)]]:l.hasEvidence?[['Evidence',()=>open(`/api/movements/${id}/evidence/${l.id}`)]]:[]);
  $('view').replaceChildren(withSide(adCard(d.employeeAd),card('Movement',null,info),card('Assets',x.kind==='Branch'?'The named receiver confirms each asset; when the receiver is not the person who had it, the custody passes with a new delivery note to sign.':
    x.kind==='Warehouse'?'Dispatch some or all assets; the destination IT receiver confirms each one. Missing, damaged or incorrect assets stay open until an approver (Team Leader, System Administrator or IT Manager) resolves them.':'Security checks the printed pass against the labels.',lines)));
  if(note)message(note);
}
function decide(approve){
  edit(`${approve?'Approve':'Reject'} ${d.movement.no}`,approve?[{key:'valid',label:'Valid until (dispatch)',type:'date',value:d.movement.validUntil,hint:'You can shorten the dispatch validity.'}]:[{key:'reason',label:'Reason',type:'textarea',required:true,wide:true,max:500}],
    x=>api(`/movements/${id}/decision`,{method:'POST',json:{approve,reason:x.reason||null,validUntil:x.valid||null}}),{reason:false,button:approve?'Approve':'Reject',done:()=>load(approve?'Approved: print the gate pass for Security.':'Rejected.')});
}
function dispatch(){
  const pending=d.lines.filter(l=>l.state==='Pending');
  if(d.movement.kind!=='Warehouse')return edit('Record the dispatch',[],()=>api(`/movements/${id}/dispatch`,{method:'POST',json:{lineIds:null}}),{reason:false,button:'The assets left',hint:`${pending.length} asset(s) leave ${d.movement.from} now.`,done:()=>load('Dispatch recorded.')});
  edit('Record the dispatch',pending.map(l=>({key:'l'+l.id,label:`${l.assetNo} · ${l.type} · ${l.serial||''}`,type:'checkbox',value:true,wide:true})),
    x=>api(`/movements/${id}/dispatch`,{method:'POST',json:{lineIds:pending.filter(l=>x['l'+l.id]).map(l=>l.id)}}),{reason:false,button:'Dispatch the ticked assets',hint:'Untick the assets that stay at the source for now.',done:()=>load('Dispatch recorded.')});
}
function receive(){
  const lines=d.lines.filter(l=>l.state==='Dispatched');
  edit('Confirm the receipt',lines.flatMap(l=>[{key:'o'+l.id,label:`${l.assetNo} · ${l.type} · ${l.serial||''}`,type:'select',options:[['Received','Received in good condition'],['Damaged','Damaged'],['Incorrect','Incorrect (another asset)'],['Missing','Missing'],['','Not yet (still on the way)']]},
    {key:'n'+l.id,label:'Note',max:500,hint:'Required when damaged, incorrect or missing.'}]),
    x=>api(`/movements/${id}/receipt`,{method:'POST',json:{lines:lines.filter(l=>x['o'+l.id]).map(l=>({lineId:l.id,outcome:x['o'+l.id],note:x['n'+l.id]||null}))}}),
    {reason:false,button:'Confirm',hint:'Check each label against the pass. Missing, damaged or incorrect assets go to an approver to resolve.',done:()=>load('Receipt recorded.')});
}
function resolve(l){
  edit(`Resolve ${l.assetNo}`,[{key:'res',label:'Resolution',type:'select',required:true,wide:true,options:[['','Choose'],['ArrivedLater','Arrived later (received at the destination)'],['AcceptedAtDestination','Accepted at the destination (damaged or incorrect)'],
      ['ReturnedToSource','Returned to the source'],['NeverDispatched','Never dispatched (still at the source)'],['ConfirmedLost','Confirmed lost']]},
    {key:'note',label:'Investigation findings',type:'textarea',required:true,wide:true,max:1000},{key:'file',label:'Evidence',type:'file',required:true,wide:true}],
    async x=>{if(!x.file)throw Error('Attach the evidence.');await api(`/movements/${id}/resolve`,{method:'POST',body:form({lineId:l.id,resolution:x.res,note:x.note},x.file)});await load(`${l.assetNo} resolved.`);},{reason:false,button:'Resolve'});
}
function returned(){edit('The assets are back',[],()=>api(`/movements/${id}/return`,{method:'POST'}),{reason:false,button:'Confirm the return',done:()=>load('Return confirmed: the pass is closed.')});}
function cancel(){edit(`Cancel ${d.movement.no}?`,[{key:'reason',label:'Reason',required:true,wide:true,max:500}],x=>api(`/movements/${id}/cancel`,{method:'POST',json:{reason:x.reason}}),{reason:false,button:'Cancel',done:()=>load('Cancelled.')});}

// New movement ------------------------------------------------------------------------------------------------------------------------
async function newMovement(kindParam){
  const md=await api('/my');const warehouse=kindParam==='warehouse';const L=m.lists;
  $('title').textContent=warehouse?'New warehouse transfer':'New gate pass';$('eyebrow').textContent='Movement';
  $('subtitle').textContent=warehouse?'Move IT stock between IT warehouses: no approval; the stock is reserved and a pass is issued for Security.':'Take assets out of a building: to another branch (transfer) or for another purpose. The IT Helpdesk Team Leader, System Administrator or IT Manager approves; print the pass for Security.';
  const sel=(list,blank)=>{const s=document.createElement('select');if(blank!==undefined)s.append(option('',blank));for(const [v,t] of list)s.append(option(v,t));return s;};
  const inp=(type='text',max)=>{const i=document.createElement('input');i.type=type;if(max)i.maxLength=max;i.autocomplete='off';return i;};
  const field=(label,input,{req,hint,wide}={})=>{const l=el('label',null,'x-f'+(wide?' wide':''));const t=el('span',label,'x-l');t.append(req?el('span','*','req'):el('span','Optional','opt'));l.append(t,input);if(hint)l.append(el('span',hint,'x-help'));return l;};
  const person=()=>{const i=inp();i.setAttribute('list','people');i.placeholder='Type a name or account';i.oninput=()=>people(i.value).catch(()=>{});return i;};
  const personId=i=>{const p=(window.itamPeople||[]).find(s=>s.label===i.value.trim());if(i.value.trim()&&!p)throw Error('Choose the person from the list.');return p?.id||null;};
  const locs=L.locations.filter(l=>l.active);const whs=locs.filter(l=>l.kind==='ITWarehouse');
  const purpose=sel(warehouse?[]:md.purposes.map(p=>[p,p]),'Choose');const purposeText=inp('text',60);const details=document.createElement('textarea');details.maxLength=1000;
  const from=sel((warehouse?whs:locs).map(l=>[l.id,`${l.name} (${l.region})`]),'Choose');const to=sel((warehouse?whs:locs).map(l=>[l.id,`${l.name} (${l.region})`]),'Choose');
  const destination=inp('text',300);const passType=sel([['Returnable','Returnable (comes back)'],['NonReturnable','Non-returnable (IT only)']]);
  const exit=inp('date');exit.value=today();exit.min=today();const expected=inp('date');expected.value=addDays(today(),3);
  const receiver=person();const sender=person();const carried=sel([['Employee','The employee (sender)'],['Other','Another person']]);if(warehouse)carried.value='Other';
  const carrier=inp('text',200);const mobile=inp('text',30);mobile.placeholder='05XXXXXXXX';
  // Assets: own custody for employees; the assets of my regions for IT (warehouse: In Store at the source).
  const picked=new Map();const results=el('div',null,'x-pick-list');const chosen=el('div',null,'x-roles');const search=inp('search',100);search.placeholder='Asset No., serial or service tag';
  const drawChosen=()=>{chosen.replaceChildren(...[...picked.values()].map(a=>{const t=el('span',`${a.assetNo} · ${a.type}`,'tag');const x=el('button','×','x-tag-x');x.type='button';x.onclick=()=>{picked.delete(a.id);drawChosen();};t.append(x);return t;}));
    if(!picked.size)chosen.append(el('span','No asset chosen yet.','hint'));};
  const find=async()=>{try{const list=warehouse&&!from.value?[]:await api(`/movements/assets?q=${encodeURIComponent(search.value.trim())}${warehouse&&from.value?'&warehouse='+from.value:''}`);
    results.replaceChildren(...(list.length?list.map(a=>{const b=btn([a.assetNo,a.type,a.model,a.serial,a.holder?'with '+a.holder:a.keptAt].filter(Boolean).join(' · '),()=>{picked.set(a.id,a);drawChosen();},'x-pick');return b;}):[el('p',warehouse&&!from.value?'Choose the source warehouse first.':'No asset found that you can move.','hint')]));}catch(e){fail(e);}};
  let timer;search.oninput=()=>{clearTimeout(timer);timer=setTimeout(find,250);};from.onchange=()=>{if(warehouse){picked.clear();drawChosen();}find();};
  const g=el('div',null,'x-sec-body');
  const parts={purpose:field('Purpose',warehouse?purposeText:purpose,{req:true}),details:field('Purpose details',details,{wide:true,hint:'Required when the purpose is Other.'}),
    from:field(warehouse?'Source IT warehouse':'From branch',from,{req:true}),to:field(warehouse?'Destination IT warehouse':'Destination branch',to,{req:true}),destination:field('Destination',destination,{req:true,hint:'For example the customer site or the supplier.'}),
    passType:field('Pass type',passType,{req:true}),exit:field(warehouse?'Planned dispatch':'Exit date',exit,{req:true,hint:'The pass is valid for dispatch for 3 days from this date.'}),
    expected:field(warehouse?'Expected arrival':'Expected receipt / return',expected,{req:true}),receiver:field(warehouse?'Destination IT receiver':'Named receiving employee',receiver,{req:true}),
    sender:field('Named sender',sender,{hint:'Leave empty for yourself (or the employee whose assets they are).'}),carried:field('Carried by',carried,{req:true}),carrier:field('Carrier name',carrier),mobile:field('Carrier mobile',mobile)};
  g.append(...Object.values(parts));
  const show=()=>{const p=purpose.value;const transfer=!warehouse&&p==='Move to another branch';
    parts.to.hidden=!(warehouse||transfer);parts.receiver.hidden=!(warehouse||transfer);parts.destination.hidden=warehouse||transfer||!p;parts.passType.hidden=warehouse||transfer||!p;
    parts.sender.hidden=warehouse;parts.carried.hidden=warehouse;const other=warehouse||carried.value==='Other';parts.carrier.hidden=!other;parts.mobile.hidden=!other;
    parts.expected.hidden=!(warehouse||transfer||passType.value==='Returnable');};
  purpose.onchange=show;carried.onchange=show;passType.onchange=show;show();
  const assetsBox=el('div',null,'wide');assetsBox.append(field('Find assets',search,{hint:warehouse?'Only assets In Store at the source warehouse.':'Your own assets, or (IT) the assets of your regions.'}),results,el('h3','Assets on this movement'),chosen);
  drawChosen();
  const save=btn(warehouse?'Submit and reserve the stock':'Send for approval',async()=>{try{
      const p=warehouse?'':purpose.value;const transfer=p==='Move to another branch';
      const body={kind:warehouse?'Warehouse':transfer?'Branch':'Exit',assetIds:[...picked.keys()],purpose:warehouse?purposeText.value:p,purposeDetails:details.value||null,passType:passType.value,
        fromLocationId:Number(from.value)||null,toLocationId:Number(to.value)||null,destination:destination.value||null,exitDate:exit.value||null,expectedDate:expected.value||null,
        carriedBy:warehouse?'Other':carried.value,carrierName:carrier.value||null,carrierMobile:mobile.value||null,receiverId:personId(receiver),senderId:personId(sender)};
      const r=await api('/movements',{method:'POST',json:body});location.assign('/movement.html?id='+r.id+'&created=1');}catch(e){fail(e);}},'primary','check');
  const acts=el('div',null,'actions');acts.append(save,btn('Cancel',()=>history.back()));
  $('view').replaceChildren(card(warehouse?'Warehouse transfer':'Gate pass',null,g),card('Assets',null,assetsBox),acts);find();
}

try{m=await shell(q.get('new')?'movements':'movements');
  if(q.get('new'))await newMovement(q.get('new'));
  else{if(!id&&q.get('no'))id=(await api('/movements/find?no='+encodeURIComponent(q.get('no')))).id;await load(q.get('created')?'Created.':null);}}catch(e){fail(e);}
