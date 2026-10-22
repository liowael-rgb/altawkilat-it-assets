// Ink page (build step 3a): ask for ink, approve as the direct manager, and (IT) order from the supplier and deliver with
// the ink delivery note the employee signs on paper.
import {$,el,api,shell,fail,message,date,when,card,table,edit,people,form,option,svg} from '/itam.js';
let m,d,filter='Approved';
const FILTERS=[['Approved','To order or deliver'],['Ordered','Ordered'],['Delivered','Delivered'],['all','All']];
const stTag=r=>el('span',r.statusLabel,'tag '+(r.status==='Delivered'?'ok':r.status==='Rejected'||r.status==='Cancelled'?'off':r.status==='WaitingApproval'?'warn':''));
const open=href=>window.open(href,'_blank');
const btn=(text,fn,cls)=>{const b=el('button',text,cls||'');b.type='button';b.onclick=fn;return b;};
function noteCell(r){
  if(!r.deliveryId)return '';
  const s=el('span');const a=el('a',r.deliveryNo,'mono');a.href=`/api/ink-notes/${r.deliveryId}/note.pdf`;a.target='_blank';s.append(a,' ',el('span',r.signed?'Signed':'Waiting for signed copy','tag '+(r.signed?'ok':'warn')));return s;
}
async function load(note){
  d=await api('/ink');
  const parts=[];
  const waiting=d.approvals.filter(r=>r.status==='WaitingApproval');
  if(d.approvals.length)parts.push(card('Waiting for your approval',"You approve as the employee's direct manager, or as a manager's backup during leave. A rejection needs a reason.",
    table(['Request','Employee','Printer','Toner','Qty','Note','Asked','State'],d.approvals,r=>[r.no,r.employee,r.printer,r.toner,String(r.quantity),r.note||'',when(r.requestedAt),stTag(r)],
      r=>r.status==='WaitingApproval'?[['Approve',()=>decide(r,true)],['Reject',()=>decide(r,false)]]:[],{empty:'Nothing waiting for you.'})));
  parts.push(card('My ink requests','Requests for you, or that you asked for someone.',
    table(['Request','Printer','Toner','Qty','Asked','State','Ink delivery note'],d.mine,r=>[r.no,`${r.printer}${r.printerLocation?' · '+r.printerLocation:''}`,r.toner,String(r.quantity),when(r.requestedAt),stTag(r),noteCell(r)],
      r=>r.status==='WaitingApproval'&&(r.employeeId===d.me)?[['Cancel',()=>cancel(r)]]:[],{empty:'No ink requests yet. Use Ask for ink.'})));
  if(d.canHandle){parts.push(handleCard());parts.push(ordersCard());}
  $('view').replaceChildren(...parts);
  if(note)message(note);
}

