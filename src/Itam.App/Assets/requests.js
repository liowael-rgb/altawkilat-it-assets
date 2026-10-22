// Requests (IT): every device and accessory request, by stage. The IT Assets Officer sees all; an IT Support Specialist sees
// those assigned to them and of their regions.
import {$,el,api,shell,fail,when,money,card,table,timeLeft} from '/itam.js';
const GROUPS=[['it','With IT',['InReview','StockAssigned','PricingInRegion','Approved','Ordered','CashPurchase']],['waiting','Waiting approval',['WaitingRequestApproval','WaitingCostApproval']],
  ['done','Fulfilled',['Fulfilled']],['closed','Rejected or cancelled',['Rejected','Cancelled']],['all','All',null]];
let filter=new URLSearchParams(location.search).get('filter')||'it';
const two=(a,b)=>{const s=el('span');s.append(el('span',a));if(b)s.append(el('small',b,'x-sub'));return s;};
async function load(){
  const d=await api('/requests');
  const seg=el('div',null,'x-seg');
  for(const [k,t,st] of GROUPS){const n=d.requests.filter(r=>!st||st.includes(r.status)).length;const b=el('button',t);b.append(el('span',String(n),'x-segc'+(k==='it'&&n?' hot':'')));
    b.type='button';b.setAttribute('aria-pressed',String(k===filter));b.onclick=()=>{filter=k;history.replaceState(null,'','?filter='+k);load();};seg.append(b);}
  const bar=el('div',null,'x-filterbar');bar.append(seg);const st=GROUPS.find(g=>g[0]===filter)[2];
  const list=d.requests.filter(r=>!st||st.includes(r.status));
  $('view').replaceChildren(card('Requests',null,bar,table(['Request','Employee','What','Source','Status','Assigned to','Grand total','Asked'],list,
    r=>[el('span',r.no,'mono'),two(r.employee,[r.department,r.region].filter(Boolean).join(' · ')),two(r.type==='Replacement'?`Replacement of ${r.replaced}`:'New device',r.description),r.sourceLabel||'',
      (()=>{const s=el('span');s.append(el('span',r.statusLabel,'tag '+(r.status==='Fulfilled'?'ok':r.status==='Rejected'||r.status==='Cancelled'?'off':r.status.startsWith('Waiting')?'warn':'')));if(r.waitingSince)s.append(' ',timeLeft(r.waitingSince,d.approvalDays));return s;})(),r.assignedTo||'',r.grand?money(r.grand):'',when(r.requestedAt)],
    null,{empty:'No requests here.',onRow:r=>location.assign('/request.html?id='+r.id)})));
}
try{const m=await shell('requests');if(!m.can.requests)location.replace('/my.html');else await load();}catch(e){fail(e);}
