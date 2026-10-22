// AD computer comparison (form 34, plan v1.9): computer accounts in the chosen OUs against the computer names on active
// delivery notes. Compare now runs it (read-only on AD); every run is kept with its counts; a difference marked reviewed
// stays hidden until the computer changes.
import {$,el,api,shell,fail,message,date,when,card,table,edit} from '/itam.js';
import {btn,two,assetLink,seg} from '/eol.js';
let d,result='open',showReviewed=false;
const q=new URLSearchParams(location.search);let run=Number(q.get('run'))||null;
const tagFor=l=>el('span',l.resultLabel,'tag '+(l.result==='Matched'?'ok':l.result==='NeedsCheck'||l.result==='RemovalWaiting'?'warn':'bad'));

async function load(note){
  d=await api('/ad-compare'+(run?'?run='+run:''));
  $('head-actions').replaceChildren(...(d.canRun?[btn('Compare now',compare,'primary','adcompare')]:[]));
  const parts=[];
  if(!d.runs.length){parts.push(card('No comparison yet',d.canRun?'Press Compare now to read the computer accounts from Active Directory.':'The comparison runs every morning once Active Directory is connected.'));$('view').replaceChildren(...parts);if(note)message(note);return;}
  const cur=d.runs.find(r=>r.id===d.run)||d.runs[0];
  const SHORT={NoDelivery:'No delivery note',RemovalWaiting:'Removal waiting',NotInAd:'Not in AD',NeedsCheck:'Needs a check',Matched:'Matched'};
  const FILTERS=[['open','Differences'],...d.results.map(r=>[r.code,SHORT[r.code]||r.label]),['all','All']];
  const visible=d.lines.filter(l=>showReviewed||!l.reviewed);
  const match=(l,k)=>k==='all'||(k==='open'?l.result!=='Matched':l.result===k);
  const rows=visible.filter(l=>match(l,result));
  const toggle=el('label',null,'inline');const cb=document.createElement('input');cb.type='checkbox';cb.checked=showReviewed;cb.onchange=()=>{showReviewed=cb.checked;load();};toggle.append(cb,'Show reviewed lines');
  const bar=seg(FILTERS,result,k=>visible.filter(l=>match(l,k)).length,k=>{result=k;load();});bar.append(toggle);
  parts.push(card(`Run of ${when(cur.at)}`,`${cur.computers} computers in ${cur.ous}; inactive after ${cur.inactiveDays} days without sign-in.${cur.by?' Run by '+cur.by+'.':''}`,bar,
    table(['Computer','Result','AD (OU, status, last sign-in)','Delivery note','Asset','Employee'],rows,
      l=>[el('span',l.computerName,'mono'),two(tagFor(l),l.reviewed?`Reviewed: ${l.reviewReason}`:null),
        l.ou?two(l.ou,[l.enabled===false?'Disabled':l.enabled?'Enabled':null,l.lastLogon?'Last sign-in '+date(l.lastLogon):'Never signed in',l.os].filter(Boolean).join(' · ')):'Not in AD',
        l.deliveryNo?two(l.deliveryNo,[date(l.deliveredOn),l.ownerType].filter(Boolean).join(' · ')):'',l.assetNo?assetLink(l.assetNo,null):'',l.employee?two(l.employee,[l.username,l.branch,l.region].filter(Boolean).join(' · ')):''],
      d.canReview?l=>l.result==='Matched'?[]:l.reviewed?[['Undo review',()=>unreview(l)]]:[['Mark reviewed',()=>review(l)]]:null,{empty:'Nothing here.'})));
  parts.push(card('Runs','Each run is kept with its counts, so the trend shows whether the differences go down.',
    table(['Run','Computers','Matched','In AD, no delivery note','Removal waiting','Not in AD','Needs a check','Reviewed'],d.runs,
      r=>[two(when(r.at),r.by||'Daily run'),String(r.computers),String(r.matched),String(r.noDelivery),String(r.removalWaiting),String(r.notInAd),String(r.needsCheck),String(r.reviewed)],
      null,{onRow:r=>{run=r.id;history.replaceState(null,'','?run='+r.id);load();}})));
  $('view').replaceChildren(...parts);
  if(note)message(note);
}
async function compare(){try{const r=await api('/ad-compare/run',{method:'POST',json:{}});run=r.runId;history.replaceState(null,'','?run='+r.runId);await load('Comparison done.');}catch(e){fail(e);}}
function review(l){
  edit(`Mark ${l.computerName} reviewed`,[{key:'reason',label:'Reason (for example a training or test computer)',type:'textarea',required:true,max:500,wide:true}],
    async x=>{await api('/ad-compare/review',{method:'POST',json:{lineId:l.id,reason:x.reason}});await load(`${l.computerName} reviewed: hidden until the computer changes.`);},{reason:false,button:'Mark reviewed'});
}
async function unreview(l){try{await api('/ad-compare/unreview',{method:'POST',json:{lineId:l.id}});await load(`${l.computerName} shows again.`);}catch(e){fail(e);}}
try{await shell('adcompare');await load();}catch(e){fail(e);}
