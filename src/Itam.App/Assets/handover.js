// Handover page (build step 2): delivery notes by state, with the ones overdue for their signed copy, and the
// infrastructure tasks (AD and Endpoint Central, CUCM) created by returns.
import {$,el,api,shell,fail,message,date,when,table,edit} from '/itam.js';
const FILTERS=[['waiting','Waiting for signed copy'],['open','With employees'],['returned','Returned'],['all','All']];
let filter=new URLSearchParams(location.search).get('status')||'waiting';
const stat=(n,t,cls)=>{const s=el('div',null,'stat'+(cls?' '+cls:''));s.append(el('b',String(n)),el('span',t));return s;};
async function load(note){
  const d=await api(`/handover?status=${filter}&q=${encodeURIComponent($('q').value.trim())}`);
  $('stats').replaceChildren(stat(d.counts.waiting,'waiting for the signed copy'),stat(d.counts.overdue,`overdue (more than ${d.signedCopyDays} days)`),stat(d.counts.withEmployees,'assets with employees'),stat(d.counts.tasks,'open infrastructure tasks'));
  $('filter').replaceChildren(...FILTERS.map(([k,t])=>{const b=el('button',t);b.type='button';b.setAttribute('aria-pressed',String(k===filter));b.onclick=()=>{filter=k;history.replaceState(null,'','?status='+k);load();};return b;}));
  const stTag=x=>el('span',x.state+(x.overdue?' (overdue)':''),'tag '+(x.state==='Returned'?'off':x.state==='Signed'?'ok':x.overdue?'bad':'warn'));
  $('list').replaceChildren(table(['Delivery note','Asset No.','Type','Employee','Delivered','Issued by','Region','State'],d.items,
    x=>[el('span',x.no,'mono'),x.assetNo,x.typeName,x.employee,date(x.deliveredOn),x.issuedBy||'',x.region,stTag(x)],null,
    {empty:filter==='waiting'?'Every delivery note has its signed copy.':'No delivery notes.',onRow:x=>location.assign('/asset.html?id='+x.assetId)}));
  $('tasks-card').hidden=!d.tasks.length;
  $('tasks').replaceChildren(table(['Task','Asset No.','Created','Done'],d.tasks,t=>[t.subject,t.assetNo,when(t.created),t.done?`${when(t.done)} by ${t.doneBy||''}${t.note?' · '+t.note:''}`:el('span','Open','tag warn')],
    t=>t.canDo?[['Mark done',()=>edit(t.subject,[{key:'note',label:'Note',type:'textarea',wide:true,max:500,hint:'Optional.'}],x=>api(`/tasks/${t.id}/done`,{method:'POST',json:{note:x.note}}),{reason:false,button:'Mark done',done:()=>load('Task done.')})]]:[],{empty:'No tasks.'}));
  if(note)message(note);
}
try{const m=await shell('handover');if(!m.can.handover)throw Error('You cannot see handovers.');$('search').onsubmit=e=>{e.preventDefault();load();};await load();}catch(e){fail(e);}
