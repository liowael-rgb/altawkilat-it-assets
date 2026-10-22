// Dashboard (plan v1.9 step 6): counts and charts of the assets the signed-in person can see, and what waits for them.
// Charts are SVG drawn with attributes only (the site's security policy allows no inline styles). Tiles and bars open the
// matching list on the Assets page.
import {$,el,api,shell,fail,card,money,svg} from '/itam.js';
const NS='http://www.w3.org/2000/svg';
const s=(tag,attrs,text)=>{const e=document.createElementNS(NS,tag);for(const [k,v] of Object.entries(attrs||{}))e.setAttribute(k,String(v));if(text!==undefined)e.textContent=String(text);return e;};
const go=href=>()=>location.assign(href);

// Horizontal bars: one row per item, one or two series (In use / In store), the total at the end; a click opens the list.
function bars(items,{series,href}){
  const rowH=32,labelW=170,width=540,barW=width-labelW-50,max=Math.max(1,...items.map(i=>i.total??i.count));
  const g=s('svg',{viewBox:`0 0 ${width} ${Math.max(1,items.length)*rowH+8}`,class:'x-chart',role:'img'});
  items.forEach((i,n)=>{const y=n*rowH+6;const row=s('g',{class:href?'x-chart-row link':'x-chart-row'});
    row.append(s('text',{x:0,y:y+17,class:'x-chart-label'},i.label.length>24?i.label.slice(0,23)+'…':i.label));if(!href)row.append(s('title',{},i.label));
    let x=labelW;
    for(const [key,cls] of series){const v=i[key]||0;const w=v/max*barW;if(w>0){row.append(s('rect',{x,y:y+4,width:Math.max(2,w),height:18,rx:4,class:cls}));x+=w;}}
    row.append(s('text',{x:x+8,y:y+18,class:'x-chart-value'},i.total??i.count));
    if(href){row.addEventListener('click',go(href(i)));row.append(s('title',{},`${i.label}: open the list`));}
    g.append(row);});
  return g;
}
// Columns: deliveries per month.
function columns(items){
  const width=540,height=200,base=170,max=Math.max(1,...items.map(i=>i.count)),slot=width/items.length;
  const g=s('svg',{viewBox:`0 0 ${width} ${height}`,class:'x-chart',role:'img'});
  items.forEach((i,n)=>{const h=i.count/max*130;const x=n*slot+slot*0.25;
    g.append(s('rect',{x,y:base-h,width:slot*0.5,height:Math.max(h,1),rx:5,class:'x-bar-a'}),s('text',{x:x+slot*0.25,y:base-h-8,class:'x-chart-value mid'},i.count),
      s('text',{x:x+slot*0.25,y:base+20,class:'x-chart-label mid'},i.label));});
  return g;
}
function legend(list){const l=el('div',null,'x-legend');for(const [t,cls] of list){const k=el('span');k.append(el('i',null,cls),t);l.append(k);}return l;}

async function load(){
  const d=await api('/dashboard');const t=d.tiles;
  const tile=(n,label,href,cls)=>{const a=el('a',null,'x-dtile'+(cls?' '+cls:''));a.href=href;a.append(el('b',String(n)),el('span',label));return a;};
  const tiles=el('div',null,'x-dtiles');
  tiles.append(tile(t.assets,'Assets in service','/'),tile(t.inUse,'In use with employees','/?status=InUse'),tile(t.inStore,'In store','/?status=InStore'),
    tile(t.inRepair,'In repair','/?status=InRepair'),tile(t.inTransit,'In transit','/?status=InTransit'),tile(t.pendingDisposal,'Pending disposal','/?status=PendingDisposal',t.pendingDisposal?'warn':''),
    tile(t.lost,'Lost','/?status=Lost',t.lost?'bad':''),tile(t.unsigned,'Delivery notes not signed','/handover.html',t.unsigned?'warn':''),tile(t.alerts,'Alerts for you','/alerts.html',t.alerts?'bad':''));
  const two=el('div',null,'x-dgrid');
  two.append(card('Assets by type','In use and in store; a bar opens the list.',legend([['In use','x-bar-a'],['In store','x-bar-b']]),bars(d.byType,{series:[['inUse','x-bar-a'],['inStore','x-bar-b']],href:i=>'/?type='+encodeURIComponent(i.code)})),
    card('Assets by region',null,legend([['In use','x-bar-a'],['In store','x-bar-b']]),bars(d.byRegion,{series:[['inUse','x-bar-a'],['inStore','x-bar-b']],href:i=>'/?region='+encodeURIComponent(i.label)})),
    card('All assets by status','Including disposed and sold.',bars(d.byStatus,{series:[['count','x-bar-a']],href:i=>'/?status='+i.code})),
    card('Stock in store','Where the In Store assets are kept.',bars(d.stock,{series:[['count','x-bar-b']]})),
    card('Asset ageing','From the purchase date.',bars(d.ageing,{series:[['count','x-bar-c']]})),
    card('Deliveries per month','Delivery notes issued in the last six months.',columns(d.deliveries)));
  const money2=el('div',null,'x-dtiles small');const mo=d.money;
  if(mo.maintenance!==null)money2.append(tile(money(mo.maintenance),`Maintenance cost ${mo.year}`,'/reports.html?r=maintenance'));
  if(mo.sales!==null)money2.append(tile(money(mo.sales),`Sales ${mo.year}`,'/reports.html?r=sales'));
  if(mo.inkThisMonth!==null)money2.append(tile(mo.inkThisMonth,'Toner cartridges delivered this month','/reports.html?r=ink'));
  const wait=el('div',null,'x-alerts two');
  for(const a of d.alerts){const x=el('a',null,'x-alert'+(a.urgent?' urgent':''));x.href=a.href;const tx=el('div',null,'x-alert-text');tx.append(el('b',a.label));x.append(el('span',String(a.count),'x-alert-count'),tx,svg('chev'));wait.append(x);}
  $('view').replaceChildren(tiles,...(money2.children.length?[money2]:[]),...(d.alerts.length?[card('Waiting for you','The most urgent items from your alerts.',wait)]:[]),two);
  $('head-actions').replaceChildren((()=>{const a=el('a');a.href='/reports.html';const b=el('button',null,'primary');b.append(svg('doc'),'Reports');a.append(b);return a;})());
}
try{await shell('dashboard');await load();}catch(e){fail(e);}
