// The handover part of the asset page (build step 2): deliver to an employee, print the delivery note, upload the copy the
// employee signed on paper himself, receive the asset back (receipt note -R), technical assessment (TA-), and the label.
import {el,api,fail,message,date,when,edit,people,form,svg} from '/itam.js';

const state=d=>d.receivedAt?['Returned','off']:d.hasSigned?['Signed','ok']:d.overdue?['Waiting for signed copy (overdue)','bad']:['Waiting for signed copy','warn'];
const tag=([t,c])=>el('span',t,'tag '+c);
const open=href=>window.open(href,'_blank');
function btn(text,icon,fn,cls){const b=el('button',null,cls||'');b.type='button';if(icon)b.append(svg(icon));b.append(text);b.onclick=fn;return b;}

/** Returns {card, side}: the Handover card for the left column and the Label card for the right column. */
export async function custody(m,asset,reload){
  const h=await api(`/assets/${asset.id}/handover`);
  const card=el('section',null,'card');const head=el('h2');head.append('Handover');card.append(head);
  card.append(el('p',asset.status==='InStore'?'In IT stock. Deliver it to an employee: print the delivery note, the employee signs it on paper himself, then upload the signed copy.'
    :'The employee signs the printed delivery note by hand himself; IT uploads the scan. Nobody signs or accepts for him.','hint'));
  const acts=el('div',null,'actions');
  if(h.canDeliver)acts.append(btn('Deliver to an employee','user',()=>deliver(m,asset,h,reload),'primary'));
  if(acts.children.length)card.append(acts);
  if(!h.deliveries.length)card.append(el('p','Never delivered.','hint x-gap'));
  for(const d of h.deliveries)card.append(row(m,asset,h,d,reload));
  // Label (form 35): PDF at real size for the Zebra Windows driver, or the ZPL file.
  let side=null;
  if(h.canLabel){side=el('div',null,'x-card');const hd=el('div',null,'x-card-h');hd.append(svg('tag'),el('span','Print the label'));side.append(hd);
    const b=el('div',null,'x-side-actions');b.append(el('p','Zebra 63.5 × 38.1 mm. The QR code opens this asset.','hint'),
      btn('Print label (PDF)','doc',()=>open(`/api/assets/${asset.id}/label.pdf`),'primary'),btn('Zebra file (ZPL)','tag',()=>{location.href=`/api/assets/${asset.id}/label.zpl`;}));
    side.append(b);}
  const holder=h.deliveries.find(d=>!d.receivedAt)?.employee||null;
  return {card,side,holder};
}

function row(m,asset,h,d,reload){
  const box=el('div',null,'x-dn'+(d.receivedAt?' closed':''));
  const top=el('div',null,'x-dn-top');const no=el('b',d.no,'mono');top.append(no,tag(state(d)));box.append(top);
  const facts=el('dl',null,'kv');
  const add=(k,v)=>{if(v===null||v===undefined||v==='')return;facts.append(el('dt',k));const dd=el('dd');dd.append(v instanceof Node?v:document.createTextNode(String(v)));facts.append(dd);};
  add('Employee',d.employee);add('Delivered on',date(d.deliveredOn));add('Owner type',d.ownerType);add('Computer name',d.computerName);
  add('Issued by',`${d.issuedBy||''} · ${when(d.issuedAt)}`);add('Signed copy',d.hasSigned?`Uploaded ${when(d.signedAt)} by ${d.signedBy||''}`:null);add('Notes',d.notes);
  if(d.receivedAt){add('Receipt note',d.receiptNo);add('Received back',`${when(d.receivedAt)} by ${d.receivedBy||''}`);add('Condition',d.condition);add('Received at',d.receivedAtLocation);add('Receipt notes',d.receiptNotes);}
  if(d.assessment){add('Technical assessment',`${d.assessment.no}: ${d.assessment.recommendation}`);add('Assessed',`${when(d.assessment.at)} by ${d.assessment.by||''}`);}
  box.append(facts);
  const acts=el('div',null,'actions');
  acts.append(btn('Delivery note','doc',()=>open(`/api/deliveries/${d.id}/note.pdf`)));
  if(d.hasSigned)acts.append(btn('Signed copy','doc',()=>open(`/api/deliveries/${d.id}/files/signed`)));
  else if(h.canHandle)acts.append(btn('Upload signed copy','upload',()=>signed(d,reload),'primary'));
  if(!d.receivedAt&&h.canHandle)acts.append(btn('Receive back','back',()=>receive(m,asset,h,d,reload)));
  if(d.receivedAt)acts.append(btn('Receipt note','doc',()=>open(`/api/deliveries/${d.id}/receipt.pdf`)));
  if(d.receivedAt&&!d.assessment&&h.canHandle)acts.append(btn('Technical assessment','edit',()=>assess(h,d,reload)));
  if(d.assessment){acts.append(btn(d.assessment.no,'doc',()=>open(`/api/deliveries/${d.id}/assessment.pdf`)));if(d.assessment.evidence)acts.append(btn('Evidence','doc',()=>open(`/api/deliveries/${d.id}/files/evidence`)));}
  box.append(acts);return box;
}

