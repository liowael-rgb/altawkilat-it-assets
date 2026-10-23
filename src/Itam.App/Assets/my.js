// My IT Assets (plan v1.9, self-service for every employee): my assets with their delivery notes, my documents, my device
// requests, what waits for my decision as a manager (request and cost approvals), and my team. Delivery notes are signed on
// paper (no electronic Accept, owner 22 October 2026). Costs, keys and supplier details are not shown here.
import {$,el,api,shell,fail,message,date,when,money,card,table,edit,form,svg,timeLeft} from '/itam.js';
let m,d;
const open=href=>window.open(href,'_blank');
const btn=(text,fn,cls,icon)=>{const b=el('button',null,cls||'');b.type='button';if(icon)b.append(svg(icon));b.append(text);b.onclick=fn;return b;};
const pdf=(text,href)=>{const a=el('a',text,'mono');a.href=href;a.target='_blank';return a;};
const two=(a,b)=>{const s=el('span');s.append(typeof a==='string'?el('span',a):a);if(b)s.append(el('small',b,'x-sub'));return s;};
const go=r=>location.assign('/request.html?from=my&id='+r.id);
const reqTag=r=>{const s=el('span');s.append(el('span',r.statusLabel,'tag '+(r.status==='Fulfilled'?'ok':r.status==='Rejected'||r.status==='Cancelled'?'off':r.status.startsWith('Waiting')?'warn':'')));if(r.waitingSince&&d)s.append(' ',timeLeft(r.waitingSince,d.approvalDays));return s;};
const what=r=>r.type==='Replacement'?`Replacement of ${r.replaced}`:'New device';
const assetCells=a=>[pdf(a.assetNo,`/api/deliveries/${a.deliveryId}/note.pdf`),a.type,a.model||'',a.serial||'',a.computerName||'',date(a.deliveredOn),a.signed?el('span','Signed','tag ok'):el('span','Sign the printed note','tag warn')];

async function load(note){
  d=await api('/my');
  // Request a gate pass next to Ask for a device (owner, 23 October 2026): to take my assets out of the building or to another branch.
  $('head-actions').replaceChildren(btn('Ask for a device',ask,'primary','plus'),btn('Request a gate pass',()=>location.assign('/movement.html?new=gate'),'','movements'),btn('Ask for ink',()=>location.assign('/ink.html'),'','ink'));
  const parts=[];
  if(d.waiting.length||d.inkApprovals){
    const c=card('Waiting for your decision','As the direct manager, or as the IT Manager for every request (or a backup during leave). A rejection needs a reason.');
    if(d.waiting.length)c.append(table(['Request','Employee','What','Step','Cost','Time left'],d.waiting,
      r=>[el('span',r.no,'mono'),two(r.employee,r.department||''),two(what(r),r.description),r.status==='WaitingCostApproval'?`Cost approval${r.sourceLabel?' · '+r.sourceLabel:''}`:'Request approval',r.status==='WaitingCostApproval'?money(r.grand):'',timeLeft(r.waitingSince,d.approvalDays)],
      r=>[['Open',()=>go(r)],['Approve',()=>decide(r,true)],['Reject',()=>decide(r,false)]]));
    if(d.inkApprovals){const p=el('p',null,'hint');const a=el('a',`${d.inkApprovals} ink request${d.inkApprovals>1?'s':''} waiting for your approval`);a.href='/ink.html';p.append(a,' on the Ink page.');c.append(p);}
    parts.push(c);
  }
  parts.push(card('My assets',d.notSigned?`${d.notSigned} delivery note${d.notSigned>1?'s are':' is'} waiting for your signature: sign the printed note and give it to IT.`:'The IT assets in your custody. Open the Asset No. for its delivery note.',
    table(['Asset No.','Type','Model','Serial / service tag','Computer name','Delivered','Delivery note'],d.assets,assetCells,a=>[['Gate pass',()=>location.assign('/movement.html?new=gate&asset='+a.assetId)]],{empty:'No IT assets in your custody.'})));
  parts.push(card('My requests','Device requests for you, or that you made for a member of your team. Ink requests are on the Ink page.',
    table(['Request','For','What','Status','Asked'],d.requests,r=>[el('span',r.no,'mono'),r.employeeId===d.me?'Me':r.employee,two(what(r),r.description),two(reqTag(r),r.rejectReason||r.cancelledReason||''),when(r.requestedAt)],
      null,{empty:'No requests yet. Use Ask for a device.',onRow:go})));
  // My gate passes and transfers: request a pass for my own assets; follow, dispatch as sender, confirm receipt as receiver.
  const mv=d.movements||[];
  parts.push(card('My gate passes and transfers','Gate passes and transfers you asked for, carry or receive. Open one to confirm the dispatch (as the sender) or the receipt (as the named receiver).',
    (()=>{const a=el('div',null,'actions');a.append(btn('Request a gate pass',()=>location.assign('/movement.html?new=gate'),'','movements'));return a;})(),
    table(['No.','Purpose','From → to','My part','Status','Exit'],mv,x=>[el('span',x.no,'mono'),x.purpose,`${x.from} → ${x.to||x.destination||''}`,
      x.receiverId===d.me?'Receiver':x.senderId===d.me?'Sender':x.employeeId===d.me?'My assets':'Requested',
      (()=>{const s=el('span');s.append(el('span',x.statusLabel,'tag '+(x.status==='Completed'?'ok':['Rejected','Cancelled','Expired'].includes(x.status)?'off':['Submitted','ReceiptDisputed','AwaitingCustody'].includes(x.status)?'warn':'')));if(x.overdue)s.append(' ',el('span','Overdue','tag bad'));return s;})(),date(x.exitDate)],
      null,{empty:'No gate passes or transfers.',onRow:x=>location.assign('/movement.html?id='+x.id)})));
  parts.push(card('My documents','Delivery and receipt notes, newest first.',
    table(['Document','Asset No.','Date'],d.documents.flatMap(x=>[...(x.receivedAt?[{...x,receipt:true}]:[]),x]),
      x=>[x.receipt?pdf(x.deliveryNo+'-R',`/api/deliveries/${x.deliveryId}/receipt.pdf`):pdf(x.deliveryNo,`/api/deliveries/${x.deliveryId}/note.pdf`),x.assetNo,x.receipt?when(x.receivedAt):date(x.deliveredOn)],
      null,{empty:'No documents yet.'})));
  if(d.team.length){
    const c=card('My team','The assets of each member of your team, their open requests and delivery notes not yet signed: follow up before someone leaves.');
    for(const p of d.team){
      const h=el('h3');h.append(p.name);if(p.title)h.append(el('small',' · '+p.title,'x-sub'));
      if(p.notSigned)h.append(' ',el('span',`${p.notSigned} not signed`,'tag warn'));c.append(h);
      c.append(table(['Asset No.','Type','Model','Serial / service tag','Computer name','Delivered','Delivery note'],p.assets,assetCells,null,{empty:'No IT assets.'}));
      if(p.requests.length)c.append(table(['Request','What','Status','Asked'],p.requests,r=>[el('span',r.no,'mono'),two(what(r),r.description),reqTag(r),when(r.requestedAt)],null,{onRow:go}));
    }
    parts.push(c);
  }
  $('view').replaceChildren(...parts);
  if(note)message(note);
}

