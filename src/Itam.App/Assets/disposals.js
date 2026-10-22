// Disposals DS-n (forms 26 to 28, plan v1.9): IT asks (or the technical assessment decides Dispose); the IT Manager (or a
// backup on leave) approves or rejects, never their own request; the IT Assets Officer records the completion with the
// evidence (data wiped and licenses removed for computers). Only the completion makes the asset Disposed.
import {$,el,api,shell,fail,message,date,when,card,table,edit,form} from '/itam.js';
import {requestDisposal,btn,two,assetLink,fileLink,seg,today} from '/eol.js';
const q=new URLSearchParams(location.search);
let m,d,filter=q.get('filter')||'open';
const FILTERS=[['open','Open'],['WaitingApproval','Waiting approval'],['Approved','Awaiting completion'],['Completed','Disposed'],['closed','Rejected or cancelled'],['all','All']];
const match=(x,k)=>k==='all'||(k==='open'?['WaitingApproval','Approved'].includes(x.row.status):k==='closed'?['Rejected','Cancelled'].includes(x.row.status):x.row.status===k);
const stTag=r=>el('span',r.statusLabel,'tag '+(r.status==='Completed'?'ok':r.status==='Rejected'||r.status==='Cancelled'?'off':'warn'));

async function load(note){
  d=await api('/disposals');
  $('head-actions').replaceChildren(...(d.canRequest?[btn('Ask for a disposal',()=>requestDisposal(''),'primary','plus')]:[]));
  const mine=d.disposals.filter(x=>x.decide);
  const parts=[];
  if(mine.length)parts.push(card('Waiting for your approval','Approval alone does not dispose: the asset stays Pending disposal approval until the completion is recorded.',
    table(['Disposal','Asset','Reason','Condition','Asked by'],mine.map(x=>x.row),r=>[el('span',r.no,'mono'),two(assetLink(r.assetNo,r.assetId),[r.type,r.model].filter(Boolean).join(' · ')),
      two(r.reason,r.assessmentNo?'Assessment '+r.assessmentNo:null),r.condition,`${r.requestedBy||''} · ${when(r.requestedAt)}`],
      r=>[['Approve',()=>decide(r,true)],['Reject',()=>decide(r,false)]])));
  const rows=d.disposals.filter(x=>match(x,filter));
  parts.push(card('Disposals',null,seg(FILTERS,filter,k=>d.disposals.filter(x=>match(x,k)).length,k=>{filter=k;history.replaceState(null,'','?filter='+k);load();}),
    table(['Disposal','Asset','Kept at','Reason','Status','Decision','Completion'],rows.map(x=>x.row),
      r=>[el('span',r.no,'mono'),two(assetLink(r.assetNo,r.assetId),[r.type,r.model,r.serial].filter(Boolean).join(' · ')),two(r.location,r.region),two(r.reason,r.assessmentNo?'Assessment '+r.assessmentNo:r.condition),
        stTag(r),r.decidedBy?two(`${r.status==='Rejected'?'Rejected':'Approved'} by ${r.decidedBy}`,r.rejectReason||when(r.decidedAt)):r.cancelledReason?two('Cancelled',r.cancelledReason):'',
        r.completedOn?two(`${r.method} · ${date(r.completedOn)}`,[r.completedBy,r.recipient,r.dataBearing?(r.dataWiped?'data wiped':'')+(r.licensesRemoved?', licenses removed':''):''].filter(Boolean).join(' · ')):''],
      r=>{const x=d.disposals.find(y=>y.row.id===r.id);const a=[];if(x.complete)a.push(['Record completion',()=>complete(r)]);if(x.cancel)a.push(['Cancel',()=>cancel(r)]);
        if(r.evidence)a.push(['Evidence',()=>window.open(`/api/disposals/${r.id}/evidence`,'_blank')]);return a;},{empty:'No disposals here.'})));
  $('view').replaceChildren(...parts);
  if(note)message(note);
}
function decide(r,approve){
  edit(`${approve?'Approve':'Reject'} ${r.no}`,[{key:'reason',label:approve?'Note':'Reason for the rejection',type:'textarea',required:!approve,max:500,wide:true}],
    async x=>{await api(`/disposals/${r.id}/decision`,{method:'POST',json:{approve,reason:x.reason}});await load(approve?`${r.no} approved: record the completion when the asset is disposed.`:`${r.no} rejected: the asset is back In Store.`);},
    {reason:false,button:approve?'Approve':'Reject',hint:`${r.assetNo}: ${r.reason}`});
}
function complete(r){
  edit(`Disposal completed: ${r.no}`,[
    {key:'completedOn',label:'Disposed on',type:'date',required:true,value:today()},
    {key:'method',label:'Method',type:'select',required:true,options:[['','Choose'],...d.methods.map(x=>[x.code,x.label])]},
    {key:'completedByName',label:'Disposed by (person)',required:true,max:200},
    {key:'recipient',label:'Recipient or vendor',max:300,hint:'Required for recycling, return to the vendor and donation.'},
    ...(r.dataBearing?[{key:'dataWiped',label:'Company data wiped (or the disk destroyed)',type:'checkbox',wide:true},{key:'licensesRemoved',label:'Company licenses removed',type:'checkbox',wide:true}]:[]),
    {key:'note',label:'Notes',type:'textarea',max:1000,wide:true},
    {key:'file',label:'Evidence (handover or destruction certificate)',type:'file',required:true,wide:true}],
    async x=>{const {file,...data}=x;await api(`/disposals/${r.id}/complete`,{method:'POST',body:form(data,file)});await load(`${r.assetNo} is Disposed.`);},
    {reason:false,button:'Record the completion'});
}
function cancel(r){
  edit(`Cancel ${r.no}`,[{key:'reason',label:'Reason',type:'textarea',required:true,max:500,wide:true}],
    async x=>{await api(`/disposals/${r.id}/cancel`,{method:'POST',json:x});await load(`${r.no} cancelled: the asset is back In Store.`);},{reason:false,button:'Cancel the disposal'});
}
try{m=await shell('disposals');await load(q.get('created')?`${q.get('created')} sent for approval: the asset is Pending disposal approval.`:null);}catch(e){fail(e);}
