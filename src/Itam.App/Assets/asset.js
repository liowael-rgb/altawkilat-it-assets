import {$,el,api,shell,fail,message,when,date,money,card,dl,table,statusTag,svg} from '/itam.js';
import {assetForm,labelCard} from '/assetform.js';
const id=Number(new URLSearchParams(location.search).get('id'));
let m,d;
async function load(note){
  d=await api('/assets/'+id);const a=d.asset;
  const product=[a.manufacturer,a.model||d.attributes.Software||d.attributes.TonerCode].filter(Boolean).join(' ')||a.typeName;
  $('eyebrow').textContent=`Assets › ${a.typeName}`;$('title').textContent=a.assetNo;$('subtitle').textContent=`${product} · ${a.keptAt}`;
  const label=c=>d.fields.find(f=>f.code===c)?.label||c;
  const listName=(f,v)=>f?.list?(m.lists.values.find(x=>x.list===f.list&&x.code===v)?.name||v):v;
  const acts=$('head-actions');acts.replaceChildren();
  if(d.canEdit||d.phoneOnly){const b=el('button',null,'primary');b.type='button';b.append(svg('edit'),'Edit details');b.onclick=editMode;acts.append(b);}
  const all=el('a');all.href='/';all.append(el('button','All assets'));acts.append(all);
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
    card('History','Everything that happened to this asset, newest first.',(()=>{const t=table(['When','Who','What','Details'],d.history,h=>[when(h.at),h.who,h.kind,h.detail||'']);t.classList.add('x-history');return t;})())].filter(Boolean));
  const lc=labelCard();lc.live.textContent=a.statusLabel;lc.live.className='x-tag '+(a.status==='InStore'?'ok':'acc');
  const first=d.fields[0];
  lc.set({type:a.typeName,no:a.assetNo,model:`${product}`,cells:[['Serial',a.serial],a.serviceTag?['Service tag',a.serviceTag]:first?[first.label,listName(first,d.attributes[first.code])]:['Service tag',''],
    ['Kept at',a.keptAt],a.mac?['MAC',a.mac]:['Condition',a.condition]]});
  const side=el('aside',null,'x-side');side.append(lc.card);
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
