# Yaparr.Cloud

Optional services for Yaparr, licensed under the [GNU Affero General Public License v3.0](LICENSE) (AGPL-3.0): anyone may run and change it, and if you offer a modified version to others over a network you must offer them its source. The hosted service at cloud.yaparr.com is offered separately. Nothing here is needed to use Yaparr, and nothing in the client may depend on it or be held back without it.

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

### Signup codes: friends sign themselves up

Instead of making every account by hand, make a **code** and send a link with it in. Anyone with the link can sign up, and **one code can be used by many people** (any number, unless you give it a limit). Everyone who signs up gets a **lifetime pass**: the account's `pass` is `lifetime`, which is recorded and shown in `accounts list`. (Nothing here ends an account on a date.)

```
dotnet Yaparr.Cloud.dll codes add [--note "who it is for"] [--uses 10] [--days 30]
dotnet Yaparr.Cloud.dll codes list
dotnet Yaparr.Cloud.dll codes revoke <id>
```

`codes add` prints the code and the link, once: only a hash of the code is kept, so it cannot be shown again (the `id` that `list` shows is only for telling codes apart and revoking them). `--uses` limits how many people can use it, `--days` how long it works. `codes list` shows each code, whether it still works (`works`, `used up`, `expired`, `revoked`), and who signed up with it. `revoke` stops a code; the people who already signed up keep their accounts.

The link is the sign-up page on the website with the server and the code in the fragment (`https://yaparr.com/signup#server=…&code=…`), so neither reaches the website's host. `PublicUrl` (this service's address) and `SignupPageUrl` (the page on the website) come from `appsettings.json`, which is part of every build and image: they are **`https://cloud.yaparr.com`** and `https://yaparr.com/signup`, so the link comes out whole. Set `Service__PublicUrl` or `Service__SignupPageUrl` to change them for another deployment; the app ships with the same server address in its own `yaparr.release.json`.

What the friend does: opens the link, types their email address and presses Sign up. The page posts the address and the code straight to this service (`POST /signup`, an ordinary form, so no CORS is involved), which makes the account and shows **its own page** to choose a password. So the password is typed on this service's page and never passes through the website. The link then tells them to open Yaparr and sign in to the backup server. Nothing is emailed anywhere.

- A code that is revoked, expired, used up or never existed all get the same answer, and eight wrong codes from one address block it for a quarter of an hour.
- An address that already has an account is told to sign in. An address that signed up but has not yet chosen a password can come back with the link and gets a fresh page (the old one stops working; it is not counted as another use). The page that chooses the password works once and for 30 minutes.
- Anyone with the link can sign up with *any* email address, so send a code to people you trust, give a code a limit or an end date, and revoke it when you are done.

### Carrying on in the app after signing up (`SignInHandoff`)

When someone finishes choosing their password, the "You're in" page has a button, `yaparr://backup-signin/<code>/<this server, escaped>`, which opens the Yaparr app on that computer. The app asks the person to confirm ("Sign in to cloud.yaparr.com as …?"), trades the code for a token (`POST /v1/token/handoff`, body `{"code"}`, answer `{token, username}`) and is signed in, so they do not type the password again. The button is left out on a phone, since the Android app has no backup server yet.

This is weaker than the browser sign-in below, and only for this moment. There the app starts the sign-in and keeps a secret (the PKCE verifier) that makes a stolen code useless; here the browser starts it, so the code alone is the proof, and another program on the same computer that also claims `yaparr://` links could take it. That is limited by: the code is 256 random bits, works once and for five minutes, is made only for the account whose password was just chosen, gives a token for that account's locked backups and nothing else, and the app shows the server and the account and asks before it trades the code.

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
| `POST /signup` | form: `email`, `code` | the page to choose a password (an account without one is made), or why the code does not work |
| `POST /account/setup` | form: `token`, `password`, `again` | the welcome page; the token works once, for 30 minutes |
| `GET /login`, `POST /login` | the sign-in page (see above) | `302` back to the app with `code` and `state` |
| `POST /v1/token` | `{"code","verifier"}` | `{token, username}`; `400` if the code is spent, late or the verifier is wrong |
| `POST /v1/token/handoff` | `{"code"}` from the button on the "You're in" page | `{token, username}`; `400` if the code is spent, late or never made |
| `DELETE /v1/token` | `Authorization: Bearer <token>` | `204`; signs that device out |
| `GET /account/password`, `POST` | the change-password page | signs every device out |
| `PUT /v1/backup` | body: the locked backup; `If-Match: "n"` = the newest version the client knows (`"0"` for none) | `201 {version,size}`; `409` if a newer version exists; `428` without `If-Match`; `413` too big; `400` not a locked backup; `429` too many saves |
| `GET /v1/backup[?version=n]` | | the backup; `X-Yaparr-Version` says which; `404` if none |
| `GET /v1/backup/versions` | | `{username, versions:[{version,size,at}]}`, newest first |
| `DELETE /v1/backup` | | `204`; `404` if none |
| `GET /healthz` | | `200` |

