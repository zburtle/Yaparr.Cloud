# Yaparr.Cloud

Optional services for Yaparr, licensed under the [GNU Affero General Public License v3.0](LICENSE) (AGPL-3.0): anyone may run and change it, and if you offer a modified version to others over a network you must offer them its source. The hosted service at cloud.yaparr.com is offered separately. To contribute, see [CONTRIBUTING.md](CONTRIBUTING.md) (it asks you to agree to a [contributor agreement](CLA.md)). Nothing here is needed to use Yaparr, and nothing in the client may depend on it or be held back without it.

## Backup storage

A place to keep a person's account in sync across their devices, as **locked** data (the apps call it Yaparr Cloud sync; the API below still says `backup` for the locked copy of the profile). The server never holds anything it can read: the app locks it on the device with a Sync key that never reaches the server (`ProfileCipher` in the app: Argon2id and AES-GCM), and the server refuses a file that does not start like a locked one.

### Accounts: a password to sign in, a Sync key to open what is stored

Two secrets, with different jobs:

- **The password signs a person in**, on a page of this service in their browser. The server keeps an Argon2id hash of it (`Passwords`). It can be changed by the person (`/account/password`) or reset by the admin, and nothing is lost when it is: it does not lock anything.
- **The Sync key locks everything stored.** The app makes it and shows it once; the server never sees it. Nobody here can open a stored copy, and a lost key cannot be recovered.

**An account is named by its owner's email address** (`sam@example.com`, kept trimmed and in lower case), which is what they type on the sign-in page. Nothing is ever sent to it: it only names the account, so there is no email verification and no "forgot password" email. Resetting a password is the admin's job (`accounts password`).

Accounts are made by hand. Nobody can make one by asking, and the sign-in page answers a name that does not exist exactly as it answers a wrong password. Eight wrong tries for a name lock that name for a quarter of an hour. An address is plain ASCII (up to 254 characters, the part before the @ up to 64), with nothing that could be taken for a path or markup; a password needs 10 characters or more.

### Deleting an account

An account's owner can delete it at **`/account/delete`** (the sign-in page links to it, and so do the apps' Yaparr Cloud settings): the email address, the password, and the words `delete my account` typed. That removes the account and everything held for it (`AccountErasure`): every signed-in device, the backup, the devices registered to be woken, and the synced records. It cannot be undone. Wrong tries are limited like sign-in (eight, then a quarter of an hour), an admin account is refused (take the portal away first with `accounts admin <email> --remove`), and the log says only that an account was deleted, not whose. `accounts remove` and the admin portal use the same code, so they no longer leave push registrations or synced records behind.

### Looking after accounts

Run these where the service runs, with the same `Service__DataDirectory` (in Docker: `docker exec -it <container> dotnet Yaparr.Cloud.dll accounts ...`). The running service sees the change at once, with no restart.

```
dotnet Yaparr.Cloud.dll accounts add <email> [--password <password>]
dotnet Yaparr.Cloud.dll accounts password <email> [--password <password>]
dotnet Yaparr.Cloud.dll accounts remove <email> [--keep-backup]
dotnet Yaparr.Cloud.dll accounts list
dotnet Yaparr.Cloud.dll accounts admin <email> [--remove]
```

`add` and `password` without `--password` make one up (like `K7QD-M3XF-9PLR-2WTE-HV8N`) and show it once; give it to the person, who can change it on the sign-in page. Typing a password after `--password` leaves it in your shell history, so prefer the made-up one. `password` also signs the account's devices out. `remove` deletes the backup unless told not to. `list` shows how many devices are signed in and the space used.

An account whose name is not an email address (from before accounts were named by one) cannot sign in, and `accounts list` says so: add the person again with their address. An account made by the first version of the service (which kept a public key and no password) shows as `NO PASSWORD` in the list and cannot sign in until `accounts password <name>` gives it one. Backups from that version were locked with the old password, not a recovery code.

### The admin portal (`/admin`)

