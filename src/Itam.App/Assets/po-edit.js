// Record or change a PO (form 12): header, the PO document from Procurement, and one line per product.
import {$,el,api,shell,fail,option,money,form} from '/itam.js';
const id=Number(new URLSearchParams(location.search).get('id'))||null;
let m,cur=null;const lines=[];
function sel(list,value,blank){const s=document.createElement('select');if(blank!==undefined)s.append(option('',blank));for(const [v,t] of list)s.append(option(v,t));if(value!==undefined&&value!==null)s.value=String(value);return s;}
function inp(value,type='text',max){const i=document.createElement('input');i.type=type;if(max)i.maxLength=max;if(value!==undefined&&value!==null)i.value=String(value);return i;}
const H={};
function head(){
  const L=m.lists;const p=cur?.po;
  H.number=inp(p?.number,'text',40);H.date=inp(p?.date,'date');H.supplier=sel(L.suppliers.filter(s=>s.active||s.id===p?.supplierId).map(s=>[s.id,s.name]),p?.supplierId??'','Choose');
  H.company=sel(L.companies.filter(c=>c.active||c.code===p?.company).map(c=>[c.code,c.name]),p?.company??(L.companies.length===1?L.companies[0].code:''),'Choose');
  H.raised=inp(p?.raisedBy??'Procurement Department','text',200);H.request=inp(p?.requestRef,'text',20);H.request.placeholder='PR-12 (if it came from an IT request)';
  H.invoice=inp(p?.invoiceNo,'text',60);H.note=document.createElement('textarea');H.note.value=p?.note||'';H.file=inp(null,'file');H.file.accept='application/pdf';
  const f=(label,input,req,hint,wide)=>{const l=el('label',label+(req?' *':''));if(wide)l.className='wide';l.append(input);if(hint)l.append(el('span',hint,'hint'));return l;};
  $('head').replaceChildren(f('PO number',H.number,true,'As written on the PO document.'),f('PO date',H.date,true),f('Supplier',H.supplier,true),f('Company',H.company,true),
    f('Raised by',H.raised,true),f('Request number',H.request,false),f('Invoice number',H.invoice,false,'Can be added when the invoice arrives.'),
    f(cur?'Replace the PO document (PDF)':'PO document (PDF)',H.file,!cur,cur?'Leave empty to keep the current document.':'The PDF Procurement sent by email.'),f('Note',H.note,false,null,true));
}
function addLine(l){
  const L=m.lists;const row={received:l?.received||0,no:l?.no};
  row.type=sel(L.types.filter(t=>t.active||t.code===l?.type).map(t=>[t.code,t.name]),l?.type??'','Choose');row.type.disabled=row.received>0;
  row.mf=sel(L.manufacturers.filter(x=>x.active||x.id===l?.manufacturerId).map(x=>[x.id,x.name]),l?.manufacturerId??'','Choose');
  row.model=document.createElement('select');row.newModel=inp('','text',150);row.newModel.placeholder='New model';
  row.spec=inp(l?.specification,'text',500);row.qty=inp(l?.quantity??1,'number');row.qty.min=String(Math.max(1,row.received));row.cost=inp(l?.unitCost??'','number');row.cost.step='0.01';row.cost.min='0';
  const models=()=>{const keep=row.model.value||l?.modelId;row.model.replaceChildren(option('','Choose'),...L.models.filter(x=>x.type===row.type.value&&x.manufacturer===Number(row.mf.value)&&(x.active||x.id===l?.modelId)).map(x=>option(x.id,x.name)),option('new','+ A model not in the list'));
    if(keep&&[...row.model.options].some(o=>o.value===String(keep)))row.model.value=String(keep);row.newModel.hidden=row.model.value!=='new';};
  row.type.onchange=models;row.mf.onchange=models;row.model.onchange=()=>{row.newModel.hidden=row.model.value!=='new';};models();
  for(const x of [row.qty,row.cost])x.oninput=totals;
  row.tr=el('tr');const td=x=>{const t=el('td');t.append(...[].concat(x));return t;};
  const rm=el('button','Remove','link');rm.type='button';rm.disabled=row.received>0;rm.title=row.received>0?`${row.received} devices received on this line`:'';
  rm.onclick=()=>{lines.splice(lines.indexOf(row),1);row.tr.remove();number();totals();};
  row.noCell=el('td');row.tr.append(row.noCell,td(row.type),td(row.mf),td([row.model,row.newModel]),td(row.spec),td(row.qty),td(row.cost),td(rm));
  lines.push(row);$('lines').append(row.tr);number();totals();
}
function number(){let n=0;for(const r of lines){n=Math.max(n,r.no||0);}for(const r of lines){if(!r.no)r.no=++n;r.noCell.textContent=String(r.no)+(r.received?` (${r.received} received)`:'');}}
function totals(){const net=lines.reduce((s,r)=>s+(Number(r.qty.value)||0)*(Number(r.cost.value)||0),0);$('totals').textContent=`Subtotal ${money(net)} · VAT 15% ${money(net*0.15)} · Total ${money(net*1.15)}`;}
try{
  m=await shell('pos');if(!m.can.poManage)throw Error('Only the IT Helpdesk Team Leader and the IT Assets Officer record purchase orders.');
  if(id){cur=await api('/pos/'+id);$('title').textContent=`Change PO ${cur.po.number}`;$('subtitle').textContent='Lines with received devices keep at least that quantity and their product type.';$('po-save').textContent='Save changes';}
  head();for(const l of cur?.lines||[])addLine(l);if(!lines.length)addLine();
  $('add-line').onclick=()=>addLine();
  $('po-form').onsubmit=async e=>{e.preventDefault();
    const v=x=>x.value.trim()===''?null:x.value.trim();
    const data={poNumber:v(H.number),poDate:v(H.date),supplierId:Number(H.supplier.value)||null,company:H.company.value||null,raisedBy:v(H.raised),requestRef:v(H.request),invoiceNo:v(H.invoice),note:v(H.note),
      lines:lines.map(r=>({lineNo:r.no,type:r.type.value||null,manufacturerId:Number(r.mf.value)||null,modelId:r.model.value&&r.model.value!=='new'?Number(r.model.value):null,newModel:r.model.value==='new'?v(r.newModel):null,
        specification:v(r.spec),quantity:Number(r.qty.value)||null,unitCost:r.cost.value===''?null:Number(r.cost.value)})),version:cur?.po.version,reason:$('po-reason').value};
    try{const body=form(data,H.file.files[0]);
      if(cur){await api('/pos/'+id,{method:'PUT',body});location.assign('/po.html?id='+id+'&saved=1');}
      else{const r=await api('/pos',{method:'POST',body});location.assign('/po.html?id='+r.id+'&saved=1');}}catch(err){fail(err);}};
}catch(e){fail(e);}
