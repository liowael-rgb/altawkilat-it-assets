import {$,el,api,shell,fail,message,when,date,money,card,dl,table,statusTag,svg,edit} from '/itam.js';
import {assetForm,labelCard} from '/assetform.js';
import {custody} from '/custody.js';
import {reportLoss,requestDisposal,recordSale,btn} from '/eol.js';
const id=Number(new URLSearchParams(location.search).get('id'));
let m,d,tab='delivery';
async function load(note){
  d=await api('/assets/'+id);const a=d.asset;
  const product=[a.manufacturer,a.model||d.attributes.Software||d.attributes.TonerCode].filter(Boolean).join(' ')||a.typeName;
  $('eyebrow').textContent=`Assets › ${a.typeName}`;$('title').textContent=a.assetNo;$('subtitle').textContent=`${product} · ${a.keptAt}`;
  const label=c=>d.fields.find(f=>f.code===c)?.label||c;
  const listName=(f,v)=>f?.list?(m.lists.values.find(x=>x.list===f.list&&x.code===v)?.name||v):v;
  const acts=$('head-actions');acts.replaceChildren();
  if(d.canEdit||d.phoneOnly){const b=el('button',null,'primary');b.type='button';b.append(svg('edit'),'Edit details');b.onclick=editMode;acts.append(b);}
  const all=el('a');all.href='/';all.append(el('button','All assets'));acts.append(all);
  // End of life (step 5): the server checks the region and the asset's state.
  if(['InUse','InStore'].includes(a.status)&&m.can.loss)acts.append(btn('Report loss',()=>reportLoss(a.assetNo),'','loss'));
  if(a.status==='InStore'&&m.can.disposals)acts.append(btn('Ask for disposal',()=>requestDisposal(a.assetNo),'','disposals'));
  if(['InUse','InStore'].includes(a.status)&&m.can.sales)acts.append(btn('Record sale',()=>recordSale(a.assetNo,null),'','sales'));
  const details=dl([['Asset No.',el('strong',a.assetNo)],['Status',statusTag(a.statusLabel,a.status)],['Product type',a.typeName],['Manufacturer and model',a.manufacturer||a.model?[a.manufacturer,a.model].filter(Boolean).join(' '):null],
    ['Serial number',a.serial],['Service tag',a.serviceTag],['MAC address',a.mac?el('span',a.mac,'mono'):null],['IP address',a.ip],
    ...Object.entries(d.attributes).map(([k,v])=>[label(k),listName(d.fields.find(f=>f.code===k),v)]),['Device condition',a.condition]]);
  const where=dl([['Kept at',a.keptAt],['Kind',a.stockLocation==='ITWarehouse'?'IT warehouse':a.stockLocation==='Branch'?'Branch':'With an employee'],['Region',a.region]]);
  const purchase=dl([['PO',a.poId?(()=>{const l=el('a',a.poNumber);l.href='/po.html?id='+a.poId;return l;})():a.poNumber],['PO line',a.poLine],['Supplier',a.supplier],['Purchase date',date(a.purchaseDate)],
    ['Unit cost',a.unitCost===null?null:money(a.unitCost)],['Invoice number',a.invoiceNo],['Received',a.receivedAt?`${when(a.receivedAt)} by ${d.receivedByName}`:null],['Registered',`${when(a.createdAt)} by ${d.createdByName}`]]);
  const keys=d.keys.length?el('div'):null;
  if(keys)for(const k of d.keys){const row=el('div',null,'x-keyrow');row.append(el('b',k==='OsKey'?'Windows key':'Software key'),el('span','•••••-•••••-•••••-•••••','mono'));
    if(d.canSeeKeys){const b=el('button','Show','link');b.type='button';b.onclick=async()=>{try{const r=await api(`/assets/${id}/key/${k}`,{method:'POST'});b.replaceWith(el('span',r.value,'mono'));message('The key was shown; the view is logged.','info');}catch(e){fail(e);}};row.append(b);}
    else row.append(el('span','Only the IT Administrator and the IT Assets Officer can see it.','hint'));keys.append(row);}
  const left=el('div');left.append(...[
    card('Device',null,details),card('Where it is',null,where),card('Purchase',a.poId?'Received on a PO.':a.poNumber||a.supplier?null:'Registered without a PO (old stock).',purchase),
    keys&&card('Keys','Encrypted. Every view is logged with who and when.',keys),a.notes&&card('Notes',null,el('p',a.notes)),
    historyCard()].filter(Boolean));
  const lc=labelCard();lc.live.textContent=a.statusLabel;lc.live.className='x-tag '+(a.status==='InStore'?'ok':'acc');
  const first=d.fields[0];
  lc.set({type:a.typeName,no:a.assetNo,model:`${product}`,cells:[['Serial',a.serial],a.serviceTag?['Service tag',a.serviceTag]:first?[first.label,listName(first,d.attributes[first.code])]:['Service tag',''],
    ['Kept at',a.keptAt],a.mac?['MAC',a.mac]:['Condition',a.condition]]});
  const side=el('aside',null,'x-side');side.append(lc.card);
  // Handover: delivery notes, signed copies, receipt back, technical assessment; and the label.
  try{const hc=await custody(m,{id,assetNo:a.assetNo,status:a.status},msg=>load(msg));left.insertBefore(hc.card,left.children[1]||null);if(hc.side)side.append(hc.side);
    // With an employee: say who has it, rather than the IT warehouse it came from.
    if(hc.holder){$('subtitle').textContent=`${product} · with ${hc.holder}`;lc.set({type:a.typeName,no:a.assetNo,model:product,cells:[['Serial',a.serial],a.serviceTag?['Service tag',a.serviceTag]:['Condition',a.condition],['With',hc.holder],a.mac?['MAC',a.mac]:['Condition',a.condition]]});}}catch(e){fail(e);}
  const grid=el('div',null,'x-detail');grid.append(left,side);
  $('view').replaceChildren(grid);$('view').hidden=false;$('asset-form').hidden=true;$('head-actions').hidden=false;
  if(note)message(note);
}
function editMode(){
  const a=d.asset;const read=assetForm($('fields'),m,{asset:a,attributes:d.attributes,keys:d.keys,phoneOnly:d.phoneOnly});
  $('view').hidden=true;$('asset-form').hidden=false;$('head-actions').hidden=true;$('edit-reason').value='';$('title').textContent=`Edit ${a.assetNo}`;window.scrollTo(0,0);
  $('edit-cancel').onclick=()=>{$('view').hidden=false;$('asset-form').hidden=true;$('head-actions').hidden=false;$('title').textContent=a.assetNo;};
  $('asset-form').onsubmit=async e=>{e.preventDefault();
    try{await api('/assets/'+id,{method:'PUT',json:{...read(),version:d.asset.version,reason:$('edit-reason').value}});await load('Saved. The change is in the history.');}catch(err){fail(err);}};
}
try{m=await shell('assets');const fresh=new URLSearchParams(location.search).get('new');await load(fresh?`Registered as ${fresh}.`:null);}catch(e){fail(e);}

