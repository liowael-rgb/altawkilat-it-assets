import {$,el,api,shell,fail,message,when,date,money,card,dl,table,buttons,statusTag} from '/itam.js';
import {assetForm} from '/assetform.js';
const id=Number(new URLSearchParams(location.search).get('id'));
let m,d;
async function load(note){
  d=await api('/assets/'+id);const a=d.asset;
  $('eyebrow').textContent=`${a.typeName} · ${a.companyName}`;$('title').textContent=a.assetNo;$('subtitle').textContent=`${a.manufacturer} ${a.model}`;
  const label=c=>d.fields.find(f=>f.code===c)?.label||c;
  const listName=(f,v)=>f?.list?(m.lists.values.find(x=>x.list===f.list&&x.code===v)?.name||v):v;
  const details=dl([['Asset No.',el('strong',a.assetNo)],['Status',statusTag(a.statusLabel,a.status)],['Kept at',`${a.keptAt} (${a.stockLocation==='ITWarehouse'?'IT warehouse':a.stockLocation==='Branch'?'Branch':'Employee'})`],['Region',a.region],
    ['Product type',a.typeName],['Manufacturer and model',`${a.manufacturer} ${a.model}`],['Serial number',a.serial],['Service tag',a.serviceTag],['MAC address',a.mac?el('span',a.mac,'mono'):null],['IP address',a.ip],
    ...Object.entries(d.attributes).map(([k,v])=>[label(k),listName(d.fields.find(f=>f.code===k),v)]),['Device condition',a.condition],['Owning company',a.companyName]]);
  const purchase=dl([['PO',a.poId?(()=>{const l=el('a',a.poNumber);l.href='/po.html?id='+a.poId;return l;})():a.poNumber],['PO line',a.poLine],['Supplier',a.supplier],['Purchase date',date(a.purchaseDate)],
    ['Unit cost',a.unitCost===null?null:money(a.unitCost)],['Invoice number',a.invoiceNo],['Warranty end',date(a.warrantyEnd)],['Received',a.receivedAt?`${when(a.receivedAt)} by ${d.receivedByName}`:null],['Registered',`${when(a.createdAt)} by ${d.createdByName}`]]);
  const keys=d.keys.length?el('div'):null;
  if(keys)for(const k of d.keys){const row=el('p');row.append(el('span',(k==='OsKey'?'OS key':'Software key')+': ••••••••  '));
    if(d.canSeeKeys){const b=el('button','Show','link');b.type='button';b.onclick=async()=>{try{const r=await api(`/assets/${id}/key/${k}`,{method:'POST'});b.replaceWith(el('span',r.value,'mono'));message('The key was shown; the view is logged.','info');}catch(e){fail(e);}};row.append(b);}
    else row.append(el('span','(only the IT Administrator and the IT Assets Officer can see it)','hint'));keys.append(row);}
  $('view').replaceChildren(...[
    card('Asset details',null,details,buttons((d.canEdit||d.phoneOnly)&&['Edit details',()=>editMode(),'primary'])),
    card('Purchase',null,purchase),
    keys&&card('Keys','Encrypted. Every view is logged with who and when.',keys),
    a.notes&&card('Notes',null,el('p',a.notes)),
    card('History','Everything that happened to this asset, newest first.',table(['When','Who','What','Details'],d.history,h=>[when(h.at),h.who,h.kind,h.detail||'']))].filter(Boolean));
  $('view').hidden=false;$('asset-form').hidden=true;
  if(note)message(note);
}
function editMode(){
  const a=d.asset;const read=assetForm($('fields'),m,{asset:a,attributes:d.attributes,keys:d.keys,phoneOnly:d.phoneOnly});
  $('view').hidden=true;$('asset-form').hidden=false;$('edit-reason').value='';
  $('edit-cancel').onclick=()=>{$('view').hidden=false;$('asset-form').hidden=true;};
  $('asset-form').onsubmit=async e=>{e.preventDefault();
    try{await api('/assets/'+id,{method:'PUT',json:{...read(),version:d.asset.version,reason:$('edit-reason').value}});await load('Saved. The change is in the history.');}catch(err){fail(err);}};
}
try{m=await shell('assets');const fresh=new URLSearchParams(location.search).get('new');await load(fresh?`Registered as ${fresh}.`:null);}catch(e){fail(e);}