The same jobs in a browser: the accounts (devices, the backup and when it was made, and what each holds against its limits: kept in sync, pictures left for people who are away, and the gateway today; amber over nine tenths of a limit; each store is read in one pass, not one request for each account), adding one, resetting a password, removing one with its backup, and making, listing and revoking signup codes. It can never read a backup (they are locked on the person's device).

It is **off until you make an account an admin**, on the server: `accounts admin you@example.com` (the account needs a password). That shows, once, the key for an authenticator app (add it by key, or from the `otpauth://` link). Until then every address under `/admin` answers 404. Signing in at `/admin` asks for the email address, the password **and** a code from the app; every wrong try gets the same answer, eight wrong tries lock the address and the name for a quarter of an hour, and each code works once. A session ends after half an hour of nothing (eight hours at the most), is a cookie that scripts cannot read and that is only sent to `/admin` from this site (`HttpOnly`, `SameSite=Strict`, `Secure` over https), and every change is a form carrying a token that belongs to the session. What an admin does is written to the service's log (`docker compose logs cloud`).

The overview has a **Storage** card: where the accounts and codes are kept (files, or PostgreSQL with its version and size), where the backups are (files, or the bucket by name, never by address or key), whether each answers right now (a bucket or database that is down shows as a problem and the page still loads, with the backup columns marked not available), and, on Postgres, when the last daily dump was made (`scripts/pg-backup.sh`, flagged when more than two days old). What is stored for every account is read in one pass, not one request for each account, so a bucket is not asked dozens of times for a page. The disk tile shows only where backups are files on the disk.

Making an account an admin, or taking that away (`accounts admin <email> --remove`), is possible only from the command line, and the portal will not remove an admin account. Running `accounts admin` again makes a new key, which is what to do with a lost phone. The key is kept in `accounts.json` (the service has to check the codes), so protect that file like the password hashes beside it. To keep the portal off the internet altogether, add to the `Caddyfile` a `@admin path /admin*` matcher with `respond @admin 404` (or an `remote_ip` allowlist).

### Signup codes: friends sign themselves up

Instead of making every account by hand, make a **code** and send a link with it in. Anyone with the link can sign up, and **one code can be used by many people** (any number, unless you give it a limit). Everyone who signs up gets a **lifetime pass**: the account's `pass` is `lifetime`, which is recorded and shown in `accounts list`. (Nothing here ends an account on a date.)

```
dotnet Yaparr.Cloud.dll codes add [--note "who it is for"] [--uses 10] [--days 30]
dotnet Yaparr.Cloud.dll codes list
dotnet Yaparr.Cloud.dll codes revoke <id>
dotnet Yaparr.Cloud.dll codes delete <id>
```

`codes add` prints the code and the link, once: only a hash of the code is kept, so it cannot be shown again (the `id` that `list` shows is only for telling codes apart and revoking them). `--uses` limits how many people can use it, `--days` how long it works. `codes list` shows each code, whether it still works (`works`, `used up`, `expired`, `revoked`), and who signed up with it. `revoke` stops a code but keeps its record; `delete` takes it out altogether, with the record of who used it. Either way the people who already signed up keep their accounts.

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
| `POST /v1/token` | `{"code","verifier","deviceKey"?}` | `{token, username}`; `400` if the code is spent, late or the verifier is wrong. `deviceKey` is a random id (16 to 64 letters, digits, `-` and `_`) the app makes once for the device and keeps: the same device signing in again (a reinstall that kept it, a sign-in after being signed out) takes the place of its earlier sign-in instead of adding a duplicate to the list of devices. Only a hash of it is kept; without one (older apps) each sign-in is a device of its own. `machineKey` is a second id derived by the app from the machine itself (a hash of the system install's id and this server's address, never the id itself): the same machine signing in again after the app lost its own id (a wiped profile, a reinstall) replaces its earlier entry, but only when the device name is the same too, because machines cloned from one image share the machine id. |
| `POST /v1/token/handoff` | `{"code"}` from the button on the "You're in" page | `{token, username}`; `400` if the code is spent, late or never made |
| `DELETE /v1/token` | `Authorization: Bearer <token>` | `204`; signs that device out |
| `GET /account/password`, `POST` | the change-password page | signs every device out |
| `PUT /v1/backup` | body: the locked backup; `If-Match: "n"` = the newest version the client knows (`"0"` for none) | `201 {version,size}`; `409` if a newer version exists; `428` without `If-Match`; `413` too big; `400` not a locked backup; `429` too many saves |
| `GET /v1/backup[?version=n]` | | the backup; `X-Yaparr-Version` says which; `404` if none |
| `GET /v1/backup/versions` | | `{username, versions:[{version,size,at}]}`, newest first |
| `DELETE /v1/backup` | | `204`; `404` if none |
| `GET /v1/push/options` | | `{unifiedPush, fcm}`: which ways of waking a phone this service has |
| `PUT /v1/push` | `Bearer`; `{deviceId, kind: "unifiedpush"\|"fcm", endpoint}` | `201 {token}` for a new device (the wake token, shown once); `200 {token:null}` for one already registered; `409` at the device limit |
| `GET /v1/push` | `Bearer` | `{devices:[{deviceId,kind,created}]}` (no tokens, no addresses) |
| `POST /v1/push/{deviceId}/rotate` | `Bearer` | `{token}`, a new wake token; the old one stops at once |
| `DELETE /v1/push/{deviceId}` | `Bearer` | `204` |
| `POST /v1/push/wake/{token}` | no account | `202` sent; `404` no such token; `410` the push service says the device is gone (it is forgotten); `429` too often; `502` could not send |
| `GET /v1/sync[?since=n&limit=k]` | `Bearer` | `{cursor, more, records:[{kind,id,clock,device,deleted,body,cursor}]}`: the records changed after cursor `n` (0 for all), oldest change first, at most 100; `cursor` is where to ask next |
| `PUT /v1/sync` | `Bearer`; `{records:[{kind,id,clock,device,deleted,body}]}` (1 to 100) | `{cursor, stale:["kind/id"]}`; a record whose clock is not newer than the one held is named in `stale` and not taken; `400` for a bad record (nothing is stored), `409` past the account's record limit, `429` too often |
| `GET /v1/plan` | `Bearer` | `{billing:"off"}` when billing is off, else `{billing:"on", state:"lifetime"\|"subscribed"\|"lapsed", plan, endsAt, willRenew}` (see *Paid plans*) |
| `GET /healthz` | | `200` |

Everything under `/v1/backup` needs `Authorization: Bearer <token>`; without a token the service gave out, the answer is `401`. `If-Match` is what keeps two devices from overwriting each other, and also makes a replayed upload a `409` instead of a second copy.

### Push notifications (`PushApi`)

Yaparr's messages go from phone to phone and never pass through this service, so it cannot know that one is waiting. What it can do is **wake a phone**: the person sending finds that the other is not connected and asks this service to wake them, with a **wake token** the other person gave to the people in their rooms (inside the room, where this service never looks). The service sends a push with nothing in it (`{"t":"wake"}`) and forgets it; the phone starts, connects and picks up what it missed. **The sender needs no account here; the person who is woken does**, because only a signed-in device can register.

- **Registering** (`PUT /v1/push`, signed in): a device says how it is woken, through **UnifiedPush** (an https address that the phone's distributor app gave it, such as ntfy) or **Firebase Cloud Messaging** (a device token, only when the service has a service account). A new device is given its wake token once; only a hash is kept (like sign-in tokens). Re-registering changes where it is woken and keeps the token. `rotate` makes a new token and cuts off everyone who had the old one. An account may have `PushMaxPerAccount` (5) devices.
- **Waking** (`POST /v1/push/wake/{token}`): the token is the permission. A token that does not exist, or whose account was removed, answers `404`. A token may be used `PushWakesPerMinute` (6) times a minute and `PushWakesPerDay` (200) a day, so the person who would make a phone buzz all night is stopped, and the cap also bounds what the service sends. The answer tells the sender when the device is gone (`410`), and the registration is forgotten.
- **The service sends only to addresses that are safe to send to.** A UnifiedPush address must be https, a name (not a number), a port of 443 or 1024 and up, with no password in it, and it is checked again when connected to: the service resolves the name itself, refuses loopback, private, link-local and carrier-grade addresses, makes the connection to the address it checked, does not follow redirects, and gives up after 10 seconds (`PushHttp`, `PushEndpoints`). Otherwise a signed-in person could aim the service at the machine's own network.
- **Firebase.** Download a service account key from the Firebase console (Project settings, Service accounts), keep it out of the repository, readable only by the service, and set `Service__PushFcmServiceAccountFile` to its path inside the container (mount it as a volume or a secret). Without it only UnifiedPush works and `GET /v1/push/options` says so. The service trades a signed request for an access token (kept until it runs out) and sends a high priority message with data and no notification (`FcmClient`); a `404` from Firebase means the token is not registered any more.
- **Storage:** `IPushRegistry`, in the database (`push_registrations`, schema version 2) or in `push.json` in the data folder, held to the same behaviour by the same tests (`StoreContractTests`).
- **Logging:** a wake-up request logs its outcome and nothing else (`wake: sent (fcm)`, `wake: gone (unifiedpush)`, `wake: failed (fcm)`, `wake: limited`, `wake: no such token`), so that the log can say whether wake-ups work. It never holds a wake token, an account, a device id, a push address, an IP address or who asked; a test (`PushApiTests`) checks that. The web server's own request logging is off, and the proxy keeps no access log.
- **Not checked against the real services yet:** the UnifiedPush POST and the Firebase calls are tested with a fake server; no distributor and no Firebase project has received one from this code.

### Synced state (`SyncApi`)

What an account should have the same on all its devices (a room joined and its key, the name, a block) is kept as **records**, one for each thing, and the service hands out what changed since a device last asked. It never reads or merges them: the body of a record is locked by the devices (with a key made from the account's recovery code, so this service sees only ciphertext), and the devices do all the merging. What the service does decide is which write of a record is the newest.

- **Record:** `{kind, id, clock, device, deleted, body}`. `kind` is lowercase letters, digits and `-` (`room`, `block`, `profile`), `id` is the thing's own name (up to 128 characters of letters, digits and `. _ - : @ + = / |`), `body` is base64 (at most `SyncMaxRecordBytes` characters; empty only when `deleted`), `device` is the id of the device that wrote it.
- **Clock:** up to 96 characters of letters, digits and `. _ -`, made by the device so that plain text order is write order (a hybrid logical clock: the time, a counter, then the device id as a tiebreak). The service compares clocks as ordinary text, byte for byte, in the files and in the database alike (a test holds both to that), and takes a write only if its clock is greater than the held one. An equal clock is the same write sent again and is ignored; a lower one is named in `stale`.
- **Cursor:** each stored change gets the account's next number (1, 2, 3…), so `GET /v1/sync?since=N` is exactly what changed after N. A device keeps the cursor it was given.
- **Deleting:** a deleted record stays as a marker with no body, so a device that was away still learns of it, and a write older than the marker does not bring the record back. A later write with a higher clock (joining the room again) replaces the marker. Markers count against the limit.
- **Limits:** `SyncMaxRecords` (2000 records and markers an account; a call that would pass it stores nothing), `SyncMaxRecordBytes` (65536), `SyncWritesPerMinute` (30) and `SyncWritesPerDay` (5000) an account.
- **Storage:** `ISyncStore`, in the database (`sync_records`, `sync_cursors`, schema version 3) or as a file for each account in `sync` in the data folder, held to the same behaviour by the same tests (`SyncStoreContractTests`).

### Mailboxes (`MailboxApi`)

A room's members who are away miss what is said in it. Messages travel peer to peer first, and this service is only the last resort: a device leaves a message, **locked**, in the room's **mailbox** only when someone it knows is away (and the person has not turned that off), a minute after writing it, and the away member's devices fetch it when they are back. Nothing is left when everyone known is connected, and the service never carries live traffic (design: `docs/mailbox.md` in the Yaparr repository). The service cannot read what it holds: the items are sealed on the devices with a key made from the room's secret, which the service never has, and the mailbox id is a value only the room's members can compute.

- **Calls:** `POST /v1/mailbox/{id}` with `{items: [{id, body}]}` (up to 20) and `GET /v1/mailbox/{id}?since=N&limit=M` for `{cursor, more, items: [{id, body, at, cursor}]}`, with the device's bearer token. `id` in the path is 43 characters of base64url; an item's `id` is letters, digits, `-` and `_` (up to 64; the app uses the message's own key, so posting the same message again stores it once); `body` is base64.
- **Not given to a reader:** who left an item. The account is kept beside it only to count limits and so that deleting the account (or removing it in the admin portal or with `accounts remove`) removes what it left.
- **Limits:** `MailboxKeepDays` (7, then an item goes whether it was read or not; each post clears its own mailbox and the service clears the rest when it starts), `MailboxMaxItems` (2000 in a mailbox; a call that would pass it stores nothing, 409), `MailboxMaxItemChars` (43700 base64 characters, about 32 KB locked), `MailboxPostsPerMinute` (60) and `MailboxPostsPerDay` (5000), `MailboxReadsPerMinute` (120) and `MailboxReadsPerDay` (20000) an account.
- **What the service sees:** that an account posted to or read a mailbox (an opaque id), when, from which address, and how big the item was; nothing of what is in it.
- **Storage:** `IMailboxStore`, in the database (`mailbox_items`, `mailbox_cursors`, schema version 4) or as a file for each mailbox in `mailbox` in the data folder, held to the same behaviour by the same tests.

### Blobs (`BlobApi`)

Pictures for people who are away (uploaded only when someone known is away): a device locks a picture with a key this service never has and uploads the locked bytes under the id `SHA-256 of those bytes`; a member fetches them by that id and opens them (design: `docs/mailbox.md` in the Yaparr repository).

- **Calls:** `PUT /v1/blob/{id}` with the locked bytes as the body and the device's bearer token (the id has to be the SHA-256 of the body, 64 lower case hex digits, or it is refused); `GET /v1/blob/{id}` with no token, so a reader whose server is another one can still fetch, and the service cannot tie a read to an account (the id is the capability). The service per-address limit applies to fetching.
- **Limits:** `BlobMaxBytes` (9437184: a picture is at most 8 MiB, plus the lock), `BlobQuotaBytes` (268435456, 256 MiB of unexpired blobs an account; a call past it stores nothing, 409), `BlobKeepDays` (7, whether fetched or not; each upload clears the expired ones and the service clears them when it starts), `BlobUploadsPerMinute` (20) and `BlobUploadsPerDay` (500) an account.
- **Deleting:** deleting an account, in the app, the admin portal or `accounts remove`, removes the blobs it uploaded.
- **Storage:** `IBlobStore`, one of two ways, held to the same behaviour by the same tests (`BlobStoreContractTests`):
  - **Files** (the default), `BlobStore`: in `blobs` in the data folder (the bytes in a file named by the id, a small file beside it with the account, size and time), also when the records are in PostgreSQL.
  - **An S3-compatible bucket**, `S3BlobStore`, when `Service__S3Bucket` is set (the same bucket and key as the backups): `<prefix>data/<id>` holds the bytes, with the time they were stored as object metadata, and `<prefix>index/<account hash>/<time>.<size>.<id>` is an empty entry for each blob an account uploaded, so the quota, the sweep of expired blobs and the removal of an account's blobs read one listing and never the blobs. No email address is ever in the bucket. The folder is `Service__S3BlobPrefix` (`blobs/`); it may not contain, or be inside, the backups' or the dumps' folder, or the service will not start. Pictures still go through the service (it checks the id, the size and the quota), so a bucket only takes the disk and its backups off the server: reads and uploads are not sent to the bucket directly. Switching an existing service to a bucket does not move the files already in `blobs`; they stay on the disk and those pictures cannot be fetched (they would have expired within `BlobKeepDays` anyway). A lifecycle rule on the bucket for the blob folder, a day or two longer than `BlobKeepDays`, is a harmless backstop for anything the service did not get to remove.

### Kept pieces (`KeepApi`)

What an account keeps in sync that is too big for a synced record, or has to last: a day of one room's message history, an emoji, a sound, a GIF. A device locks a **piece** with a key this service never has (made from the account's Sync key) and uploads the locked bytes under the id `SHA-256 of those bytes`; which pieces there are, and what each one is, is said by the account's synced records, which carry the ids, so the service never needs to know. A piece is the same format as a picture left for someone who is away, but it is a store of its own and differs in four ways: it **never expires**, it counts against a quota of its own, it is read **only with a sign-in** (the id is not a capability), and the account **removes** the ones it no longer needs.

- **Calls:** `PUT /v1/keep/{id}` with the locked bytes as the body and the device's bearer token (the id has to be the SHA-256 of the body, 64 lower case hex digits: `200 {stored}`, `409` past the quota, `413` too big, `400` for a wrong id, `429`); `GET /v1/keep/{id}` and `DELETE /v1/keep/{id}` with the token (`404` when there is none, or for a delete when it is another account's; `204` when removed).
- **Limits:** `KeepMaxBytes` (16777216, 16 MiB a piece), `KeepQuotaBytes` (1073741824, 1 GiB an account; a call past it stores nothing, 409; removing a piece makes room), `KeepUploadsPerMinute` (60) and `KeepUploadsPerDay` (5000) an account.
- **Deleting:** deleting an account, in the app, the admin portal or `accounts remove`, removes every piece it kept.
- **Storage:** `IKeepStore`, which is an `IBlobStore` (the same two ways, files and bucket, held to the same tests) in a folder of its own, so the sweep of expired pictures can never reach it: **files** in `keep` in the data folder, or the bucket folder `Service__S3KeepPrefix` (`keep/`), which may not contain, or be inside, any other folder of the bucket (the backups', the dumps', the pictures'). Nothing has to be set up: the folder is made when first used.
- **What the service sees:** that an account stored or fetched a piece (an opaque id), when, from which address, and how big it was; nothing of what is in it.

### UDP gateway (`GatewayApi`)

For a device on a network that **blocks UDP** (some workplaces, schools and hotels let only web traffic out). Yaparr's devices talk to each other over UDP, so such a device cannot find anyone or be found. With the gateway on, it sends the datagrams it would have sent inside **one WebSocket on port 443** (which passes where UDP does not), and the service sends them from a UDP socket of its own and hands back what arrives there. The device then has a public address, the service's, and everything above UDP (finding people, connecting, the chat protocol) works unchanged. Only the device that is blocked needs it; the people it talks to do not know.

It is **for messages and voice, not screens**, so it is capped: chat and voice fit in the limits, screen sharing does not (and the apps turn their share and watch buttons off while the gateway is in use). It is **off by default** (`Service__GatewayEnabled=true` turns it on), because it uses the service's bandwidth.

- **Calls:** `GET /v1/gateway` with the device's bearer token, as a WebSocket upgrade (`401` without a sign-in, `404` when off, `429` over the connection limits). One binary WebSocket message is one frame.
- **Frames:** `0x01 DATA | family (4 or 6) | port (2, big endian) | address (4 or 16) | datagram`, to the service (where to send it) and from it (who it came from); `0x10 HELLO | family | port | address`, once, from the service: the address the world is likely to see for this connection; `0x11 LIMIT | reason`, from the service, at most one a second: a datagram was not sent or not handed on (1 speed, 2 the day's bytes, 3 destination, 4 size).
- **Limits** (`appsettings.json` / `Service__…`): `GatewayBytesPerSecond` (24576, about 196 kbit/s) and `GatewayBurstBytes` (131072) for each direction of a connection (a voice is about 90 kbit/s on the wire for each person it goes to, so voice with up to two others fits; a screen share needs several times more and is held back); `GatewayBytesPerDay` (2147483648, 2 GiB) for an account, both directions together (about 8 hours of one-to-one voice); `GatewayMaxPacket` (1400) for a datagram; `GatewayDestinationsPerMinute` (300) different addresses; `GatewayConnectionsPerAccount` (2) and `GatewayMaxConnections` (500); `GatewayIdleSeconds` (300), after which a connection that has carried nothing is closed.
- **Where it may send:** public unicast addresses only. Loopback, private, link-local (the cloud metadata address among them), shared (carrier-grade NAT) and multicast addresses, broadcast, the unspecified address and port 0 are refused, so the service is not a way into the network it runs on. `GatewayAllowPrivate` lifts this for tests and for a service run for one network; never turn it on for a public one.
- **What comes in:** anything sent to the connection's socket is handed on (a device has to be found, so it cannot only hear from places it has sent to), held to the same speed and day as what goes out.
- **Firewall:** the service sends from a UDP port of its own for each connection: by default any port, or one in `GatewayPortMin`..`GatewayPortMax` (open that range for UDP, in and out). `GatewayPublicAddress` is the machine's public address, told to the device as a hint (default: the address it connected to). Behind a reverse proxy, the proxy has to pass WebSockets (`Upgrade` and `Connection` headers).
- **What the service sees:** that an account used the gateway, when, how much, and the addresses its datagrams went to and came from (IP addresses and ports); never what is in them, which the devices have locked between themselves. This belongs on the privacy page.

### Devices and usage (`AccountApi`)

What a signed-in device can ask about its own account, for the apps to show: which devices are signed in, and what the account holds against its limits.

- `GET /v1/devices` with the bearer token: `{devices: [{id, name, created, current}]}`, newest first. `current` marks the device that asked. The `id` is the first 16 hex digits of the hash of the device's token: enough to sign it out, of no use for signing in. `DELETE /v1/devices/{id}`: signs that device out (`204`; `404` when the account has no such device; this device may sign itself out).
- `GET /v1/usage`: `{kept: {usedBytes, quotaBytes}, pictures: {usedBytes, quotaBytes, keptDays}, backups: {count, bytes}, gateway: {enabled, usedTodayBytes, perDayBytes, bytesPerSecond}}`: what the account keeps in sync, what it has left for someone who is away, its backups, and how much of today's gateway allowance it has used (all by UTC day).
- The gateway's daily bytes are kept in `gateway-usage.json` in the data folder (by a short hash of the account, not its name), written now and then and when the service stops, so a restart (every deploy) does not hand everyone a new day. The gateway also limits an address (`GatewayConnectionsPerAddress`, 10 connections at once, and `GatewayAttemptsPerMinute`, 30 tries in a minute, wrong tokens included), as well as an account.

### Paid plans (`PlanGate`; off unless a provider is named)

The hosted service can ask for a subscription (design: `docs/billing.md` in the Yaparr repository). **A service that does not turn it on is exactly what it was**, so a server run for friends or for one network never sees any of this. It is on only when `Service__Billing__Provider` names a payment provider this build has; naming one it has not stops the service starting, and says which it has. This build has the model, the rules, the webhook, the pages and **one provider, Polar** (`Service__Billing__Provider=polar`). Everything that talks to a provider is written against `IBillingProvider`; the service's own tests use a fake one, and the Polar adapter is tested against answers shaped as Polar's API reference shows them. **It has not been tried against Polar's sandbox yet.**

What an account is (`AccountPlan`, `PlanGate`):

| State | When | Signing in on a device | Syncing and the services | Taking data away, deleting, the account pages |
|---|---|---|---|---|
| `lifetime` | the account's pass is `lifetime`: every account made before billing, by `accounts add` or a signup code | yes | yes | yes |
| `subscribed` | some subscription has a paid period that has not ended, plus `Billing__GraceDays` (3) | yes | yes | yes |
| `lapsed` | billing is on, the pass is `none`, and no subscription covers now | **no** | **no** | yes |

A lapsed account is refused with `402` `{"error": "<a sentence>", "code": "subscription_required"}` at: signing in (`POST /login` shows a page, `POST /v1/token` and `/v1/token/handoff` answer `402`), `GET` and `PUT /v1/sync`, `PUT /v1/backup`, `GET` and `POST /v1/mailbox/{id}`, `PUT /v1/blob/{id}`, `PUT /v1/keep/{id}`, `PUT /v1/push` and the gateway; a wake for its push token answers like a token that leads nowhere (`404`; the registration is kept). It can still read its plan, usage and devices, `GET /v1/backup` and `GET /v1/keep/{id}` (the data is theirs to take away), do every `DELETE`, and use the `/account/*` pages. **Nothing is ever removed when an account lapses**, a device that is already signed in keeps its token, and renewing brings everything back with no sign-in.

- **Subscriptions** are kept by `ISubscriptions` (`subscriptions.json` in the data folder, or the database, schema 6: `subscriptions`, `billing_accounts`, `billing_events`), held to the same behaviour by the same tests as the other stores. A subscription is what a provider last said: its status (`Active`, `PastDue`, `Canceling`, `Ended`) and the end of the paid period. `Ended` gives nothing, whatever its dates say. Each account is known to a provider by a random **billing id**, never by its email address.
- **Passes:** `accounts pass <email> lifetime|none`. `lifetime` is never asked to pay; `none` follows the subscriptions. Giving `lifetime` to an account that is being charged says to cancel it where it is charged.
- **`accounts list`** shows each account's state when billing is on.
- **Deleting an account** removes its subscriptions and billing id here. (Cancelling at the provider comes with the provider.)
- **The webhook** (`POST /billing/webhook/{provider}`, public): the raw body is checked by the provider's adapter (`400` for a bad signature or an old timestamp; at most 256 KB, `413` above); the same event is handled once (`billing_events`, kept 30 days); an event that names no subscription is `200` and nothing; then **the provider is asked what the subscription is now, and that is what is stored**, so the order events arrive in, repeats, and what each is called do not matter. A provider that cannot be asked answers `503` and the event is forgotten, so its retry is handled. The provider's name is in the address so that two can be live while moving from one to the other.
- **Reconciling** (every six hours, a minute after the service starts): every subscription that is not over and has not been heard of for three days, or whose period ended a day ago, is asked about again; a provider that cannot be reached or does not know it leaves it as it was. Events older than 30 days and unpaid sign-ups are cleared in the same run.
- **Paying first** (`POST /billing/start`, the website's form: `email`, `interval` = `monthly` or `yearly`): an address with no account gets one (no pass, no password) and a one-use token, and is sent to the provider's page with the account's random billing id as the reference. An address that already has an account (or a lifetime sign-up) is sent to sign in instead and nothing is started. Eight tries in a quarter of an hour from one address, then `429`. `GET /billing/done?t=…` is where the provider sends them back: once the payment is stored (or the provider says it has it) it goes on to the page that chooses a password, which says the payment has arrived and not "lifetime pass"; before that it looks again every two seconds (no script) and after fifteen looks says to come back to the same link. An account made this way that is never paid for, and whose token has run out, is removed.
- **The account page** (`/account/subscription`): the password is asked once, then a ticket (random, in memory, ten minutes) stands for it. It says where the account stands and offers **Go to payment** (monthly or yearly: the provider's page shows the price) and, once something is paid through a provider, **Manage payment and invoices** (the provider's portal, made fresh at each click). A lifetime account has no buttons; one that is already subscribed is not sent to pay a second time.
- **Deleting an account** (the account page, the admin portal) ends everything it pays for at the provider first, at once, and if a provider cannot be reached nothing is deleted. `accounts remove` has no provider to ask, so it says which subscriptions to cancel by hand.
- **Logging:** only what happened (`billing: webhook subscription (polar)`, `bad signature`, `the same event again`, `reconciled 3`), never an address, a reference, a token or a body; a test holds it to that.
- **Not done yet:** a provider (Polar), `accounts billing sync <email>` (it needs a provider, which only the running service has), and the state in the admin portal.

### Limits (`appsettings.json`, section `Service`, or `Service__Name` environment variables)

`MaxBlobBytes` (512 MiB), `KeepVersions` (3) and `KeepDays` (7), `RequestsPerMinute` per address (120), `WritesPerHour` per account (12), `DataDirectory` (`data`), `Billing` (`Provider`, `GraceDays`, `PendingHours`; see *Paid plans*), `PublicUrl` (`https://cloud.yaparr.com`), `SignupPageUrl` (`https://yaparr.com/signup`), `BehindProxy`, and for push notifications `PushWakesPerMinute`, `PushWakesPerDay`, `PushMaxPerAccount` and `PushFcmServiceAccountFile`, and for synced state `SyncMaxRecords`, `SyncMaxRecordBytes`, `SyncWritesPerMinute` and `SyncWritesPerDay`, and for pictures `BlobMaxBytes`, `BlobQuotaBytes`, `BlobKeepDays`, `BlobUploadsPerMinute` and `BlobUploadsPerDay`, and for mailboxes `MailboxKeepDays`, `MailboxMaxItems`, `MailboxMaxItemChars`, `MailboxPostsPerMinute`, `MailboxPostsPerDay`, `MailboxReadsPerMinute` and `MailboxReadsPerDay`.

Set `BehindProxy` to `true` only when a reverse proxy is in front (it should be, for https): then the caller's address comes from `X-Forwarded-For`. Left on without a proxy, anyone can claim any address.

### Storage

Backups are kept one of two ways, behind `IBackupStore` (versions, open, save with the `If-Match` version check, delete):

- **Files** (the default), `BackupStore`: one folder per account (the hex SHA-256 of its email address) with numbered versions, written to a temporary file and moved into place.
- **An S3-compatible bucket** (Cloudflare R2, DigitalOcean Spaces, MinIO), `S3BackupStore`, when `Service__S3Bucket` is set: one object per version under `<prefix><account hash>/v00000003.bin`. A save is written to a temporary file first (so the size and the locked-backup header are checked before anything leaves the machine), then put with `If-None-Match: *`, so two saves of the same version cannot both win, even from two copies of the service (a store that does not take the header: `Service__S3ConditionalWrites=false`). Old versions are deleted by the same rule as for files. `backups copy-to-s3` copies existing files into the bucket. The same tests run against both (`BackupStoreContractTests`); the bucket ones need `YAPARR_TEST_S3` (`endpoint;bucket;access key;secret key;region`, CI runs a test server) and the one that races two copies needs a store that decides conditional puts atomically (`YAPARR_TEST_S3_ATOMIC=1`: R2 does).

The daily dump of the database goes to a folder of its own in the same bucket (`Service__S3DumpPrefix`, `db-dumps/`) when there is one: `scripts/pg-backup.sh` calls `dumps upload`, `dumps prune --days 30`, and records whether it worked for the admin portal; `dumps list` and `dumps get <name>` bring one back. `DumpStore` refuses anything that is not a gzip file with a dump's name, and refuses a dumps folder that overlaps the backups folder, so pruning one cannot reach the other.

Whichever way, the server never holds anything readable: a plain zip, which would hold someone's identity key, is refused. They are not put in the database (the S3 option above is where they go if the disk is not): they are large, already encrypted (so a database could neither compress nor search them), and would be copied into every database backup.

Accounts (email addresses, password hashes, token hashes, passes, the admin flag and authenticator secret) and signup codes (as hashes, with who used them) are kept one of two ways, behind `IAccounts` and `ISignupCodes`:

- **Files** (the default): `accounts.json` and `codes.json` beside the backups. One service instance only, since each keeps its own copy in memory.
- **Postgres**: when `Service__Database` is a connection string. The schema is made and updated when the service starts (`PostgresDatabase.Migrate`; a database from a newer version is refused, not damaged), and the first start on an empty database copies the two files in, in one transaction, then renames them `.imported`. See [docs/setup-guide.md](docs/setup-guide.md), "Moving to Postgres". The same tests run against both (`StoreContractTests`); the Postgres ones run when `YAPARR_TEST_POSTGRES` is a connection string (CI has one), and are skipped otherwise.

**Which versions are kept.** A backup is saved when something that cannot be lost changes (a room, a key, a linked device) and once a day for the history, so saving often must not push the older, known-good copies out: a bad save would then replace every good one. After each save the server keeps the newest `KeepVersions` (3), and the newest version of each of the last `KeepDays` (7) UTC days, counting today, and removes the rest (`Retention`). So an account holds at most ten versions: the latest few, and one from each morning of the week. The newest is never removed. (Before this, the newest five were kept whatever their age.)

**Keeping everything off the machine's disk.** With `Service__RequireRemoteStorage` (`REQUIRE_REMOTE_STORAGE=true` in `.env`) the service refuses to start unless `Service__Database` and the bucket settings (`S3Bucket`, `S3Endpoint`, `S3AccessKey`, `S3SecretKey`) are all set, and says which are missing, so a forgotten setting cannot quietly leave accounts, backups or pictures as files. It is off by default, since files are how it runs without a database or a bucket. What still touches the disk is a backup's temporary staging file while it is checked and uploaded, the database itself, and the daily dump `scripts/pg-backup.sh` leaves in `~/db-backups`.

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

- **Polar** (`PolarBilling`, the only file that knows Polar's words). Make a Polar organization (the sandbox, <https://sandbox.polar.sh>, first), a product for each of monthly and yearly, an organization access token (scopes `checkouts:write`, `customer_sessions:write`, `subscriptions:read`, `subscriptions:write`) and a webhook endpoint at `https://<your service>/billing/webhook/polar` with the subscription and customer events. Then set, **in the environment and nowhere else** (they are secrets): `Service__Billing__Provider=polar`, `Service__Billing__Polar__AccessToken`, `Service__Billing__Polar__WebhookSecret` (the endpoint's `whsec_…`), and the product ids `Service__Billing__Polar__CloudMonthlyProduct` and `Service__Billing__Polar__CloudYearlyProduct`; `Service__Billing__Polar__Server` is `sandbox` or `production` (the default). A half-set Polar stops the service starting and names what is missing. Calls pin Polar's API version (`Service__Billing__Polar__ApiVersion`, `2026-10`) so that a change by Polar is one we choose. The accounts are Polar's *external customer ids* (the random billing id, never an email address; the address is only given to Polar for the receipt). Webhooks are Standard Webhooks (id, timestamp and signature headers; five minutes of tolerance); both Polar's newer secrets and the older ones are accepted. A Polar subscription is `Active`, or `Canceling` when it is set to end with the period, `PastDue`, or `Ended` for anything else (cancelled, unpaid, incomplete, paused, or a state Polar adds later). Deleting an account revokes its subscriptions at once.