// Ask for a device: New or Replacement (the device replaced, from the custody of the person, and its technical assessment
// when none is recorded); for a manager, a member of the team. The direct manager approves first.
function ask(){
  const people=[['','Me'],...d.team.map(p=>[p.id,p.name])];
  const assetsOf=id=>id?d.team.find(p=>p.id===id)?.assets||[]:d.assets;
  edit('Ask for a device',[
    ...(d.team.length?[{key:'who',label:'For',type:'select',options:people,onchange:i=>fill(i)}]:[]),
    {key:'type',label:'Request type',type:'select',required:true,options:[['','Choose'],['New','New'],['Replacement','Replacement']],onchange:i=>fill(i)},
    {key:'replaced',label:'Device being replaced',type:'select',options:[['','Choose']],wide:true},
    {key:'file',label:'Technical assessment of the device (if not recorded in IT Assets)',type:'file',wide:true,hint:'IT records the technical assessment when the device is checked; attach it here only if you have it as a file.'},
    {key:'description',label:'What is needed and why',type:'textarea',required:true,wide:true,max:2000}],
    async x=>{const r=await api('/requests',{method:'POST',body:form({employeeId:x.who||null,type:x.type,replacedAssetId:Number(x.replaced)||null,description:x.description},x.file)});
      await load(`${r.requestNo} sent to the direct manager for approval.`);},{reason:false,button:'Send the request',hint:'Your direct manager approves the request first; then IT handles it.'});
  function fill(i){const rep=i.type.value==='Replacement';i.replaced.closest('label').hidden=!rep;i.file.closest('label').hidden=!rep;
    const keep=i.replaced.value;i.replaced.replaceChildren();const list=assetsOf(i.who?.value||'');
    for(const [v,t] of [['','Choose'],...list.map(a=>[a.assetId,`${a.assetNo} · ${a.type} · ${a.model||''}`])]){const o=document.createElement('option');o.value=v;o.textContent=t;i.replaced.append(o);}
    i.replaced.value=keep;}
}
function decide(r,approve){
  const cost=r.status==='WaitingCostApproval';
  edit(`${approve?'Approve':'Reject'} ${r.no}`,approve?[]:[{key:'reason',label:'Reason',type:'textarea',required:true,wide:true,max:500}],
    x=>api(`/requests/${r.id}/${cost?'cost-decision':'decision'}`,{method:'POST',json:{approve,reason:x.reason||null}}),
    {reason:false,button:approve?'Approve':'Reject',hint:cost?`${r.employee}: ${what(r)}. ${r.sourceLabel}, grand total ${money(r.grand)} including VAT. Open the request to see the cost table.`:`${r.employee}: ${what(r)}. ${r.description}`,
      done:()=>load(`${r.no} ${approve?'approved':'rejected'}.`)});
}

try{m=await shell('my');await load();}catch(e){fail(e);}
