// One IT procurement request PR-n (plan v1.9 step 3): what was asked, each step with who and when, the cost table, and the
// actions the signed-in person may take now: the manager approves the request and the cost; the IT Assets Officer chooses the
// source, prices a new device, records the order, the delivery and the date sent to Accounting; the assigned IT Support
// Specialist prices an accessory, records the cash purchase or links the delivery.
import {$,el,api,shell,fail,message,date,when,money,card,table,edit,form,option,svg,dl,timeLeft,adCard,withSide} from '/itam.js';
import {bilingual} from '/ar.js';
bilingual();
const q=new URLSearchParams(location.search);let id=Number(q.get('id'));let m,d;
const open=href=>window.open(href,'_blank');
const btn=(text,fn,cls,icon)=>{const b=el('button',null,cls||'');b.type='button';if(icon)b.append(svg(icon));b.append(text);b.onclick=fn;return b;};
const today=()=>new Date(Date.now()+3*3600e3).toISOString().slice(0,10);
const file=(text,which)=>{const a=el('a',text);a.href=`/api/requests/${id}/files/${which}`;a.target='_blank';return a;};

async function load(note){
  d=await api('/requests/'+id);const r=d.request,can=d.can;
  document.title='IT Assets · '+r.no;$('title').textContent=r.no;$('eyebrow').textContent=r.type==='Replacement'?'Device request · Replacement':'Device request · New';
  $('subtitle').replaceChildren(el('span',r.statusLabel,'tag '+(r.status==='Fulfilled'?'ok':r.status==='Rejected'||r.status==='Cancelled'?'off':r.status.startsWith('Waiting')?'warn':'')),` · ${r.employee}${r.department?' · '+r.department:''}`,...(r.waitingSince?[' · time left for the manager: ',timeLeft(r.waitingSince,d.approvalDays),' (then cancelled automatically)']:[]));
  const acts=[];
  if(can.decideRequest)acts.push(btn('Approve the request',()=>decide('decision',true),'primary','check'),btn('Reject',()=>decide('decision',false)));
  if(can.decideCost)acts.push(btn('Approve the cost',()=>decide('cost-decision',true),'primary','check'),btn('Reject',()=>decide('cost-decision',false)));
  if(can.review)acts.push(btn('Choose the source',review,'primary','edit'));
  if(can.priceNewDevice||can.priceAccessory)acts.push(btn(d.lines.length?'Change the cost table':'Prepare the cost table',costForm,'primary','doc'));
  if(can.ordered)acts.push(btn('Ordered',ordered,'primary','cart'));
  if(can.cash)acts.push(btn('Record the purchase',cash,'primary','check'));
  if(can.fulfil)acts.push(btn('Link the delivery note',fulfil,'primary','handover'));
  if(can.document)acts.push(btn('Request document',()=>open(`/api/requests/${id}/document.pdf`),'','doc'));
  if(r.deliveryId&&r.source==='NewDevice')acts.push(btn('Request + delivery note',()=>open(`/api/requests/${id}/pack.pdf`),'','doc'));
  if(can.accounting)acts.push(btn(r.accountingSentOn?'Sent to Accounting: change date':'Sent to Accounting',accounting));
  if(can.cancel)acts.push(btn('Cancel the request',cancel));
  $('head-actions').replaceChildren(...acts);
  const parts=[];
  parts.push(card('Request',null,dl([['Employee',`${r.employee} (${r.username})`],['Cost center',[r.department,r.region].filter(Boolean).join(' / ')],['Request type',r.type],
    ['Device being replaced',r.replaced],['Technical assessment',r.assessmentNo||(r.hasAssessmentFile?file('Attached file','assessment'):null)],['What is needed and why',r.description],
    ['Asked',`${when(r.requestedAt)} by ${r.requestedBy||''}`],['Direct manager',r.manager]])));
  // Steps with who and when (approvals recorded electronically).
  const steps=[['Submitted',r.requestedBy,r.requestedAt],['Request approved (direct manager or IT Manager)',r.approvedBy,r.approvedAt],
    ...(r.source?[[`Source: ${r.sourceLabel}`+(r.reserved?` · ${r.reserved}`:'')+(r.assignedTo?` · assigned to ${r.assignedTo}`:''),null,null]]:[]),
    ['Priced',r.pricedBy,r.pricedAt],['Cost approved (direct manager or IT Manager)',r.costApprovedBy,r.costApprovedAt],['Ordered'+(r.po?` on PO ${r.po}`:''),null,r.orderedAt],
    ['Fulfilled'+(r.deliveryNo?` · delivery note ${r.deliveryNo}`:r.paidAmount!=null?` · bought for ${money(r.paidAmount)} on ${date(r.purchaseDate)}`:''),null,r.fulfilledAt],
    ...(r.accountingSentOn?[[`Sent to Accounting on ${date(r.accountingSentOn)}`,null,null]]:[]),
    ...(r.status==='Rejected'?[[`Rejected by ${r.rejectedBy||''}: ${r.rejectReason||''}`,null,null]]:[]),...(r.status==='Cancelled'?[[`Cancelled${r.cancelledReason?': '+r.cancelledReason:''}`,null,null]]:[])]
    .filter(s=>s[1]||s[2]||!s[0].startsWith('Priced')&&!s[0].startsWith('Cost')&&!s[0].startsWith('Ordered')&&!s[0].startsWith('Fulfilled')&&!s[0].startsWith('Request approved'));
  parts.push(card('Progress',null,table(['Step','By','When'],steps,s=>[s[0],s[1]||'',s[2]?when(s[2]):''])));
  if(r.source==='NewDevice'||r.source==='Accessory'){
    const files=el('p');if(r.hasQuotation)files.append(file('Quotation','quotation'),' ');if(r.hasReceipt)files.append(file('Receipt','receipt'));
    parts.push(card('Cost',r.source==='NewDevice'?(r.supplier?`Supplier: ${r.supplier}`:'Prepared by the IT Assets Officer.'):(r.shop?`Shop: ${r.shop}`:'Priced in the employee\'s region.'),
      table(['Item','Specification','Qty','Unit cost (SAR, before VAT)','Line total'],d.lines,l=>[l.item,l.specification||'',String(l.quantity),money(l.unitCost),money(l.total)],null,{empty:'No cost table yet.'}),
      ...(d.lines.length?[dl([['Subtotal',money(r.net)],['VAT 15%',money(r.vat)],['Grand total including VAT',el('strong',money(r.grand))]])]:[]),files));
  }
  $('view').replaceChildren(withSide(adCard(d.employeeAd),...parts));
  if(note)message(note);
}
function decide(path,approve){
  const r=d.request;
  edit(`${approve?'Approve':'Reject'} ${r.no}`,approve?[]:[{key:'reason',label:'Reason',type:'textarea',required:true,wide:true,max:500}],
    x=>api(`/requests/${id}/${path}`,{method:'POST',json:{approve,reason:x.reason||null}}),{reason:false,button:approve?'Approve':'Reject',
      hint:path==='cost-decision'?`Grand total ${money(r.grand)} including VAT.`:r.description,done:()=>load(approve?'Approved.':'Rejected.')});
}
function review(){
  edit('Choose the source',[
    {key:'source',label:'Source',type:'select',required:true,options:[['','Choose'],['Stock','From stock (a device In Store)'],['NewDevice','New device (buy with a PO)'],['Accessory','IT accessory: buy in the region'],['Reject','Reject the request']],
      onchange:i=>{const v=i.source.value;i.asset.closest('label').hidden=v!=='Stock';i.assign.closest('label').hidden=!(v==='Stock'||v==='Accessory');i.reason.closest('label').hidden=v!=='Reject';}},
    {key:'asset',label:'Device In Store',type:'select',wide:true,options:[['','Choose'],...d.stock.map(a=>[a.id,`${a.assetNo} · ${a.type} · ${a.model||''} · ${a.serial||''} · ${a.keptAt} (${a.region})`])],hint:'Reserved on this request until it is delivered.'},
    {key:'assign',label:'IT Support Specialist',type:'select',wide:true,options:[['','Choose'],...d.specialists.map(s=>[s.id,`${s.name}${s.regions?' ('+s.regions+')':''}`])],hint:`The employee's region: ${d.request.region||'not known'}.`},
    {key:'reason',label:'Reason',type:'textarea',wide:true,max:500}],
    x=>api(`/requests/${id}/review`,{method:'POST',json:x.source==='Reject'?{rejectReason:x.reason||' '}:{source:x.source,reservedAssetId:Number(x.asset)||null,assignTo:x.assign||null}}),
    {reason:false,button:'Save',done:()=>load('Saved.')});
}
// The cost table: item, specification, quantity and unit cost before VAT; subtotal, VAT 15% and grand total.
function costForm(){
  const r=d.request,nd=r.source==='NewDevice';const rows=el('tbody');const sum=el('p',null,'hint');
  const recalc=()=>{let net=0;for(const tr of rows.children){const [, , q,u]=tr.querySelectorAll('input');net+=(Number(q.value)||0)*(Number(u.value)||0);}const vat=Math.round(net*15)/100;sum.textContent=`Subtotal ${money(net)} · VAT 15% ${money(vat)} · Grand total ${money(net+vat)}`;};
  const add=l=>{const tr=el('tr');const cells=[['text',l?.item,200],['text',l?.specification,300],['number',l?.quantity??1],['number',l?.unitCost]];
    for(const [t,v,max] of cells){const td=el('td');const i=document.createElement('input');i.type=t;if(max)i.maxLength=max;if(t==='number'){i.step=t==='number'?'0.01':'';i.min='0';}if(v!=null)i.value=v;i.oninput=recalc;td.append(i);tr.append(td);}
    const td=el('td');td.append(btn('Remove',()=>{tr.remove();recalc();},'link'));tr.append(td);rows.append(tr);recalc();};
  if(d.lines.length)for(const l of d.lines)add(l);else add(null);
  const t=el('table');const th=el('thead');const hr=el('tr');for(const h of ['Item','Specification','Qty','Unit cost (SAR, before VAT)',''])hr.append(el('th',h));th.append(hr);t.append(th,rows);
  const wrap=el('div',null,'table-wrap x-cost');wrap.append(t);
  const sup=document.createElement('select');sup.append(option('','Choose'));for(const s of m.lists.suppliers.filter(s=>s.active))sup.append(option(s.id,s.name));
  const shop=document.createElement('input');shop.maxLength=200;shop.value=r.shop||'';const f=document.createElement('input');f.type='file';f.accept='.pdf,.jpg,.jpeg,.png';
  const lab=(t,i,h)=>{const l=el('label',null,'x-f');l.append(el('span',t,'x-l'),i);if(h)l.append(el('span',h,'x-help'));return l;};
  const grid=el('div',null,'x-sec-body');grid.append(nd?lab('Supplier',sup):lab('Shop',shop,'Where it is bought in the region.'),lab(nd?'Quotation (required)':'Quotation or price photo (optional)',f));
  const save=btn('Send the cost to the manager',async()=>{try{
      const lines=[...rows.children].map(tr=>{const [i,s,q,u]=tr.querySelectorAll('input');return {item:i.value,specification:s.value,quantity:Number(q.value)||null,unitCost:u.value===''?null:Number(u.value)};});
      await api(`/requests/${id}/cost`,{method:'POST',body:form({supplierId:Number(sup.value)||null,shop:shop.value,lines},f.files[0])});await load('Cost table sent to the direct manager for approval.');}catch(e){fail(e);}},'primary','doc');
  const acts=el('div',null,'actions');acts.append(btn('Add a line',()=>add(null),'','plus'),save,btn('Cancel',()=>load()));
  $('view').replaceChildren(card(`Cost table for ${r.no}`,nd?'New device: the supplier\'s prices before VAT, with the quotation. The employee\'s direct manager or the IT Manager approves the cost.':'IT accessory: local prices in the employee\'s region before VAT. The employee\'s direct manager approves the cost.',grid,wrap,sum,acts));
}
function ordered(){
  edit('Ordered',[{key:'po',label:'PO',type:'select',wide:true,options:[['','Not recorded yet'],...d.pos.map(p=>[p.id,p.no])],hint:'The PO the device is ordered on; receive the device on the PO as usual, then deliver it to the employee.'}],
    x=>api(`/requests/${id}/ordered`,{method:'POST',json:{poId:Number(x.po)||null}}),{reason:false,button:'Save',done:()=>load('Ordered.')});
}
function cash(){
  edit('Record the purchase',[{key:'shop',label:'Shop',required:true,max:200,value:d.request.shop},{key:'on',label:'Purchase date',type:'date',value:today(),required:true},
    {key:'amount',label:'Amount paid including VAT (SAR)',type:'number',required:true,hint:`Not above the approved total, ${money(d.request.grand)}.`},{key:'file',label:'Receipt',type:'file',required:true,wide:true}],
    async x=>{if(!x.file)throw Error('Attach the receipt.');await api(`/requests/${id}/cash`,{method:'POST',body:form({shop:x.shop,purchaseDate:x.on,amount:x.amount},x.file)});await load('Purchase recorded: the request is fulfilled.');},{reason:false,button:'Save'});
}
function fulfil(){
  edit('Link the delivery note',[{key:'delivery',label:'Delivery note',type:'select',required:true,wide:true,options:[['','Choose'],...d.deliveries.map(x=>[x.id,`${x.no} · ${x.assetNo} · ${date(x.deliveredOn)}`])],
    hint:d.deliveries.length?'The delivery note of the device given to the employee for this request.':'Deliver the device to the employee from its asset page first.'}],
    x=>api(`/requests/${id}/fulfil`,{method:'POST',json:{deliveryId:Number(x.delivery)||null}}),{reason:false,button:'Save',done:()=>load('Fulfilled.')});
}
function accounting(){
  edit('Sent to Accounting',[{key:'on',label:'Date',type:'date',value:d.request.accountingSentOn||today(),required:true}],x=>api(`/requests/${id}/accounting`,{method:'POST',json:{on:x.on}}),{reason:false,done:()=>load('Saved.')});
}
function cancel(){
  edit(`Cancel ${d.request.no}?`,[{key:'reason',label:'Reason',wide:true,max:500}],x=>api(`/requests/${id}/cancel`,{method:'POST',json:{reason:x.reason}}),{reason:false,button:'Cancel the request',done:()=>load('Cancelled.')});
}

try{m=await shell(new URLSearchParams(location.search).get('from')==='my'?'my':'requests');if(!id&&q.get('no'))id=(await api('/requests/find?no='+encodeURIComponent(q.get('no')))).id;await load();}catch(e){fail(e);}