// IT: approved requests to order or deliver; tick several approved requests to put them in one supplier order.
function handleCard(){
  const box=el('div');const seg=el('div',null,'x-seg');
  for(const [k,t] of FILTERS){const b=el('button',t);b.type='button';b.setAttribute('aria-pressed',String(k===filter));b.onclick=()=>{filter=k;load();};seg.append(b);}
  const bar=el('div',null,'x-filterbar');bar.append(seg);
  const list=d.handle.filter(r=>filter==='all'||r.status===filter);
  const picked=new Set();
  const orderBtn=btn('Put the ticked requests in a supplier order',()=>order([...picked]),'primary');orderBtn.disabled=true;
  if(filter==='Approved')bar.append(orderBtn);
  const wrap=el('div',null,'table-wrap');const t=el('table');const hr=el('tr');
  for(const h of ['','Request','Employee','Branch','Printer','Toner','Qty','Region','State','Order','Ink delivery note',''])hr.append(el('th',h));
  const th=el('thead');th.append(hr);const tb=el('tbody');
  for(const r of list){
    const tr=el('tr');const cb=document.createElement('input');cb.type='checkbox';cb.disabled=r.status!=='Approved';cb.onchange=()=>{cb.checked?picked.add(r.id):picked.delete(r.id);orderBtn.disabled=!picked.size;};
    const td=x=>{const c=el('td');c.append(x??'');tr.append(c);};
    td(cb);td(r.no);td(r.employee);td(r.branch||'');td(`${r.printer}${r.printerModel?' · '+r.printerModel:''}`);td(r.toner);td(String(r.quantity));td(r.region);td(stTag(r));td(r.orderNo?`${r.orderNo} · ${r.supplier}`:'');td(noteCell(r));
    const act=el('td',null,'act');
    if(r.status==='Approved'||r.status==='Ordered'){act.append(btn('Deliver',()=>deliver(r),'link'),btn('Cancel',()=>cancel(r),'link'));}
    if(r.deliveryId&&!r.signed)act.append(btn('Upload signed copy',()=>signed(r),'link'));
    if(r.deliveryId&&r.signed)act.append(btn('Signed copy',()=>open(`/api/ink-notes/${r.deliveryId}/signed`),'link'));
    tr.append(act);tb.append(tr);
  }
  if(!list.length){const tr=el('tr');const c=el('td','Nothing here.','empty');c.colSpan=12;tr.append(c);tb.append(tr);}
  t.append(th,tb);wrap.append(t);box.append(bar,wrap);
  return card('Requests to handle (IT)','Approved requests of your regions. Deliver from the shelf, or tick several and put them in one supplier order (today\'s order email); when the toner arrives with the invoice, deliver from the invoice line.',box);
}

function ordersCard(){
  return card('Supplier orders','Print the order, or open it as an email to the supplier (nothing is sent automatically).',
    table(['Order','Supplier','Region','Requests','Delivered','Created'],d.orders,o=>[o.no,o.supplier,o.region,String(o.requests),`${o.delivered} of ${o.requests}`,`${when(o.at)} by ${o.by||''}`],
      o=>[['Order PDF',()=>open(`/api/orders/${o.id}/order.pdf`)],['Email draft',()=>mail(o)]],{empty:'No orders yet.'}));
}

function mail(o){
  const rows=d.handle.filter(r=>r.orderNo===o.no);const day=new Date();
  const body=`Dear ${o.supplier},\n\nPlease deliver the below request ASAP, with the invoice:\n\n`+rows.map(r=>`${r.no} | ${r.employee} | ${r.department||''} | ${r.branch||''} | ${r.printer} – ${r.toner} | Qty ${r.quantity}`).join('\n')+`\n\nOrder ${o.no}\nIT Department`;
  location.href=`mailto:?subject=${encodeURIComponent(`Toner Request ${day.getDate()}-${day.getMonth()+1}-${day.getFullYear()} (${o.no})`)}&body=${encodeURIComponent(body)}`;
}

async function ask(){
  const isIt=d.canHandle;
  edit('Ask for ink: find your printer',[{key:'q',label:'Printer Asset No., serial number or toner code',required:true,wide:true,hint:'The Asset No. is on the label of the printer (for example PNT-4811).'}],
    async x=>{const list=await api('/printers?q='+encodeURIComponent(x.q||''));if(!list.length)throw Error('No active printer found. Check the Asset No. on the label.');setTimeout(()=>details(list,isIt),0);},{reason:false,button:'Find'});
}
async function details(list,isIt){
  if(isIt)await people('');
  edit('Ask for ink',[
    {key:'printer',label:'Printer',type:'select',options:list.map(p=>[p.id,`${p.assetNo} · ${p.model}${p.printerLocation?' · '+p.printerLocation:''} · ${p.keptAt}`]),wide:true,
      onchange:i=>{const p=list.find(x=>String(x.id)===i.printer.value);if(p&&!i.toner.dataset.typed)i.toner.value=p.toner||'';}},
    {key:'toner',label:'Toner',required:true,max:100,hint:'Filled from the printer; change it if needed (for example which colour).'},
    {key:'qty',label:'Quantity',type:'number',value:1,required:true},
    ...(isIt?[{key:'employee',label:'For employee (IT only)',type:'person',hint:'Leave empty to ask for yourself.'}]:[]),
    {key:'ticket',label:'ServiceDesk ticket',max:40},{key:'note',label:'Note',type:'textarea',wide:true,max:500,hint:'For example which colour is empty.'}],
    async x=>{const r=await api('/ink',{method:'POST',json:{printerId:Number(x.printer),toner:x.toner,quantity:Math.round(x.qty||0),employeeId:x.employee||null,ticketNo:x.ticket,note:x.note}});
      await load(`${r.requestNo} sent to the direct manager for approval.`);},{reason:false,button:'Send the request'});
  const t=document.querySelector('#dialog-body input[maxlength="100"]');if(t)t.addEventListener('input',()=>{t.dataset.typed='1';});
}

