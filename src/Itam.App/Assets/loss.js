// Loss cases LC-n (forms 22 to 25, plan v1.9): the list, and one case (?id=) with what the signed-in person may do now.
// Reporting never receives the asset or clears the employee; only the IT Manager's decision (and, for a settlement, its
// evidence) does. The write-off is decided separately and never clears the employee.
import {$,el,api,shell,fail,message,date,when,money,card,table,edit,form,dl} from '/itam.js';
import {reportLoss,btn,two,assetLink,fileLink,seg,today} from '/eol.js';
const q=new URLSearchParams(location.search);const id=Number(q.get('id'))||null;
let m,filter=q.get('filter')||'open';
const OPEN=['Reported','UnderInvestigation','AwaitingDecision','AwaitingAction','RecoveredReview'];
const FILTERS=[['open','Open'],['Reported','Reported'],['UnderInvestigation','Under investigation'],['AwaitingDecision','Awaiting decision'],['AwaitingAction','Awaiting settlement'],['closed','Closed'],['all','All']];
const stTag=c=>el('span',c.statusLabel,'tag '+(c.status==='ClosedByDecision'||c.status==='Recovered'?'ok':c.status==='AwaitingDecision'||c.status==='Reported'?'warn':''));
const respTag=c=>el('span',c.responsibility,'tag '+(c.responsibilityCode==='Cleared'?'ok':'warn'));
const match=(c,k)=>k==='all'||(k==='open'?OPEN.includes(c.status):k==='closed'?!OPEN.includes(c.status):c.status===k);

async function list(note){
  const d=await api('/loss');
  $('head-actions').replaceChildren(...(d.canReport?[btn('Report a loss',()=>reportLoss(''),'primary','plus')]:[]));
  const rows=d.cases.filter(c=>match(c,filter));
  $('view').replaceChildren(card('Loss cases','Open a case to see the investigation, the decision and the write-off.',
    seg(FILTERS,filter,k=>d.cases.filter(c=>match(c,k)).length,k=>{filter=k;history.replaceState(null,'','?filter='+k);list();}),
    table(['Case','Asset','Held by','Reported','Status','Responsibility','Write-off'],rows,
      c=>[el('span',c.caseNo,'mono'),two(assetLink(c.assetNo,c.assetId),[c.type,c.model].filter(Boolean).join(' · ')),c.custodian||'In stock',two(date(c.discoveredOn),`by ${c.reportedBy||''}`),stTag(c),respTag(c),c.writeOff],
      null,{empty:'No loss cases here.',onRow:c=>location.assign('/loss.html?id='+c.id)})));
  if(note)message(note);
}

