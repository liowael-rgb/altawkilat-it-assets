import {$,el,api,shell,fail,date,money,table} from '/itam.js';
async function load(){
  try{const list=await api('/pos?q='+encodeURIComponent($('q').value.trim()));
    $('list').replaceChildren(table(['PO number','PO date','Supplier','Ordered','Received','Total with VAT','Invoice','Sent to Accounting'],list,
      p=>[p.poNumber,date(p.poDate),p.supplier,String(p.ordered),`${p.received} of ${p.ordered}`,money(p.total),p.invoiceNo||'',p.sentOn?el('span','Sent '+date(p.sentOn),'tag ok'):el('span','Not sent','tag warn')],null,
      {empty:'No purchase orders yet.',onRow:p=>location.assign('/po.html?id='+p.id)}));}catch(e){fail(e);}
}
try{const m=await shell('pos');if(!m.can.poView)throw Error('You cannot see purchase orders.');$('new-link').hidden=!m.can.poManage;
  $('filters').onsubmit=e=>{e.preventDefault();load();};await load();}catch(e){fail(e);}
