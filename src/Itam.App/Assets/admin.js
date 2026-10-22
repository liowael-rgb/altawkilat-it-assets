import {$,el,api,shell,fail,message,when,card,table,buttons,edit,people,tags} from '/itam.js';
let d,area=new URLSearchParams(location.search).get('area')||'roles';
const AREAS=[['roles','Roles'],['types','Asset types'],['locations','IT warehouses and branches'],['regions','Regions'],['suppliers','Suppliers'],
  ['manufacturers','Manufacturers and models'],['values','Value lists'],['employees','Employees'],['audit','Audit log']];
const L=()=>d.lists;const yes=v=>v?'Yes':'No';
const change=(a,data,reason,action='save')=>api('/admin',{method:'POST',json:{area:a,action,data,reason}});
const reload=msg=>()=>load(msg||'Saved.');
const add=(text,fn)=>buttons([text,fn,'primary']);
const kindLabel=k=>k==='ITWarehouse'?'IT warehouse':'Branch';

const render={
  roles:()=>{const form=()=>edit('Give a role',[{key:'employee',label:'Person',type:'person',required:true,hint:'Type part of the name, then choose.'},
      {key:'role',label:'Role',type:'select',options:d.roleCodes.map(r=>[r.code,r.label])},
      {key:'region',label:'Region (IT Support Specialist only)',type:'select',options:[['',''],...L().regions.filter(r=>r.active).map(r=>[r.code,r.name])],hint:'An IT Support Specialist gets one row per region.'}],
      (x,r)=>change('role',{employee:x.employee,role:x.role,region:x.region},r),{done:reload('Role given.')});
    return [card('Roles','IT roles and the regions of each IT Support Specialist. A person can hold several roles; their rights add up. Employees come from Active Directory.',
      table(['Person','Role','Region','Since'],d.roles,x=>[x.name+(x.enabled?'':' (disabled in AD)'),x.roleLabel,x.region||'All',when(x.since)],d.can.roles?x=>[['Remove',()=>edit(`Remove ${x.roleLabel} from ${x.name}?`,[],(y,r)=>change('role',{id:x.id},r,'delete'),{button:'Remove',done:reload('Role removed.')})]]:null),
      d.can.roles&&add('Give a role',async()=>{await people('');form();}))];},
  types:()=>[card('Asset types','The short code starts every Asset No. (PC-822); it never changes.',
    table(['Code','Name','Order','Active'],L().types,t=>[t.code,t.name,String(t.sort),yes(t.active)],d.can.masterData?t=>[['Edit',()=>ref('type',t,[['code','Short code',true,true],['name','Name'],['sort','Order','number']])]]:null),
    d.can.masterData&&add('Add a type',()=>ref('type',null,[['code','Short code'],['name','Name'],['sort','Order','number']])))],
  regions:()=>[card('Regions','IT Support Specialists work per region; an asset belongs to the region of where it is kept.',
    table(['Code','Name','Order','Active'],L().regions,t=>[t.code,t.name,String(t.sort),yes(t.active)],d.can.masterData?t=>[['Edit',()=>ref('region',t,[['code','Code',true,true],['name','Name'],['sort','Order','number']])]]:null),
    d.can.masterData&&add('Add a region',()=>ref('region',null,[['code','Code'],['name','Name'],['sort','Order','number']])))],
  locations:()=>{const form=x=>edit(x?x.name:'Add an IT warehouse or branch',[{key:'name',label:'Name',value:x?.name,required:true},{key:'kind',label:'Kind',type:'select',options:[['ITWarehouse','IT warehouse'],['Branch','Branch']],value:x?.kind},
      {key:'region',label:'Region',type:'select',options:L().regions.map(r=>[r.code,r.name]),value:x?.region},{key:'city',label:'City',value:x?.city},{key:'active',label:'Active',type:'checkbox',value:x?x.active:true}],
      (v,r)=>change('location',{id:x?.id,...v},r),{done:reload()});
    return [card('IT warehouses and branches','Where assets are kept. Stock can be kept at a branch in cities without an IT warehouse.',
      table(['Name','Kind','Region','City','Active'],L().locations,x=>[x.name,kindLabel(x.kind),x.region,x.city||'',yes(x.active)],d.can.masterData?x=>[['Edit',()=>form(x)]]:null),d.can.masterData&&add('Add',()=>form(null)))];},
  suppliers:()=>{const form=x=>edit(x?x.name:'Add a supplier',[{key:'name',label:'Name',value:x?.name,required:true},{key:'vat',label:'VAT number (15 digits)',value:x?.vat},{key:'active',label:'Active',type:'checkbox',value:x?x.active:true}],
      (v,r)=>change('supplier',{id:x?.id,...v},r),{done:reload()});
    return [card('Suppliers',null,table(['Supplier','VAT number','Active'],L().suppliers,x=>[x.name,x.vat||'',yes(x.active)],d.can.masterData?x=>[['Edit',()=>form(x)]]:null),d.can.masterData&&add('Add a supplier',()=>form(null)))];},
  manufacturers:()=>{const mf=x=>edit(x?x.name:'Add a manufacturer',[{key:'name',label:'Name',value:x?.name,required:true},{key:'active',label:'Active',type:'checkbox',value:x?x.active:true}],(v,r)=>change('manufacturer',{id:x?.id,...v},r),{done:reload()});
    const mo=x=>edit(x?x.name:'Add a model',[{key:'manufacturer',label:'Manufacturer',type:'select',options:L().manufacturers.map(m=>[m.id,m.name]),value:x?.manufacturer},
      {key:'type',label:'Product type',type:'select',options:L().types.map(t=>[t.code,t.name]),value:x?.type},{key:'name',label:'Model',value:x?.name,required:true},{key:'active',label:'Active',type:'checkbox',value:x?x.active:true}],
      (v,r)=>change('model',{id:x?.id,name:v.name,manufacturer:Number(v.manufacturer),type:v.type,active:v.active},r),{done:reload()});
    const mname=id=>L().manufacturers.find(m=>m.id===id)?.name||id;const tname=c=>L().types.find(t=>t.code===c)?.name||c;
    return [card('Manufacturers',null,table(['Manufacturer','Models','Active'],L().manufacturers,x=>[x.name,String(L().models.filter(m=>m.manufacturer===x.id).length),yes(x.active)],d.can.masterData?x=>[['Edit',()=>mf(x)]]:null),d.can.masterData&&add('Add a manufacturer',()=>mf(null))),
      card('Models','Models per manufacturer and type. People who register assets can also add a model while registering (kept in the audit log).',
        table(['Manufacturer','Type','Model','Active'],L().models,x=>[mname(x.manufacturer),tname(x.type),x.name,yes(x.active)],d.can.masterData?x=>[['Edit',()=>mo(x)]]:null),d.can.masterData&&add('Add a model',()=>mo(null)))];},
  values:()=>{const lists=[['ComputerType','Computer type'],['OperatingSystem','Operating system'],['PrinterType','Printer type'],['TonerType','Toner type']];
    return lists.map(([code,label])=>card(label,null,table(['Code','Name','Order','Active'],L().values.filter(v=>v.list===code),v=>[v.code,v.name,String(v.sort),yes(v.active)],
      d.can.masterData?v=>[['Edit',()=>ref('value',{...v,list:code},[['code','Code',true,true],['name','Name'],['sort','Order','number']],code)]]:null),
      d.can.masterData&&add('Add a value',()=>ref('value',{list:code,active:true},[['code','Code'],['name','Name'],['sort','Order','number']],code))));},
  employees:()=>[card('Employees','Read from the Active Directory users OU (read-only). Accounts no longer found are kept, marked disabled, so history and custody stay linked.',
    el('p',`${d.employees.active} active of ${d.employees.total} employees. Last read: ${when(d.employees.lastRead)}.`))],
  audit:()=>[card('Audit log','The last 300 changes: who, when, what and why.',table(['When','Who','What','Key','Action','Details','Reason'],d.audit,x=>[when(x.at),x.who,x.entity,x.key,x.action,x.detail||'',x.reason||'']))]
};
// A simple list item: fields = [key,label,type?,readonlyWhenEditing?]
function ref(a,x,fields,list){
  edit(x?.name?x.name:'Add',[...fields.map(([k,label,type,ro])=>({key:k,label,type:type==='number'?'number':'text',value:x?.[k],required:k!=='sort',hint:ro&&x?.name?'Cannot change.':undefined})),{key:'active',label:'Active',type:'checkbox',value:x?x.active:true}],
    (v,r)=>change(a,{...v,code:x?.code&&x?.name?x.code:v.code,list},r),{done:reload()});
}
async function load(msg){d=await api('/admin');
  $('side').replaceChildren(...AREAS.filter(([k])=>k!=='audit'||d.can.audit).map(([k,t])=>{const b=el('button',t);b.type='button';b.setAttribute('aria-selected',String(k===area));b.onclick=()=>{area=k;history.replaceState(null,'','?area='+k);load();};return b;}));
  $('panel').replaceChildren(...render[area]().filter(Boolean));
  if(!d.can.masterData&&area!=='roles'&&area!=='audit')message('Read only: master data is changed by the IT Administrator.','info');else message(msg||'');
}
try{await shell('admin');await load();}catch(e){fail(e);}
