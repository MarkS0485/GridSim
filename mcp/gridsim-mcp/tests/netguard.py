"""Forbid network access in this process: any socket connect, bind, send-to or name lookup raises and is reported.

The only exception is the loopback socket pair Python's event loop builds for itself on Windows (socket.socketpair),
which never leaves the process."""

import socket
import sys
import threading

_local = threading.local()
ATTEMPTS = []
_EVENTS = {"socket.connect", "socket.bind", "socket.sendto", "socket.sendmsg", "socket.getaddrinfo",
           "socket.gethostbyname", "socket.gethostbyaddr", "socket.getnameinfo"}
_installed = False


def _hook(event, args):
    if event in _EVENTS and not getattr(_local, "pair", False):
        ATTEMPTS.append((event, repr(args)[:200]))
        sys.stderr.write(f"NETWORK-ATTEMPT {event} {args!r}\n")
        raise RuntimeError(f"network access is forbidden here ({event})")


def install():
    global _installed
    if _installed:
        return
    real_pair = socket.socketpair

    def socketpair(*a, **k):
        _local.pair = True
        try:
            return real_pair(*a, **k)
        finally:
            _local.pair = False

    socket.socketpair = socketpair
    sys.addaudithook(_hook)
    _installed = True
