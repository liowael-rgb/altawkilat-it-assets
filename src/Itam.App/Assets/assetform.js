// The asset form (form 01 Asset Registration and form 02 Asset Details Edit): the fields shown depend on the product type.
import {el,option} from '/itam.js';

const kindLabel=k=>k==='ITWarehouse'?'IT warehouse':'Branch';
function field(label,input,{required,wide,hint}={}){const l=el('label',label+(required?' *':''));if(wide)l.className='wide';l.append(input);if(hint)l.append(el('span',hint,'hint'));return l;}
function select(list,value,blank){const s=document.createElement('select');if(blank!==undefined)s.append(option('',blank));for(const [v,t] of list)s.append(option(v,t));if(value!==undefined&&value!==null)s.value=String(value);return s;}
function text(value,{type='text',max,placeholder}={}){const i=document.createElement(type==='textarea'?'textarea':'input');if(type!=='textarea')i.type=type;if(max)i.maxLength=max;if(placeholder)i.placeholder=placeholder;if(value!==undefined&&value!==null)i.value=String(value);i.autocomplete='off';return i;}

/** Builds the form in `box`; returns read() that gives the input for the API. */
export function assetForm(box,m,{asset,attributes={},keys=[],phoneOnly=false}={}){
  const L=m.lists;const isNew=!asset;
  const onlySpecialist=m.roles.length>0&&m.roles.every(r=>r.code==='SupportSpecialist');
  const locs=L.locations.filter(l=>(l.active||l.id===asset?.locationId)&&(!onlySpecialist||m.regions.includes(l.region)));
  const i={};
  i.type=select(L.types.filter(t=>t.active).map(t=>[t.code,`${t.name} (${t.code})`]),asset?.type??'',isNew?'Choose':undefined);i.type.disabled=!isNew;
  i.company=select(L.companies.filter(c=>c.active||c.code===asset?.company).map(c=>[c.code,c.name]),asset?.company??(L.companies.length===1?L.companies[0].code:''),'Choose');
  i.location=select(locs.map(l=>[l.id,`${l.region} · ${l.name} (${kindLabel(l.kind)})`]),asset?.locationId??'','Choose');
  if(asset&&asset.status!=='InStore')i.location.disabled=true;
  i.manufacturer=select(L.manufacturers.filter(x=>x.active||x.id===asset?.manufacturerId).map(x=>[x.id,x.name]),asset?.manufacturerId??'','Choose');
  i.model=document.createElement('select');i.newModel=text('',{max:150,placeholder:'Type the new model'});
  i.serial=text(asset?.serial,{max:100});i.tag=text(asset?.serviceTag,{max:100});i.mac=text(asset?.mac,{max:17,placeholder:'AA:BB:CC:DD:EE:FF'});i.ip=text(asset?.ip,{max:15,placeholder:'10.1.2.3'});
  i.condition=select([['New','New'],['Used','Used']],asset?.condition??'New');
  i.purchase=text(asset?.purchaseDate,{type:'date'});i.warranty=text(asset?.warrantyEnd,{type:'date'});
  i.supplier=select(L.suppliers.filter(x=>x.active||x.id===asset?.supplierId).map(x=>[x.id,x.name]),asset?.supplierId??'','None');
  i.po=text(asset?.poNumber,{max:40});i.cost=text(asset?.unitCost,{type:'number'});i.cost.step='0.01';i.cost.min='0';i.invoice=text(asset?.invoiceNo,{max:60});
  i.key=text('',{max:200,placeholder:keys.length?'Saved (encrypted) — leave empty to keep':''});
  i.notes=text(asset?.notes,{type:'textarea'});
  const attrInputs={};const typeBox=el('div',null,'grid two wide');
  if(asset?.poId){for(const x of [i.supplier,i.po,i.cost,i.invoice,i.purchase])x.disabled=true;}
  function models(){const t=i.type.value,mf=Number(i.manufacturer.value);const keep=i.model.value||asset?.modelId;
    i.model.replaceChildren(option('','Choose'),...L.models.filter(x=>x.type===t&&x.manufacturer===mf&&(x.active||x.id===asset?.modelId)).map(x=>option(x.id,x.name)),option('new','+ A model not in the list'));
    if(keep&&[...i.model.options].some(o=>o.value===String(keep)))i.model.value=String(keep);i.newModel.hidden=i.model.value!=='new';}
  function render(){
    const t=L.types.find(x=>x.code===i.type.value);box.replaceChildren();
    box.append(field('Product type',i.type,{required:true,hint:isNew?'Decides the fields and the Asset No. code.':'The type never changes (it is part of the Asset No.).'}),
      field('Owning company',i.company,{required:true}),field('Kept at',i.location,{required:true,hint:asset&&asset.status!=='InStore'?'Changes with deliveries and stock moves.':'The IT warehouse or branch where it is kept.'}),
      field('Manufacturer',i.manufacturer,{required:true}),(()=>{const l=field('Model',i.model,{required:true});l.append(i.newModel);return l;})());
    if(!t)return;
    box.append(field('Serial number',i.serial,{required:t.serialRequired}));
    if(t.serviceTag)box.append(field('Service tag',i.tag,{required:true}));
    if(t.mac)box.append(field('MAC address',i.mac,{required:t.macRequired}));
    if(t.ip)box.append(field('IP address',i.ip));
    box.append(field('Device condition',i.condition,{required:true}));
    typeBox.replaceChildren();
    for(const f of t.fields){
      let input=attrInputs[f.code];
      if(!input){input=f.kind==='list'?select(L.values.filter(v=>v.list===f.list&&(v.active||v.code===attributes[f.code])).map(v=>[v.code,v.name]),attributes[f.code]??'','Choose')
        :text(attributes[f.code],{type:f.kind==='number'?'number':'text',max:200});attrInputs[f.code]=input;}
      typeBox.append(field(f.label,input,{required:f.required,hint:f.code==='ComputerName'?'Required at delivery; can be filled later.':undefined}));
    }
    if(t.fields.length)box.append(typeBox);
    if(t.key)box.append(field(t.code==='PC'?'OS key':'Software key',i.key,{hint:'Encrypted; shown only to the IT Administrator and the IT Assets Officer, and every view is logged.'}));
    box.append(field('Purchase date',i.purchase),field('Warranty end',i.warranty),field('Supplier',i.supplier),
      field('PO number',i.po,{hint:asset?.poId?'Received on the PO; purchase details come from it.':'Reference only. A PO recorded in IT Assets: receive the device on the PO instead.'}),
      field('Unit cost (SAR, before VAT)',i.cost),field('Invoice number',i.invoice),field('Notes',i.notes,{wide:true}));
    if(phoneOnly){for(const x of box.querySelectorAll('input,select,textarea'))x.disabled=true;for(const x of [i.mac,i.ip,attrInputs.Extension])if(x)x.disabled=false;
      box.prepend(el('p','Voice Administrator: you can change the MAC address, IP address and extension of IP phones.','hint wide'));}
  }
  i.type.onchange=()=>{models();render();};i.manufacturer.onchange=models;i.model.onchange=()=>{i.newModel.hidden=i.model.value!=='new';if(!i.newModel.hidden)i.newModel.focus();};
  models();render();
  const v=x=>x.value.trim()===''?null:x.value.trim();
  return ()=>{const t=i.type.value;const attrs={};for(const [k,x] of Object.entries(attrInputs))attrs[k]=x.value.trim()===''?null:x.value.trim();
    return {type:t,company:i.company.value||null,locationId:Number(i.location.value)||null,manufacturerId:Number(i.manufacturer.value)||null,
      modelId:i.model.value&&i.model.value!=='new'?Number(i.model.value):null,newModel:i.model.value==='new'?v(i.newModel):null,
      serialNo:v(i.serial),serviceTag:v(i.tag),macAddress:v(i.mac),ipAddress:v(i.ip),condition:i.condition.value,purchaseDate:v(i.purchase),warrantyEnd:v(i.warranty),
      supplierId:Number(i.supplier.value)||null,poNumber:v(i.po),unitCost:i.cost.value===''?null:Number(i.cost.value),invoiceNo:v(i.invoice),notes:v(i.notes),attributes:attrs,
      osKey:t==='PC'?v(i.key):null,softwareKey:t==='SOF'?v(i.key):null};};
}
