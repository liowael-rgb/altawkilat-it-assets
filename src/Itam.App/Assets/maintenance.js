// Maintenance page (form 07 Maintenance Order, plan v1.9): the IT Assets Officer creates an order for an asset repaired by
// an outside supplier, with the quotation; the IT Manager (or the backup on leave) approves or rejects; the officer records
// the actual dispatch (asset In Repair) and the return with the outcome, repair details, final cost and invoice.
import {$,el,api,shell,fail,message,date,when,money,card,table,edit,form,option,svg} from '/itam.js';
let m,d,filter=new URLSearchParams(location.search).get('filter')||'open';
const FILTERS=[['open','Open'],['WaitingApproval','Waiting approval'],['Approved','To send'],['WithSupplier','With the supplier'],['Closed','Closed'],['all','All']];
const OPEN=['WaitingApproval','Approved','WithSupplier'];
const open=href=>window.open(href,'_blank');
const btn=(text,fn,cls,icon)=>{const b=el('button',null,cls||'');b.type='button';if(icon)b.append(svg(icon));b.append(text);b.onclick=fn;return b;};
const sel=(list,blank)=>{const s=document.createElement('select');if(blank!==undefined)s.append(option('',blank));for(const [v,t] of list)s.append(option(v,t));return s;};
const inp=(type='text',max)=>{const i=document.createElement('input');i.type=type;if(max)i.maxLength=max;i.autocomplete='off';return i;};
const field=(label,input,{req,hint,wide}={})=>{const l=el('label',null,'x-f'+(wide?' wide':''));const t=el('span',label,'x-l');t.append(req?el('span','*','req'):el('span','Optional','opt'));l.append(t,input);if(hint)l.append(el('span',hint,'x-help'));return l;};
const today=()=>new Date(Date.now()+3*3600e3).toISOString().slice(0,10);
const stTag=o=>el('span',o.statusLabel+(o.overdue?` · ${o.daysOverdue} day${o.daysOverdue>1?'s':''} overdue`:''),'tag '+(o.status==='Closed'?'ok':o.status==='Rejected'||o.status==='Cancelled'?'off':o.overdue||o.status==='WaitingApproval'?'warn':''));
const two=(a,b)=>{const s=el('span');s.append(typeof a==='string'?el('span',a):a);if(b)s.append(el('small',b,'x-sub'));return s;};
const assetLink=o=>{const a=el('a',o.assetNo,'mono');a.href='/asset.html?id='+o.assetId;return a;};

async function load(note){
  d=await api('/maintenance');
  $('head-actions').replaceChildren(...(d.canManage?[btn('New maintenance order',()=>newOrder(''),'primary','plus')]:[]));
  const parts=[];
  if(d.approvals.length)parts.push(card('Waiting for your approval','Open the quotation, then approve or reject. The asset goes to the supplier only after approval; a rejection needs a reason.',
    table(['Order','Asset','Supplier','Quotation','Cost','Maintenance details','Requested by'],d.approvals,
      o=>[el('span',o.no,'mono'),two(o.assetNo,`${o.productType} · ${o.product||''}`),o.supplier,o.quotationNo,money(o.cost),o.details,`${o.createdBy||''} · ${when(o.createdAt)}`],
      o=>[['Quotation',()=>open(`/api/maintenance/${o.id}/quotation`)],['Approve',()=>decide(o,true)],['Reject',()=>decide(o,false)]])));
  if(d.orders.length||d.canManage){
    const seg=el('div',null,'x-seg');
    for(const [k,t] of FILTERS){const n=d.orders.filter(o=>k==='all'||(k==='open'?OPEN.includes(o.status):o.status===k)).length;const b=el('button',t);
      b.append(el('span',String(n),'x-segc'+((k==='Approved'||k==='WithSupplier')&&n?' hot':'')));b.type='button';b.setAttribute('aria-pressed',String(k===filter));
      b.onclick=()=>{filter=k;history.replaceState(null,'','?filter='+k);load();};seg.append(b);}
    const bar=el('div',null,'x-filterbar');bar.append(seg);
    const list=d.orders.filter(o=>filter==='all'||(filter==='open'?OPEN.includes(o.status):o.status===filter));
    parts.push(card('Maintenance orders',null,bar,table(['Order','Asset','Supplier','Cost','Expected return','Status','Dates','Outcome'],list,
      o=>[el('span',o.no,'mono'),two(assetLink(o),`${o.productType} · ${o.product||''}`),two(o.supplier,'Quotation '+o.quotationNo),money(o.finalCost??o.cost),date(o.expectedReturn),
        (()=>{const s=el('span');s.append(stTag(o));const why=o.rejectionReason||o.cancelledReason;if(why)s.append(el('small',why,'x-sub'));else if(o.decidedBy)s.append(el('small',`${o.status==='Rejected'?'Rejected':'Approved'} by ${o.decidedBy}`,'x-sub'));return s;})(),
        two(o.dispatchedOn?'Sent '+date(o.dispatchedOn):'Not sent',o.returnedOn?'Back '+date(o.returnedOn)+(o.returnLocation?' · '+o.returnLocation:''):''),
        o.outcome?two(o.outcome,[o.repairDetails,o.invoiceNo?'Invoice '+o.invoiceNo:''].filter(Boolean).join(' · ')):''],
      o=>[['Quotation',()=>open(`/api/maintenance/${o.id}/quotation`)],...(d.canManage?actions(o):[])],{empty:'No maintenance orders here.'})));
  }
  $('view').replaceChildren(...parts);
  if(note)message(note);
}
function actions(o){
  const a=[];
  if(o.status==='Approved')a.push(['Sent to the supplier',()=>dispatch(o)]);
  if(o.status==='WithSupplier')a.push(['Back from the supplier',()=>back(o)],['Expected return',()=>expected(o)]);
  if(o.status==='WaitingApproval'||o.status==='Approved')a.push(['Cancel',()=>cancel(o)]);
  return a;
}

