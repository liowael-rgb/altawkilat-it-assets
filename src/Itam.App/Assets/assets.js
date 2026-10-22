import {$,el,api,shell,option,fail,statusTag} from '/itam.js';
let page=1;
async function load(){
  const q=new URLSearchParams({page});for(const k of ['q','type','status','region','location','model','computerType','processor','memory','user','sap']){const v=$(k).value.trim();if(v)q.set(k,v);}
  try{
    const d=await api('/assets?'+q);const rows=$('rows');rows.replaceChildren();
    for(const a of d.items){
      const tr=el('tr',null,'clickable');tr.tabIndex=0;const go=()=>location.assign('/asset.html?id='+a.id);tr.onclick=go;tr.onkeydown=e=>{if(e.key==='Enter')go();};
      const no=el('td');const link=el('a',a.assetNo);link.href='/asset.html?id='+a.id;no.append(link);const st=el('td');st.append(statusTag(a.statusLabel,a.status));
      tr.append(no,el('td',a.typeName),el('td',[a.manufacturer,a.model].filter(Boolean).join(' ')),el('td',a.serial||a.serviceTag||''),el('td',a.computerName||''),(()=>{const td=el('td');if(a.employee){td.append(el('span',a.employee));td.append(el('small',[a.username,a.sapId?'SAP '+a.sapId:''].filter(Boolean).join(' · '),'x-sub'));}return td;})(),st,(()=>{const td=el('td');td.append(el('span',a.keptAt),el('small',a.region,'x-sub'));return td;})(),el('td',a.po||''));rows.append(tr);
    }
    if(!d.items.length){const tr=el('tr');const td=el('td','No assets match.','empty');td.colSpan=9;tr.append(td);rows.append(tr);}
    $('prev').disabled=page<=1;$('next').disabled=!d.hasMore;$('page').textContent='Page '+page;
  }catch(e){fail(e);}
}
try{
  const m=await shell('assets');
  if(!m.can.viewAssets){location.replace('/my.html');$('filters').hidden=true;$('rows').closest('.card').hidden=true;document.getElementById('message').className='msg info';document.getElementById('message').textContent='You have no IT role that shows assets. Your own assets will appear on My IT Assets (coming in step 3).';}
  else{
    $('new-link').hidden=!m.can.register;
    for(const t of m.lists.types)$('type').append(option(t.code,t.name));for(const s of m.lists.statuses)$('status').append(option(s.code,s.label));
    for(const r of m.lists.regions)$('region').append(option(r.code,r.name));for(const l of m.lists.locations)$('location').append(option(l.id,`${l.region} · ${l.name}`));
    const maker=id=>(m.lists.manufacturers.find(f=>f.id===id)||{}).name||'';
    for(const x of [...m.lists.models].sort((p,q)=>(maker(p.manufacturer)+p.name).localeCompare(maker(q.manufacturer)+q.name)))$('model').append(option(x.id,`${maker(x.manufacturer)} ${x.name}`));
    for(const v of m.lists.values.filter(v=>v.list==='ComputerType'&&v.active))$('computerType').append(option(v.code,v.name));
    api('/assets/choices').then(c=>{for(const p of c.processors)$('processor').append(option(p,p));for(const g of c.memory)$('memory').append(option(g,g+' GB'));}).catch(fail);
    $('filters').onsubmit=e=>{e.preventDefault();page=1;load();};$('clear').onclick=()=>{$('filters').reset();page=1;load();};
    $('prev').onclick=()=>{page--;load();};$('next').onclick=()=>{page++;load();};
    const p=new URLSearchParams(location.search);for(const k of ['q','status','type','region','computerType'])if(p.get(k))$(k).value=p.get(k);
    await load();
  }
}catch(e){fail(e);}
