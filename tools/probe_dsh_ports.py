"""Probe DSH's own ports (8326, 61922) for a usable ACP/RPC channel."""
import base64
import json
import os
import socket
import struct
import subprocess


def http_probe(port, path, method='GET'):
    out = subprocess.run(
        ['curl.exe', '-s', '-i', '--max-time', '5', '-X', method,
         f'http://127.0.0.1:{port}{path}'],
        capture_output=True)
    return out.stdout.decode('utf-8', 'replace')


def ws_probe(port, path, payload=None):
    key = base64.b64encode(os.urandom(16)).decode()
    req = (f"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n"
           "Upgrade: websocket\r\nConnection: Upgrade\r\n"
           f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n")
    try:
        s = socket.create_connection(('127.0.0.1', port), timeout=5)
    except Exception as exc:
        return f'connect failed: {exc}', None
    s.sendall(req.encode())
    head = b''
    while b'\r\n\r\n' not in head:
        chunk = s.recv(1024)
        if not chunk:
            break
        head += chunk
    status = head.decode('utf-8', 'replace').split('\r\n', 1)[0] if head else 'no response'
    reply = None
    if '101' in status and payload:
        data = json.dumps(payload).encode()
        frame = bytearray([0x81])
        ln = len(data)
        if ln < 126:
            frame.append(0x80 | ln)
        elif ln < 65536:
            frame.append(0x80 | 126)
            frame += struct.pack('>H', ln)
        else:
            frame.append(0x80 | 127)
            frame += struct.pack('>Q', ln)
        mask = os.urandom(4)
        frame += mask
        frame += bytes(b ^ mask[i % 4] for i, b in enumerate(data))
        s.sendall(bytes(frame))
        try:
            s.settimeout(4)
            reply = s.recv(2048)
        except Exception as exc:
            reply = f'no reply: {exc}'
    s.close()
    return status, reply


def main():
    for port in (8326, 61922):
        print(f'===== port {port} =====')
        for path in ('/', '/acp', '/ws', '/health'):
            head = http_probe(port, path)
            first = head.split('\r\n', 1)[0] if head else '(empty)'
            print(f'  GET {path:8} -> {first}')
            low = head.lower()
            if 'upgrade' in low and 'websocket' in low:
                print('     (websocket upgrade advertised)')

        payload = {'jsonrpc': '2.0', 'id': 1, 'method': 'session/prompt',
                   'params': {'sessionId': 'probe', 'prompt': [{'type': 'text', 'text': 'hi'}]}}
        status, reply = ws_probe(port, '/acp', payload)
        print(f'  WS /acp -> {status}')
        if reply:
            print(f'     reply: {str(reply)[:220]}')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())