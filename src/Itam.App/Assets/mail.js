// Email outbox (IT Manager): every email written by IT Assets, its recipients and status, and a preview of the email as sent
// (same layout as the CMS emails). Queued emails wait for the mail service; failed ones can be sent again.
import {$,el,api,shell,fail,message,when,card,table,svg} from '/itam.js';
import {btn,two,seg} from '/eol.js';
const q=new URLSearchParams(location.search);let status=q.get('status')||'',d,openId=Number(q.get('id'))||null;
const LABEL={Queued:'Queued',Sent:'Sent',Failed:'Failed',NoRecipient:'No recipient'};
const tag=s=>el('span',LABEL[s]||s,'tag '+(s==='Sent'?'ok':s==='Queued'?'':s==='NoRecipient'?'warn':'bad'));
async function load(note){
  d=await api('/mail'+(status?'?status='+status:''));
  $('head-actions').replaceChildren(btn('Run reminders now',async()=>{try{const r=await api('/mail/run-reminders',{method:'POST',json:{}});await load(`${r.queued} reminder email(s) written.`);}catch(e){fail(e);}},'','alerts'));
  const all=d.counts.reduce((n,c)=>n+c.count,0),count=k=>k===''?all:(d.counts.find(c=>c.status===k)?.count||0);
  const bar=seg([['','All'],['Queued','Queued'],['Sent','Sent'],['NoRecipient','No recipient'],['Failed','Failed']],status,count,k=>{status=k;history.replaceState(null,'','?status='+k);load();});
  const list=table(['Written','Email','Record','To','Subject','Status'],d.mails,
    m=>[when(m.createdAt),two(m.template,m.name),m.record||'',two(m.to||'—',m.recipients),m.subject,two(tag(m.status),m.error||(m.sentAt?'Sent '+when(m.sentAt):null))],
    null,{empty:'No emails here.',onRow:m=>{openId=m.id;history.replaceState(null,'',`?status=${status}&id=${m.id}`);show();}});
  $('view').replaceChildren(el('div',null,'x-mail-preview'),card('Emails',null,bar,list));
  show();
  if(note)message(note);
}
function show(){
  const box=document.querySelector('.x-mail-preview');const m=d.mails.find(x=>x.id===openId);if(!m){box.replaceChildren();return;}
  const f=document.createElement('iframe');f.src=`/api/mail/${m.id}/html`;f.title=m.subject;f.setAttribute('height','760');f.className='x-mail-frame';
  const info=el('div',null,'x-mail-meta');
  for(const [k,v] of [['To',m.to||'—'],['CC',m.cc],['Recipients',m.recipients],['Status',LABEL[m.status]],['Error',m.error],['Sent',m.sentAt?when(m.sentAt):null]])if(v){info.append(el('b',k),el('span',v));}
  const acts=[];if(m.status==='Failed')acts.push(btn('Send again',async()=>{try{await api(`/mail/${m.id}/retry`,{method:'POST',json:{}});await load('Queued again.');}catch(e){fail(e);}},'primary'));
  acts.push(btn('Close',()=>{openId=null;show();}));
  const a=el('div',null,'actions');a.append(...acts);
  box.replaceChildren(card(`${m.template} · ${m.subject}`,null,info,a,f));
}
try{await shell('mail');await load();}catch(e){fail(e);}
