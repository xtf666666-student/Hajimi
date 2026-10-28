"""Try speaking ACP over WebSocket to the DSH desktop host.

The desktop app has no `--remote-debugging-port` and its HTTP surface is
compressed/opaque, so this checks whether a plain WS client can reach
`sessions/prompt` and `sessions/cancel` at all.
"""
import base64
import json
import os
import socket
import struct
import sys


def ws_handshake(host, port, path, timeout=5):
    key = base64.b64encode(os.urandom(16)).decode()
    req = (
        f"GET {path} HTTP/1.1\r\n"
        f"Host: {host}:{port}\r\n"
        "Upgrade: websocket\r\n"
        "Connection: Upgrade\r\n"
        f"Sec-WebSocket-Key: {key}\r\n"
        "Sec-WebSocket-Version: 13\r\n"
        "\r\n"
    )
    s = socket.create_connection((host, port), timeout=timeout)
    s.sendall(req.encode())
    head = b''
    while b'\r\n\r\n' not in head:
        chunk = s.recv(1024)
        if not chunk:
            break
        head += chunk
    return s, head.decode('utf-8', 'replace')


def ws_send_text(sock, text):
    payload = text.encode()
    header = bytearray([0x81])
    ln = len(payload)
    if ln < 126:
        header.append(0x80 | ln)
    elif ln < 65536:
        header.append(0x80 | 126)
        header += struct.pack('>H', ln)
    else:
        header.append(0x80 | 127)
        header += struct.pack('>Q', ln)
    mask = os.urandom(4)
    header += mask
    masked = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
    sock.sendall(bytes(header) + masked)


def main():
    for port, path in ((4310, '/acp'), (56322, '/acp')):
        try:
            sock, head = ws_handshake('127.0.0.1', port, path)
        except Exception as exc:
            print(f'port {port}: connect failed - {exc}')
            continue
        status = head.split('\r\n', 1)[0] if head else 'no response'
        print(f'port {port}{path}: {status}')
        if '101' in status:
            msg = {
                'jsonrpc': '2.0', 'id': 1,
                'method': 'session/prompt',
                'params': {'sessionId': 'probe', 'prompt': [{'type': 'text', 'text': 'hi'}]},
            }
            ws_send_text(sock, json.dumps(msg))
            try:
                sock.settimeout(4)
                reply = sock.recv(2048)
                print('   reply:', reply[:300])
            except Exception as exc:
                print('   no reply:', exc)
        sock.close()
    return 0


if __name__ == '__main__':
    raise SystemExit(main())