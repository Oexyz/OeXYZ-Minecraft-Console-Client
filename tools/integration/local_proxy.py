#!/usr/bin/env python3
"""Loopback-only SOCKS5/HTTP CONNECT integration proxy with optional auth."""

from __future__ import annotations

import argparse
import base64
import hmac
import ipaddress
import os
import secrets
import select
import socket
import struct


MAXIMUM_HTTP_HEADER_BYTES = 8192
MAXIMUM_CREDENTIAL_BYTES = 255


def read_exact(connection: socket.socket, length: int) -> bytes:
    result = bytearray()
    while len(result) < length:
        block = connection.recv(length - len(result))
        if not block:
            raise EOFError("connection closed")
        result.extend(block)
    return bytes(result)


def read_password(path: str) -> bytes:
    try:
        descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    except FileExistsError:
        pass
    else:
        with os.fdopen(descriptor, "wb") as output:
            output.write(secrets.token_urlsafe(24).encode("ascii"))
    with open(path, "rb") as source:
        password = source.read(MAXIMUM_CREDENTIAL_BYTES + 1)
    if not 1 <= len(password) <= MAXIMUM_CREDENTIAL_BYTES:
        raise ValueError("password file length is outside integration limits")
    return password


def validate_target(host: str, port: int, allowed_port: int) -> str:
    if port != allowed_port:
        raise PermissionError("destination port is not authorized")
    addresses = socket.getaddrinfo(host, port, type=socket.SOCK_STREAM)
    if not addresses:
        raise PermissionError("destination did not resolve")
    for _, _, _, _, endpoint in addresses:
        if not ipaddress.ip_address(endpoint[0]).is_loopback:
            raise PermissionError("only loopback destinations are authorized")
    return addresses[0][4][0]


def relay(client: socket.socket, upstream: socket.socket) -> None:
    peers = [client, upstream]
    while peers:
        readable, _, _ = select.select(peers, [], [], 5)
        for source in readable:
            block = source.recv(65536)
            if not block:
                return
            destination = upstream if source is client else client
            destination.sendall(block)


def handle_http(
    client: socket.socket,
    username: bytes,
    password: bytes,
    allowed_port: int,
) -> None:
    request = bytearray()
    while b"\r\n\r\n" not in request and len(request) < MAXIMUM_HTTP_HEADER_BYTES:
        block = client.recv(1024)
        if not block:
            raise EOFError("client closed during headers")
        request.extend(block)
    if b"\r\n\r\n" not in request:
        raise ValueError("HTTP headers exceeded the integration limit")
    lines = bytes(request).split(b"\r\n")
    first = lines[0].split()
    headers: dict[bytes, bytes] = {}
    for line in lines[1:]:
        if b":" in line:
            name, value = line.split(b":", 1)
            headers[name.strip().lower()] = value.strip()
    expected = b"Basic " + base64.b64encode(username + b":" + password)
    if not hmac.compare_digest(headers.get(b"proxy-authorization", b""), expected):
        client.sendall(
            b"HTTP/1.1 407 Proxy Authentication Required\r\n"
            b"Proxy-Authenticate: Basic realm=OeXYZIntegration\r\n"
            b"Content-Length: 0\r\n\r\n"
        )
        return
    if len(first) != 3 or first[0] != b"CONNECT":
        raise ValueError("only CONNECT is supported")
    host_bytes, port_bytes = first[1].rsplit(b":", 1)
    host = host_bytes.decode("ascii").strip("[]")
    port = int(port_bytes)
    target = validate_target(host, port, allowed_port)
    with socket.create_connection((target, port), 5) as upstream:
        client.sendall(b"HTTP/1.1 200 Connection Established\r\n\r\n")
        relay(client, upstream)


def handle_socks5(
    client: socket.socket,
    username: bytes,
    password: bytes,
    allowed_port: int,
) -> None:
    version, count = read_exact(client, 2)
    methods = read_exact(client, count)
    if version != 5 or 2 not in methods:
        client.sendall(b"\x05\xff")
        return
    client.sendall(b"\x05\x02")
    auth_version, username_length = read_exact(client, 2)
    supplied_username = read_exact(client, username_length)
    password_length = read_exact(client, 1)[0]
    supplied_password = read_exact(client, password_length)
    authorized = (
        auth_version == 1
        and hmac.compare_digest(supplied_username, username)
        and hmac.compare_digest(supplied_password, password)
    )
    client.sendall(b"\x01\x00" if authorized else b"\x01\x01")
    if not authorized:
        return
    version, command, _, address_type = read_exact(client, 4)
    if version != 5 or command != 1:
        raise ValueError("only SOCKS5 CONNECT is supported")
    if address_type == 1:
        host = socket.inet_ntoa(read_exact(client, 4))
    elif address_type == 3:
        host = read_exact(client, read_exact(client, 1)[0]).decode("ascii")
    elif address_type == 4:
        host = socket.inet_ntop(socket.AF_INET6, read_exact(client, 16))
    else:
        raise ValueError("unsupported SOCKS5 address type")
    port = struct.unpack("!H", read_exact(client, 2))[0]
    target = validate_target(host, port, allowed_port)
    with socket.create_connection((target, port), 5) as upstream:
        client.sendall(b"\x05\x00\x00\x01\x7f\x00\x00\x01\x00\x00")
        relay(client, upstream)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--kind", choices=("http", "socks5"), required=True)
    parser.add_argument("--listen-port", type=int, required=True)
    parser.add_argument("--destination-port", type=int, required=True)
    parser.add_argument("--username", default="oexyz-integration")
    parser.add_argument("--password-file", required=True)
    arguments = parser.parse_args()
    if not 1 <= arguments.listen_port <= 65535:
        raise ValueError("listen port is invalid")
    if not 1 <= arguments.destination_port <= 65535:
        raise ValueError("destination port is invalid")
    username = arguments.username.encode("utf-8")
    if not 1 <= len(username) <= MAXIMUM_CREDENTIAL_BYTES:
        raise ValueError("username length is outside integration limits")
    password = read_password(arguments.password_file)
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(("127.0.0.1", arguments.listen_port))
    listener.listen(8)
    try:
        while True:
            client, _ = listener.accept()
            with client:
                client.settimeout(10)
                try:
                    if arguments.kind == "http":
                        handle_http(client, username, password, arguments.destination_port)
                    else:
                        handle_socks5(client, username, password, arguments.destination_port)
                except (EOFError, OSError, PermissionError, ValueError):
                    continue
    finally:
        listener.close()


if __name__ == "__main__":
    main()
