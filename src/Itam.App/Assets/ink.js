// Ink page (build step 3a): ask for ink, the ink officer sends it to the approving manager, and IT orders from the supplier
// and delivers. No delivery note to sign: the request document with its approval goes in the payment pack.
import {$,el,api,shell,fail,message,date,when,card,table,edit,people,option,svg,adDrawer} from '/itam.js';
import {bilingual,dialogSteps} from '/ar.js';
bilingual();
// The employee's name opens the Active Directory details (requester panel) in a side drawer.
const who=r=>{const e=d?.employees?.[r.employeeId];if(!e)return r.employee;const b=el('button',r.employee,'x-person-link');b.type='button';b.title='Active Directory details';b.onclick=x=>{x.stopPropagation();adDrawer(e);};return b;};
let m,d,filter=new URLSearchParams(location.search).get('filter')||'Submitted';
const FILTERS=[['Submitted','To send for approval'],['WaitingApproval','Waiting approval'],['Approved','To order or deliver'],['Ordered','Ordered'],['Delivered','Delivered'],['all','All']];
const stTag=r=>el('span',r.statusLabel,'tag '+(r.status==='Delivered'?'ok':r.status==='Rejected'||r.status==='Cancelled'?'off':r.status==='WaitingApproval'?'warn':''));
const open=href=>window.open(href,'_blank');
const btn=(text,fn,cls)=>{const b=el('button',text,cls||'');b.type='button';b.onclick=fn;return b;};
function docCell(r){
  const s=el('span');const a=el('a','Request PDF');a.href=`/api/ink/${r.id}/request.pdf`;a.target='_blank';s.append(a);
  if(r.deliveredOn)s.append(el('small',`Delivered ${date(r.deliveredOn)}${r.source==='Invoice'?' · invoice '+r.invoiceNo:r.source==='Shelf'?' · IT shelf':''}`,'x-sub'));return s;
}
async function load(note){
  d=await api('/ink');
  const parts=[];
  const waiting=d.approvals.filter(r=>r.status==='WaitingApproval');
  if(d.approvals.length)parts.push(card('Waiting for your approval',"The ink officer sent these to you to approve (or to the manager you stand in for during leave). A rejection needs a reason.",
    table(['Request','Employee','Printer','Toner','Qty','Note','Asked','State'],d.approvals,r=>[r.no,who(r),r.printer,r.toner,String(r.quantity),r.note||'',when(r.requestedAt),stTag(r)],
      r=>r.status==='WaitingApproval'?[['Approve',()=>decide(r,true)],['Reject',()=>decide(r,false)]]:[],{empty:'Nothing waiting for you.'})));
  parts.push(card('My ink requests','Requests for you, or that you asked for someone.',
    table(['Request','Printer','Toner','Qty','Asked','State','Document'],d.mine,r=>[r.no,`${r.printer}${r.printerLocation?' · '+r.printerLocation:''}`,r.toner,String(r.quantity),when(r.requestedAt),stTag(r),docCell(r)],
      r=>(r.status==='Submitted'||r.status==='WaitingApproval')&&(r.employeeId===d.me)?[['Cancel',()=>cancel(r)]]:[],{empty:'No ink requests yet. Use Ask for ink.'})));
  if(d.canHandle){parts.push(handleCard());parts.push(ordersCard());}
  $('view').replaceChildren(...parts);
  if(note)message(note);
}

