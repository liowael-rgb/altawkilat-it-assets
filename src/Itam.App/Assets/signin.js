// The sign-in page: AD username and password, checked by the server with Active Directory. The password is sent once, over
// HTTPS, and never kept in the page.
import {$,svg} from './itam.js';
import {bilingual} from './ar.js';
bilingual();

$('mark').append(svg('laptop'));
const back=new URLSearchParams(location.search).get('return')||'/';
const show=(text)=>{const m=$('message');m.className=text?'msg error':'';m.textContent=text||'';};
let csrf='';
fetch('/api/signin',{cache:'no-store'}).then(r=>r.json()).then(x=>{csrf=x.csrf;}).catch(()=>show('The site did not answer. Reload the page.'));
$('show').onchange=()=>{$('password').type=$('show').checked?'text':'password';};
$('signin').onsubmit=async e=>{
  e.preventDefault();show('');
  const account=$('account').value.trim(),password=$('password').value;
  if(!account||!password){show('Type your username and password.');(account?$('password'):$('account')).focus();return;}
  $('go').disabled=true;$('go').textContent='Signing in…';
  try{
    const r=await fetch('/api/signin',{method:'POST',cache:'no-store',headers:{'Content-Type':'application/json','X-CSRF-TOKEN':csrf},body:JSON.stringify({account,password,return:back})});
    const x=await r.json().catch(()=>({}));
    if(r.ok){location.replace(x.go||'/');return;}
    show(x.error||`Sign-in failed (HTTP ${r.status}).`);$('password').value='';$('password').focus();
  }catch{show('The site did not answer. Check your connection and try again.');}
  $('go').disabled=false;$('go').textContent='Sign in';
};
