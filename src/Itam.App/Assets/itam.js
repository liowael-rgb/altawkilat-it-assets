// IT Assets shared page code: API calls with the anti-forgery token, the page frame (white side menu and top bar, the
// approved desktop design), the form dialog, tables and formats. English screens; no inline styles (CSP).
export const $=id=>document.getElementById(id);
export function el(tag,text,cls){const e=document.createElement(tag);if(text!==undefined&&text!==null)e.textContent=String(text);if(cls)e.className=cls;return e;}
export function option(v,t){const o=document.createElement('option');o.value=v;o.textContent=t;return o;}
let csrf='';let mePromise=null;
export async function api(path,options={}){
  const opts={cache:'no-store',...options,headers:{...(options.headers||{})}};
  if(opts.method&&opts.method!=='GET'){opts.headers['X-CSRF-TOKEN']=csrf;if(opts.json!==undefined){opts.headers['Content-Type']='application/json';opts.body=JSON.stringify(opts.json);delete opts.json;}}
  const r=await fetch('/api'+path,opts);
  const body=r.headers.get('content-type')?.includes('json')?await r.json().catch(()=>({})):null;
  if(!r.ok)throw Error(body?.error||`The request failed (HTTP ${r.status}).`);
  return body;
}
export function me(){mePromise??=api('/me').then(x=>{csrf=x.csrf;return x.me;});return mePromise;}
export function form(data,file){const f=new FormData();f.append('data',JSON.stringify(data));if(file)f.append('file',file,file.name);return f;}
export function message(text,kind='ok'){const m=$('message');if(!m)return;m.className='msg '+kind;m.textContent=text||'';if(text)m.scrollIntoView({block:'nearest'});}
export const fail=e=>message(e.message||String(e),'error');
const riyadh=new Intl.DateTimeFormat('en-GB',{timeZone:'Asia/Riyadh',day:'2-digit',month:'2-digit',year:'numeric',hour:'2-digit',minute:'2-digit'});
export const when=v=>v?riyadh.format(new Date(v)):'';
export const date=v=>{if(!v)return '';const [y,m,d]=String(v).slice(0,10).split('-');return `${d}/${m}/${y}`;};
export const money=v=>v===null||v===undefined?'':Number(v).toLocaleString('en-US',{minimumFractionDigits:2,maximumFractionDigits:2})+' SAR';
export function tags(list,cls='tag'){const s=el('span');for(const t of list)s.append(el('span',t,cls));return s;}
export function statusTag(label,code){return el('span',label,'tag'+(code==='InStore'?' ok':code==='InUse'?'':(code==='Lost'||code==='Disposed')?' off':' warn'));}

export const svg=p=>{const s=document.createElementNS('http://www.w3.org/2000/svg','svg');s.setAttribute('viewBox','0 0 24 24');s.setAttribute('aria-hidden','true');s.setAttribute('class','x-i');s.innerHTML=ICONS[p]||p;return s;};
export const ICONS={
  assets:'<path d="M3 7.5L12 3l9 4.5v9L12 21l-9-4.5z"/><path d="M3 7.5l9 4.5 9-4.5M12 12v9"/>',
  pos:'<path d="M3 4h2l2.4 11h11.2L21 7H6.2"/><circle cx="9" cy="19" r="1.5"/><circle cx="17" cy="19" r="1.5"/>',
  backups:'<circle cx="9" cy="8" r="3.5"/><path d="M2.5 20a6.5 6.5 0 0 1 13 0M19 8v6M16 11h6"/>',
  admin:'<path d="M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z"/>',
  laptop:'<rect x="4" y="5" width="16" height="11" rx="1.5"/><path d="M2 19h20"/>',
  search:'<circle cx="11" cy="11" r="7"/><path d="M20 20l-4-4"/>',
  check:'<path d="M5 12.5l4.5 4.5L19 7.5"/>',
  alert:'<path d="M12 3l9.5 17h-19z"/><path d="M12 10v4M12 17h.01"/>',
  info:'<circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 8h.01"/>',
  box:'<path d="M3 7.5L12 3l9 4.5v9L12 21l-9-4.5z"/><path d="M3 7.5l9 4.5 9-4.5M12 12v9"/>',
  pin:'<path d="M12 21s7-6.2 7-11.5A7 7 0 0 0 5 9.5C5 14.8 12 21 12 21z"/><circle cx="12" cy="9.5" r="2.5"/>',
  cart:'<path d="M3 4h2l2.4 11h11.2L21 7H6.2"/><circle cx="9" cy="19" r="1.5"/><circle cx="17" cy="19" r="1.5"/>',
  key:'<circle cx="8" cy="15" r="4"/><path d="M11 12l9-9M16 7l3 3M14 9l2 2"/>',
  barcode:'<path d="M3 7V5a2 2 0 0 1 2-2h2M17 3h2a2 2 0 0 1 2 2v2M21 17v2a2 2 0 0 1-2 2h-2M7 21H5a2 2 0 0 1-2-2v-2M7 8v8M10 8v8M13 8v8M17 8v8"/>',
  save:'<path d="M5 3h11l3 3v15H5z"/><path d="M8 3v5h7V3M8 21v-7h8v7"/>',
  edit:'<path d="M4 20h4L19 9l-4-4L4 16z"/><path d="M13.5 6.5l4 4"/>',
  shield:'<path d="M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z"/>',
  chev:'<path d="M9 6l6 6-6 6"/>',
  handover:'<path d="M7 11l3-3 4 2 4-4"/><path d="M3 14l4-3 3 3 5-1 6-5"/><path d="M3 20h18"/>',
  doc:'<path d="M6 3h8l4 4v14H6z"/><path d="M14 3v4h4M9 12h6M9 16h6"/>',
  upload:'<path d="M12 15V4M7.5 8.5L12 4l4.5 4.5"/><path d="M4 15v4a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-4"/>',
  user:'<circle cx="12" cy="8" r="4"/><path d="M4 21a8 8 0 0 1 16 0"/>',
  back:'<path d="M9 14L4 9l5-5"/><path d="M4 9h11a5 5 0 0 1 0 10h-3"/>',
  tag:'<path d="M3 12V4a1 1 0 0 1 1-1h8l9 9-9 9z"/><circle cx="8" cy="8" r="1.5"/>'
};
const initials=n=>(n||'').split(/\s+/).filter(Boolean).slice(0,2).map(w=>w[0]).join('').toUpperCase();
/** The page frame: white side menu (logo, the pages the person may open, who is signed in) and a top bar with the asset
 *  search; the page's <main> moves into it. Desktop only. */
