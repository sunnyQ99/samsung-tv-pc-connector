"""Pair this PC with the Samsung TV remote API (run once).

    py -3.14 tools/pair-tv.py --remote-only

Accept the prompt on the TV. The pairing token is stored with Windows DPAPI in
%LOCALAPPDATA%\\TVCITATION\\samsung-token.dpapi and the TV's TLS certificate
fingerprint is written to diagnostics/tv-remote-result.json (the backend pins it).
Without --remote-only it also probes SDB port 26101. Sends no TV key commands.
"""
import base64
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import socket
import ssl
import struct
import sys
import time

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / 'tools' / 'python'))
import websocket

TV = '192.168.1.5'
report = {'tv': TV, 'commands_sent': 0, 'checks': {}}

def emit(check, value):
    report['checks'][check] = value
    print(json.dumps({check: value}, ensure_ascii=False), flush=True)

if '--remote-only' not in sys.argv:
    try:
        with socket.create_connection((TV, 26101), timeout=4) as conn:
            conn.settimeout(4)
            command = 0x4E584E43
            payload = b'host::sdb-net-client'
            header = struct.pack('<6I', command, 0x01000000, 4096,
                                 len(payload), sum(payload), command ^ 0xFFFFFFFF)
            conn.sendall(header + payload)
            data = conn.recv(24)
            emit('sdb', {'tcp_connected': True, 'reply_bytes': len(data),
                         'reply_type': data[:4].decode('ascii', errors='replace'),
                         'accepted': len(data) == 24 and data[:4] == b'CNXN'})
    except Exception as error:
        emit('sdb', {'accepted': False, 'error': type(error).__name__, 'detail': str(error)})

# Samsung televisions normally use a device-local self-signed certificate.
# Record and pin its SHA-256 before the single pairing attempt.
context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
context.check_hostname = False
context.verify_mode = ssl.CERT_NONE
try:
    with socket.create_connection((TV, 8002), timeout=12) as raw:
        with context.wrap_socket(raw, server_hostname=TV) as secure:
            fingerprint = hashlib.sha256(secure.getpeercert(binary_form=True)).hexdigest()
    emit('tls', {'certificate_sha256': fingerprint})
    name = base64.b64encode(b'TVCITATION').decode('ascii')
    ws = websocket.create_connection(
        f'wss://{TV}:8002/api/v2/channels/samsung.remote.control?name={name}',
        timeout=45, sslopt={'context': context}, http_no_proxy=[TV])
    actual = hashlib.sha256(ws.sock.getpeercert(binary_form=True)).hexdigest()
    if actual != fingerprint:
        ws.close()
        raise RuntimeError('TV TLS certificate changed between checks')
    print(json.dumps({'remote': 'waiting_for_tv_pairing', 'commands_sent': 0}), flush=True)
    events = []
    deadline = time.monotonic() + 45
    paired = False
    try:
        while time.monotonic() < deadline:
            ws.settimeout(max(1, deadline - time.monotonic()))
            raw_message = ws.recv()
            if not raw_message:
                break
            message = json.loads(raw_message)
            event = message.get('event', 'unknown')
            events.append(event)
            print(json.dumps({'remote_event': event}), flush=True)
            if event == 'ms.channel.connect':
                paired = True
                token = message.get('data', {}).get('token')
                if token:
                    class Blob(ctypes.Structure):
                        _fields_ = [('size', wintypes.DWORD), ('data', ctypes.POINTER(ctypes.c_byte))]
                    token_buffer = ctypes.create_string_buffer(str(token).encode('utf8'))
                    source = Blob(len(token_buffer.raw) - 1,
                                  ctypes.cast(token_buffer, ctypes.POINTER(ctypes.c_byte)))
                    protected = Blob()
                    ok = ctypes.windll.crypt32.CryptProtectData(
                        ctypes.byref(source), 'TVCITATION Samsung TV pairing', None,
                        None, None, 0, ctypes.byref(protected))
                    if not ok:
                        raise ctypes.WinError()
                    credential_dir = Path(os.environ['LOCALAPPDATA']) / 'TVCITATION'
                    credential_dir.mkdir(exist_ok=True)
                    (credential_dir / 'samsung-token.dpapi').write_bytes(
                        ctypes.string_at(protected.data, protected.size))
                    ctypes.windll.kernel32.LocalFree(protected.data)
                break
            if event in ('ms.channel.unauthorized', 'ms.channel.timeOut'):
                break
    except websocket.WebSocketTimeoutException:
        events.append('client_wait_timeout')
    finally:
        ws.close()
    emit('remote', {'paired': paired, 'events': events, 'commands_sent': 0})
except Exception as error:
    emit('remote', {'paired': False, 'error': type(error).__name__, 'detail': str(error)})

output_name = 'tv-remote-result.json' if '--remote-only' in sys.argv else 'tv-access-result.json'
(ROOT / 'diagnostics').mkdir(exist_ok=True)
(ROOT / 'diagnostics' / output_name).write_text(
    json.dumps(report, ensure_ascii=False, indent=2), encoding='utf8')