async function deliver(m,asset,h,reload){
  await people('');
  const today=new Date(Date.now()+3*3600e3).toISOString().slice(0,10);
  edit(`Deliver ${asset.assetNo} to an employee`,[
    {key:'employee',label:'Employee',type:'person',required:true,hint:'Active employees from Active Directory. Type part of the name, then choose.'},
    {key:'on',label:'Delivery date',type:'date',value:today,required:true},
    {key:'owner',label:'Owner type',type:'select',options:h.ownerTypes.map(o=>[o,o]),value:'Personal',hint:'Other = a computer that is not a member of the domain.'},
    ...(h.needsComputerName?[{key:'computer',label:'Computer name',required:true,max:15,hint:'Up to 15 letters, numbers or dashes; not used on another open delivery.'}]:[]),
    {key:'notes',label:'Notes',type:'textarea',wide:true,max:1000}],
    async x=>{const r=await api(`/assets/${asset.id}/deliver`,{method:'POST',json:{employeeId:x.employee,deliveredOn:x.on,ownerType:x.owner,computerName:x.computer||null,notes:x.notes}});
      window.open(`/api/deliveries/${r.id}/note.pdf`,'_blank');reload(`Delivered on ${r.deliveryNo}. Print the delivery note for the employee to sign, then upload the signed copy.`);},
    {reason:false,button:'Deliver and print the note',hint:'The asset goes In Use with the employee. The delivery note opens to print.'});
}

function signed(d,reload){
  edit(`Upload the signed copy of ${d.no}`,[{key:'file',label:'Signed delivery note (scan or photo)',type:'file',required:true,wide:true,hint:'PDF, JPEG or PNG up to 10 MB. Signed by the employee himself. Kept unchanged once uploaded.'}],
    async x=>{if(!x.file)throw Error('Choose the scan of the signed delivery note.');await api(`/deliveries/${d.id}/signed`,{method:'POST',body:form({},x.file)});reload('Signed copy uploaded.');},{reason:false,button:'Upload'});
}

function receive(m,asset,h,d,reload){
  const onlySpecialist=m.roles.length>0&&m.roles.every(r=>r.code==='SupportSpecialist');
  const locs=m.lists.locations.filter(l=>l.active&&(!onlySpecialist||m.regions.includes(l.region)));
  edit(`Receive ${asset.assetNo} back from ${d.employee}`,[
    {key:'at',label:'Received on',type:'datetime-local',hint:'Empty = now.'},
    {key:'condition',label:'Condition',type:'select',options:[['','Choose'],...h.conditions.map(c=>[c.code,c.label])],required:true},
    {key:'location',label:'Kept at (IT warehouse or branch)',type:'select',options:[['','Choose'],...locs.map(l=>[l.id,`${l.region} · ${l.name}`])],required:true},
    {key:'notes',label:'Notes',type:'textarea',wide:true,max:1000}],
    async x=>{const r=await api(`/deliveries/${d.id}/return`,{method:'POST',json:{receivedAt:x.at?new Date(x.at).toISOString():null,condition:x.condition,locationId:Number(x.location)||null,notes:x.notes}});
      window.open(`/api/deliveries/${d.id}/receipt.pdf`,'_blank');reload(`Received back on ${r.receiptNo}. The asset is In Store.`);},
    {reason:false,button:'Receive back',hint:'Only the physical return of the device. A loss or a sale is not a receipt.'});
}

function assess(h,d,reload){
  edit(`Technical assessment for ${d.no}`,[
    {key:'review',label:'Technical review',type:'textarea',required:true,wide:true,max:4000},
    {key:'rec',label:'Recommendation',type:'select',options:[['','Choose'],...h.recommendations.map(r=>[r.code,r.label])],required:true,hint:'Dispose starts Pending disposal approval; it does not approve disposal.'},
    {key:'file',label:'Supporting evidence',type:'file',hint:'Optional: PDF, JPEG or PNG.'}],
    async x=>{const r=await api(`/deliveries/${d.id}/assessment`,{method:'POST',body:form({review:x.review,recommendation:x.rec},x.file)});
      window.open(`/api/deliveries/${d.id}/assessment.pdf`,'_blank');reload(`Technical assessment ${r.assessmentNo} recorded.`);},{reason:false,button:'Save assessment'});
}
