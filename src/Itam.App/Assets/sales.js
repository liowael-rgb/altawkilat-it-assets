// Sales SL-n (form 21, plan v1.9): devices sold to employees, with the price and the proof of payment. Any IT role records
// a sale, with no approval; the custody closes with the sale (no receipt note); computers leave AD and IP phones leave CUCM.
import {$,el,api,shell,fail,message,date,when,money,card,table} from '/itam.js';
import {recordSale,btn,two,fileLink} from '/eol.js';
const q=new URLSearchParams(location.search);
async function load(note){
  const d=await api('/sales');
  $('head-actions').replaceChildren(...(d.canRecord?[btn('Record a sale',()=>recordSale('',null),'primary','plus')]:[]));
  const tiles=el('div',null,'x-stats');for(const [k,v] of [['Sales',String(d.sales.length)],['Total',money(d.total)]]){const t=el('div',null,'x-stat');t.append(el('b',v),el('span',k));tiles.append(t);}
  $('view').replaceChildren(card('Sales',null,tiles,table(['Sale','Asset','Buyer','Price','Sold on','Custody closed','Recorded by','Proof'],d.sales,
    s=>[el('span',s.no,'mono'),two(el('span',s.assetNo,'mono'),[s.type,s.model,s.serial].filter(Boolean).join(' · ')),two(s.buyer,s.username),money(s.price),date(s.soldOn),s.deliveryNo||'Sold from stock',
      two(s.recordedBy||'',when(s.recordedAt)),fileLink('Open',`/api/sales/${s.id}/proof`)],null,{empty:'No sales yet.'})));
  if(note)message(note);
}
try{await shell('sales');await load(q.get('created')?`${q.get('created')} recorded. The removal from AD or CUCM is on the Tasks page.`:null);}catch(e){fail(e);}
