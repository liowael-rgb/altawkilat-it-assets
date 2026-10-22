import {$,api,shell,fail} from '/itam.js';
import {assetForm} from '/assetform.js';
try{
  const m=await shell('assets');
  if(!m.can.register)throw Error('You cannot register assets.');
  const read=assetForm($('fields'),m);
  $('asset-form').onsubmit=async e=>{e.preventDefault();
    try{const r=await api('/assets',{method:'POST',json:read()});location.assign(`/asset.html?id=${r.id}&new=${encodeURIComponent(r.assetNo)}`);}catch(err){fail(err);}};
}catch(e){fail(e);}
