# Yaparr.Cloud

Private. Optional services for Yaparr. Nothing here is needed to use Yaparr, and nothing in the client may depend on it or be held back without it.

## Backup storage

A place to keep a **locked** profile backup, so a person can get it back from any device. The server never holds anything it can read: the app locks the backup with a password on the device (`ProfileCipher` in the app: Argon2id and AES-GCM) before it is sent, and the server refuses a file that does not start like a locked one.

### Accounts: a username and a password, and the server never sees the password

For the person using it, it is an ordinary username and password. Behind it, the app makes an Ed25519 key from the two (`LoginKeys`: Argon2id, with the username as the salt, then HKDF) and signs its requests with it. The server holds only each account's **public key**, so it can check a signature but cannot sign, and cannot learn the password or unlock a backup. The same password locks the backup, with a salt and key of its own.

- **Accounts are made by hand.** Nobody can make one by asking; a request for a name that is not on the list is refused (with the same words as a wrong password, so nothing says who has an account).
- **No password reset.** The server cannot reset a password, because it never had it. An admin can remove and remake an account, but a backup locked with a forgotten password stays locked.
- **A username cannot change** (it is part of the key), and is kept trimmed and lower case: 3 to 32 letters, digits, dots, dashes and underscores.
- **Passwords** need 12 characters or more.

### Looking after accounts

Run these on the server, in the same folder and with the same `Service__DataDirectory` as the service. The running service sees the change at once, with no restart.

```
dotnet Yaparr.Cloud.dll accounts add <username> [--password <password>]
dotnet Yaparr.Cloud.dll accounts remove <username> [--keep-backup]
dotnet Yaparr.Cloud.dll accounts list
```

`add` without `--password` makes one up (like `K7QD-M3XF-9PLR-2WTE-HV8N`) and shows it once; give it to the person, who can change it from the app. The password is used to work out the public key and kept nowhere (only `accounts.json` in the data folder, which holds usernames and public keys). Typing a password after `--password` leaves it in your shell history, so prefer the made-up one. `remove` also deletes the backup unless told not to.

### Signing

Every request carries `X-Yaparr-User`, `X-Yaparr-Time`, `X-Yaparr-Content-Sha256` and `X-Yaparr-Sig` (`RequestSigner`, described in the file). The signed text has the username, method, path and query, the time (five minutes either way) and the body hash, so a signature cannot be moved to another request, and an upload is refused before its body is read.

### API

| | | |
|---|---|---|
| `PUT /v1/backup` | body: the locked backup; `If-Match: "n"` = the newest version the client knows (`"0"` for none) | `201 {version,size}`; `409` if a newer version exists; `428` without `If-Match`; `413` too big; `400` not a locked backup or the body is not what was signed; `429` too many saves |
| `GET /v1/backup[?version=n]` | | the backup; `X-Yaparr-Version` says which; `404` if none |
| `GET /v1/backup/versions` | | `[{version,size,at}]`, newest first |
| `DELETE /v1/backup` | | `204`; `404` if none |
| `PUT /v1/account/key` | body: `{"publicKey":"<base64url, 32 bytes>"}`, signed with the current key | `204`; the changed-password step: the app then locks the backup again with the new password and saves it |
| `GET /healthz` | | `200` |

`If-Match` is what keeps two devices from overwriting each other, and also makes a replayed upload a `409` instead of a second copy.

### Limits (`appsettings.json`, section `Service`, or `Service__Name` environment variables)

`MaxBlobBytes` (512 MiB), `KeepVersions` (5), `RequestsPerMinute` per address (120), `WritesPerHour` per account (12), `ClockSkewSeconds` (300), `DataDirectory` (`data`), `BehindProxy`.

Set `BehindProxy` to `true` only when a reverse proxy is in front (it should be, for https): then the caller's address comes from `X-Forwarded-For`. Left on without a proxy, anyone can claim any address.

### Storage

Files, one folder per account (the hex SHA-256 of its username) with numbered versions, written to a temporary file and moved into place. `BackupStore` is the only thing that touches them, so S3 or similar can replace it later.

### Not done yet

- A client in the app (the login screen, making the key, upload, list, download, restore, change password).
- Android can make plain backups only and cannot open a locked one.
- Hosting: https through a reverse proxy, a container image, backups of the data folder, and monitoring.
- A per-account storage quota, if one is wanted.

## Running it with Docker

`docker-compose.yml` runs the service behind Caddy, which gets and renews an https certificate by itself.

```
cp .env.example .env            # put the name that points at this machine in DOMAIN
docker compose up -d --build
docker compose exec cloud dotnet Yaparr.Cloud.dll accounts add sam
```

Point a DNS name at the machine and open ports 80 and 443 first. Friends use `https://<DOMAIN>` as the server address. Everything (the account list and every backup) is in the `cloud-data` volume, so that is what to back up. The ports are set in `.env` (see `.env.example`): `HTTP_PORT` and `HTTPS_PORT` for Caddy (80 and 443 by default; Caddy needs them reachable from the internet to get a certificate, so change them only when something in front forwards 80 and 443 to them), and `SERVICE_PORT` and `SERVICE_BIND` for the service itself (8080 on `127.0.0.1` by default). To use a proxy you already have, remove the `caddy` service and point the proxy at the service's port, keeping `Service__BehindProxy=true`. Settings are the `Service__...` environment variables in the compose file.

## Building

```
dotnet test
dotnet run --project src/Yaparr.Cloud
dotnet run --project src/Yaparr.Cloud -- accounts list
```