// IT: approved requests to order or deliver; tick several approved requests to put them in one supplier order.
function handleCard(){
  const box=el('div');const seg=el('div',null,'x-seg');
  for(const [k,t] of FILTERS){const n=k==='all'?d.handle.length:d.handle.filter(r=>r.status===k).length;const b=el('button',t);b.append(el('span',String(n),'x-segc'+(n&&k!=='all'&&k!=='Delivered'?' hot':'')));b.type='button';b.setAttribute('aria-pressed',String(k===filter));b.onclick=()=>{filter=k;load();};seg.append(b);}
  const bar=el('div',null,'x-filterbar');bar.append(seg);
  const list=d.handle.filter(r=>filter==='all'||r.status===filter);
  const picked=new Set();
  const orderBtn=btn('Put the ticked requests in a supplier order',()=>order([...picked]),'primary');orderBtn.disabled=true;
  if(filter==='Approved'&&d.canOrder)bar.append(orderBtn);
  const wrap=el('div',null,'table-wrap x-ink-handle');const t=el('table');const hr=el('tr');
  for(const h of ['','Request','Employee','Printer','Toner','Qty','State','Approver / order','Document'])hr.append(el('th',h));
  const th=el('thead');th.append(hr);const tb=el('tbody');
  const two=(a,b)=>{const s=el('span');s.append(a instanceof Node?a:el('span',a));if(b)s.append(el('small',b,'x-sub'));return s;};
  for(const r of list){
    const tr=el('tr');const cb=document.createElement('input');cb.type='checkbox';cb.disabled=r.status!=='Approved';cb.onchange=()=>{cb.checked?picked.add(r.id):picked.delete(r.id);orderBtn.disabled=!picked.size;};
    const td=x=>{const c=el('td');c.append(x??'');tr.append(c);return c;};
    td(r.status==='Approved'&&d.canOrder?cb:'');td(r.no).className='nowrap';td(two(who(r),r.branch));td(r.registered?two(r.printer,r.printerModel):(()=>{const s=el('span');s.append(el('span','Not registered','tag warn'),el('small',`${r.printerModel||''} · S/N ${r.newSerial}`,'x-sub'));return s;})()).className=r.registered?'nowrap':'';td(r.toner);td(String(r.quantity));const stCell=td(stTag(r));stCell.className='x-state-cell';
    td(r.orderNo?two(r.orderNo,r.supplier):r.manager?two(r.manager,r.status==='WaitingApproval'?'approver':'approved by'):'');td(docCell(r));
    const act=el('div',null,'x-row-actions');
    // Actions by role (permission matrix): printers by IT handlers, sending for approval also by the Team Leader, ordering, delivering and cancelling by the ink handlers.
    if(r.status==='Submitted'&&!r.registered){if(d.canPrinter)act.append(btn('Link printer',()=>linkPrinter(r),'link x-strong'),btn('Register printer',()=>registerPrinter(r),'link'));if(d.canOrder)act.append(btn('Cancel',()=>cancel(r),'link'));}
    else if(r.status==='Submitted'||r.status==='WaitingApproval'){if(d.canSend)act.append(btn(r.status==='Submitted'?'Send for approval':'Change approver',()=>send(r),r.status==='Submitted'?'link x-strong':'link'));if(d.canOrder)act.append(btn('Cancel',()=>cancel(r),'link'));}
    if((r.status==='Approved'||r.status==='Ordered')&&d.canOrder){act.append(btn('Deliver',()=>deliver(r),'link'),btn('Cancel',()=>cancel(r),'link'));}
    if(act.children.length)stCell.append(act);tb.append(tr);
  }
  if(!list.length){const tr=el('tr');const c=el('td','Nothing here.','empty');c.colSpan=9;tr.append(c);tb.append(tr);}
  t.append(th,tb);wrap.append(t);box.append(bar,wrap);
  return card('Requests to handle (Ink Officer)','Ink requests of your regions. Deliver from the shelf, or tick several and put them in one supplier order (today\'s order email); when the toner arrives with the invoice, deliver from the invoice line.',box);
}