// History (owner, 22 October 2026): deliveries, receipts and maintenance of this asset, and every event.
function historyCard(){
  const h=d.record;const box=el('div');const seg=el('div',null,'x-seg');
  const receipts=h.deliveries.filter(x=>x.receivedAt);const open=h.orders.find(x=>['WaitingApproval','Approved','WithSupplier'].includes(x.status));
  const TABS=[['delivery','Delivery',h.deliveries.length],['receipt','Receipt',receipts.length],['maintenance','Maintenance',h.orders.length+h.itRepairs.length],['all','All events',d.history.length]];
  const doc=(text,href)=>{const a=el('a',text,'mono');a.href=href;a.target='_blank';return a;};
  const two=(x,y)=>{const s=el('span');s.append(typeof x==='string'?el('span',x):x);if(y)s.append(el('small',y,'x-sub'));return s;};
  const draw=()=>{
    for(const b of seg.children)b.setAttribute('aria-pressed',String(b.dataset.k===tab));
    if(tab==='delivery')box.replaceChildren(table(['Delivery note','Employee','Delivered','Owner type','Issued by','Signed copy','Returned'],h.deliveries,
      x=>[doc(x.no,`/api/deliveries/${x.id}/note.pdf`),two(x.employee,[x.username,x.sapId?'SAP '+x.sapId:''].filter(Boolean).join(' · ')),date(x.deliveredOn),x.ownerType||'',x.issuedBy||'',
        x.signedAt?el('span','Signed '+when(x.signedAt),'tag ok'):el('span','Waiting','tag warn'),x.receivedAt?date(x.receivedAt):el('span','With the employee','tag')],null,{empty:'Never delivered.'}));
    else if(tab==='receipt')box.replaceChildren(table(['Receipt note','From employee','Received','Received by','Condition','Received into','Technical assessment'],receipts,
      x=>[doc(x.receiptNo,`/api/deliveries/${x.id}/receipt.pdf`),x.employee,when(x.receivedAt),x.receivedBy||'',x.condition||'',x.receivedInto||'',
        x.assessmentNo?two(doc(x.assessmentNo,`/api/deliveries/${x.id}/assessment.pdf`),x.recommendation):''],null,{empty:'Never received back from an employee.'}));
    else if(tab==='maintenance'){
      const acts=el('div',null,'actions');
      if(h.canOrder&&!open){const b=el('button',null,'primary');b.type='button';b.append(svg('wrench'),'New maintenance order');b.onclick=()=>location.assign('/maintenance.html?asset='+encodeURIComponent(d.asset.assetNo));acts.append(b);}
      if(h.canItRepair){const b=el('button');b.type='button';b.append(svg('plus'),'Record an IT repair');b.onclick=itRepair;acts.append(b);}
      box.replaceChildren(...(acts.children.length?[acts]:[]),
        el('h3','Maintenance orders (outside supplier)'),table(['Order','Supplier','Maintenance details','Cost','Status','Sent / back','Outcome'],h.orders,
          x=>[(()=>{const a=el('a',x.no,'mono');a.href='/maintenance.html?filter=all';return a;})(),two(x.supplier,'Quotation '+x.quotationNo),x.details,money(x.finalCost??x.cost),
            el('span',x.statusLabel+(x.overdue?' · overdue':''),'tag '+(x.status==='Closed'?'ok':x.status==='Rejected'||x.status==='Cancelled'?'off':'warn')),
            two(x.dispatchedOn?'Sent '+date(x.dispatchedOn):'Not sent',x.returnedOn?'Back '+date(x.returnedOn):'Expected '+date(x.expectedReturn)),
            x.outcome?two(x.outcome,[x.repairDetails,x.invoiceNo?'Invoice '+x.invoiceNo:''].filter(Boolean).join(' · ')):''],null,{empty:'No maintenance orders.'}),
        el('h3','Repairs by IT'),table(['When','By','Repair done'],h.itRepairs,x=>[when(x.at),x.by||'',x.details||''],null,{empty:'No repairs by IT recorded.'}));}
    else{const t=table(['When','Who','What','Details'],d.history,x=>[when(x.at),x.who,x.kind,x.detail||'']);t.classList.add('x-history');box.replaceChildren(t);}
  };
  for(const [k,t,n] of TABS){const b=el('button',t);b.dataset.k=k;b.type='button';b.append(el('span',String(n),'x-segc'+(k==='maintenance'&&open?' hot':'')));b.onclick=()=>{tab=k;draw();};seg.append(b);}
  const bar=el('div',null,'x-filterbar');bar.append(seg);draw();
  return card('History','Delivery notes, receipts back from employees and maintenance of this asset; All events shows every change.',bar,box);
}
function itRepair(){
  edit(`Record an IT repair on ${d.asset.assetNo}`,[{key:'details',label:'Repair done by IT',type:'textarea',wide:true,required:true,max:1000,hint:'What was wrong and what IT did, for example "Replaced the keyboard from IT stock".'}],
    x=>api(`/assets/${id}/it-repair`,{method:'POST',json:{details:x.details}}),{reason:false,button:'Record',done:()=>{tab='maintenance';load('IT repair recorded on the history.');}});
}
