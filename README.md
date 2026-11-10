# Yaparr.Cloud

Private. Optional services for Yaparr. Nothing here is needed to use Yaparr, and nothing in the client may depend on it or be held back without it.

## Backup storage

A place to keep a **locked** profile backup, so a person can get it back from any device. The server never holds anything it can read: the app locks the backup on the device with a recovery code that never reaches the server (`ProfileCipher` in the app: Argon2id and AES-GCM), and the server refuses a file that does not start like a locked one.

### Accounts: a password to sign in, a recovery code to open the backup

Two secrets, with different jobs:

- **The password signs a person in**, on a page of this service in their browser. The server keeps an Argon2id hash of it (`Passwords`). It can be changed by the person (`/account/password`) or reset by the admin, and nothing is lost when it is: it does not lock anything.
- **The recovery code locks the backup.** The app makes it and shows it once; the server never sees it. Nobody here can open a backup, and a lost code cannot be recovered.

**An account is named by its owner's email address** (`sam@example.com`, kept trimmed and in lower case), which is what they type on the sign-in page. Nothing is ever sent to it: it only names the account, so there is no email verification and no "forgot password" email. Resetting a password is the admin's job (`accounts password`).

Accounts are made by hand. Nobody can make one by asking, and the sign-in page answers a name that does not exist exactly as it answers a wrong password. Eight wrong tries for a name lock that name for a quarter of an hour. An address is plain ASCII (up to 254 characters, the part before the @ up to 64), with nothing that could be taken for a path or markup; a password needs 10 characters or more.

### Looking after accounts

Run these where the service runs, with the same `Service__DataDirectory` (in Docker: `docker exec -it <container> dotnet Yaparr.Cloud.dll accounts ...`). The running service sees the change at once, with no restart.

```
dotnet Yaparr.Cloud.dll accounts add <email> [--password <password>]
dotnet Yaparr.Cloud.dll accounts password <email> [--password <password>]
dotnet Yaparr.Cloud.dll accounts remove <email> [--keep-backup]
dotnet Yaparr.Cloud.dll accounts list
```

`add` and `password` without `--password` make one up (like `K7QD-M3XF-9PLR-2WTE-HV8N`) and show it once; give it to the person, who can change it on the sign-in page. Typing a password after `--password` leaves it in your shell history, so prefer the made-up one. `password` also signs the account's devices out. `remove` deletes the backup unless told not to. `list` shows how many devices are signed in and the space used.

An account whose name is not an email address (from before accounts were named by one) cannot sign in, and `accounts list` says so: add the person again with their address. An account made by the first version of the service (which kept a public key and no password) shows as `NO PASSWORD` in the list and cannot sign in until `accounts password <name>` gives it one. Backups from that version were locked with the old password, not a recovery code.

### Signing in (`LoginFlow`)

The way programs on a computer sign in to web services: OAuth's authorization code with PKCE, cut down to what is needed.

1. The app listens on a port of its own computer and opens the browser at `/login?redirect_uri=http://127.0.0.1:<port>/…&code_challenge=…&state=…&device=…`.
2. The person signs in on the page. The browser is only ever sent back to the computer it is on (`127.0.0.1` or `[::1]`), so a made-up link cannot send a code elsewhere.
3. The browser goes back to the app with a code that works once, for two minutes.
4. The app trades the code and its verifier for a token (`POST /v1/token`). Only the SHA-256 of a token is kept (`accounts.json`), so the file is of no use for signing in. An account keeps at most 20 devices; the oldest is signed out to make room.

The pages are plain HTML with no scripts, are never cached or framed, and everything that came from a request is encoded before it is shown.

### API

| | | |
|---|---|---|
| `GET /login`, `POST /login` | the sign-in page (see above) | `302` back to the app with `code` and `state` |
| `POST /v1/token` | `{"code","verifier"}` | `{token, username}`; `400` if the code is spent, late or the verifier is wrong |
| `DELETE /v1/token` | `Authorization: Bearer <token>` | `204`; signs that device out |
| `GET /account/password`, `POST` | the change-password page | signs every device out |
| `PUT /v1/backup` | body: the locked backup; `If-Match: "n"` = the newest version the client knows (`"0"` for none) | `201 {version,size}`; `409` if a newer version exists; `428` without `If-Match`; `413` too big; `400` not a locked backup; `429` too many saves |
| `GET /v1/backup[?version=n]` | | the backup; `X-Yaparr-Version` says which; `404` if none |
| `GET /v1/backup/versions` | | `{username, versions:[{version,size,at}]}`, newest first |
| `DELETE /v1/backup` | | `204`; `404` if none |
| `GET /healthz` | | `200` |

Everything under `/v1/backup` needs `Authorization: Bearer <token>`; without a token the service gave out, the answer is `401`. `If-Match` is what keeps two devices from overwriting each other, and also makes a replayed upload a `409` instead of a second copy.

### Limits (`appsettings.json`, section `Service`, or `Service__Name` environment variables)

`MaxBlobBytes` (512 MiB), `KeepVersions` (5), `RequestsPerMinute` per address (120), `WritesPerHour` per account (12), `DataDirectory` (`data`), `BehindProxy`.

Set `BehindProxy` to `true` only when a reverse proxy is in front (it should be, for https): then the caller's address comes from `X-Forwarded-For`. Left on without a proxy, anyone can claim any address.

### Storage

Files, one folder per account (the hex SHA-256 of its email address) with numbered versions, written to a temporary file and moved into place. `BackupStore` is the only thing that touches them, so S3 or similar can replace it later. `accounts.json` beside them holds the usernames, password hashes and token hashes.

### Not done yet

- Android has no backup server support, and cannot open a locked backup.
- Backups of the data folder, and monitoring.
- A per-account storage quota, if one is wanted.
- Other ways to sign in (a passkey, single sign-on): the app would not change, since it only sees the browser come back.

## Running it with Docker

`docker-compose.yml` runs the service on its own, on plain http. Put your own reverse proxy in front of it for https.

```
docker compose up -d --build
docker compose exec cloud dotnet Yaparr.Cloud.dll accounts add sam
```

Then send your domain to `http://127.0.0.1:8080` in the proxy. Friends use `https://<your domain>` as the server address. Everything (the account list and every backup) is in the `cloud-data` volume, so that is what to back up.

- **Ports:** `SERVICE_PORT` (8080) and `SERVICE_BIND` (`127.0.0.1`, this computer only) are set in `.env` (see `.env.example`). `0.0.0.0` opens it to other computers as plain http, so only on a network you trust.
- **A proxy in a container:** it cannot reach `127.0.0.1` of the host. The compose file has a commented-out shared network for that: the proxy then sends to `http://cloud:8080`.
- **The proxy must** set `X-Forwarded-For` (most do), or every caller looks like the proxy and shares one rate limit, and it must allow request bodies as large as `Service__MaxBlobBytes` (512 MiB by default).
- **Settings** are the `Service__...` environment variables in the compose file. Keep `Service__BehindProxy=true` while a proxy is in front.
- **Komodo:** a Stack with a Git Repo source, this repository and `docker-compose.yml` works as it is.

## Building

```
dotnet test
dotnet run --project src/Yaparr.Cloud
dotnet run --project src/Yaparr.Cloud -- accounts list
```
