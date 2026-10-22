// Invoices page (build step 3a): record supplier invoices (toners, devices, parts and maintenance) with their lines, see what
// each covers, and prepare the monthly payment pack (cover memo, then each invoice with its ink request documents and approvals) for Accounting.
import {$,el,api,shell,fail,message,date,when,money,card,table,edit,form,option,svg,accounting} from '/itam.js';
let m,d,cat='all';
const open=href=>window.open(href,'_blank');
const btn=(text,fn,cls,icon)=>{const b=el('button',null,cls||'');b.type='button';if(icon)b.append(svg(icon));b.append(text);b.onclick=fn;return b;};
const sel=(list,blank)=>{const s=document.createElement('select');if(blank!==undefined)s.append(option('',blank));for(const [v,t] of list)s.append(option(v,t));return s;};
const inp=(type='text',max)=>{const i=document.createElement('input');i.type=type;if(max)i.maxLength=max;i.autocomplete='off';return i;};
const field=(label,input,{req,hint,wide}={})=>{const l=el('label',null,'x-f'+(wide?' wide':''));const t=el('span',label,'x-l');t.append(req?el('span','*','req'):el('span','Optional','opt'));l.append(t,input);if(hint)l.append(el('span',hint,'x-help'));return l;};

async function load(note){
  d=await api('/invoices');
  const acts=$('head-actions');acts.replaceChildren();
  if(d.categories.length)acts.append(btn('Record an invoice',()=>newInvoice(),'primary','invoices'));
  if(d.canPack)acts.append(btn('Prepare a payment pack',()=>newPack()));
  const seg=el('div',null,'x-seg');
  for(const [k,t] of [['all','All'],['Toners','Toners'],['Devices','Devices'],['PartsMaintenance','Parts and maintenance']]){const b=el('button',t);const n=k==='all'?d.invoices.length:d.invoices.filter(i=>i.category===k).length;b.append(el('span',String(n),'x-segc'));b.type='button';b.setAttribute('aria-pressed',String(k===cat));b.onclick=()=>{cat=k;load();};seg.append(b);}
  const bar=el('div',null,'x-filterbar');bar.append(seg);
  const list=d.invoices.filter(i=>cat==='all'||i.category===cat);
  $('view').replaceChildren(
    card('Invoices',null,bar,table(['Invoice','Date','Supplier','Category','Total incl. VAT','Order / PO','Accounting'],list,
      i=>[el('span',i.no,'mono'),date(i.date),i.supplier,i.categoryLabel,money(i.total),i.order||i.po||'',accounting(i)],
      null,{empty:'No invoices yet.',onRow:i=>show(i.id)})),
    card('Payment packs','Picked invoices of one supplier per pack (also from the Suppliers page): the Inter-Office Memo cover, then each invoice followed by the ink request documents it pays for, with their approvals. Record when it went to Accounting and when the supplier was paid.',
      table(['Pack','Supplier','Category','Invoices','Total','Prepared','Sent to Accounting','Paid'],d.packs,
        p=>[el('span',p.no,'mono'),p.supplier,p.category,String(p.invoices),money(p.total),`${when(p.at)} by ${p.by||''}`,p.sentOn?date(p.sentOn):'',p.paidOn?`${date(p.paidOn)}${p.paidNote?' · '+p.paidNote:''}`:''],
        p=>[['Print',()=>open(`/api/packs/${p.id}/pack.pdf`)],...(d.canPack?[['Record dates',()=>dates(p)]]:[])],{empty:'No payment packs yet.'})));
  if(note)message(note);
}

async function show(id){
  const x=await api('/invoices/'+id);const i=x.invoice;
  const box=el('div');
  box.append(table(['Line','Description','Quantity','Unit cost','Line total',...(i.category==='Toners'?['Delivered','Left']:[])],x.lines,
    l=>[String(l.no),l.description,String(l.quantity),money(l.unitCost),money(l.lineTotal),...(i.category==='Toners'?[String(l.delivered),String(l.remaining)]:[])]));
  box.append(el('p',`Subtotal ${money(i.net)} · VAT 15% ${money(i.vat)} · Total ${money(i.total)}`,'hint x-gap'));
  if(x.requests.length)box.append(el('h3',`Order ${i.orderNo}`),table(['Request','Employee','Printer','Toner','Qty','State','Approved by','Delivered',''],x.requests,r=>[r.no,r.employee,r.printer,r.toner,String(r.quantity),r.statusLabel,r.decidedBy||'',r.deliveredOn?date(r.deliveredOn):'',(()=>{const a=el('a','Request PDF');a.href=`/api/ink/${r.id}/request.pdf`;a.target='_blank';return a;})()]));
  if(x.receipts.length)box.append(el('h3',`PO ${i.poNumber}: receipts`),table(['Receipt','Received','At','Devices','On this invoice'],x.receipts,r=>[String(r.id),when(r.at),r.location,String(r.devices),r.onThis?'Yes':r.onOther?'Another invoice':'No']));
  const acts=el('div',null,'actions');acts.append(btn('Open the invoice PDF',()=>open(`/api/invoices/${id}/file`),'primary','doc'),btn('Close',()=>load()));
  if(i.poId){const a=el('a');a.href='/po.html?id='+i.poId;a.append(el('button',`Open PO ${i.poNumber}`));acts.append(a);}
  $('view').replaceChildren(card(`Invoice ${i.no}`,`${i.supplier}${i.supplierVat?' (VAT '+i.supplierVat+')':''} · ${date(i.date)} · ${i.categoryLabel} · recorded ${when(i.at)} by ${i.by||''}`,(()=>{const p=el('p');p.append(el('b','Accounting: '),accounting(i));return p;})(),box,acts));
  window.scrollTo(0,0);
}

