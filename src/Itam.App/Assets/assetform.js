// The asset form (form 01 Asset Registration and form 02 Asset Details Edit) in the approved design: numbered sections,
// the fields of the chosen product type only, a live asset label and a checklist of the required fields.
import {el,option,svg} from '/itam.js';

const kindLabel=k=>k==='ITWarehouse'?'IT warehouse':'Branch';
function select(list,value,blank){const s=document.createElement('select');if(blank!==undefined)s.append(option('',blank));for(const [v,t] of list)s.append(option(v,t));if(value!==undefined&&value!==null)s.value=String(value);return s;}
function text(value,{type='text',max,placeholder,mono}={}){const i=document.createElement(type==='textarea'?'textarea':'input');if(type!=='textarea')i.type=type;if(max)i.maxLength=max;if(placeholder)i.placeholder=placeholder;if(mono)i.className='mono';if(value!==undefined&&value!==null)i.value=String(value);i.autocomplete='off';return i;}
/** One field: label (red star when required, "Optional" otherwise), the input (with a unit when given) and a hint. */
function field(label,input,{required,wide,hint,unit,extra}={}){
  const l=el('label',null,'x-f'+(wide?' wide':''));const t=el('span',label,'x-l');t.append(required?el('span','*','req'):el('span','Optional','opt'));l.append(t);
  if(unit){const b=el('span',null,'x-box');b.append(input,el('span',unit,'x-unit'));l.append(b);}else l.append(input);
  if(extra)l.append(extra);if(hint)l.append(el('span',hint,'x-help'));return l;
}
function section(id,icon,color,title,desc){
  const s=el('section',null,'x-sec'+(color?' '+color:''));s.id=id;const h=el('div',null,'x-sec-head');const ic=el('span',null,'x-sec-ico');ic.append(svg(icon));
  const tag=el('span','Optional','x-tag');h.append(ic,el('h2',title),el('p',desc),tag);const body=el('div',null,'x-sec-body');s.append(h,body);return {s,body,tag,req:[]};
}

/** The asset label card: Asset No., type, model, serial, a second identifier, where it is kept and a third detail. */
export function labelCard(){
  const card=el('div',null,'x-card');const h=el('div',null,'x-card-h');h.append(svg('barcode'),el('span','Asset label'));const live=el('span','Live','x-tag acc');h.append(live);
  const lab=el('div',null,'x-label');const top=el('div',null,'x-label-top');const typ=el('span','-');top.append(el('span','ALTAWKILAT · IT'),typ);
  const no=el('div','--- ----','x-label-no');const model=el('div','Choose a product type','x-label-model');const grid=el('div',null,'x-label-grid');
  const cell=()=>{const d=el('div');const k=el('span','-');const v=el('b','-');d.append(k,v);grid.append(d);return {k,v};};const c=[cell(),cell(),cell(),cell()];
  const bars=el('div',null,'x-bars');for(let n=0;n<28;n++)bars.append(el('i'));lab.append(top,no,model,grid,bars);card.append(h,lab);
  return {card,live,set(x){typ.textContent=x.type||'-';no.textContent=x.no||'--- ----';model.textContent=x.model||'-';
    (x.cells||[]).forEach(([k,v],n)=>{c[n].k.textContent=k;c[n].v.textContent=v||'-';});}};
}

/** Builds the form in `box`; returns read() that gives the input for the API. Options: side (an element for the live label and
 *  the checklist), onProgress(left) after each change. */
