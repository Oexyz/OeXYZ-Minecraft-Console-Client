# Local management API

The management listener is disabled unless `--health-port` is supplied and
binds `127.0.0.1` by default. Existing `GET /health`, `/ready`, and `/status`
remain available; `GET /metrics` returns aggregate Prometheus text metrics.

Create a private token without printing it:

```bash
oexyz control-token-create --file ~/.config/oexyz/control.token
oexyz supervise --no-input --health-port 8765 \
  --control-token-file ~/.config/oexyz/control.token
```

Authenticated routes use `Authorization: Bearer <base64 token>`:

- `GET /v1/sessions`
- `GET /v1/sessions/{id}`
- `POST /v1/sessions/{id}/start`
- `POST /v1/sessions/{id}/stop`
- `POST /v1/sessions/{id}/respawn`
- `POST /v1/sessions/{id}/send` with `{"message":"/list"}`

The parser accepts HTTP/1.1 only, rejects transfer encoding and duplicate or
invalid content lengths, caps headers at 8 KiB and bodies at 64 KiB, limits
concurrency to eight clients, applies a five-second request timeout, and allows
at most 30 write actions per minute. Responses contain no stack traces and set
`Cache-Control: no-store` and `X-Content-Type-Options: nosniff`.

`--allow-remote-control` changes the bind from loopback and is rejected without
a valid token. In remote-bind mode every route, including health, status, and
Prometheus metrics, requires the same bearer token. OeXYZ does not provide TLS;
use a VPN or authenticated TLS reverse proxy and do not publish the Compose
port by default.

Prometheus metrics are aggregate and use no account, server, chat, token, or
error-text labels. They cover session/process/traffic/reconnect/drop counters.