// Record an invoice: header, PDF, lines; toners link to their order, devices to the PO receipts they cover.
function newInvoice(){
  const H={};H.category=sel(d.categories.map(c=>[c.code,c.label]),'Choose');H.supplier=sel(m.lists.suppliers.filter(s=>s.active).map(s=>[s.id,s.name]),'Choose');
  H.no=inp('text',60);H.date=inp('date');H.date.value=new Date(Date.now()+3*3600e3).toISOString().slice(0,10);H.file=inp('file');H.file.accept='application/pdf';H.note=inp('text',500);
  H.order=sel([], 'None');H.po=sel([],'Choose');const receiptsBox=el('div',null,'wide');const picked=new Set();
  const orderF=field('Supplier order',H.order,{hint:'The order these toners were delivered for.'}),poF=field('PO',H.po,{req:true}),recF=field('Receipts on this invoice',receiptsBox,{wide:true,hint:'Tick the deliveries of the PO this invoice covers.'});
  const lines=[];const linesBody=el('tbody');const totals=el('p',null,'hint');
  const sum=()=>{const net=lines.reduce((s,l)=>s+(Number(l.q.value)||0)*(Number(l.u.value)||0),0);totals.textContent=`Subtotal ${money(net)} · VAT 15% ${money(net*0.15)} · Total ${money(net*1.15)}`;};
  const addLine=()=>{const l={d:inp('text',200),q:inp('number'),u:inp('number')};l.q.value='1';l.u.step='0.01';l.d.placeholder=H.category.value==='Toners'?'Toner code, for example HP 207A W2210A Black':'Description';
    for(const x of [l.q,l.u])x.oninput=sum;const tr=el('tr');for(const x of [l.d,l.q,l.u]){const td=el('td');td.append(x);tr.append(td);}
    const rm=btn('Remove',()=>{lines.splice(lines.indexOf(l),1);tr.remove();sum();},'link');const td=el('td',null,'act');td.append(rm);tr.append(td);lines.push(l);linesBody.append(tr);sum();};
  const refresh=async()=>{const c=H.category.value,s=Number(H.supplier.value);
    orderF.hidden=c!=='Toners';poF.hidden=c!=='Devices';recF.hidden=c!=='Devices';
    H.order.replaceChildren(option('','None'),...d.orders.filter(o=>o.supplierId===s).map(o=>option(o.id,o.no)));
    H.po.replaceChildren(option('','Choose'),...d.pos.filter(p=>p.supplierId===s).map(p=>option(p.id,p.no)));receiptsBox.replaceChildren();picked.clear();};
  H.category.onchange=refresh;H.supplier.onchange=refresh;
  H.po.onchange=async()=>{receiptsBox.replaceChildren();picked.clear();if(!H.po.value)return;const p=await api('/pos/'+H.po.value);
    for(const r of p.receipts){const l=el('label',null,'inline');const cb=document.createElement('input');cb.type='checkbox';cb.onchange=()=>cb.checked?picked.add(r.id):picked.delete(r.id);l.append(cb,`Receipt ${when(r.at)} · ${r.location} · ${r.devices} devices${r.invoiceRef?' · ref '+r.invoiceRef:''}`);receiptsBox.append(l);}
    if(!p.receipts.length)receiptsBox.append(el('p','No receipts on this PO yet.','hint'));};
  const grid=el('div',null,'x-sec-body');
  grid.append(field('Category',H.category,{req:true}),field('Supplier',H.supplier,{req:true}),field('Invoice number',H.no,{req:true,hint:'As printed on the invoice.'}),field('Invoice date',H.date,{req:true}),
    field('Invoice (PDF)',H.file,{req:true,wide:true,hint:'The supplier\'s invoice. Reading it automatically comes next; for now type the lines below.'}),orderF,poF,recF,field('Note',H.note,{wide:true}));
  const t=el('table');const hr=el('tr');for(const h of ['Description','Quantity','Unit cost (SAR, before VAT)',''])hr.append(el('th',h));const th=el('thead');th.append(hr);t.append(th,linesBody);
  const tw=el('div',null,'table-wrap');tw.append(t);
  const save=btn('Save the invoice',async()=>{try{
    const data={category:H.category.value||null,supplierId:Number(H.supplier.value)||null,invoiceNo:H.no.value.trim()||null,invoiceDate:H.date.value||null,note:H.note.value.trim()||null,
      orderId:H.category.value==='Toners'?Number(H.order.value)||null:null,poId:H.category.value==='Devices'?Number(H.po.value)||null:null,receiptIds:[...picked],
      lines:lines.map(l=>({description:l.d.value.trim()||null,quantity:Number(l.q.value)||null,unitCost:l.u.value===''?null:Number(l.u.value)}))};
    const r=await api('/invoices',{method:'POST',body:form(data,H.file.files[0])});await load(`Invoice ${r.invoiceNo} recorded.`);}catch(e){fail(e);}},'primary','save');
  const linesCard=el('div');linesCard.append(el('h3','Invoice lines'),tw,(()=>{const a=el('div',null,'actions');a.append(btn('Add a line',addLine),totals);return a;})());
  $('view').replaceChildren(card('Record an invoice','Check the totals against the paper invoice before saving.',grid,linesCard,(()=>{const a=el('div',null,'actions');a.append(save,btn('Cancel',()=>load()));return a;})()));
  refresh();addLine();window.scrollTo(0,0);
}

