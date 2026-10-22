// One PO: counts, lines, receiving devices (form 13), devices, receipts, and the PO pack for Accounting.
import {$,el,api,shell,fail,message,when,date,money,card,dl,table,buttons,option,form,edit} from '/itam.js';
const id=Number(new URLSearchParams(location.search).get('id'));
let m,d;
const stat=(n,t)=>{const s=el('div',null,'stat');s.append(el('b',String(n)),el('span',t));return s;};
async function load(note){
  d=await api('/pos/'+id);const p=d.po,c=d.counts;
  $('title').textContent=`PO ${p.number}`;$('subtitle').textContent=`${p.supplier} · ${date(p.date)} · ${p.companyName}`;
  const actions=$('po-actions');actions.replaceChildren();
  const btn=(t,fn,cls)=>{const b=el('button',t,cls||'');b.type='button';b.onclick=fn;actions.append(b);};
  btn('Print PO pack',()=>{window.open(`/api/pos/${id}/pack.pdf`,'_blank');setTimeout(()=>load('The PO pack was printed; record the date it goes to Accounting below.'),1500);},'primary');
  btn('PO Delivery Report',()=>window.open(`/api/pos/${id}/report.pdf`,'_blank'));
  if(d.canManage)btn('Change PO',()=>location.assign('/po-edit.html?id='+id));
  const back=el('a');back.href='/pos.html';const bb=el('button','All POs');bb.type='button';back.append(bb);actions.append(back);
  const doc=el('a','Open the PO document');doc.href='/api/files/'+p.documentFile;doc.target='_blank';
  const counts=el('div',null,'stat-row');counts.append(stat(c.ordered,'ordered'),stat(c.received,'received by IT'),stat(c.stillToCome,'still to come'),stat(c.inStock,'in IT stock'),
    stat(c.reserved,'reserved'),stat(c.deliveredWaitingSigned,'delivered, waiting for signed copy'),stat(c.deliveredSigned,'delivered and signed'));
  const toCome=d.lines.filter(l=>l.toCome>0);
  $('view').replaceChildren(...[counts,
    card('PO',null,dl([['PO number',el('strong',p.number)],['PO date',date(p.date)],['Supplier',p.supplier+(p.supplierVat?` (VAT ${p.supplierVat})`:'')],['Company',p.companyName],['Raised by',p.raisedBy],
      ['Request',p.requestRef],['Invoice number',p.invoiceNo],['Note',p.note],['PO document',doc]])),
    card('Lines',`Subtotal ${money(d.totals.net)} · VAT 15% ${money(d.totals.vat)} · Total ${money(d.totals.total)}`,
      table(['Line','Type','Manufacturer and model','Specification','Quantity','Unit cost','Line total','Received','To come'],d.lines,
        l=>[String(l.no),l.typeName,`${l.manufacturer} ${l.model||''}`,l.specification||'',String(l.quantity),money(l.unitCost),money(l.lineTotal),String(l.received),String(l.toCome)])),
    d.canManage&&toCome.length?receiveCard(toCome):null,
    card('Devices received','Each device has its own Asset No.; open one to see its details and history.',
      table(['Asset No.','Line','Model','Serial / service tag','Status','Kept at','Received'],d.devices,x=>[x.assetNo,String(x.line),x.model,x.serial||x.serviceTag||'',x.status,x.keptAt,when(x.receivedAt)],null,
        {empty:'No device received yet.',onRow:x=>location.assign('/asset.html?id='+x.id)})),
    card('Receipts',null,table(['When','By','Received at','Devices','Invoice','Evidence','Note'],d.receipts,r=>[when(r.at),r.by||'',r.location,String(r.devices),r.invoiceRef||'',
      r.evidence?(()=>{const a=el('a','Open');a.href='/api/files/'+r.evidence;a.target='_blank';return a;})():'',r.note||''],null,{empty:'No receipt yet.'})),
    card('PO pack prints','Print the PO pack, then record the date it went to Accounting.',table(['Printed','By','Devices','Sent to Accounting'],d.prints,
      k=>[when(k.at),k.by||'',String(k.devices),k.sentOn?`${date(k.sentOn)} (${k.sentBy||''})`:''],d.canManage?k=>k.sentOn?[]:[['Sent to Accounting',()=>sent(k)]]:null,{empty:'Not printed yet.'}))].filter(Boolean));
  if(note)message(note);
}
function sent(k){edit('Sent to Accounting',[{key:'on',label:'Date sent',type:'date',value:new Date(Date.now()+3*3600e3).toISOString().slice(0,10),required:true}],
  x=>api(`/pos/${id}/prints/${k.id}/sent`,{method:'POST',json:{on:x.on}}),{reason:false,done:()=>load('Recorded.')});}