export async function shell(current){
  document.body.classList.add('x');
  const main=document.querySelector('main');
  const sh=el('div',null,'x-shell');const rail=el('aside',null,'x-rail');rail.setAttribute('aria-label','IT Assets navigation');
  const logo=el('a',null,'x-logo');logo.href='/';const img=document.createElement('img');img.src='/company-logo.png';img.alt='ALTAWKILAT';
  const app=el('div',null,'x-app');const mark=el('span',null,'x-app-mark');mark.append(svg('laptop'));const appText=el('div');appText.append(el('b','IT Assets'),el('small','Asset management'));app.append(mark,appText);logo.append(img,app);
  const navh=el('div','Workspace','x-navh');const nav=el('nav');nav.setAttribute('aria-label','Pages');
  const adminH=el('div','Administration','x-navh');adminH.hidden=true;const adminNav=el('nav');adminNav.setAttribute('aria-label','Administration');
  const meBox=el('div',null,'x-me');rail.append(logo,navh,nav,adminH,adminNav,meBox);
  const right=el('div');const top=el('header',null,'x-top');right.append(top);if(main)right.append(main);sh.append(rail,right);document.body.prepend(sh);
  let m;try{m=await me();}catch(e){top.append(el('span',e.message,'hint'));throw e;}
  if(m.can.viewAssets){const f=el('form',null,'x-search');f.setAttribute('role','search');const q=document.createElement('input');q.name='q';q.type='search';q.maxLength=100;
    q.placeholder='Search assets: Asset No., serial, service tag, model or PO';q.value=current==='assets'?new URLSearchParams(location.search).get('q')||'':'';q.setAttribute('aria-label','Search assets');
    f.append(svg('search'),q,el('span','Enter','x-kbd'));f.onsubmit=e=>{e.preventDefault();location.assign('/?q='+encodeURIComponent(q.value.trim()));};top.append(f);}
  const link=(n,key,text,href)=>{const a=el('a',null,'x-nav'+(key===current?' on':''));a.href=href;a.append(svg(key),el('span',text));if(key===current)a.setAttribute('aria-current','page');n.append(a);return a;};
  if(m.can.viewAssets)link(nav,'assets','Assets','/');
  if(m.can.handover)link(nav,'handover','Handover','/handover.html');
  if(m.can.poView)link(nav,'pos','Purchase orders','/pos.html');
  if(m.can.ownBackup||m.can.backupsView)link(nav,'backups','My Backup','/backups.html');
  if(m.can.admin){adminH.hidden=false;link(adminNav,'admin','Administration Center','/admin.html');const areas=$('side');if(areas&&current==='admin'){areas.className='it-subnav';adminNav.append(areas);}}
  const roles=[...new Set(m.roles.map(r=>r.label))].join(', ')||'Employee';
  const who=el('div');who.append(el('b',m.name),el('small',roles+(m.viewsAll?' · All regions':m.regions.length?' · '+m.regions.join(', '):'')));meBox.append(el('span',initials(m.name),'x-avatar'),who);
  return m;
}

