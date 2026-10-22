// Alerts (owner, 22 October 2026): every role sees what is waiting for them, grouped by module, with a count, the first
// references and a link to the page where the work is done. Urgent items (decisions with a deadline, overdue items) first.
import {$,el,api,shell,fail,card,svg} from '/itam.js';
async function load(){
  const d=await api('/alerts');
  $('subtitle').textContent=d.total?`${d.total} item${d.total===1?'':'s'} waiting for you. Open one to do the work on its page.`:'Nothing is waiting for you.';
  if(!d.groups.length){$('view').replaceChildren(card('All clear','When something needs your decision or your action, it appears here and on the menu.',el('p','Nothing is waiting for you.','empty')));return;}
  $('view').replaceChildren(...d.groups.map(g=>{
    const items=[...g.items].sort((a,b)=>(b.urgent-a.urgent)||(b.count-a.count));
    const box=el('div',null,'x-alerts');
    for(const i of items){
      const a=el('a',null,'x-alert'+(i.urgent?' urgent':''));a.href=i.href;
      const text=el('div',null,'x-alert-text');text.append(el('b',i.label),el('span',i.hint,'x-sub'));
      if(i.refs.length){const r=el('div',null,'x-alert-refs');for(const n of i.refs)r.append(el('span',n,'tag mono'));if(i.count>i.refs.length)r.append(el('span',`+${i.count-i.refs.length} more`,'x-sub'));text.append(r);}
      a.append(el('span',String(i.count),'x-alert-count'),text,svg('chev'));box.append(a);
    }
    return card(g.name,null,box);
  }));
}
try{await shell('alerts');await load();}catch(e){fail(e);}
