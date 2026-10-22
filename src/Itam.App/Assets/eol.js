// End of life actions shared by the asset page and the end-of-life pages (plan v1.9 step 5): report a loss, ask for a
// disposal, record a sale. The server checks every rule; these are the forms.
import {el,api,edit,form,svg} from '/itam.js';
export const today=()=>new Date(Date.now()+3*3600e3).toISOString().slice(0,10);
export const btn=(text,fn,cls,icon)=>{const b=el('button',null,cls||'');b.type='button';if(icon)b.append(svg(icon));b.append(text);b.onclick=fn;return b;};
export const two=(a,b)=>{const s=el('span');s.append(typeof a==='string'||a===null||a===undefined?el('span',a??''):a);if(b)s.append(el('small',b,'x-sub'));return s;};
export const assetLink=(no,id)=>{if(!id)return el('span',no,'mono nowrap');const a=el('a',no,'mono nowrap');a.href='/asset.html?id='+id;return a;};
export const fileLink=(text,href)=>{const a=el('a',text);a.href=href;a.target='_blank';a.rel='noopener';return a;};
export const seg=(filters,current,count,pick)=>{const s=el('div',null,'x-seg');
  for(const [k,t] of filters){const b=el('button',t);b.append(el('span',String(count(k)),'x-segc'));b.type='button';b.setAttribute('aria-pressed',String(k===current));b.onclick=()=>pick(k);s.append(b);}
  const bar=el('div',null,'x-filterbar');bar.append(s);return bar;};

export function reportLoss(assetNo,done){
  edit('Report a lost or stolen asset',[
    {key:'assetNo',label:'Asset No.',required:true,max:20,value:assetNo||'',hint:'The asset becomes Lost. The custody stays open: reporting never receives the asset back or clears the employee.'},
    {key:'discoveredOn',label:'Discovered on',type:'date',required:true,value:today()},
    {key:'lastLocation',label:'Last known location',required:true,max:300},
    {key:'circumstances',label:'What happened',type:'textarea',required:true,max:2000,wide:true},
    {key:'file',label:'Supporting evidence (police report, photo)',type:'file',wide:true}],
    async x=>{const {file,...data}=x;const r=await api('/loss',{method:'POST',body:form(data,file)});location.assign('/loss.html?id='+r.id+'&created='+encodeURIComponent(r.caseNo));},
    {reason:false,button:'Report the loss',done});
}
export function requestDisposal(assetNo,done){
  edit('Ask for a disposal',[
    {key:'assetNo',label:'Asset No.',required:true,max:20,value:assetNo||'',hint:'Only an asset In Store (received back) is disposed. It becomes Pending disposal approval until the IT Manager decides.'},
    {key:'reason',label:'Reason',type:'textarea',required:true,max:1000,wide:true},
    {key:'condition',label:'Current condition',required:true,max:500,wide:true}],
    async x=>{const r=await api('/disposals',{method:'POST',json:x});location.assign('/disposals.html?created='+encodeURIComponent(r.disposalNo));},
    {reason:false,button:'Send for approval',done});
}
export function recordSale(assetNo,holder,done){
  edit('Record a sale',[
    {key:'assetNo',label:'Asset No.',required:true,max:20,value:assetNo||'',hint:holder?`In the custody of ${holder}: only the holder buys it (the custody closes with the sale, no receipt note).`:'An asset In Use (bought by its holder) or In Store.'},
    {key:'buyerId',label:'Buyer (employee)',type:'person',required:true},
    {key:'price',label:'Price (SAR)',type:'number',required:true},
    {key:'soldOn',label:'Sale date',type:'date',required:true,value:today()},
    {key:'notes',label:'Notes',type:'textarea',max:1000,wide:true},
    {key:'file',label:'Proof of payment (receipt from Accounting)',type:'file',required:true,wide:true}],
    async x=>{const {file,...data}=x;const r=await api('/sales',{method:'POST',body:form(data,file)});location.assign('/sales.html?created='+encodeURIComponent(r.saleNo));},
    {reason:false,button:'Record the sale',done});
}