export function assetForm(box,m,{asset,attributes={},keys=[],phoneOnly=false,side,onProgress}={}){
  const L=m.lists;const isNew=!asset;
  // People without a role that works in every region (or the Voice Administrator) register in their own regions only.
  const onlySpecialist=!m.viewsAll&&!m.roles.some(r=>r.code==='VoiceAdministrator')&&m.roles.some(r=>r.code==='SupportSpecialist');
  const locs=L.locations.filter(l=>(l.active||l.id===asset?.locationId)&&(!onlySpecialist||m.regions.includes(l.region)));
  const i={};
  i.type=select(L.types.filter(t=>t.active).map(t=>[t.code,`${t.name} (${t.code})`]),asset?.type??'',isNew?'Choose':undefined);i.type.disabled=!isNew;
  i.location=select(locs.map(l=>[l.id,`${l.region} · ${l.name} (${kindLabel(l.kind)})`]),asset?.locationId??'','Choose');
  if(asset&&asset.status!=='InStore')i.location.disabled=true;
  i.manufacturer=select(L.manufacturers.filter(x=>x.active||x.id===asset?.manufacturerId).map(x=>[x.id,x.name]),asset?.manufacturerId??'','Choose');
  // A manufacturer not in the list (owner, 23 October 2026): typed here, added to the list when the asset is saved.
  i.manufacturer.append(option('new','+ A manufacturer not in the list'));i.newManufacturer=text('',{max:100,placeholder:'Type the new manufacturer'});i.newManufacturer.classList.add('x-newmodel');i.newManufacturer.hidden=true;
  i.model=document.createElement('select');i.newModel=text('',{max:150,placeholder:'Type the new model'});i.newModel.classList.add('x-newmodel');
  i.serial=text(asset?.serial,{max:100,mono:true,placeholder:'As printed on the sticker'});i.tag=text(asset?.serviceTag,{max:100,mono:true});
  i.mac=text(asset?.mac,{max:17,mono:true,placeholder:'AA:BB:CC:DD:EE:FF'});i.ip=text(asset?.ip,{max:15,mono:true,placeholder:'10.1.2.3'});
  i.condition=select([['New','New'],['Used','Used']],asset?.condition??(isNew?'':'New'),isNew?'Choose':undefined);
  i.purchase=text(asset?.purchaseDate,{type:'date'});
  i.supplier=select(L.suppliers.filter(x=>x.active||x.id===asset?.supplierId).map(x=>[x.id,x.name]),asset?.supplierId??'','None');i.supplier.append(option('new','+ A supplier not in the list'));
  // A new supplier: name and VAT number, added to the list when the asset is saved.
  i.newSupplier=text('',{max:200,placeholder:'Supplier name'});i.newSupplierVat=text('',{max:15,mono:true,placeholder:'VAT number (15 digits, optional)'});
  const newSup=el('div',null,'x-newsupplier');newSup.append(i.newSupplier,i.newSupplierVat);newSup.hidden=true;
  i.supplier.addEventListener('change',()=>{newSup.hidden=i.supplier.value!=='new';if(!newSup.hidden)i.newSupplier.focus();});
  i.po=text(asset?.poNumber,{max:40});i.cost=text(asset?.unitCost,{type:'number',placeholder:'0.00'});i.cost.step='0.01';i.cost.min='0';i.invoice=text(asset?.invoiceNo,{max:60,placeholder:'As on the invoice'});
  i.key=text('',{max:200,mono:true,placeholder:keys.length?'Saved (encrypted). Leave empty to keep it.':'XXXXX-XXXXX-XXXXX-XXXXX-XXXXX'});
  i.notes=text(asset?.notes,{type:'textarea',max:2000,placeholder:'Anything the next person should know about this device'});
  const attrInputs={};
  if(asset?.poId){for(const x of [i.supplier,i.po,i.cost,i.invoice,i.purchase])x.disabled=true;}
  function models(){const t=i.type.value,mf=Number(i.manufacturer.value);const keep=i.model.value||asset?.modelId;
    i.model.replaceChildren(option('','Choose'),...L.models.filter(x=>x.type===t&&x.manufacturer===mf&&(x.active||x.id===asset?.modelId)).map(x=>option(x.id,x.name)),option('new','+ A model not in the list'));
    if(keep&&[...i.model.options].some(o=>o.value===String(keep)))i.model.value=String(keep);i.newModel.hidden=i.model.value!=='new';}

  // Live label and checklist (registration page).
  const label=side?labelCard():null;let ring,ringT,ringS,list;
  if(side){const c=el('div',null,'x-card');const r=el('div',null,'x-ring');ring=el('div',null,'x-ring-meter x-p0');ringT=el('b','0%');ring.append(ringT);
    const rt=el('div');ringS=el('small','');rt.append(el('b',isNew?'Ready to register':'Ready to save'),ringS);r.append(ring,rt);list=el('ul',null,'x-check');c.append(r,list);side.replaceChildren(label.card,c);}
  else if(!isNew){/* edit: no side panel */}

  let secs=[];
  function render(){
    const t=L.types.find(x=>x.code===i.type.value);box.replaceChildren();secs=[];
    const s1=section('s1','box',null,'Product type',isNew?'Decides which fields are asked and the Asset No. code.':'The type never changes: it is part of the Asset No.');
    const given=el('div',null,'x-given');given.append(isNew?(t?`${t.code}-····`:'---'):asset.assetNo,el('small',isNew?'given when you save':'Asset No.'));
    const no=el('div',null,'x-f');no.append(el('span','Asset No.','x-l'),given);
    s1.body.append(field('Product type',i.type,{required:true}),no);s1.req.push(['Product type',()=>i.type.value]);secs.push(s1);
    const s2=section('s2','laptop','cyan','Device details','Copy the serial number'+(t?.serviceTag?' and service tag':'')+' from the sticker on the device.');
    // Software has no manufacturer or model; toner has no model (its toner code identifies it).
    const useMf=!t||t.manufacturerUsed,useModel=!t||t.modelUsed;
    if(useMf){s2.body.append(field('Manufacturer',i.manufacturer,{required:true,extra:i.newManufacturer,hint:'Not in the list? Choose "+ A manufacturer not in the list" and type it.'}));s2.req.push(['Manufacturer',()=>i.manufacturer.value&&(i.manufacturer.value!=='new'||i.newManufacturer.value.trim())]);}
    if(useModel){s2.body.append(field('Model',i.model,{required:true,extra:i.newModel}));s2.req.push(['Model',()=>i.model.value&&(i.model.value!=='new'||i.newModel.value.trim())]);}
    if(t){
      if(t.serialUsed){s2.body.append(field('Serial number',i.serial,{required:t.serialRequired,hint:'Checked against every asset already registered.'}));if(t.serialRequired)s2.req.push(['Serial number',()=>i.serial.value.trim()]);}
      if(t.serviceTag){s2.body.append(field('Service tag',i.tag,{required:true,hint:'Dell and Lenovo: 7 letters and numbers.'}));s2.req.push(['Service tag',()=>i.tag.value.trim()]);}
      if(t.mac){s2.body.append(field('MAC address',i.mac,{required:t.macRequired,hint:'Any format; it is tidied to AA:BB:CC:DD:EE:FF.'}));if(t.macRequired)s2.req.push(['MAC address',()=>i.mac.value.trim()]);}
      if(t.ip)s2.body.append(field('IP address',i.ip));
      for(const f of t.fields){
        let input=attrInputs[f.code];
        if(!input){input=f.kind==='list'?select(L.values.filter(v=>v.list===f.list&&(v.active||v.code===attributes[f.code])).map(v=>[v.code,v.name]),attributes[f.code]??'','Choose')
          :text(attributes[f.code],{type:f.kind==='number'?'number':'text',max:200,mono:f.code==='TonerCode',placeholder:f.code==='TonerCode'?'W2210A':undefined});attrInputs[f.code]=input;}
        s2.body.append(field(f.label,input,{required:f.required,hint:f.code==='ComputerName'?'Required at delivery; can be filled later.':f.code==='TonerCode'?'As printed on the box, for example W2210A or 59A.':undefined}));
        if(f.required)s2.req.push([f.label,()=>input.value.trim()]);
      }
      s2.body.append(field('Device condition',i.condition,{required:true}));s2.req.push(['Device condition',()=>i.condition.value]);
    }
    secs.push(s2);
    const s3=section('s3','pin',null,'Where it is','The IT warehouse or branch where IT keeps it until it is handed over.');
    s3.body.append(field('Kept at',i.location,{required:true,hint:asset&&asset.status!=='InStore'?'Changes with deliveries and stock moves.':'Support Specialists see their own region only.'}));s3.req.push(['Kept at',()=>i.location.value]);secs.push(s3);
    const s4=section('s4','cart','amber','Purchase',asset?.poId?'Received on a PO: these details come from the PO.':'Old stock with no PO: leave this empty. Fill in only what you know.');
    s4.body.classList.add('three');
    s4.body.append(field('Supplier',i.supplier,{extra:newSup}),field('Purchase date',i.purchase),field('Unit cost',i.cost,{unit:'SAR',hint:'Before VAT.'}),field('Invoice number',i.invoice),
      field('PO number',i.po,{hint:asset?.poId?'Received on the PO.':'Reference only, for an old PO not recorded here.'}));secs.push(s4);
    const s5=section('s5','key','green','Keys and notes',t?.key?'Keys are encrypted. Every view is logged with who and when.':'Anything the next person should know.');
    if(t?.key)s5.body.append(field(t.code==='PC'?'Windows key':'Software key',i.key,{wide:true,hint:'Only the IT Administrator and the IT Assets Officer can see it after saving.'}));
    s5.body.append(field('Notes',i.notes,{wide:true}));secs.push(s5);
    box.append(...secs.map(x=>x.s));
    if(phoneOnly){for(const x of box.querySelectorAll('input,select,textarea'))x.disabled=true;for(const x of [i.mac,i.ip,attrInputs.Extension])if(x)x.disabled=false;
      const n=el('div',null,'msg info');n.textContent='Voice Administrator: you can change the MAC address, IP address and extension of IP phones.';box.prepend(n);}
    status();
  }
  function status(){
    const t=L.types.find(x=>x.code===i.type.value);let total=0,done=0;const items=[];
    for(const s of secs){
      const miss=s.req.filter(([,ok])=>!ok()).length;total+=s.req.length;done+=s.req.length-miss;for(const [l,ok] of s.req)items.push([l,!!ok()]);
      s.tag.className='x-tag'+(!s.req.length?'':miss?'':' ok');s.tag.replaceChildren();
      if(!s.req.length)s.tag.textContent='Optional';else if(miss)s.tag.textContent=`${miss} to fill`;else{s.tag.append(svg('check'),'Complete');}
    }
    const left=total-done;
    if(side){const pct=total?Math.round(done/total*10)*10:0;ring.className='x-ring-meter x-p'+pct;ringT.textContent=(total?Math.round(done/total*100):0)+'%';
      ringS.textContent=left?`${left} required field${left>1?'s':''} left`:'Everything required is filled';
      list.replaceChildren(...items.map(([l,ok])=>{const li=el('li',null,ok?'done':'');const d=el('span',null,'x-dot');d.append(svg('check'));li.append(d,l);return li;}));
      const opt=s=>s.selectedIndex>0?s.selectedOptions[0].text:'';const mdl=!t?.modelUsed?(attrInputs.Software||attrInputs.TonerCode)?.value.trim()||'':i.model.value==='new'?i.newModel.value.trim():opt(i.model);
      const first=t?.fields?.[0];const second=t?.serviceTag?['Service tag',i.tag.value.trim().toUpperCase()]:first?[first.label,attrInputs[first.code]?.value.trim()]:['Service tag',''];
      label.set({type:t?.name||'-',no:t?`${t.code}-····`:'--- ----',model:t?[t.manufacturerUsed?(i.manufacturer.value==='new'?i.newManufacturer.value.trim():opt(i.manufacturer)):'',mdl].filter(Boolean).join(' ')||t.name:'Choose a product type',
        cells:[['Serial',i.serial.value.trim()],second,['Kept at',opt(i.location).split(' (')[0]],t?.mac?['MAC',i.mac.value.trim()]:['Condition',i.condition.value]]});}
    onProgress?.(left,total);return left;
  }
  i.type.onchange=()=>{models();render();};i.manufacturer.onchange=()=>{const isNew=i.manufacturer.value==='new';i.newManufacturer.hidden=!isNew;models();if(isNew){i.model.value='new';i.newModel.hidden=false;i.newManufacturer.focus();}status();};i.model.onchange=()=>{i.newModel.hidden=i.model.value!=='new';if(!i.newModel.hidden)i.newModel.focus();status();};
  box.addEventListener('input',status);box.addEventListener('change',status);
  i.tag.addEventListener('input',()=>{i.tag.value=i.tag.value.toUpperCase();});
  models();render();
  const v=x=>x.value.trim()===''?null:x.value.trim();
  const read=()=>{const t=i.type.value;const attrs={};for(const [k,x] of Object.entries(attrInputs))attrs[k]=x.value.trim()===''?null:x.value.trim();
    return {type:t,locationId:Number(i.location.value)||null,manufacturerId:L.types.find(x=>x.code===t)?.manufacturerUsed===false||i.manufacturer.value==='new'?null:Number(i.manufacturer.value)||null,
      newManufacturer:L.types.find(x=>x.code===t)?.manufacturerUsed===false||i.manufacturer.value!=='new'?null:v(i.newManufacturer),
      modelId:L.types.find(x=>x.code===t)?.modelUsed===false?null:i.model.value&&i.model.value!=='new'?Number(i.model.value):null,newModel:L.types.find(x=>x.code===t)?.modelUsed===false?null:i.model.value==='new'?v(i.newModel):null,
      serialNo:v(i.serial),serviceTag:v(i.tag),macAddress:v(i.mac),ipAddress:v(i.ip),condition:i.condition.value||null,purchaseDate:v(i.purchase),
      supplierId:i.supplier.value==='new'?null:Number(i.supplier.value)||null,newSupplier:i.supplier.value==='new'?v(i.newSupplier):null,newSupplierVat:i.supplier.value==='new'?v(i.newSupplierVat):null,poNumber:v(i.po),unitCost:i.cost.value===''?null:Number(i.cost.value),invoiceNo:v(i.invoice),notes:v(i.notes),attributes:attrs,
      osKey:t==='PC'?v(i.key):null,softwareKey:t==='SOF'?v(i.key):null};};
  read.left=status;
  return read;
}
