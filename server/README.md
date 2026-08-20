# Asobu API

Identity + friends. One binary, one JSON state file, no database yet.

Live at `https://api.asobu.cc`, deployed on a small VPS behind Caddy.
Caddy terminates TLS and proxies to `127.0.0.1:3000`, which is where this listens.

Everything lives in `/home/asobu/asobu-api` — source, binary, and state side by side. Nothing is
installed system-wide: the binary is static and the state is one file, so a redeploy is a
rebuild in place plus a restart, and the service reads the same state file whether it was
started by systemd or by hand.

## Build (on the VPS)

Debian 12's own Go is 1.19 and too old; a current toolchain lives in `~/go-toolchain`.

```
export PATH=$HOME/go-toolchain/bin:$PATH
cd ~/asobu-api
go build -o asobu-api .
```

## Install the service (once, needs root)

```
sudo cp ~/asobu-api/asobu-api.service /etc/systemd/system/ && sudo systemctl daemon-reload && sudo systemctl enable --now asobu-api
```

## Redeploy

Copy the new source up, then:

```
export PATH=$HOME/go-toolchain/bin:$PATH && cd ~/asobu-api && go build -o asobu-api . && sudo systemctl restart asobu-api
```

State is saved on SIGTERM, so a restart never loses the last half minute.

## Check

```
curl https://api.asobu.cc/v1/health
```

## Endpoints

```
GET    /v1/health
POST   /v1/auth/begin        {"name"}              -> {"serverId"}
POST   /v1/auth/complete     {"name","serverId"}   -> {"token","uuid","name"}
GET    /v1/friends                                 -> {friends[], incoming[], outgoing[]}
POST   /v1/friends/requests  {"name"}
POST   /v1/friends/accept    {"uuid"}
DELETE /v1/friends/{uuid}
```

Auth is `Authorization: Bearer <token>`. The token is proof of a Mojang-verified identity: the
API issues a random serverId, the launcher joins it against Mojang's session server with the
Minecraft token it already holds for launching, and the API asks Mojang who joined. No Microsoft
or Minecraft token ever reaches this service.
