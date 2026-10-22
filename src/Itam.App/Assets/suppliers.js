// Suppliers page (owner, 22 October 2026): every supplier with contact details and what is waiting for a payment pack; one
// supplier's invoices, payment packs, toner orders and POs. The Ink Officer, IT Assets Officer and IT Administrator add and
// change suppliers. Tick a supplier's invoices to put them in one payment pack for Accounting.
import {$,el,api,shell,fail,message,date,when,money,card,table,edit,svg,call,accounting} from '/itam.js';
const id=Number(new URLSearchParams(location.search).get('id'))||null;
let d,q='',show='active';
const open=href=>window.open(href,'_blank');
const btn=(text,fn,cls,icon)=>{const b=el('button',null,cls||'');b.type='button';if(icon)b.append(svg(icon));b.append(text);b.onclick=fn;return b;};
const mail=e=>{if(!e)return '';const a=el('a',e);a.href='mailto:'+e;return a;};

const FIELDS=s=>[
  {key:'name',label:'Supplier name',required:true,max:200,wide:true,value:s?.name,hint:'As on the supplier\'s tax invoice.'},
  {key:'vat',label:'VAT number',max:20,value:s?.vat,hint:'15 digits, on the tax invoice.'},
  {key:'cr',label:'CR number',max:20,value:s?.cr,hint:'Commercial registration.'},
  {key:'contact',label:'Contact person',max:200,value:s?.contact},
  {key:'phone',label:'Phone',max:30,value:s?.phone,hint:'For example 0551234567.'},
  {key:'email',label:'Email',max:200,value:s?.email,hint:'Where the toner orders are sent.'},
  {key:'address',label:'Address',max:300,value:s?.address},
  {key:'notes',label:'Notes',type:'textarea',wide:true,max:500,value:s?.notes},
  ...(s?[{key:'active',label:'Active (can be chosen on new invoices, orders and POs)',type:'checkbox',value:s.active}]:[])];
function save(s){
  edit(s?`Change ${s.name}`:'Add a supplier',FIELDS(s),
    async x=>{const r=await api(s?`/suppliers/${s.id}`:'/suppliers',{method:'POST',json:{name:x.name,vatNo:x.vat,crNo:x.cr,contactName:x.contact,phone:x.phone,email:x.email,address:x.address,notes:x.notes,active:s?!!x.active:true}});
      if(s)await detail('Saved.');else location.assign('/suppliers.html?id='+r.id+'&added=1');},{reason:false,button:s?'Save':'Add the supplier'});
}

async function list(note){
  d=await api('/suppliers');
  $('head-actions').replaceChildren(...(d.canManage?[btn('Add a supplier',()=>save(null),'primary','plus')]:[]));
  const bar=el('div',null,'x-filterbar');const seg=el('div',null,'x-seg');
  for(const [k,t] of [['active','Active'],['waiting','Invoices waiting for a pack'],['all','All']]){
    const n=k==='all'?d.suppliers.length:k==='active'?d.suppliers.filter(s=>s.active).length:d.suppliers.filter(s=>s.waiting).length;
    const b=el('button',t);b.append(el('span',String(n),'x-segc'+(k==='waiting'&&n?' hot':'')));b.type='button';b.setAttribute('aria-pressed',String(k===show));b.onclick=()=>{show=k;list();};seg.append(b);}
  const search=document.createElement('input');search.type='search';search.placeholder='Supplier, VAT number or contact';search.value=q;search.className='x-filter-search';
  search.oninput=()=>{q=search.value;draw();};bar.append(seg,search);
  const box=el('div');
  const draw=()=>{const t=q.trim().toLowerCase();
    const rows=d.suppliers.filter(s=>(show==='all'||(show==='active'?s.active:s.waiting>0))&&(!t||[s.name,s.vat,s.contact].some(v=>(v||'').toLowerCase().includes(t))));
    box.replaceChildren(table(['Supplier','VAT number','Contact','Phone','Invoices','Waiting for a pack','Unpaid packs'],rows,
      s=>[(()=>{const x=el('span');x.append(el('strong',s.name));if(!s.active)x.append(' ',el('span','Inactive','tag off'));return x;})(),s.vat||'',s.contact||'',call(s.phone),String(s.invoices),
        s.waiting?el('span',`${s.waiting} · ${money(s.waitingTotal)}`,'tag warn'):'',s.unpaidPacks?String(s.unpaidPacks):''],null,
      {empty:'No supplier found.',onRow:s=>location.assign('/suppliers.html?id='+s.id)}));};
  draw();
  $('view').replaceChildren(card('Suppliers',null,bar,box));
  if(note)message(note);
}

