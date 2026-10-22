// Reports (plan v1.9 step 6): choose a report, set the dates and the region, read it on screen (the list has the usual
// search and sorting) and export it to Excel. The server keeps each person's visibility and logs who opened what.
import {$,el,api,shell,fail,card,table,option,svg,date,when,money} from '/itam.js';
const q=new URLSearchParams(location.search);
let list,current=q.get('r');
const fmt=(v,col)=>{if(v===null||v===undefined)return '';if(typeof v==='number')return /SAR/.test(col)?money(v).replace(' SAR',''):String(v);
  if(typeof v==='boolean')return v?'Yes':'No';if(/^\d{4}-\d{2}-\d{2}T/.test(v))return when(v);if(/^\d{4}-\d{2}-\d{2}$/.test(v))return date(v);return String(v);};
const params=()=>{const p=new URLSearchParams();for(const k of ['from','to','region']){const v=$('f-'+k)?.value;if(v)p.set(k,v);}return p;};

async function menu(){
  const box=el('div',null,'x-report-list');
  const areas=[...new Set(list.reports.map(r=>r.area))];
  for(const a of areas){const g=el('div',null,'x-report-group');g.append(el('h3',a));
    for(const r of list.reports.filter(x=>x.area===a)){const b=el('a',null,'x-report-card');b.href='?r='+r.key;b.append(el('b',r.title),el('span',r.description,'x-sub'));g.append(b);}
    box.append(g);}
  $('title').textContent='Reports';$('head-actions').replaceChildren();
  $('view').replaceChildren(card('Choose a report','Each opens on screen with search and sorting, and exports to Excel.',box));
}
async function open(){
  const def=list.reports.find(r=>r.key===current);if(!def){current=null;return menu();}
  $('title').textContent=def.title;$('subtitle').textContent=def.description;
  const f=el('form',null,'x-report-filters');
  const field=(label,input)=>{const l=el('label');l.append(el('span',label,'x-l'),input);return l;};
  const inp=(id,type)=>{const i=document.createElement('input');i.type=type;i.id='f-'+id;i.value=q.get(id)||'';return i;};
  if(def.dates)f.append(field('From',inp('from','date')),field('To',inp('to','date')));
  const reg=document.createElement('select');reg.id='f-region';reg.append(option('','All my regions'),...list.regions.map(r=>option(r,r)));reg.value=q.get('region')||'';f.append(field('Region',reg));
  const run=el('button',null,'primary');run.type='submit';run.append(svg('search'),'Show');f.append(run);
  const out=el('div');
  f.onsubmit=async e=>{e.preventDefault();const p=params();p.set('r',current);history.replaceState(null,'','?'+p.toString());await show(out,def);};
  const back=el('button',null,'');back.type='button';back.append('All reports');back.onclick=()=>{history.replaceState(null,'','reports.html');current=null;menu();};
  const xl=el('button',null,'primary');xl.type='button';xl.append(svg('save'),'Export to Excel');xl.onclick=()=>{location.href=`/api/reports/${current}/excel?`+params().toString();};
  $('head-actions').replaceChildren(back,xl);
  $('view').replaceChildren(card('Filters',null,f),out);
  await show(out,def);
}
async function show(out,def){
  out.replaceChildren(el('p','Loading…','hint'));
  const r=await api(`/reports/${current}?`+params().toString());
  const n=r.rows.filter(x=>x[0]!=='Total').length;const t=table(r.columns,r.rows,row=>row.map((v,i)=>fmt(v,r.columns[i])),null,{empty:'Nothing in this report for these filters.'});
  for(const tr of t.querySelectorAll('tbody tr'))if(tr.cells[0]?.textContent==='Total')tr.classList.add('x-total');
  out.replaceChildren(card(`${n} row${n===1?'':'s'}`,null,t));
}
try{await shell('reports');list=await api('/reports');if(current)await open();else await menu();}catch(e){fail(e);}
