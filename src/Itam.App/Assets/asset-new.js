import {$,api,shell,fail,svg} from '/itam.js';
import {assetForm} from '/assetform.js';
try{
  const m=await shell('assets');
  if(!m.can.register)throw Error('You cannot register assets.');
  const note=$('cmd-note');
  const read=assetForm($('fields'),m,{side:$('side'),onProgress:left=>{note.replaceChildren(svg(left?'info':'check'),left?`${left} required field${left>1?'s':''} left`:'Everything required is filled');}});
  const save=async()=>{const btn=$('register');btn.disabled=true;
    try{const r=await api('/assets',{method:'POST',json:read()});location.assign(`/asset.html?id=${r.id}&new=${encodeURIComponent(r.assetNo)}`);}catch(err){fail(err);btn.disabled=false;}};
  $('asset-form').onsubmit=e=>{e.preventDefault();save();};
  document.addEventListener('keydown',e=>{if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='s'){e.preventDefault();save();}});
}catch(e){fail(e);}
