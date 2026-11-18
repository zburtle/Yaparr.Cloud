# Server changes: pictures and backups in R2, accounts in Postgres, nothing else on the disk

For a session that has access to the droplet (SSH) and to the Cloudflare account. The code is in https://github.com/zburtle/Yaparr.Cloud/pull/27
(branch `claude/dreamy-curie-54utj9`). Read `docs/setup-guide.md`, sections 10c (Postgres) and 10d (S3), alongside this: this file adds the order, the pictures,
and the new startup check.

## What the end state is

- Backups and locked pictures are objects in one Cloudflare R2 bucket (`backups/`, `blobs/`, and `db-dumps/` folders).
- Accounts, signup codes, push registrations, sync records and mailboxes are in Postgres (the container on the droplet is fine).
- `REQUIRE_REMOTE_STORAGE=true`, so the service refuses to start if any of that is missing instead of quietly using files in `/data`.
- Allowed on the disk: Postgres data, the dump `scripts/pg-backup.sh` leaves in `~/db-backups`, Caddy's certificates, and a backup's temporary staging file while it is
  uploaded (deleted afterwards).

## Rules for this session

- **Never print, paste, log or commit a secret** (R2 keys, the Postgres password). Write them to `.env` directly; `chmod 600 .env`. When reporting back, name settings, not values.
- **Do not merge the pull request or deploy without the owner's go-ahead** in this conversation. Ask once, at step 5.
- The code was written without being compiled or run. **Do not deploy unless CI on the PR is green** (the bucket tests run in CI against a test server).
- Skip any step that is already done. Step 0 tells you which.
- If a step fails, stop, say what failed, and do not improvise a workaround on production data.

## 0. Look first (changes nothing)

On the droplet, in `~/Yaparr.Cloud`:

```bash
git status --short && git log --oneline -3
./deploy.sh check
docker compose ps
grep -oE '^(COMPOSE_PROFILES|SERVICE_DATABASE|POSTGRES_PASSWORD|S3_[A-Z_]+|REQUIRE_REMOTE_STORAGE)=' .env    # names only, never the values
docker compose exec cloud dotnet Yaparr.Cloud.dll accounts list
docker compose exec cloud sh -c 'du -sh /data; ls /data'
df -h /
```

Write down: is Postgres already on (`SERVICE_DATABASE`)? Is the bucket already on (`S3_BUCKET`)? How many accounts, and how big is `/data`? If `/data` has a
`blobs` folder, note its size (those pictures are not moved, see step 6).

## 1. Take a copy before changing anything

```bash
docker compose cp cloud:/data ./cloud-data-$(date +%F)
```

If Postgres is already running, also run `scripts/pg-backup.sh` once and confirm it prints "wrote ...".

## 2. Cloudflare R2 (skip if the bucket and token exist)

Ask the owner **before creating the bucket** about its location: Automatic, a location hint, or the EU jurisdiction. **A bucket's jurisdiction cannot be changed
afterwards.**

1. R2 Object Storage: create a bucket (suggested name `yaparr-data`; the older docs say `yaparr-backups`, any name works).
2. Make an **S3 API token** with **Object Read & Write**, limited to that one bucket. Keep the Access Key ID and Secret Access Key; they are shown once.
3. The endpoint is `https://<account id>.r2.cloudflarestorage.com` (with `.eu.` before `r2` for the EU jurisdiction), **without** the bucket name.
4. Optional backstop: a lifecycle rule that deletes objects under the prefix `blobs/` after 9 days (the service keeps pictures 7). **Only `blobs/`.** Do not put a
   rule on `backups/` (the service keeps the newest versions on its own rule) or `db-dumps/`.

## 3. Postgres (skip if `SERVICE_DATABASE` is already set)

Follow section 10c steps 1 to 4 of `docs/setup-guide.md`: add `COMPOSE_PROFILES=postgres`, `POSTGRES_PASSWORD` (long, random, no `;` or `=`) and `SERVICE_DATABASE` to
`.env`. The first start with an empty database copies `accounts.json` and `codes.json` in. Turn swap on first if the droplet has 1 GB (section 2 of the guide).
Set up the cron line for `scripts/pg-backup.sh` from step 4 of that section.

You can do this in the same deploy as step 4; they do not depend on each other.

## 4. Point the service at the bucket