// Ink Officer: a request for a printer not in IT Assets is linked to the printer's Asset No., or the printer is registered
// (old stock, no PO) and its label printed. Only then can it be sent for approval.
function linkPrinter(r){
  edit(`Link ${r.no} to its printer`,[{key:'assetNo',label:'Printer Asset No.',required:true,max:20,hint:`The employee typed serial number ${r.newSerial} (${r.printerModel||'model not given'}). Use Register printer if it has no IT label.`}],
    async x=>{const list=await api('/printers?q='+encodeURIComponent(x.assetNo.trim()));const p=list.find(p=>p.assetNo.toUpperCase()===x.assetNo.trim().toUpperCase());
      if(!p)throw Error(`${x.assetNo.trim()} is not an active printer in IT Assets.`);
      await api(`/ink/${r.id}/printer`,{method:'POST',json:{printerId:p.id}});await load(`${r.no} linked to ${p.assetNo}. Send it for approval.`);},{reason:false,button:'Link'});
}
function registerPrinter(r){
  // Manufacturer first, then its printer models (or a new model name), as on the register form.
  const makers=m.lists.manufacturers.filter(f=>f.active);const pnt=m.lists.models.filter(x=>x.type==='PNT'&&x.active);
  const full=x=>`${(makers.find(f=>f.id===x.manufacturer)||{}).name||''} ${x.name}`.trim();
  const pick=pnt.find(x=>full(x)===r.printerModel);
  const guess=pick?null:makers.find(f=>(r.printerModel||'').toLowerCase().startsWith(f.name.toLowerCase()+' '));
  const typed=pick?'':guess?(r.printerModel||'').slice(guess.name.length).trim():(r.printerModel||'');
  let want=pick?String(pick.id):r.printerModel?'other':'';
  const locs=m.lists.locations.filter(l=>l.active);const here=locs.find(l=>l.name===r.printerLocation);
  edit(`Register the printer of ${r.no}`,[
    {key:'location',label:'Kept at',type:'select',required:true,options:[['','Choose'],...locs.map(l=>[l.id,`${l.name} (${l.region})`])],value:here?.id,hint:r.printerLocation?`The employee's office in Active Directory: ${r.printerLocation}.`:'The employee has no office in Active Directory.'},
    {key:'maker',label:'Manufacturer',type:'select',required:true,options:[['','Choose'],...makers.map(f=>[f.id,f.name])],value:pick?.manufacturer??guess?.id,
      onchange:i=>{const keep=i.model.value||want;want='';i.model.replaceChildren(option('','Choose'),...pnt.filter(x=>String(x.manufacturer)===i.maker.value).map(x=>option(x.id,x.name)),option('other','New model (type its name)'));
        i.model.value=[...i.model.options].some(o=>o.value===keep)?keep:'';i.model.dispatchEvent(new Event('change'));}},
    {key:'model',label:'Model',type:'select',required:true,options:[['','Choose']],onchange:i=>{i.modelText.closest('label').hidden=i.model.value!=='other';i.modelText.required=i.model.value==='other';}},
    {key:'modelText',label:'New model name',max:100,value:typed,hint:'As written on the printer, without the manufacturer (for example HL-L2350DW).'},
    {key:'serial',label:'Serial number',required:true,max:100,value:r.newSerial},
    {key:'toner',label:'Toner code',max:100,value:r.toner},
    {key:'where',label:'Printer location',max:200,hint:'For example Reception or 2nd floor.'},
    {key:'condition',label:'Condition',type:'select',options:[['Used','Used'],['New','New']]}],
    async x=>{const other=x.model==='other';if(!x.maker)throw Error('Choose the manufacturer.');if(!x.model)throw Error('Choose the model.');if(other&&!(x.modelText||'').trim())throw Error('Type the new model name.');
      const res=await api(`/ink/${r.id}/register-printer`,{method:'POST',json:{locationId:Number(x.location)||null,manufacturerId:Number(x.maker),
        modelId:other?null:Number(x.model),newModel:other?x.modelText:null,serialNo:x.serial,tonerCode:x.toner,printerLocation:x.where,condition:x.condition}});
      open(`/api/assets/${res.id}/label.pdf`);await load(`Printer ${res.assetNo} registered and linked to ${r.no}. Stick its label on the printer, then send the request for approval.`);},
    {reason:false,button:'Register and print the label',hint:`The employee typed: ${r.printerModel||'no model'}, serial number ${r.newSerial}. Old stock, no PO; the printer is In Use where it is kept.`});
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
  edit('Ask for ink: find your printer',[{key:'q',label:'Printer Asset No., serial number or toner code',required:true,wide:true,hint:'The Asset No. is on the IT label of the printer (for example PNT-4811). No label? Type the serial number (S/N) from the sticker on the printer.'}],
    async x=>{const list=await api('/printers?q='+encodeURIComponent(x.q||''));setTimeout(()=>list.length?details(list,isIt):newPrinter(isIt,x.q),0);},{reason:false,button:'Find'});
  dialogSteps(['Find your printer by its Asset No. (on the label), serial number or toner code, or choose it from the list.',
    'Choose the senior manager who approves (for example your Head of Department). They get an email.',
    'After approval, the Ink Officer of your region delivers the toner from the shelf or orders it from the supplier. Follow it under My ink requests.']);
}
async function details(list,isIt){
  if(isIt)await people('');
  edit('Ask for ink',[
    {key:'printer',label:'Printer',type:'select',options:[...list.map(p=>[p.id,`${p.assetNo} · ${p.model}${p.printerLocation?' · '+p.printerLocation:''} · ${p.keptAt}`]),['new','None of these: my printer is not in the list']],wide:true,
      onchange:i=>{const p=list.find(x=>String(x.id)===i.printer.value);if(p&&!i.toner.dataset.typed)i.toner.value=p.toner||'';}},
    {key:'toner',label:'Toner',max:100,hint:'Filled from the printer; change it if needed (for example which colour).'},
    {key:'qty',label:'Quantity',type:'number',value:1,required:true},
    ...(isIt?[{key:'employee',label:'For employee (IT only)',type:'person',hint:'Leave empty to ask for yourself.'}]:[]),
    {key:'note',label:'Note',type:'textarea',wide:true,max:500,hint:'For example which colour is empty.'}],
    async x=>{if(x.printer==='new'){setTimeout(()=>newPrinter(isIt,''),0);return;}
      const r=await api('/ink',{method:'POST',json:{printerId:Number(x.printer),toner:x.toner,quantity:Math.round(x.qty||0),employeeId:x.employee||null,note:x.note}});
      await load(`${r.requestNo} sent to the Ink Officer, who sends it for approval.`);},{reason:false,button:'Send the request'});
  const t=document.querySelector('#dialog-body input[maxlength="100"]');if(t)t.addEventListener('input',()=>{t.dataset.typed='1';});
}
// A printer not in IT Assets (owner, 22 October 2026): the serial number and model are required; the location comes from the
// employee's Active Directory office. The Ink Officer links or registers the printer before sending for approval.
const printerModels=()=>m.lists.models.filter(x=>x.type==='PNT'&&x.active).map(x=>[x.id,`${(m.lists.manufacturers.find(f=>f.id===x.manufacturer)||{}).name||''} ${x.name}`.trim()]);
async function newPrinter(isIt,typed){
  if(isIt)await people('');
  edit('Ask for ink: printer not in IT Assets',[
    {key:'serial',label:'Serial number of the printer',required:true,max:100,wide:true,value:typed||'',hint:'On the sticker at the back or bottom of the printer (S/N or Serial No.).'},
    {key:'model',label:'Printer model',type:'select',required:true,options:[['','Choose'],...printerModels(),['other','Not in the list']],
      onchange:i=>{i.modelText.closest('label').hidden=i.model.value!=='other';}},
    {key:'modelText',label:'Printer model (as written on the printer)',max:100},
    {key:'toner',label:'Toner',required:true,max:100,hint:'The code printed on the cartridge (for example W2210A).'},
    {key:'qty',label:'Quantity',type:'number',value:1,required:true},
    ...(isIt?[{key:'employee',label:'For employee (IT only)',type:'person',hint:'Leave empty to ask for yourself.'}]:[]),
    {key:'note',label:'Note',type:'textarea',wide:true,max:500,hint:'For example which colour is empty.'}],
    async x=>{const r=await api('/ink',{method:'POST',json:{printerId:null,newSerial:x.serial,newModelId:x.model&&x.model!=='other'?Number(x.model):null,newModel:x.model==='other'?x.modelText:null,
        toner:x.toner,quantity:Math.round(x.qty||0),employeeId:x.employee||null,note:x.note}});
      await load(`${r.requestNo} sent to the Ink Officer, who adds the printer to IT Assets and sends it for approval.`);},
    {reason:false,button:'Send the request',hint:'Not found in IT Assets. Your region and office are taken from Active Directory.'});
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
      await load(`${res.requestNo} delivered.`);},
    {reason:false,button:'Deliver'});
}

try{m=await shell('ink');$('new-request').onclick=()=>ask().catch(fail);await load();}catch(e){fail(e);}

// The ink officer sends the request to the manager who approves it: a senior manager chosen by IT (not the Active
// Directory manager). The manager last chosen for the same department is suggested.
async function send(r){
  const last=r.department?d.lastApprover[r.department]:null;
  await people(last?last.label.replace(/^.*\((.*)\)$/,'$1'):'');
  edit(`${r.status==='Submitted'?'Send':'Re-send'} ${r.no} for approval`,[{key:'approver',label:'Approving manager',type:'person',required:true,wide:true,value:last?.label,
    hint:'A senior manager (for example the Head of Department). Type part of the name, then choose.'}],
    x=>api(`/ink/${r.id}/send`,{method:'POST',json:{approverId:x.approver}}),
    {reason:false,button:'Send for approval',hint:`${r.employee}${r.department?' ('+r.department+')':''} asks for ${r.quantity} × ${r.toner} for printer ${r.printer}.`,done:()=>load(`${r.no} sent for approval.`)});
}