// Receive devices against one line: one row per device with the fields of its type.
function receiveCard(lines){
  const L=m.lists;const box=el('div');const lineSel=document.createElement('select');for(const l of lines)lineSel.append(option(l.id,`Line ${l.no}: ${l.typeName} ${l.manufacturer} ${l.model||''} — ${l.toCome} to come`));
  const onlySpecialist=m.roles.every(r=>r.code==='SupportSpecialist');
  const locSel=document.createElement('select');locSel.append(option('','Choose'),...L.locations.filter(x=>x.active&&(!onlySpecialist||m.regions.includes(x.region))).map(x=>option(x.id,`${x.region} · ${x.name}`)));
  const at=document.createElement('input');at.type='datetime-local';const inv=document.createElement('input');inv.maxLength=60;inv.value=d.po.invoiceNo||'';
  const ev=document.createElement('input');ev.type='file';ev.accept='application/pdf,image/png,image/jpeg';const note=document.createElement('input');note.maxLength=500;
  const rowsBox=el('div',null,'table-wrap devices');let rows=[];
  const f=(label,input,req,hint)=>{const l=el('label',label+(req?' *':''));l.append(input);if(hint)l.append(el('span',hint,'hint'));return l;};
  function build(){
    const line=lines.find(l=>String(l.id)===lineSel.value);rows=[];
    const t=el('table');const hr=el('tr');const cols=['#',line.serialRequired?'Serial number *':'Serial number',...(line.serviceTag?['Service tag *']:[]),...(line.mac?[line.macRequired?'MAC address *':'MAC address']:[]),...(line.ip?['IP address']:[]),
      ...line.fields.map(x=>x.label+(x.required?' *':'')),...(line.key?[line.type==='PC'?'OS key':'Software key']:[]),'Condition',''];
    for(const c of cols)hr.append(el('th',c));const th=el('thead');th.append(hr);const tb=el('tbody');t.append(th,tb);rowsBox.replaceChildren(t);
    const add=()=>{if(rows.length>=line.toCome){alert(`Only ${line.toCome} devices are still to come on this line.`);return;}
      const r={};const tr=el('tr');const cell=x=>{const td=el('td');td.append(x);tr.append(td);};
      const input=(ph)=>{const i=document.createElement('input');i.placeholder=ph||'';i.autocomplete='off';return i;};
      cell(el('span',String(rows.length+1)));r.serial=input();cell(r.serial);if(line.serviceTag){r.tag=input();cell(r.tag);}if(line.mac){r.mac=input('AA:BB:CC:DD:EE:FF');cell(r.mac);}if(line.ip){r.ip=input();cell(r.ip);}
      r.attrs={};for(const x of line.fields){let i;if(x.kind==='list'){i=document.createElement('select');i.append(option('',''),...L.values.filter(v=>v.list===x.list&&v.active).map(v=>option(v.code,v.name)));}else{i=input();if(x.kind==='number')i.type='number';}r.attrs[x.code]=i;cell(i);}
      if(line.key){r.key=input();cell(r.key);}
      r.cond=document.createElement('select');r.cond.append(option('New','New'),option('Used','Used'));cell(r.cond);
      const rm=el('button','Remove','link');rm.type='button';rm.onclick=()=>{rows.splice(rows.indexOf(r),1);tr.remove();};cell(rm);
      rows.push(r);tb.append(tr);r.serial.focus();};
    addBtn.onclick=add;add();
  }
  const addBtn=el('button','Add a device');addBtn.type='button';
  lineSel.onchange=build;
  const save=el('button','Receive the devices','primary');save.type='button';
  save.onclick=async()=>{const v=x=>x&&x.value.trim()!==''?x.value.trim():null;
    const data={poLineId:Number(lineSel.value),locationId:Number(locSel.value)||null,receivedAt:at.value?new Date(at.value).toISOString():null,invoiceRef:v(inv),note:v(note),
      devices:rows.map(r=>{const a={};for(const [k,i] of Object.entries(r.attrs))a[k]=v(i);const line=lines.find(l=>String(l.id)===lineSel.value);
        return {serialNo:v(r.serial),serviceTag:v(r.tag),macAddress:v(r.mac),ipAddress:v(r.ip),condition:r.cond.value,attributes:a,osKey:line.type==='PC'?v(r.key):null,softwareKey:line.type==='SOF'?v(r.key):null};})};
    try{const res=await api(`/pos/${id}/receive`,{method:'POST',body:form(data,ev.files[0])});await load(`Received ${res.assets.length}: ${res.assets.join(', ')}.`);}catch(e){fail(e);}};
  const grid=el('div',null,'grid two');grid.append(f('PO line',lineSel,true),f('Received at (IT warehouse or branch)',locSel,true),f('Received on',at,false,'Empty = now.'),f('Invoice reference',inv),f('Delivery evidence',ev,false,'Supplier delivery note (PDF or photo), optional.'),f('Note',note));
  box.append(grid,el('p','One row per device. Each row becomes an asset with its own Asset No., In Store where it is received. More than the quantity still to come is refused, and so are serial numbers already in IT Assets.','hint'),rowsBox,buttons(null),save);
  box.querySelector('.actions').append(addBtn);
  build();
  return card('Receive devices',null,box);
}
try{m=await shell('pos');await load(new URLSearchParams(location.search).get('saved')?'Saved.':null);}catch(e){fail(e);}