Everything under `/v1/backup` needs `Authorization: Bearer <token>`; without a token the service gave out, the answer is `401`. `If-Match` is what keeps two devices from overwriting each other, and also makes a replayed upload a `409` instead of a second copy.

### Limits (`appsettings.json`, section `Service`, or `Service__Name` environment variables)

`MaxBlobBytes` (512 MiB), `KeepVersions` (3) and `KeepDays` (7), `RequestsPerMinute` per address (120), `WritesPerHour` per account (12), `DataDirectory` (`data`), `PublicUrl` (`https://cloud.yaparr.com`), `SignupPageUrl` (`https://yaparr.com/signup`), `BehindProxy`.

Set `BehindProxy` to `true` only when a reverse proxy is in front (it should be, for https): then the caller's address comes from `X-Forwarded-For`. Left on without a proxy, anyone can claim any address.

### Storage

Files, one folder per account (the hex SHA-256 of its email address) with numbered versions, written to a temporary file and moved into place. `BackupStore` is the only thing that touches them, so S3 or similar can replace it later. `accounts.json` beside them holds the email addresses, password hashes, token hashes and passes, and `codes.json` the signup codes (as hashes) and who used them.

**Which versions are kept.** A backup is saved when something that cannot be lost changes (a room, a key, a linked device) and once a day for the history, so saving often must not push the older, known-good copies out: a bad save would then replace every good one. After each save the server keeps the newest `KeepVersions` (3), and the newest version of each of the last `KeepDays` (7) UTC days, counting today, and removes the rest (`Retention`). So an account holds at most ten versions: the latest few, and one from each morning of the week. The newest is never removed. (Before this, the newest five were kept whatever their age.)

### Not done yet

- Android has no backup server support, and cannot open a locked backup.
- Backups of the data folder, and monitoring.
- A per-account storage quota, if one is wanted.
- Other ways to sign in (a passkey, single sign-on): the app would not change, since it only sees the browser come back.

## Running it with Docker

**The whole setup, from a new DigitalOcean droplet to friends signing up, is in [docs/setup-guide.md](docs/setup-guide.md).**

`docker-compose.yml` runs the service with Caddy in front of it, which gets and renews an https certificate for `DOMAIN` (`cloud.yaparr.com` unless `.env` says otherwise).

```
docker compose up -d --build
docker compose exec cloud dotnet Yaparr.Cloud.dll accounts add you@example.com
```

Point a DNS name at the machine and open ports 80 and 443 first. Everything (the accounts, the signup codes and every backup) is in the `cloud-data` volume, so that is what to back up.

- **Ports** are set in `.env` (see `.env.example`): `HTTP_PORT` and `HTTPS_PORT` for Caddy (80 and 443; Caddy needs them reachable from the internet to get a certificate), and `SERVICE_PORT` and `SERVICE_BIND` for the service itself (8080 on `127.0.0.1`, this computer only).
- **With a proxy of your own instead of Caddy**, start only the service (`docker compose up -d --build cloud`) and point your proxy at `http://127.0.0.1:8080`. A proxy in a container cannot reach `127.0.0.1` of the host: put them on a shared Docker network, or use the host's address with `SERVICE_BIND=0.0.0.0` (plain http, so only on a network you trust). The proxy must set `X-Forwarded-For` (most do) and allow request bodies as large as `Service__MaxBlobBytes` (512 MiB by default).
- **Settings** are the `Service__...` environment variables in the compose file. Keep `Service__BehindProxy=true` while a proxy is in front.
- **Komodo:** a Stack with a Git Repo source, this repository and `docker-compose.yml` works as it is.

## Tests and deploying

`.github/workflows/ci.yml` runs on every pull request and push: the tests, a check that the compose file and the Caddyfile are valid, and a build of the image that starts the service in it and checks it is healthy. After a merge to `master` it can deploy to the droplet over SSH, once the three secrets in [docs/setup-guide.md](docs/setup-guide.md#10a-automatic-deploys-optional) are set; until then it says so and does nothing.

By hand, on the droplet: `./deploy.sh deploy` gets `master`, rebuilds, waits for the service to be healthy and goes back to what was running if it does not become healthy. `./deploy.sh check` says what is running.

## Building

```
dotnet test
dotnet run --project src/Yaparr.Cloud
dotnet run --project src/Yaparr.Cloud -- accounts list
```
