// Movements (plan v1.9 step 4): gate passes (branch transfers and other exits) and IT warehouse transfers, by stage.
import {$,el,api,shell,fail,message,date,when,card,table,svg} from '/itam.js';
const OPEN=['Submitted','Approved','PartiallyDispatched','InTransit','Out','PartiallyReceived','ReceiptDisputed','AwaitingCustody'];
const GROUPS=[['approve','To approve',m=>m.status==='Submitted'&&m.kind!=='Warehouse'],['open','Open',m=>OPEN.includes(m.status)],['transit','In transit',m=>['InTransit','PartiallyDispatched','PartiallyReceived','Out'].includes(m.status)],
  ['disputed','Discrepancies',m=>m.status==='ReceiptDisputed'],['overdue','Overdue',m=>m.overdue],['done','Completed',m=>['Completed','ClosedWithExceptions'].includes(m.status)],['all','All',()=>true]];
let filter=new URLSearchParams(location.search).get('filter');
const btn=(text,fn,cls,icon)=>{const b=el('button',null,cls||'');b.type='button';if(icon)b.append(svg(icon));b.append(text);b.onclick=fn;return b;};
const two=(a,b)=>{const s=el('span');s.append(el('span',a));if(b)s.append(el('small',b,'x-sub'));return s;};
const moveTag=m=>{const s=el('span');s.append(el('span',m.statusLabel,'tag '+(['Completed'].includes(m.status)?'ok':['Rejected','Cancelled','Expired'].includes(m.status)?'off':['Submitted','ReceiptDisputed','AwaitingCustody','ClosedWithExceptions'].includes(m.status)?'warn':'')));
  if(m.overdue)s.append(' ',el('span','Overdue','tag bad'));return s;};
async function load(){
  const d=await api('/movements');
  filter??=d.isApprover&&d.movements.some(GROUPS[0][2])?'approve':'open';
  $('head-actions').replaceChildren(btn('New gate pass',()=>location.assign('/movement.html?new=gate'),'primary','plus'),...(d.canCreateIt?[btn('New warehouse transfer',()=>location.assign('/movement.html?new=warehouse'),'','movements')]:[]));
  const seg=el('div',null,'x-seg');
  for(const [k,t,f] of GROUPS){if(k==='approve'&&!d.isApprover)continue;const n=d.movements.filter(f).length;const b=el('button',t);b.append(el('span',String(n),'x-segc'+((k==='approve'||k==='disputed'||k==='overdue')&&n?' hot':'')));
    b.type='button';b.setAttribute('aria-pressed',String(k===filter));b.onclick=()=>{filter=k;history.replaceState(null,'','?filter='+k);load();};seg.append(b);}
  const bar=el('div',null,'x-filterbar');bar.append(seg);const f=GROUPS.find(g=>g[0]===filter)?.[2]||(()=>true);
  $('view').replaceChildren(card('Movements',null,bar,table(['No.','Kind','Purpose','From → to','Assets','Dates','Sender → receiver','Status'],d.movements.filter(f),
    m=>[el('span',m.no,'mono'),two(m.kindLabel,m.kind==='Warehouse'?'Pass '+m.passNo:m.passType==='Returnable'?'Returnable':'Non-returnable'),m.purpose,`${m.from} → ${m.to||m.destination||''}`,String(m.assets),
      two('Exit '+date(m.exitDate)+' · valid to '+date(m.validUntil),m.expectedDate?(m.kind==='Exit'?'Return ':'Receipt ')+date(m.expectedDate):''),`${m.sender||''} → ${m.receiver||m.employee||''}`,moveTag(m)],
    null,{empty:'No movements here.',onRow:m=>location.assign('/movement.html?id='+m.id)})));
}
try{const m=await shell('movements');if(!(m.can.movements||m.movementApprovals))location.replace('/my.html');else await load();}catch(e){fail(e);}