// Payment pack: choose the supplier, then tick its invoices not yet in a pack (any category you handle; also from the Suppliers page).
function newPack(){
  const sup=sel([...new Map(d.invoices.filter(i=>!i.packId).map(i=>[i.supplierId,i.supplier])).entries()],'Choose');const mine=new Set(d.categories.map(k=>k.code));
  const box=el('div',null,'wide');const picked=new Set();
  const refresh=()=>{box.replaceChildren();picked.clear();const list=d.invoices.filter(i=>!i.packId&&String(i.supplierId)===sup.value&&mine.has(i.category));
    for(const i of list){const l=el('label',null,'inline');const cb=document.createElement('input');cb.type='checkbox';cb.checked=true;picked.add(i.id);cb.onchange=()=>cb.checked?picked.add(i.id):picked.delete(i.id);
      l.append(cb,`${i.no} · ${date(i.date)} · ${i.categoryLabel} · ${money(i.total)}`);box.append(l);}
    if(!list.length)box.append(el('p',sup.value?'No invoices of this supplier waiting for a pack.':'Choose the supplier.','hint'));};
  sup.onchange=refresh;
  const grid=el('div',null,'x-sec-body');grid.append(field('Supplier',sup,{req:true}),field('Invoices',box,{req:true,wide:true}));
  const save=btn('Prepare and print the pack',async()=>{try{const r=await api('/packs',{method:'POST',json:{supplierId:Number(sup.value)||null,invoiceIds:[...picked]}});
    open(`/api/packs/${r.id}/pack.pdf`);await load(`Payment pack ${r.packNo} prepared. Sign the cover, then take it to the Head of Department and Accounting.`);}catch(e){fail(e);}},'primary','doc');
  $('view').replaceChildren(card('Prepare a payment pack','The cover lists Date | Invoice No. | Amount with the total; it is signed by IT ("Checked by"), the Head of Department and the Accountant.',grid,(()=>{const a=el('div',null,'actions');a.append(save,btn('Cancel',()=>load()));return a;})()));
  refresh();window.scrollTo(0,0);
}

function dates(p){
  edit(`Payment pack ${p.no}`,[{key:'sent',label:'Sent to Accounting on',type:'date',value:p.sentOn},{key:'paid',label:'Supplier paid on',type:'date',value:p.paidOn,hint:'When Accounting confirms the payment.'},
    {key:'note',label:'Payment note',wide:true,max:200,value:p.paidNote,hint:'For example the payment reference.'}],
    x=>api(`/packs/${p.id}/dates`,{method:'POST',json:{sentOn:x.sent,paidOn:x.paid,paidNote:x.note}}),{reason:false,done:()=>load('Saved.')});
}

try{m=await shell('invoices');if(!m.can.invoices)throw Error('You cannot see invoices.');await load();const inv=Number(new URLSearchParams(location.search).get('id'));if(inv)await show(inv);}catch(e){fail(e);}
