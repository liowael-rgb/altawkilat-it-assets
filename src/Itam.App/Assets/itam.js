// IT Assets shared page code: API calls with the anti-forgery token, the page frame (navy left pane, as the other
// company systems), the form dialog, tables and formats. English screens; no inline styles (CSP).
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

const svg=p=>{const s=document.createElementNS('http://www.w3.org/2000/svg','svg');s.setAttribute('viewBox','0 0 24 24');s.setAttribute('aria-hidden','true');s.innerHTML=p;return s;};
const ICONS={
  assets:'<rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/>',
  pos:'<path d="M6 2h9l5 5v15H6z"/><path d="M14 2v6h6M9 13h8M9 17h6"/>',
  backups:'<path d="M16 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2"/><circle cx="10" cy="7" r="4"/><path d="M20 8v6M17 11h6"/>',
  admin:'<rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><rect x="14" y="14" width="7" height="7" rx="1"/>'
};
/** The page frame: navy left pane with the logo and the pages the person may open; the page's <main> moves into it. */
export async function shell(current){
  const main=document.querySelector('main');
  const sh=el('div',null,'it-shell');const side=el('aside',null,'it-sidebar');side.id='it-sidebar';side.setAttribute('aria-label','IT Assets navigation');
  const brand=el('a',null,'it-brand');brand.href='/';const logo=document.createElement('img');logo.src='/company-logo.png';logo.alt='ALTAWKILAT';
  const name=el('span',null,'it-brand-name');name.append(el('strong','IT Assets'),el('small','IT asset management'));brand.append(logo,name);
  const nav=el('nav');nav.setAttribute('aria-label','Pages');const navLabel=el('p','Workspace','it-nav-label');
  const adminNav=el('nav');adminNav.setAttribute('aria-label','Administration');const adminLabel=el('p','Administration','it-nav-label');adminLabel.hidden=true;
  side.append(brand,navLabel,nav,adminLabel,adminNav,el('div','IT Assets · ALTAWKILAT','it-side-footer'));
  const right=el('div',null,'it-main');const top=el('header',null,'it-top');
  const toggle=el('button','Menu','it-toggle');toggle.type='button';toggle.setAttribute('aria-controls','it-sidebar');toggle.setAttribute('aria-expanded','false');
  const sys=el('div',null,'it-system');sys.append(el('strong','IT Assets'),el('span','IT asset management'));const who=el('div',null,'who');top.append(toggle,sys,who);
  right.append(top);if(main)right.append(main);sh.append(side,right);document.body.prepend(sh);
  const backdrop=el('button',null,'it-backdrop');backdrop.type='button';backdrop.hidden=true;backdrop.setAttribute('aria-label','Close navigation');document.body.append(backdrop);
  const close=()=>{sh.dataset.navOpen='false';toggle.setAttribute('aria-expanded','false');backdrop.hidden=true;};
  toggle.onclick=()=>{const open=sh.dataset.navOpen!=='true';sh.dataset.navOpen=String(open);toggle.setAttribute('aria-expanded',String(open));backdrop.hidden=!open;};
  backdrop.onclick=close;side.addEventListener('click',e=>{if(e.target.closest('a'))close();});
  let m;try{m=await me();}catch(e){who.append(el('span',e.message));throw e;}
  const link=(n,key,text,href)=>{const a=el('a');a.href=href;a.append(svg(ICONS[key]),el('span',text));if(key===current){a.className='is-current';a.setAttribute('aria-current','page');}n.append(a);};
  if(m.can.viewAssets)link(nav,'assets','Assets','/');
  if(m.can.poView)link(nav,'pos','Purchase orders','/pos.html');
  if(m.can.ownBackup||m.can.backupsView)link(nav,'backups','My Backup','/backups.html');
  if(m.can.admin){adminLabel.hidden=false;link(adminNav,'admin','Administration Center','/admin.html');const areas=$('side');if(areas&&current==='admin'){areas.className='it-subnav';adminNav.append(areas);}}
  who.append(el('div',m.name,'who-name'));
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
    else if(f.type==='textarea'){input=document.createElement('textarea');}
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