async function detail(note){
  d=await api('/suppliers/'+id);const s=d.supplier;
  document.title='IT Assets · '+s.name;$('title').textContent=s.name;
  $('subtitle').textContent=`Supplier${s.active?'':' (inactive)'} · added ${when(s.createdAt)}${s.createdBy?' by '+s.createdBy:''}`;
  const back=btn('All suppliers',()=>location.assign('/suppliers.html'));
  $('head-actions').replaceChildren(...(d.canManage?[btn('Change',()=>save(s),'','edit')]:[]),back);
  const info=el('dl',null,'kv');
  for(const [k,v] of [['VAT number',s.vat],['CR number',s.cr],['Contact person',s.contact],['Phone',call(s.phone)],['Email',mail(s.email)],['Address',s.address],['Notes',s.notes]]){
    if(!v)continue;info.append(el('dt',k));const dd=el('dd');dd.append(v);info.append(dd);}
  const parts=[card('Details',null,info)];
  if(d.canPack){
    // Invoices: tick the ones waiting for a pack, then put them in one payment pack.
    const picked=new Set();const packBtn=btn('Put the ticked invoices in a payment pack',()=>pack([...picked]),'primary','doc');packBtn.disabled=true;
    const sum=el('span','','hint');
    const refresh=()=>{packBtn.disabled=!picked.size;const t=d.invoices.filter(i=>picked.has(i.id)).reduce((a,i)=>a+i.total,0);sum.textContent=picked.size?`${picked.size} ticked · ${money(t)}`:'';};
    const waiting=d.invoices.filter(i=>!i.packId&&i.canPack);
    const all=document.createElement('input');all.type='checkbox';all.className='x-pick';all.title='Tick all waiting invoices';all.disabled=!waiting.length;
    const boxes=new Map();
    all.onchange=()=>{for(const [i,cb] of boxes){cb.checked=all.checked;all.checked?picked.add(i):picked.delete(i);}refresh();};
    const wrap=el('div',null,'table-wrap');const t=el('table');const hr=el('tr');const th0=el('th');th0.append(all);hr.append(th0);
    for(const h of ['Invoice','Date','Category','Total incl. VAT','Order / PO','Accounting'])hr.append(el('th',h));
    const th=el('thead');th.append(hr);const tb=el('tbody');
    for(const i of d.invoices){const tr=el('tr');const c0=el('td');
      if(!i.packId&&i.canPack){const cb=document.createElement('input');cb.type='checkbox';cb.className='x-pick';cb.onchange=()=>{cb.checked?picked.add(i.id):picked.delete(i.id);refresh();};boxes.set(i.id,cb);c0.append(cb);}
      tr.append(c0);for(const v of [el('span',i.no,'mono'),date(i.date),i.categoryLabel,money(i.total),i.order||i.po||'',accounting(i)]){const c=el('td');c.append(v);tr.append(c);}
      tr.onclick=e=>{if(e.target.tagName!=='INPUT')location.assign('/invoices.html?id='+i.id);};tr.className='click';tb.append(tr);}
    if(!d.invoices.length){const tr=el('tr');const c=el('td','No invoices from this supplier yet.','empty');c.colSpan=7;tr.append(c);tb.append(tr);}
    t.append(th,tb);wrap.append(t);
    const bar=el('div',null,'x-cmd');bar.append(packBtn,sum);
    parts.push(card('Invoices',waiting.length?`${waiting.length} waiting for a payment pack. Tick the ones to send together; one pack holds invoices of this supplier only (toners, devices, or parts and maintenance together).`:'Every invoice of this supplier is in a payment pack.',...(waiting.length?[bar]:[]),wrap));
    parts.push(card('Payment packs','Print the pack, sign the cover and take it to Accounting; then record the date it was sent, and the date the supplier was paid.',
      table(['Pack','Category','Invoices','Total','Prepared','Accounting'],d.packs,p=>[el('span',p.no,'mono'),p.category,String(p.invoices),money(p.total),when(p.at),accounting({...p,pack:p.no})],
        p=>[['Print',()=>open(`/api/packs/${p.id}/pack.pdf`)],['Sent / paid dates',()=>dates(p)]],{empty:'No payment packs yet.'})));
  }
  if(d.orders.length)parts.push(card('Toner orders',null,table(['Order','Region','Requests','Delivered','Created'],d.orders,o=>[o.no,o.region,String(o.requests),`${o.delivered} of ${o.requests}`,when(o.at)],
    o=>[['Order PDF',()=>open(`/api/orders/${o.id}/order.pdf`)]])));
  if(d.pos.length)parts.push(card('Purchase orders',null,table(['PO number','PO date'],d.pos,p=>[p.no,date(p.date)],null,{onRow:p=>location.assign('/po.html?id='+p.id)})));
  $('view').replaceChildren(...parts);
  if(note)message(note);
}

async function pack(ids){
  const list=d.invoices.filter(i=>ids.includes(i.id));
  edit(`Payment pack for ${d.supplier.name}`,[],async()=>{const r=await api('/packs',{method:'POST',json:{supplierId:d.supplier.id,invoiceIds:ids}});
      open(`/api/packs/${r.id}/pack.pdf`);await detail(`Payment pack ${r.packNo} prepared. Sign the cover, then take it to the Head of Department and Accounting.`);},
    {reason:false,button:'Prepare and print the pack',hint:`${list.length} invoice${list.length>1?'s':''}: ${list.map(i=>i.no).join(', ')} · total ${money(list.reduce((a,i)=>a+i.total,0))}. The cover lists each invoice with the total; it is signed by IT, the Head of Department and the Accountant.`});
}

function dates(p){
  edit(`Payment pack ${p.no}`,[{key:'sent',label:'Sent to Accounting on',type:'date',value:p.sentOn},{key:'paid',label:'Supplier paid on',type:'date',value:p.paidOn,hint:'When Accounting confirms the payment.'},
    {key:'note',label:'Payment note',wide:true,max:200,hint:'For example the payment reference.'}],
    x=>api(`/packs/${p.id}/dates`,{method:'POST',json:{sentOn:x.sent,paidOn:x.paid,paidNote:x.note}}),{reason:false,done:()=>detail('Saved.')});
}

try{await shell('suppliers');if(id)await detail(new URLSearchParams(location.search).get('added')?'Supplier added.':null);else await list();}catch(e){fail(e);}
