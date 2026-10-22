// Leavers (form 29 Employee Clearance, form 30 AD User Disable Confirmation, and the Assets to collect page; plan v1.9).
// The System Administrator marks the employee Resigned: every open delivery note becomes Owner Type Resigned and appears
// below for the IT of the region to collect (receipt note as usual). Lost assets wait on their loss case, apart from the
// collection. The clearance completes only when no custody and no unresolved loss remain; then the System Administrator
// disables the user in Active Directory and keeps it.
import {$,el,api,shell,fail,message,date,when,card,table,edit} from '/itam.js';
import {btn,two,assetLink,today} from '/eol.js';
let m,d,region='all';
const disableTag=k=>k.disable==='Done'?el('span','Disabled in AD','tag ok'):k.disable==='Waiting'?el('span','To disable in AD','tag warn'):el('span',k.adEnabled?'Active in AD':'Disabled in AD','tag');
const stTag=k=>el('span',k.status==='InProgress'?'In progress':k.status,'tag '+(k.status==='Completed'?'ok':k.status==='Cancelled'?'off':'warn'));

async function load(note){
  d=await api('/leavers');
  $('head-actions').replaceChildren(...(d.canResign?[btn('Mark an employee Resigned',resign,'primary','plus')]:[]));
  const parts=[];
  // Counts per region, then the lines grouped by employee.
  const tiles=el('div',null,'x-stats');
  for(const r of [{region:'all',assets:d.collect.length,employees:new Set(d.collect.map(x=>x.employeeId)).size},...d.perRegion]){
    const t=el('button',null,'x-stat'+(r.region===region?' on':''));t.type='button';t.append(el('b',String(r.assets)),el('span',`${r.region==='all'?'All regions':r.region} · ${r.employees} employee${r.employees===1?'':'s'}`));
    t.onclick=()=>{region=r.region;load();};tiles.append(t);}
  const lines=d.collect.filter(x=>region==='all'||x.region===region);
  const groups=[...new Map(lines.map(x=>[x.employeeId,x])).values()];
  const box=el('div');
  for(const g of groups){
    const mine=lines.filter(x=>x.employeeId===g.employeeId);
    const head=el('div',null,'x-group-head');
    head.append(two(el('b',`${g.employee} (${g.username})`),[g.jobTitle,g.department,g.branch,g.manager?'Manager: '+g.manager:null].filter(Boolean).join(' · ')),
      two(`${mine.length} asset${mine.length===1?'':'s'} to collect`,[g.clearanceNo,g.markedAt?`marked ${date(g.markedAt)} by ${g.markedBy||''}`:null,g.daysPending!==null?`${g.daysPending} day${g.daysPending===1?'':'s'} pending`:null].filter(Boolean).join(' · ')));
    if(g.lastWorkingDay){const t=el('span',`Last working day ${date(g.lastWorkingDay)}`,'tag '+(g.pastLastDay?'bad':'warn'));head.append(t);}
    box.append(head,table(['Asset','Product','Serial / service tag','Computer name / extension','Delivery note','Kept at'],mine,
      x=>[assetLink(x.assetNo,x.assetId),x.lost?two(x.type,'Lost: on its loss case'):two(x.type,x.model),x.serial||'',x.computerName||x.extension||'',two(x.deliveryNo,date(x.deliveredOn)),two(x.keptAt,x.region)],
      x=>x.lost?[]:[['Record receipt',()=>location.assign('/asset.html?id='+x.assetId)]]));
  }
  if(!groups.length)box.append(el('p','Nothing to collect here.','empty'));
  parts.push(card('Assets to collect','Delivery notes with Owner Type Resigned, not yet received back. Record receipt opens the asset: receive it back there (receipt note, AD and CUCM removal). A device the employee buys is recorded as a sale.',tiles,box));
  if(d.lost.length)parts.push(card('Lost: investigation or clearance pending','Kept apart from the physical collection; each one is decided on its loss case.',
    table(['Case','Asset','Employee','Status','Responsibility'],d.lost,x=>[el('span',x.caseNo,'mono'),x.assetNo,x.employee,x.status,x.responsibility],null,{onRow:x=>location.assign('/loss.html?id='+x.id)})));
  parts.push(card('Clearances','The account stays active while assets are collected. Complete when nothing is left; then the System Administrator disables the user in AD and keeps it.',
    table(['Clearance','Employee','Last working day','Status','Still open','AD user','Completed'],d.clearances,
      k=>[el('span',k.no,'mono'),two(`${k.employee} (${k.username})`,[k.department,k.branch,k.region].filter(Boolean).join(' · ')),k.lastWorkingDay?date(k.lastWorkingDay):'',
        two(stTag(k),k.cancelledReason),k.status==='InProgress'?two(`${k.custody} asset${k.custody===1?'':'s'}`,k.loss?`${k.loss} loss case${k.loss===1?'':'s'} not cleared`:null):'',
        two(disableTag(k),k.disabledBy?`${k.disabledBy} · ${when(k.disabledAt)}`:null),k.completedBy?two(`${k.completedBy} · ${when(k.completedAt)}`,k.completionNote):''],
      k=>[...(k.can.complete?[['Complete',()=>complete(k)]]:[]),...(k.can.disabled?[['User disabled in AD',()=>disabled(k)]]:[]),...(k.can.cancel?[['Cancel',()=>cancel(k)]]:[])],{empty:'No clearances yet.'})));
  $('view').replaceChildren(...parts);
  if(note)message(note);
}
function resign(){
  edit('Mark an employee Resigned',[{key:'employeeId',label:'Employee',type:'person',required:true},{key:'lastWorkingDay',label:'Last working day',type:'date'},
    {key:'note',label:'Note',type:'textarea',max:1000,wide:true}],
    async x=>{const r=await api('/leavers',{method:'POST',json:x});await load(`${r.clearanceNo} started: ${r.assets} asset${r.assets===1?'':'s'} to collect.`);},
    {reason:false,button:'Mark Resigned',hint:'Every open delivery note of the employee becomes Owner Type Resigned and appears on Assets to collect. The AD account stays active until the clearance is completed.'});
}
function complete(k){
  edit(`Complete ${k.no}`,[{key:'text',label:'Completion note (authority and evidence)',type:'textarea',required:true,max:1000,wide:true}],
    async x=>{await api(`/leavers/${k.id}/complete`,{method:'POST',json:x});await load(`${k.no} completed: the System Administrator now disables ${k.employee} in AD (kept, not deleted).`);},{reason:false,button:'Complete the clearance'});
}
function disabled(k){
  edit(`${k.employee}: disabled in AD`,[{key:'text',label:'Note',type:'textarea',max:500,wide:true}],
    async x=>{await api(`/leavers/${k.id}/disabled`,{method:'POST',json:x});await load(`${k.employee} is recorded as disabled in AD.`);},{reason:false,button:'Confirm',hint:'Disable the user account in Active Directory and keep it (do not delete it).'});
}
function cancel(k){
  edit(`Cancel ${k.no}`,[{key:'reason',label:'Reason (for example the resignation was withdrawn)',type:'textarea',required:true,max:500,wide:true}],
    async x=>{await api(`/leavers/${k.id}/cancel`,{method:'POST',json:x});await load(`${k.no} cancelled: the delivery notes have their owner type back.`);},{reason:false,button:'Cancel the clearance'});
}
try{m=await shell('leavers');await load();}catch(e){fail(e);}
