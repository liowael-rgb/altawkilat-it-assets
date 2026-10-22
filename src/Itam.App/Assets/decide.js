// One-click decision from an approval email (rules of the email-template README): opening the link only reads; the page then
// sends the decision as a signed-in request, which the server accepts only from the eligible approver and only while the
// record is still at the step the email was for. Approve needs no other click; Reject asks for a reason.
import {$,el,api,shell,fail,card,dl,when} from '/itam.js';
import {btn} from '/eol.js';
const q=new URLSearchParams(location.search);const k=q.get('k'),id=Number(q.get('id')),stage=q.get('s'),act=q.get('a');
const visible=()=>new Promise(r=>{if(document.visibilityState==='visible')return r();document.addEventListener('visibilitychange',()=>{if(document.visibilityState==='visible')r();},{once:true});});
function open(i){const a=el('a');a.href=i.link;a.append(btn('Open '+i.no,()=>{}, ''));return a;}
function render(i,result){
  $('title').textContent=i.title;$('subtitle').textContent=i.summary;$('head-actions').replaceChildren(open(i));
  const facts=dl([['Record',i.no],['Status',i.status],['Decided by',i.decidedBy?`${i.decidedBy}${i.decidedAt?' · '+when(i.decidedAt):''}`:null],['Signed in as',i.me]]);
  if(result){$('view').replaceChildren(card(result.title,result.text,facts));return;}
  if(!i.waiting){$('view').replaceChildren(card('Already decided','This record is no longer waiting for this decision, so the email link does nothing more.',facts));return;}
  if(!i.eligible){$('view').replaceChildren(card('Not your decision',`You are signed in as ${i.me}, who is not the approver of this step (or its backup on leave). Nothing was changed.`,facts));return;}
  if(act==='approve'){$('view').replaceChildren(card('Approving…',null,facts));return;}
  const reason=document.createElement('textarea');reason.maxLength=500;reason.rows=3;reason.required=true;reason.placeholder='Why it is rejected (sent to the person who asked)';
  const l=el('label',null,'wide');l.append(el('span','Reason for the rejection *','x-l'),reason);
  const go=btn('Reject',async()=>{if(!reason.value.trim()){reason.focus();return;}try{go.disabled=true;const r=await api('/quick-decision',{method:'POST',json:{kind:k,id,stage,approve:false,reason:reason.value.trim()}});
    render(r,{title:`${r.no} rejected`,text:'The person who asked is told by email, with your reason.'});}catch(e){go.disabled=false;fail(e);}},'danger');
  const ap=btn('Approve instead',async()=>{try{const r=await api('/quick-decision',{method:'POST',json:{kind:k,id,stage,approve:true}});render(r,{title:`${r.no} approved`,text:'Your approval is recorded; the next step is told by email.'});}catch(e){fail(e);}});
  const a=el('div',null,'actions');a.append(go,ap);
  $('view').replaceChildren(card('Reject',null,facts,l,a));reason.focus();
}
try{
  await shell('decide');
  const i=await api(`/quick-decision?k=${encodeURIComponent(k)}&id=${id}&s=${encodeURIComponent(stage)}`);
  render(i);
  // One click: the Approve button in the email is the decision. It is sent only from a visible page of a signed-in eligible approver.
  if(act==='approve'&&i.waiting&&i.eligible){await visible();
    try{const r=await api('/quick-decision',{method:'POST',json:{kind:k,id,stage,approve:true}});render(r,{title:`${r.no} approved`,text:'Your approval is recorded; the next step is told by email. You can close this page.'});}
    catch(e){render(i,{title:'Not approved',text:e.message});}}
}catch(e){fail(e);}
