const assert=require('assert'),fs=require('fs'),vm=require('vm');
const nodes=new Map(),listeners=new Map(),windowEvents=new Map();let now=0,callback,cancelled=false;
function node(id){if(!nodes.has(id))nodes.set(id,{id,style:{},textContent:'',clientWidth:1700,classList:{add(){},remove(){}},focus(){}});return nodes.get(id);}
const window={addEventListener:(name,fn)=>windowEvents.set(name,fn)};
const context={window,document:{getElementById:node,addEventListener:(name,fn)=>listeners.set(name,fn),querySelectorAll:()=>['red','green','blue','white'].map(node),visibilityState:'visible'},
 performance:{now:()=>now},Date,isFinite,requestAnimationFrame:fn=>{callback=fn;return 123},cancelAnimationFrame:()=>{cancelled=true}};
vm.createContext(context);vm.runInContext(fs.readFileSync(__dirname+'/app/probe.js','utf8'),context);
assert.equal(window.tvcInputEvents.length,0);
listeners.get('click')({type:'click',isTrusted:false,target:node('red')});assert.equal(window.tvcInputEvents.length,0);assert.equal(window.tvcProbeState.untrustedIgnored,1);
listeners.get('keydown')({type:'keydown',isTrusted:true,keyCode:65,key:'privateText',target:node('red')});assert.equal(window.tvcInputEvents.length,0);
for(let i=0;i<125;i++){now+=10;listeners.get('click')({type:'click',isTrusted:true,clientX:i,clientY:23,button:0,buttons:1,target:node('red')});}
assert.equal(window.tvcInputEvents.length,100);assert.equal(window.tvcInputEvents[0].sequence,26);assert.equal(window.tvcInputEvents[99].sequence,125);
assert(!JSON.stringify(window.tvcInputEvents).includes('privateText'));
listeners.get('pointermove')({type:'pointermove',isTrusted:true,clientX:100,clientY:30,pointerType:'mouse',target:node('stage')});
now+=1;listeners.get('pointermove')({type:'pointermove',isTrusted:true,clientX:101,clientY:30,pointerType:'mouse',target:node('stage')});assert.equal(window.tvcProbeState.moveEventsCoalesced,1);
callback(2500);assert.equal(window.tvcProbeState.uiTicks,1);assert.equal(nodes.get('moving').style.left,'1562px');
assert.equal(window.tvcProbeState.sourceFrameRateVerified,false);
windowEvents.get('pagehide')();assert(cancelled);callback(2600);assert.equal(window.tvcProbeState.uiTicks,1);
console.log(JSON.stringify({ok:true,untrustedRejected:true,characterKeysNotLogged:true,maxEvents:100,animationStopsOnPageHide:true,network:false}));