Add to `.env` (see `.env.example`): `S3_ENDPOINT`, `S3_BUCKET`, `S3_ACCESS_KEY`, `S3_SECRET_KEY`, `S3_REGION=auto`. Leave `S3_CONDITIONAL_WRITES` at its default (R2
takes conditional writes). **Do not set `REQUIRE_REMOTE_STORAGE` yet.**

## 5. Merge and deploy (needs the owner's go-ahead)

1. Check the PR's CI is green. Tell the owner, and ask whether to merge PR 27 into `master` and deploy now. A good time is when few people are using it: the next step
   has a short window where backups look empty.
2. After the merge, wait for CI to publish the image, then on the droplet: `./deploy.sh deploy`. The script rolls back by itself if the service does not become healthy.
3. **Immediately** copy the existing backups into the bucket (until you do, people see no backup):

   ```bash
   docker compose exec cloud dotnet Yaparr.Cloud.dll backups copy-to-s3 --dry-run
   docker compose exec cloud dotnet Yaparr.Cloud.dll backups copy-to-s3
   ```

   It is safe to run again. A line that says "Left alone" needs a look.

## 6. What happens to pictures

Pictures already in `/data/blobs` are **not** copied. After this deploy the service reads and writes them in the bucket only, so those pictures can no longer be fetched.
They would have expired within 7 days anyway, but anyone who was offline with a picture waiting will miss it. Tell the owner this before step 5, and leave the old
folder alone until step 9.

## 7. Check it works

- `./deploy.sh check` and `docker compose ps`: the service is healthy.
- Admin portal (`/admin`), Storage card: backups say Cloudflare R2 by bucket name, the database is PostgreSQL, both answer.
- `docker compose exec cloud dotnet Yaparr.Cloud.dll accounts list`: the same accounts and sizes as step 0.
- In the Yaparr app on a spare profile: **Back up now**, then **Restore**. The owner may need to do this part.
- Pictures: send a picture to a room (or have the owner do it), then list the bucket and confirm there are objects under `blobs/data/` and `blobs/index/<64 hex digits>/`,
  and that no email address appears in any key. With `rclone` or `aws s3 ls --endpoint-url <endpoint> s3://<bucket>/blobs/ --recursive` using the token's key.
- Nothing new is written to `/data`: `docker compose exec cloud sh -c 'ls -la /data /data/blobs'` shows no files newer than the deploy except what step 9 removes.
- `scripts/pg-backup.sh` once: it prints "wrote ...", and the admin portal's dump row says the last dump reached the bucket.

## 8. Turn the startup check on

1. Add `REQUIRE_REMOTE_STORAGE=true` to `.env`, then `docker compose up -d` (or `./deploy.sh deploy`). The service must come back healthy.
2. Prove the check works without touching production: run the image once with the bucket and database settings missing. It must exit with an error naming the five
   settings, and publish nothing:

   ```bash
   docker run --rm -e Service__RequireRemoteStorage=true "$(docker compose images -q cloud | head -1)"
   ```

   (If that image id does not work, use `ghcr.io/zburtle/yaparr-cloud:latest`.) Expected message: "Service__RequireRemoteStorage is on ... but these are not set:
   Service__Database, Service__S3Bucket, Service__S3Endpoint, Service__S3AccessKey, Service__S3SecretKey."

## 9. Clean up (only when the owner is satisfied, a few days later is fine)

Take another copy first (`docker compose cp cloud:/data ./cloud-data-$(date +%F)`), then:

```bash
docker compose exec cloud sh -c 'rm -r /data/[0-9a-f]*'    # the old backup folders, named by a 64-digit hash; leaves accounts.json.imported etc.
docker compose exec cloud sh -c 'rm -rf /data/blobs'        # the old pictures
```

Leave `*.imported` files and `last-dump.json`. Keep the `cloud-data-<date>` copies until the owner says to remove them.

## 10. Rolling back

- To stop requiring remote storage: remove `REQUIRE_REMOTE_STORAGE` from `.env` and redeploy.
- To go back to files for backups: section 10d step 5 of the guide (the objects have to be copied back, for example with `rclone copy`). Pictures in the bucket are not copied
  back by anything; they would simply be unavailable until the next upload.
- To go back to files for accounts: section 10c step 5.

## Report back with

A short checklist of each step as done / skipped (already done) / blocked, the sizes before and after (`/data`, bucket), the result of the restore test, and anything
that said "Left alone" or failed. No secrets.