function decide(r,approve){
  edit(`${approve?'Approve':'Reject'} ${r.no}`,approve?[]:[{key:'reason',label:'Reason',type:'textarea',required:true,wide:true,max:500}],
    x=>api(`/ink/${r.id}/decision`,{method:'POST',json:{approve,reason:x.reason||null}}),
    {reason:false,button:approve?'Approve':'Reject',hint:`${r.employee} asks for ${r.quantity} × ${r.toner} for printer ${r.printer}.`,done:()=>load(approve?`${r.no} approved.`:`${r.no} rejected.`)});
}
function cancel(r){edit(`Cancel ${r.no}?`,[{key:'reason',label:'Reason',wide:true,max:500}],x=>api(`/ink/${r.id}/cancel`,{method:'POST',json:{reason:x.reason}}),{reason:false,button:'Remove',done:()=>load(`${r.no} cancelled.`)});}

function order(ids){
  const sup=m.lists.suppliers.filter(s=>s.active);
  edit(`Supplier order for ${ids.length} request${ids.length>1?'s':''}`,[{key:'supplier',label:'Supplier',type:'select',options:[['','Choose'],...sup.map(s=>[s.id,s.name])],required:true},{key:'note',label:'Note',wide:true,max:500}],
    async x=>{const r=await api('/orders',{method:'POST',json:{supplierId:Number(x.supplier)||null,requestIds:ids,note:x.note}});open(`/api/orders/${r.id}/order.pdf`);filter='Ordered';await load(`Order ${r.orderNo} created. Send it to the supplier.`);},
    {reason:false,button:'Create the order'});
}

function deliver(r){
  const lines=d.inkLines;
  edit(`Deliver ${r.no}: ${r.quantity} × ${r.toner}`,[
    {key:'on',label:'Delivery date',type:'date',value:new Date(Date.now()+3*3600e3).toISOString().slice(0,10),required:true},
    {key:'source',label:'From',type:'select',options:[['Shelf','The IT shelf'],...(lines.length?[['Invoice','A supplier invoice']]:[])],
      onchange:i=>{i.line.closest('label').hidden=i.source.value!=='Invoice';}},
    {key:'line',label:'Invoice line',type:'select',options:[['','Choose'],...lines.map(l=>[l.id,`${l.supplier} · invoice ${l.invoice} · ${l.description} (${l.remaining} left)`])],wide:true}],
    async x=>{const res=await api(`/ink/${r.id}/deliver`,{method:'POST',json:{deliveredOn:x.on,source:x.source,invoiceLineId:x.source==='Invoice'?Number(x.line)||null:null}});
      open(`/api/ink-notes/${res.id}/note.pdf`);await load(`Delivered on ${res.deliveryNo}. Print it for the employee to sign, then upload the signed copy.`);},
    {reason:false,button:'Deliver and print the note'});
}
function signed(r){
  edit(`Upload the signed copy of ${r.deliveryNo}`,[{key:'file',label:'Signed ink delivery note (scan or photo)',type:'file',required:true,wide:true,hint:'Signed by the employee himself. Kept unchanged once uploaded.'}],
    async x=>{if(!x.file)throw Error('Choose the scan.');await api(`/ink-notes/${r.deliveryId}/signed`,{method:'POST',body:form({},x.file)});await load('Signed copy uploaded.');},{reason:false,button:'Upload'});
}

try{m=await shell('ink');$('new-request').onclick=()=>ask().catch(fail);await load();}catch(e){fail(e);}
