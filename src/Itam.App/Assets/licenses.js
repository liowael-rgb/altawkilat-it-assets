// Licenses (plan v1.9): software licenses (asset type Software, with seats) and the Windows keys of computers, with who
// holds each one. Keys stay encrypted; each one opens from its asset page, and every view is logged.
import {$,el,api,shell,fail,card,table} from '/itam.js';
import {two,assetLink} from '/eol.js';
async function load(){
  const d=await api('/licenses');
  const keyTag=x=>el('span',x.hasKey?'Key recorded':'No key','tag '+(x.hasKey?'ok':'warn'));
  $('view').replaceChildren(
    card('Software','Licenses per software: total, seats, in use and in store.',table(['Software','Licenses','Seats','In use','In store','Without a key'],d.summary,
      s=>[s.software,String(s.licenses),String(s.seats),String(s.inUse),String(s.inStore),String(s.withoutKey)],null,{empty:'No software licenses registered yet.'})),
    card('Software licenses','Open the asset to see the key (logged).',table(['Asset','Software','Seats','Holder','Status','Key'],d.software,
      x=>[assetLink(x.assetNo,x.id),x.software||'',x.seats===null?'':String(x.seats),x.holder?two(x.holder,[x.username,x.deliveryNo].filter(Boolean).join(' · ')):two('In stock',x.keptAt),x.status,keyTag(x)],null,{empty:'No software licenses.'})),
    card('Windows keys of computers','Computers with an OS key recorded.',table(['Asset','Model','Service tag','Holder','Status','Key'],d.windows,
      x=>[assetLink(x.assetNo,x.id),x.model||'',x.serial||'',x.holder?two(x.holder,[x.username,x.deliveryNo].filter(Boolean).join(' · ')):two('In stock',x.keptAt),x.status,keyTag(x)],null,{empty:'No Windows keys recorded.'})));
}
try{await shell('licenses');await load();}catch(e){fail(e);}
