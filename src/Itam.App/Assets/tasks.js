// Tasks page: the System Administrator removes returned computers from Active Directory and Endpoint Central; the Voice
// Administrator removes returned IP phone extensions from CUCM. Created automatically when an asset is received back.
import {$,el,api,shell,fail,message,when,table,edit} from '/itam.js';
const FILTERS=[['open','Open'],['done','Done'],['all','All']];
let filter='open';
const stat=(n,t)=>{const s=el('div',null,'stat');s.append(el('b',String(n)),el('span',t));return s;};
async function load(note){
  const d=await api('/tasks');
  const ad=d.tasks.filter(t=>t.kind==='RemoveFromAd'),cucm=d.tasks.filter(t=>t.kind==='RemoveFromCucm');
  $('intro').textContent=d.mine.ad&&d.mine.cucm?'Remove returned computers from Active Directory and Endpoint Central, and returned IP phone extensions from CUCM.'
    :d.mine.ad?'Remove each returned computer from Active Directory and Endpoint Central, then mark the task done.'
    :d.mine.cucm?'Remove each returned IP phone extension from CUCM, then mark the task done.'
    :'Clean-up tasks after an asset comes back. The System Administrator and the Voice Administrator mark them done.';
  $('stats').replaceChildren(...[(d.mine.ad||!d.mine.cucm)&&stat(ad.filter(t=>!t.done).length,'computers to remove from AD and Endpoint Central'),
    (d.mine.cucm||!d.mine.ad)&&stat(cucm.filter(t=>!t.done).length,'extensions to remove from CUCM'),stat(d.tasks.filter(t=>t.done).length,'done')].filter(Boolean));
  $('filter').replaceChildren(...FILTERS.map(([k,t])=>{const b=el('button',t);b.type='button';b.setAttribute('aria-pressed',String(k===filter));b.onclick=()=>{filter=k;load();};return b;}));
  const list=d.tasks.filter(t=>filter==='all'||(filter==='open'?!t.done:!!t.done));
  $('list-hint').textContent='Created automatically when an asset is received back. Open the asset to see its history.';
  $('list').replaceChildren(table(['Task','Asset No.','Created','State'],list,
    t=>[t.subject,(()=>{const a=el('a',t.assetNo);a.href='/asset.html?id='+t.assetId;return a;})(),when(t.created),t.done?el('span',`Done ${when(t.done)} by ${t.doneBy||''}${t.note?' · '+t.note:''}`,'tag ok'):el('span','Open','tag warn')],
    t=>t.canDo?[['Mark done',()=>edit(t.subject,[{key:'note',label:'Note',type:'textarea',wide:true,max:500,hint:'Optional, for example the change number.'}],
      x=>api(`/tasks/${t.id}/done`,{method:'POST',json:{note:x.note}}),{reason:false,button:'Mark done',done:()=>location.assign('/tasks.html?done=1')})]]:[],
    {empty:filter==='open'?'Nothing to do.':'No tasks.'}));
  if(note)message(note);
}
try{const m=await shell('tasks');if(!m.can.tasks)throw Error('You have no infrastructure tasks.');await load(new URLSearchParams(location.search).get('done')?'Task done.':null);}catch(e){fail(e);}