async function detail(note){
  const d=await api('/loss/'+id);const c=d.case,can=d.can;
  $('title').textContent=c.caseNo;$('subtitle').textContent=`${c.assetNo} · ${c.type}${c.model?' · '+c.model:''} · ${c.statusLabel}`;
  const acts=[];
  if(can.assign)acts.push(btn(c.investigator?'Change investigator':'Assign investigator',assign,'primary'));
  if(can.findings)acts.push(btn('Record the findings',findings,'primary'));
  if(can.decide)acts.push(btn('Record the decision',decide,'primary'));
  if(can.settle)acts.push(btn('Record the settlement',settle,'primary'));
  if(can.decideWriteOff)acts.push(btn('Approve write-off',()=>writeOffDecision(true),'primary'),btn('Reject write-off',()=>writeOffDecision(false)));
  if(can.review)acts.push(btn('Review and return to stock',review,'primary'));
  if(can.security)acts.push(btn('Security action',security));
  if(can.writeOff)acts.push(btn('Ask for write-off',writeOff));
  if(can.recover)acts.push(btn('Asset recovered',recover));
  acts.push(btn('All loss cases',()=>location.assign('/loss.html')));
  $('head-actions').replaceChildren(...acts);
  const f=(bit,which,text)=>c.files&bit?fileLink(text,`/api/loss/${id}/file/${which}`):null;
  const parts=[card('Report',null,dl([['Asset',assetLink(c.assetNo,c.assetId)],['Serial / service tag',c.serial],['Held by',c.custodian?`${c.custodian}${c.deliveryNo?' · '+c.deliveryNo:''}`:'In stock (no custody)'],
      ['Region',c.region],['Discovered on',date(c.discoveredOn)],['Last known location',c.lastLocation],['What happened',c.circumstances],['Reported',`${when(c.reportedAt)} by ${c.reportedBy||''}`],
      ['Transfer',c.movementNo],['Evidence',f(1,'report','Open the evidence')]])),
    card('Outcome','Three separate results: the case, the employee\'s responsibility, and the financial write-off.',dl([['Case',stTag(c)],['Responsibility',respTag(c)],['Write-off',c.writeOff],
      ['Decision',c.decision?`${c.decision}${c.amount?' · '+money(c.amount):''}`:null],['Rationale',c.decisionNote],['Decided',c.decidedAt?`${when(c.decidedAt)} by ${c.decidedBy||''}`:null],
      ['Signed decision',f(4,'decision','Open the signed decision')],['Settlement',c.settlementNote],['Settlement evidence',f(8,'settlement','Open the evidence')],
      ['Write-off reason',c.writeOffReason],['Write-off note',c.writeOffNote],['Recovered on',c.recoveredOn?date(c.recoveredOn):null],['Recovery',c.recoveryNote]]))];
  if(d.full)parts.splice(1,0,card('Investigation','The recommendation does not clear the employee; the decision does.',dl([['Investigator',c.investigator||'Not assigned yet'],['Findings',c.findings],['Recommendation',c.recommendation],
    ['Investigation file',f(2,'findings','Open the file')],['Security actions',c.security||'None recorded yet']])));
  $('view').replaceChildren(...parts);
  if(note)message(note);
  const step=(url,json,ok)=>async x=>{await api(url,{method:'POST',json:json(x)});await detail(ok);};
  const filed=(url,ok)=>async x=>{const {file,...data}=x;await api(url,{method:'POST',body:form(data,file)});await detail(ok);};
  function assign(){edit(`Investigator for ${c.caseNo}`,[{key:'investigatorId',label:'Investigator',type:'person',required:true,hint:'Not the employee who held the asset. HR, Security and Finance join as company policy requires.'}],
    step(`/loss/${id}/assign`,x=>x,'Investigator assigned.'),{reason:false,button:'Assign'});}
  function security(){edit('Security action',[{key:'text',label:'What was done (accounts, device management, licenses, data)',type:'textarea',required:true,max:1000,wide:true}],
    step(`/loss/${id}/security`,x=>x,'Security action recorded.'),{reason:false});}
  function findings(){edit('Investigation findings',[{key:'findings',label:'Findings (statements, evidence, what was found)',type:'textarea',required:true,max:4000,wide:true},
    {key:'recommendation',label:'Recommendation',type:'textarea',required:true,max:2000,wide:true},{key:'file',label:'Investigation report',type:'file',wide:true}],
    filed(`/loss/${id}/findings`,'Findings recorded; the case waits for the decision.'),{reason:false,button:'Send for decision'});}
  function decide(){edit(`Decision on ${c.caseNo}`,[{key:'decision',label:'Decision',type:'select',required:true,options:[['','Choose'],['NotLiable','Not liable: clear the employee'],['Settlement','Settlement required']],
      onchange:i=>{i.amount.closest('label').hidden=i.decision.value!=='Settlement';i.amount.required=i.decision.value==='Settlement';}},
    {key:'amount',label:'Settlement amount (SAR)',type:'number'},{key:'note',label:'Rationale',type:'textarea',required:true,max:2000,wide:true},
    {key:'file',label:'Signed decision',type:'file',required:true,wide:true}],
    filed(`/loss/${id}/decision`,'Decision recorded.'),{reason:false,button:'Record the decision',hint:c.recommendation?'Recommendation: '+c.recommendation:null});}
  function settle(){edit('Settlement',[{key:'text',label:'Settlement details',type:'textarea',required:true,max:1000,wide:true},{key:'file',label:'Evidence (receipt from Accounting)',type:'file',required:true,wide:true}],
    filed(`/loss/${id}/settlement`,'Settlement recorded: responsibility cleared and the custody closed.'),{reason:false,hint:c.amount?`Amount decided: ${money(c.amount)}.`:null});}
  function writeOff(){edit('Ask for a write-off',[{key:'text',label:'Reason',type:'textarea',required:true,max:1000,wide:true}],step(`/loss/${id}/writeoff`,x=>x,'Write-off sent for approval.'),
    {reason:false,button:'Send for approval',hint:'The write-off is financial only: it does not clear the employee.'});}
  function writeOffDecision(approve){edit(approve?'Approve the write-off':'Reject the write-off',[{key:'reason',label:approve?'Note':'Reason for the rejection',type:'textarea',required:!approve,max:1000,wide:true}],
    step(`/loss/${id}/writeoff/decision`,x=>({approve,reason:x.reason}),approve?'Write-off approved.':'Write-off rejected.'),{reason:false,button:approve?'Approve':'Reject',hint:c.writeOffReason});}
  function recover(){const locs=m.lists.locations.filter(l=>l.active);
    edit('Asset recovered',[{key:'recoveredOn',label:'Received by IT on',type:'date',required:true,value:today()},
      {key:'locationId',label:'Received into',type:'select',required:true,options:[['','Choose'],...locs.map(l=>[l.id,`${l.name} (${l.region})`])]},
      {key:'condition',label:'Condition',type:'select',required:true,options:[['','Choose'],['Working','Working'],['Damaged','Damaged'],['Faulty','Faulty']]},
      {key:'note',label:'Where and how it was found',type:'textarea',required:true,max:1000,wide:true}],
      step(`/loss/${id}/recovery`,x=>({...x,locationId:Number(x.locationId)}),'Recovery recorded: the asset stays Lost until the review.'),
      {reason:false,hint:'Only when IT physically has the asset. It returns to stock after the review.'});}
  function review(){edit('Review the recovered asset',[{key:'text',label:'Review (condition, earlier settlement or write-off)',type:'textarea',required:true,max:1000,wide:true}],
    step(`/loss/${id}/review`,x=>x,'Reviewed: the asset is back In Store.'),{reason:false,button:'Return to stock'});}
}

try{m=await shell('loss');if(id)await detail(q.get('created')?`${q.get('created')} reported: the asset is Lost and the case waits for an investigator.`:null);else await list();}catch(e){fail(e);}
