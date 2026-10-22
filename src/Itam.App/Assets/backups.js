import {$,el,api,shell,fail,message,date,card,table,buttons,edit,people,tags} from '/itam.js';
let d;
async function load(msg){
  d=await api('/backups');
  const mine=d.items.filter(x=>x.person===d.me),covering=d.items.filter(x=>x.backup===d.me&&x.status!=='Cancelled'&&x.status!=='Ended');
  const st=s=>tags([s],'tag'+(s==='Active'?' ok':s==='Upcoming'?' warn':' off'));
  const set=(forOthers)=>edit(forOthers?'Set a backup for someone':'Set my backup',[...(forOthers?[{key:'person',label:'Person on leave',type:'person',required:true}]:[]),
      {key:'backup',label:'Backup',type:'person',required:true,hint:'An active employee who can take your decisions while you are away.'},
      {key:'from',label:'Leave from',type:'date',required:true},{key:'to',label:'Leave to',type:'date',required:true},{key:'note',label:'Note',wide:true}],
    x=>api('/backups',{method:'POST',json:{personId:x.person||null,backupId:x.backup,from:x.from,to:x.to,note:x.note}}),{reason:false,done:()=>load('Backup set.')});
  const cancel=x=>edit(`Cancel the backup ${x.backupName} (${date(x.from)}–${date(x.to)})?`,[],()=>api(`/backups/${x.id}/cancel`,{method:'POST'}),{reason:false,button:'Remove',done:()=>load('Cancelled.')});
  $('view').replaceChildren(...[
    card('My backups','During your leave your backup receives your approvals and tasks, and acts for you; you stay in copy.',
      table(['Backup','From','To','Status','Note'],mine,x=>[x.backupName,date(x.from),date(x.to),st(x.status),x.note||''],x=>x.status==='Active'||x.status==='Upcoming'?[['Cancel',()=>cancel(x)]]:[],{empty:'No backup set.'}),
      buttons(['Set my backup',async()=>{await people('');set(false);},'primary'])),
    covering.length?card('I am the backup for',null,table(['Person','From','To','Status'],covering,x=>[x.personName,date(x.from),date(x.to),st(x.status)])):null,
    d.all?card('All backups','The IT Helpdesk Team Leader, the System Administrator and the IT Manager see every backup.',table(['Person','Backup','From','To','Status','Set by'],d.items,x=>[x.personName,x.backupName,date(x.from),date(x.to),st(x.status),x.setByName||'']),
      d.canSetForOthers?buttons(['Set a backup for someone',async()=>{await people('');set(true);}]):null):null].filter(Boolean));
  if(msg)message(msg);
}
try{await shell('backups');await load();}catch(e){fail(e);}