// New order: the Asset No. fills the product fields (read-only), then the supplier, quotation, cost and details.
function newOrder(assetNo){
  const no=inp('text',20);no.value=assetNo;no.placeholder='For example PC-4816';
  const info=el('div',null,'wide');let asset=null;
  const find=async()=>{asset=null;info.replaceChildren();if(!no.value.trim())return;
    try{const a=await api('/maintenance/asset?no='+encodeURIComponent(no.value.trim()));asset=a;
      const dl=el('dl',null,'kv');
      for(const [k,v] of [['Product type',a.productType],['Manufacturer',a.manufacturer],['Device model',a.model],['Computer type',a.computerType],['Service tag',a.serviceTag],['Processor',a.processor],
        ['Capacity',a.capacity?a.capacity+' GB':null],['Memory',a.memory?a.memory+' GB':null],['Serial No.',a.serial],['MAC address',a.mac],['Status',a.status],['Kept at',a.keptAt]]){if(!v)continue;dl.append(el('dt',k),el('dd',v));}
      info.append(dl);if(a.openOrder)info.append(el('p',`${a.assetNo} already has maintenance order ${a.openOrder}.`,'msg error'));}
    catch(e){info.append(el('p',e.message,'msg error'));}};
  no.onchange=find;
  const sup=sel(m.lists.suppliers.filter(s=>s.active).map(s=>[s.id,s.name]),'Choose');const quote=inp('text',60);const cost=inp('number');cost.step='0.01';cost.min='0';
  const details=document.createElement('textarea');details.maxLength=1000;const exp=inp('date');exp.min=today();const file=inp('file');file.accept='.pdf,.jpg,.jpeg,.png';
  const grid=el('div',null,'x-sec-body');
  grid.append(field('Asset No.',no,{req:true,hint:'Typed or scanned from the asset label; the product details fill in below.'}),field('Supplier',sup,{req:true}),info,
    field('Quotation No.',quote,{req:true}),field('Cost (SAR)',cost,{req:true,hint:'From the quotation; the IT Manager sees it to approve.'}),
    field('Maintenance details',details,{req:true,wide:true,hint:'The fault and the repair asked for.'}),field('Expected supplier return',exp,{req:true}),
    field('Quotation file',file,{req:true,hint:'The supplier\'s quotation (PDF, JPEG or PNG), sent to the IT Manager for approval.'}));
  const save=btn('Create and send for approval',async()=>{try{
      if(!file.files[0])throw Error('Attach the supplier\'s quotation.');
      const r=await api('/maintenance',{method:'POST',body:form({assetNo:no.value.trim(),supplierId:Number(sup.value)||null,quotationNo:quote.value,cost:cost.value===''?null:Number(cost.value),details:details.value,expectedReturn:exp.value||null},file.files[0])});
      filter='WaitingApproval';history.replaceState(null,'','/maintenance.html?filter=WaitingApproval');await load(`Maintenance order ${r.orderNo} created and waiting for the IT Manager's approval.`);}catch(e){fail(e);}},'primary','wrench');
  const acts=el('div',null,'actions');acts.append(save,btn('Cancel',()=>load()));
  $('view').replaceChildren(card('New maintenance order','For an asset repaired by an outside supplier. The IT Manager approves it before the asset is sent; repairs IT does itself are recorded on the asset\'s history instead.',grid,acts));
  window.scrollTo(0,0);if(assetNo)find();
}
function decide(o,approve){
  edit(`${approve?'Approve':'Reject'} ${o.no}`,approve?[]:[{key:'reason',label:'Reason',type:'textarea',required:true,wide:true,max:500}],
    x=>api(`/maintenance/${o.id}/decision`,{method:'POST',json:{approve,reason:x.reason||null}}),
    {reason:false,button:approve?'Approve':'Reject',hint:`${o.assetNo} (${o.product||o.productType}) to ${o.supplier}: ${o.details}. Quotation ${o.quotationNo}, ${money(o.cost)}.`,done:()=>load(`${o.no} ${approve?'approved':'rejected'}.`)});
}
function dispatch(o){
  edit(`${o.no}: sent to ${o.supplier}`,[{key:'on',label:'Date the asset left for the supplier',type:'date',value:today(),required:true}],
    x=>api(`/maintenance/${o.id}/dispatch`,{method:'POST',json:{dispatchedOn:x.on}}),{reason:false,button:'Record the dispatch',hint:`${o.assetNo} becomes In Repair.`,done:()=>load(`${o.assetNo} is with ${o.supplier} (In Repair).`)});
}
function back(o){
  const locs=m.lists.locations.filter(l=>l.active);
  edit(`${o.no}: back from ${o.supplier}`,[
    {key:'on',label:'End date (back from the supplier)',type:'date',value:today(),required:true},
    {key:'outcome',label:'Repair outcome',type:'select',required:true,options:[['','Choose'],['Repaired','Repaired'],['NotRepairable','Not repairable']]},
    {key:'details',label:'Repair details',type:'textarea',wide:true,required:true,max:1000,hint:'What the supplier repaired, or why it could not be repaired.'},
    {key:'cost',label:'Final cost (SAR)',type:'number',value:o.cost,hint:'From the supplier\'s invoice.'},
    {key:'invoice',label:'Invoice No.',max:60,hint:'Required when repaired. Record the invoice on the Invoices page (Parts and maintenance) to pay it.'},
    {key:'location',label:'Kept at now (if nobody has it)',type:'select',wide:true,options:[['','The employee who had it'],...locs.map(l=>[l.id,`${l.name} (${l.region})`])],hint:'An asset with an employee goes back to them (In Use); otherwise choose the branch or IT warehouse (In Store).'}],
    x=>api(`/maintenance/${o.id}/return`,{method:'POST',json:{returnedOn:x.on,outcome:x.outcome,repairDetails:x.details,finalCost:x.cost,invoiceNo:x.invoice,locationId:Number(x.location)||null}}),
    {reason:false,button:'Close the order',done:()=>load(`${o.no} closed.`)});
}
function expected(o){
  edit(`${o.no}: expected return`,[{key:'on',label:'Expected supplier return',type:'date',value:o.expectedReturn,required:true}],
    x=>api(`/maintenance/${o.id}/expected`,{method:'POST',json:{expectedReturn:x.on}}),{reason:false,done:()=>load('Saved.')});
}
function cancel(o){
  edit(`Cancel ${o.no}?`,[{key:'reason',label:'Reason',wide:true,required:true,max:500}],x=>api(`/maintenance/${o.id}/cancel`,{method:'POST',json:{reason:x.reason}}),{reason:false,button:'Cancel the order',done:()=>load(`${o.no} cancelled.`)});
}

try{m=await shell('maintenance');await load();const a=new URLSearchParams(location.search).get('asset');if(a&&d.canManage)newOrder(a);}catch(e){fail(e);}