/** A small form dialog: fields = [{key,label,type,options,value,required,wide,hint,max}]. */
export function edit(title,fields,save,{hint,button,reason=true,done}={}){
  $('dialog-title').textContent=title;const body=$('dialog-body');body.replaceChildren();$('dialog-reason').value='';$('reason-label').hidden=!reason;
  if(hint)body.append(el('p',hint,'hint wide'));$('dialog-save').textContent=button||'Save';$('dialog-save').className=button==='Remove'?'danger':'primary';
  const inputs={};
  for(const f of fields){
    const l=el('label',f.label+(f.required?' *':''));if(f.wide)l.className='wide';let input;
    if(f.type==='select'){input=document.createElement('select');for(const [v,t] of f.options)input.append(option(v,t));}
    else if(f.type==='person'){input=document.createElement('input');input.setAttribute('list','people');input.placeholder='Type a name or account';input.autocomplete='off';
      // Search the employees as the person types (the list shows the best 30 matches).
      let timer;input.addEventListener('input',()=>{clearTimeout(timer);const q=input.value.trim();if(q.includes('('))return;timer=setTimeout(()=>people(q).catch(()=>{}),250);});}
    else if(f.type==='textarea'){input=document.createElement('textarea');if(f.max)input.maxLength=f.max;}
    else if(f.type==='file'){input=document.createElement('input');input.type='file';input.accept=f.accept||'application/pdf,image/png,image/jpeg';}
    else{input=document.createElement('input');input.type=f.type||'text';if(f.max)input.maxLength=f.max;if(f.type==='number')input.step='0.01';}
    if(f.type==='checkbox')input.checked=!!f.value;else if(f.value!==undefined&&f.value!==null)input.value=String(f.value);
    if(f.required&&f.type!=='checkbox')input.required=true;
    if(f.type==='checkbox'){l.className='inline'+(f.wide?' wide':'');l.textContent='';l.append(input,f.label);}else l.append(input);
    if(f.hint)l.append(el('span',f.hint,'hint'));
    inputs[f.key]=input;body.append(l);
  }
  for(const f of fields)if(f.onchange){inputs[f.key].addEventListener('change',()=>f.onchange(inputs));f.onchange(inputs);}
  const read=()=>{const out={};for(const f of fields){const i=inputs[f.key];
    if(f.type==='checkbox')out[f.key]=i.checked;else if(f.type==='number')out[f.key]=i.value===''?null:Number(i.value);
    else if(f.type==='file')out[f.key]=i.files[0]||null;
    else if(f.type==='person'){const p=(window.itamPeople||[]).find(s=>s.label===i.value.trim());out[f.key]=p?p.id:null;if(i.value.trim()&&!p)throw Error(`${f.label}: choose a person from the list.`);}
    else out[f.key]=i.value===''?null:i.value;}return out;};
  $('dialog-form').onsubmit=async e=>{e.preventDefault();try{await save(read(),$('dialog-reason').value);$('dialog').close();if(done)await done();}catch(err){alert(err.message);}};
  $('dialog-cancel').onclick=()=>$('dialog').close();
  $('dialog').showModal();
}
/** Fills the people list used by person fields, from the employee search. */
export async function people(q){const list=await api('/employees?q='+encodeURIComponent(q||''));window.itamPeople=list.map(p=>({id:p.id,label:`${p.name} (${p.account})`}));
  let dl=$('people');if(!dl){dl=el('datalist');dl.id='people';document.body.append(dl);}dl.replaceChildren(...window.itamPeople.map(p=>option(p.label,'')));}

export function table(head,list,cells,actions,{empty='Nothing here yet.',onRow}={}){
  const wrap=el('div',null,'table-wrap');const t=el('table');const th=el('thead');const hr=el('tr');for(const h of [...head,...(actions?['']:[])])hr.append(el('th',h));th.append(hr);
  const tb=el('tbody');for(const x of list){const tr=el('tr');for(const c of cells(x))tr.append(c instanceof Node?(()=>{const td=el('td');td.append(c);return td;})():el('td',c??''));
    if(actions){const td=el('td',null,'act');for(const [t,fn] of actions(x)){const b=el('button',t,'link');b.type='button';b.onclick=e=>{e.stopPropagation();fn();};td.append(b,' ');}tr.append(td);}
    if(onRow){tr.className='clickable';tr.tabIndex=0;tr.onclick=()=>onRow(x);tr.onkeydown=e=>{if(e.key==='Enter')onRow(x);};}tb.append(tr);}
  if(!list.length){const tr=el('tr');const td=el('td',empty,'empty');td.colSpan=head.length+(actions?1:0);tr.append(td);tb.append(tr);}
  t.append(th,tb);wrap.append(t);return wrap;
}
export function card(title,hint,...content){const c=el('section',null,'card');if(title)c.append(el('h2',title));if(hint)c.append(el('p',hint,'hint'));c.append(...content.filter(Boolean));return c;}
export function buttons(...list){const a=el('div',null,'actions');for(const b of list){if(!b)continue;const [text,fn,cls]=b;const x=el('button',text,cls||'');x.type='button';x.onclick=fn;a.append(x);}return a;}
export function dl(pairs){const l=el('dl',null,'kv');for(const [k,v] of pairs){if(v===null||v===undefined||v==='')continue;l.append(el('dt',k));const dd=el('dd');dd.append(v instanceof Node?v:document.createTextNode(String(v)));l.append(dd);}return l;}
