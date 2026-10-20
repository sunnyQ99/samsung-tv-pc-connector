// Runs the standalone service template with mocked fs/http.
// No sockets, device access, process launch, or actual diagnostic file writes.
'use strict';
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const assert = require('assert/strict');
const original = '';
const template = fs.readFileSync(path.join(__dirname, 'app/service/service.js.in'), 'utf8');
const config = {token: 'offline_mock_' + 'x'.repeat(40), instanceId: '00000000-0000-4000-8000-000000000001'};
const code = original + template.replace('__TVCITATION_CONFIG__', JSON.stringify(config));
function setup(options = {}) {
  const calls = [], reads = [], writes = [], servers = [], messages = [], logs = [];
  const http = {createServer: handler => {
    if (options.createThrows) throw Object.assign(new Error('secret error text'), {code: 'EACCES'});
    const events = {};
    const server = {handler, events, on: (name, callback) => {events[name] = callback; return server;},
      setTimeout: () => {}, close: () => calls.push('close'),
      listen: (port, host, callback) => {
        assert.equal(port, 18767); assert.equal(host, '0.0.0.0');
        if (options.listenError) events.error(Object.assign(new Error('secret bind error'), {code: 'EADDRINUSE'}));
        else callback();
      }};
    servers.push(server); return server;
  }};
  const fsMock = {
    writeFileSync: (filename, value, encoding) => {
      assert.equal(filename, '/home/owner/apps_rw/TVCitBoot1/data/tvcitation-boot-health-stage.json');
      assert.equal(encoding, 'utf8');
      if (options.writeThrows) throw new Error('readonly');
      assert(!value.includes(config.token)); assert(!value.includes('secret'));
      writes.push(JSON.parse(value));
    },
    readFileSync: filename => {
      reads.push(filename);
      if (filename === '/proc/self/status') return 'Uid:\t5001\t5001\t5001\t5001\nGid:\t5001\t5001\t5001\t5001\nCapEff:\t0000000000000000\nName:\tDO_NOT_RETURN\n';
      if (filename === '/proc/sys/kernel/random/boot_id') return '11111111-2222-4333-8444-555555555555\n';
      throw new Error('Unexpected file');
    }
  };
  const modules = [];
  const context = {module: {exports: {}}, Date, String, Number, JSON, RegExp, Math, isFinite,
    process: {pid: 1234, uptime: () => 1.2, getuid: () => 5001},
    tizen: {application: {getCurrentApplication: () => ({appInfo: {packageId: 'MoonLightS'}, getRequestedAppControl: () => null})},
      messageport: {requestRemoteMessagePort: () => ({sendMessage: data => messages.push(data)})}},
    console: {log: (...args) => logs.push(args), error: (...args) => logs.push(args)},
    require: name => {
      modules.push(name);
      if (name === 'fs') return fsMock;
      if (name === 'http') return http;
      throw new Error('New require not allowed');
    }
  };
  vm.createContext(context); vm.runInContext(code, context);
  assert.deepEqual(modules, ['fs', 'http']);
  assert.equal(servers.length, 0);
  const exports = context.module.exports;
  const request = (override = {}) => {
    let status, payload;
    servers.at(-1).handler({method: 'GET', url: '/health', socket: {remoteAddress: '192.168.1.75'},
      headers: {host: '192.168.1.5:18767', authorization: 'Bearer ' + config.token}, ...override},
      {writeHead: value => status = value, end: text => payload = text ? JSON.parse(text) : null});
    return {status, payload};
  };
  return {exports, request, reads, writes, servers, messages, logs, calls};
}
const normal = setup();
assert.equal(normal.writes.at(-1).stages.at(-1).stage, 'lifecycle-wrapped');
normal.exports.onStart();
assert.equal(normal.request().payload.service, 'TVCitBoot1.Service'); normal.reads.length = 0;
assert.equal(normal.servers.length, 1);
normal.exports.onRequest();
assert.equal(normal.servers.length, 1);

const rejected = [
  {socket: {remoteAddress: '192.168.1.6'}}, {headers: {}},
  {headers: {host: 'attacker', authorization: 'Bearer ' + config.token}},
  {headers: {host: '192.168.1.5:18767', authorization: 'Bearer ' + config.token, origin: 'null'}},
  {headers: {host: '192.168.1.5:18767', authorization: 'Bearer ' + config.token.slice(0, -1) + '\u00e9'}},
  {method: 'POST'}, {url: '/health?file=/etc/passwd'}, {url: '/spawn'},
  {headers: {host: '192.168.1.5:18767', authorization: 'Bearer ' + config.token, 'content-length': '1'}}
];
for (const request of rejected) assert(normal.request(request).status >= 400);
assert.equal(normal.reads.length, 0);
const result = normal.request();
assert.equal(result.status, 200);
assert.equal(result.payload.process.uid, 5001);
assert.equal(result.payload.bootResidentVerified, false);
assert.equal(result.payload.capabilities.childProcessProbe, 'deferred');
assert.equal(result.payload.commandExecutionEnabled, false);
assert.deepEqual(normal.reads, ['/proc/self/status', '/proc/sys/kernel/random/boot_id']);
assert(!JSON.stringify(result).includes(config.token));
assert(!JSON.stringify(result).includes('DO_NOT_RETURN'));
normal.exports.onStop(); assert.equal(normal.calls.length, 1);
normal.exports.onStart(); assert.equal(normal.servers.length, 2);
normal.exports.onExit(); assert.equal(normal.calls.length, 2);
const noStageWrites = setup({writeThrows: true}); noStageWrites.exports.onStart();
assert.equal(noStageWrites.request().status, 200);
const createError = setup({createThrows: true}); createError.exports.onStart();
assert.equal(createError.writes.at(-1).stages.at(-1).errorCode, 'EACCES');
const bindError = setup({listenError: true}); bindError.exports.onStart();
assert(bindError.writes.at(-1).stages.some(record => record.errorCode === 'EADDRINUSE'));
console.log(JSON.stringify({ok: true, standaloneServiceExecutedInMock: true, rejectedCases: rejected.length,
  failureModes: 3, newRequires: 0, socketsOpened: false, secretsPrinted: false}));
