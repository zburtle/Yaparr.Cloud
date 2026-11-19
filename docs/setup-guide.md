# Setting up Yaparr's backup server (cloud.yaparr.com)

This guide takes you from nothing to friends signing up and backing up. It covers three things that live in three places:

| Piece | Where it runs | Repository |
| --- | --- | --- |
| **The backup server** (this repository): the sign-in pages and the API | a DigitalOcean droplet, at `cloud.yaparr.com` | `Yaparr.Cloud` (public, AGPL-3.0) |
| **The website**, with the sign-up page at `/signup` | GitHub Pages, at `yaparr.com` (it already is) | `Yaparr.Web` |
| **The app**, which is released with `cloud.yaparr.com` filled in | your friends' computers | `Yaparr` |

Do them in this order: the server, then the website, then the app. Each step says how to check that it worked.

## 1. What you need

- A DigitalOcean account.
- Control of the DNS for `yaparr.com` (wherever it is managed: the registrar, DigitalOcean, Cloudflare).
- Access to the three GitHub repositories.
- About an hour, most of it waiting for the first build.

## 2. The droplet

1. In DigitalOcean choose **Create > Droplets**.
   - **Image:** Ubuntu 24.04 (LTS).
   - **Size:** Basic with **2 GB of memory** is comfortable (about $12 a month at the time of writing; check the price). 1 GB runs the server, but building the image can run out of memory on it, so if you choose 1 GB, add swap first (`sudo fallocate -l 2G /swapfile && sudo chmod 600 /swapfile && sudo mkswap /swapfile && sudo swapon /swapfile`). Backups are small, so the disk size matters little at first.
   - **Region:** near most of your friends.
   - **Authentication:** an SSH key, not a password.
   - Turn on **Backups** (weekly snapshots, a fifth more on the price). They are your safety net if the machine is lost.
2. Note the droplet's IP address.
3. The firewall is done on the machine itself in step 4 (`ufw`), which costs nothing. DigitalOcean's **Cloud Firewall** (Networking > Firewalls) sits outside the machine and, as far as I know, is also free; if you want a second layer, allow inbound **TCP 22** (from your own IP address if it is steady), **TCP 80**, **TCP 443** and **UDP 443**, and attach it to the droplet. It is optional.

## 3. The DNS name

In the DNS for `yaparr.com` add:

| Type | Name | Value |
| --- | --- | --- |
| `A` | `cloud` | the droplet's IPv4 address |
| `AAAA` | `cloud` | the droplet's IPv6 address (optional; only if you enabled IPv6) |

Leave the records for `yaparr.com` itself alone: they point at GitHub Pages and keep doing so. Check from your computer once it has spread (usually minutes):

```bash
nslookup cloud.yaparr.com
```

It must show the droplet's address before the next step, or Caddy cannot get its certificate.

## 4. Prepare the machine

Log in as root, make a normal user, and install Docker:

```bash
ssh root@<droplet ip>
adduser deploy
usermod -aG sudo deploy
apt update && apt -y upgrade
curl -fsSL https://get.docker.com | sh
usermod -aG docker deploy
reboot
```

Then turn on the firewall (still as `root`, before the reboot, or `sudo` afterwards). SSH is rate-limited, and only the ports Caddy needs are open:

```bash
ufw default deny incoming
ufw default allow outgoing
ufw limit 22/tcp
ufw allow 80/tcp
ufw allow 443/tcp
ufw allow 443/udp
ufw --force enable
ufw status verbose
```

Test that a **new** SSH login works before you close the one you are in. One thing to know about `ufw` and Docker: Docker publishes the ports of a container around `ufw`, so `ufw` cannot close a port that a container publishes. That is fine here: the only ports the compose file publishes to the world are Caddy's 80 and 443, and the service's own 8080 is bound to `127.0.0.1`. But if you ever publish another port, `ufw` will not block it; bind it to `127.0.0.1` or use the Cloud Firewall.

Log in again as `deploy` (`ssh deploy@<droplet ip>`; copy your SSH key across first with `ssh-copy-id` if it asks for a password). `docker compose version` should work without `sudo`. Optional but wise: `sudo apt install unattended-upgrades` for automatic security updates.

## 5. Get the code onto the machine

The repository is private, so the machine needs its own read-only key:

```bash
ssh-keygen -t ed25519 -N "" -f ~/.ssh/yaparr-cloud
cat ~/.ssh/yaparr-cloud.pub
```

On GitHub open the `Yaparr.Cloud` repository, **Settings > Deploy keys > Add deploy key**, paste that line, leave "Allow write access" off. Then:

```bash
printf 'Host github.com\n  IdentityFile ~/.ssh/yaparr-cloud\n  IdentitiesOnly yes\n' >> ~/.ssh/config
git clone git@github.com:zburtle/Yaparr.Cloud.git
cd Yaparr.Cloud
```

(If you use Komodo, you can instead create a Stack with a Git Repo source on this droplet; the rest of this guide is then done in Komodo, and the commands that start with `docker compose exec` are run in the `cloud` service's terminal.)

## 6. Start it

```bash
docker compose up -d --build
```

The first build takes several minutes. Then:

```bash
docker compose ps                  # both "cloud" (healthy) and "caddy" are running
docker compose logs caddy | tail   # look for "certificate obtained successfully"
```

`DOMAIN` defaults to `cloud.yaparr.com`, so there is nothing to configure. (`.env.example` lists the optional settings, like other ports, if you ever need them.)

**Check:** from any computer, `curl https://cloud.yaparr.com/healthz` prints `{"ok":true}`, and `https://cloud.yaparr.com/account/password` shows a "Change your password" page in a browser, with a padlock.

## 7. Your account and your first signup code

```bash
docker compose exec cloud dotnet Yaparr.Cloud.dll accounts add you@example.com
docker compose exec cloud dotnet Yaparr.Cloud.dll codes add --note "friends"
```

- The first command makes your own account and shows its made-up password once. Write it down.
- The second makes a signup code and prints a link like `https://yaparr.com/signup#server=https%3A%2F%2Fcloud.yaparr.com&code=K7QD-M3XF-9PLR-2WTE`. That is what you send to friends. It is shown once; only a hash of the code is kept. One code works for any number of people unless you add `--uses 10`, or `--days 30` to end it. `codes revoke <id>` stops one (`codes list` shows the ids and who signed up).

## 8. The website

The sign-up page is part of the website repository (`Yaparr.Web`), and the site is already on GitHub Pages: pushing to `master` runs `.github/workflows/pages.yml`, which tests, builds and deploys it, and the `CNAME` file keeps `yaparr.com` pointed at it. So:

1. Merge the website's sign-up pull request.
2. Wait for the **Deploy site** workflow to finish (the repository's Actions tab).
3. In the repository's **Settings > Pages**, check: Source is **GitHub Actions**, the custom domain is `yaparr.com`, and **Enforce HTTPS** is ticked.

GitHub Pages can host this: the page is static and the form posts straight to `cloud.yaparr.com`, so Pages never runs anything. (Pages answers an address like `/signup` with `404.html`, which the build makes a copy of the app, so the page loads; `/join` and `/chat` already work the same way.)

**Check:** open the link from step 7. You should see "Sign up for Yaparr", with `cloud.yaparr.com` named in the text. Try it with a spare email address: after "Sign up" you land on the server's "Choose your password" page, and then "You're in". `docker compose exec cloud dotnet Yaparr.Cloud.dll accounts list` shows the new account with a `lifetime pass`.

## 9. The app

Merge the app's pull request. The app is released with `yaparr.release.json` beside it, naming `https://cloud.yaparr.com`, so Settings > Backup > Backup server and the first-start "Restore from a server" are already filled in. Until a release has that, friends can type `https://cloud.yaparr.com` themselves.

**Check:** install the new version, open Settings > Backup, choose **Sign in with your browser**, sign in with the email address and password from step 8, then **Back up now**. Yaparr shows a **Sync key** once: save it. Then on a spare computer or profile, choose **Restore from Yaparr Cloud** on the first-start screen, type the key, sign in, and the account comes back.

## 10. Day to day

| To | Run (in the `Yaparr.Cloud` folder) |
| --- | --- |
| Update the server | `./deploy.sh deploy` (gets `master`, pulls the image CI published for that commit, or builds it if there is none, waits for the service to be healthy and goes back by itself if it is not). With automatic deploys (section 10a) a merge to `master` does this for you. |
| See what is running | `./deploy.sh check` |
| See what it is doing | `docker compose logs -f cloud` |
| Add a friend by hand | `docker compose exec cloud dotnet Yaparr.Cloud.dll accounts add them@example.com` |
| Reset a password | `docker compose exec cloud dotnet Yaparr.Cloud.dll accounts password them@example.com` |
| Remove someone (and their backup) | `docker compose exec cloud dotnet Yaparr.Cloud.dll accounts remove them@example.com` |
| Who has accounts, and the space used | `docker compose exec cloud dotnet Yaparr.Cloud.dll accounts list` |
| Make, list or stop signup codes | `docker compose exec cloud dotnet Yaparr.Cloud.dll codes add` / `codes list` / `codes revoke <id>` |
| Copy all the data off the machine | `docker compose cp cloud:/data ./cloud-data-$(date +%F)` |

## 10c. Moving to Postgres (optional)

By default the accounts and signup codes are two JSON files in the data volume, which limits the service to one instance and rewrites a whole file on every change. Postgres, in a second container on the same machine, removes those limits. The backups stay files either way (they are big and already encrypted: see the README).

1. On the machine, in `~/Yaparr.Cloud`, add three lines to `.env` (make a long random password, for example with `openssl rand -base64 24`; avoid `;` and `=` in it):

   ```
   COMPOSE_PROFILES=postgres
   POSTGRES_PASSWORD=the long password
   SERVICE_DATABASE=Host=postgres;Database=yaparr;Username=yaparr;Password=the long password
   ```

2. **Make a copy of the data folder first**: `docker compose cp cloud:/data ./cloud-data-$(date +%F)`.
3. Deploy a version with this change (`./deploy.sh deploy`, or a merge to `master` with automatic deploys). The first start with a database that has nothing in it copies `accounts.json` and `codes.json` in and renames them `accounts.json.imported` and `codes.json.imported`. Check with `docker compose exec cloud dotnet Yaparr.Cloud.dll accounts list` and `codes list`: everyone is there, and everyone stays signed in.
4. **Back the database up** along with the backup files: `scripts/pg-backup.sh` makes a gzipped `pg_dump` in `~/db-backups`, checks it, keeps the last 14, and tells the service about it (`last-dump.json` in the data folder), so the admin portal's Storage card shows when the last dump was made and flags one more than two days old. Run it from cron as the user that runs docker compose: `17 3 * * * /home/deploy/Yaparr.Cloud/scripts/pg-backup.sh >> /home/deploy/db-backups/cron.log 2>&1`. The `pg-data` volume holds the live database; the data volume holds the backups and the `.imported` files. **With a bucket (section 10d), each dump is also put in the bucket**, in a folder of its own (`db-dumps/`, apart from the backups: `Service__S3DumpPrefix`), by `dumps upload` run inside the service's container, and dumps there older than 30 days are removed (`dumps prune`, never the newest). The admin portal's dump row says whether the last one reached the bucket and shows a problem if it did not. The bucket token (Object Read & Write on that one bucket) is all it needs. To get one back: `docker compose exec -T cloud dotnet Yaparr.Cloud.dll dumps list`, then `docker compose exec -T cloud dotnet Yaparr.Cloud.dll dumps get yaparr-2026-11-15.sql.gz > yaparr.sql.gz`. To restore it into an empty database: stop the service, `docker compose exec -T postgres psql -U yaparr -d postgres -c 'drop database yaparr' -c 'create database yaparr owner yaparr'`, then `gunzip -c yaparr.sql.gz | docker compose exec -T postgres psql -U yaparr yaparr`, and start the service. A dump holds password hashes, so keep downloaded copies private. Without a bucket the dumps stay on the machine only.
5. To go back to files: take the three lines out of `.env`, move the `.imported` files back to `accounts.json` and `codes.json` (changes made while on Postgres are not in them), and deploy.

**Size.** The container is tuned small (`shared_buffers` 32 MB, 20 connections, a 256 MB limit) and uses about 25 MB when idle; the service uses about 75 MB and Caddy about 12 MB. That fits 1 vCPU and 1 GB with room to spare, but turn on swap first (the 1 GB advice in section 2) and watch `docker stats` for a while before shrinking a bigger droplet. Each sign-in uses 32 MB for a moment (the password hash), so many at once cost more.

**Upgrading Postgres.** The image is `postgres:17-alpine`: security fixes within version 17 arrive by pulling the image and restarting (`docker compose pull postgres && docker compose up -d`). Moving to a new major version is a dump and restore (`pg_dump`, a new volume, `psql`), not a pull.

## 10d. Moving the backups to S3 (Cloudflare R2 or DigitalOcean Spaces; optional)

By default the backups are files in the data volume, so how many the machine can keep depends on its disk, and losing the machine loses them. In an S3-compatible bucket they are off the machine (and a smaller droplet will do). What is stored is only what the app has locked, so the bucket holds nothing readable either way.

**Cloudflare R2** (no monthly minimum, no charge for downloads, the first 10 GB free; about $0.015 per GB-month after that):

1. In the Cloudflare dashboard, **R2 Object Storage**, turn R2 on (it asks for a payment method even for the free tier) and **Create bucket**, for example `yaparr-backups`. Pick the location deliberately: **Automatic** is closest to where it is made, a location hint or the **EU jurisdiction** can be chosen only now, **a bucket's jurisdiction cannot be changed afterwards**.
2. **Manage API tokens**, make an **S3 API token** with **Object Read & Write**, limited to that one bucket. Copy the Access Key ID and Secret Access Key now, they are shown once.
3. The endpoint is `https://<your account id>.r2.cloudflarestorage.com` (the EU jurisdiction has `.eu.` before `r2`): the account id, **without** the bucket name.

**DigitalOcean Spaces** works the same with its own endpoint (`https://<region>.digitaloceanspaces.com`) and region (`nyc3`): $5 a month for 250 GiB and 1 TiB of transfer. Spaces may not take conditional writes: if the tests or a save complain about the `If-None-Match` header, set `S3_CONDITIONAL_WRITES=false` in `.env`.

Moving over:

1. Add to `.env` on the machine (see `.env.example`): `S3_ENDPOINT`, `S3_BUCKET`, `S3_ACCESS_KEY`, `S3_SECRET_KEY`, and `S3_REGION` (`auto` for R2).
2. Deploy the version that has this (`./deploy.sh deploy`). **Before it carries on, copy the existing backups** (the service reads the bucket from then on, so until they are copied people see no backup): `docker compose exec cloud dotnet Yaparr.Cloud.dll backups copy-to-s3 --dry-run`, then without `--dry-run`. It can be run again and again (a version already there is left alone, one with another size is reported, nothing is removed from the data folder). Do it right after the deploy; a backup saved in between goes to the bucket as it should, and a restore in between finds only that one.
3. Check: sign in on the backup server in Yaparr and **Back up now**, then **Restore** on a spare profile; `docker compose exec cloud dotnet Yaparr.Cloud.dll accounts list` shows the sizes from the bucket.
4. When you are satisfied, remove the old files from the data volume (`docker compose exec cloud sh -c 'rm -r /data/[0-9a-f]*'`: the folders named by a 64-digit hash; leave `accounts.json` and the rest). Take a copy of the data folder first (`docker compose cp cloud:/data ./cloud-data-$(date +%F)`).
5. To go back to files: take the S3 lines out of `.env`, copy the objects back (for example with `rclone copy`), and deploy.

**Cost.** Storage dominates: with backups of about 50 MiB and 5 versions each, 100 accounts is about 24 GiB (R2: about $0.20 a month), 1,000 accounts about 244 GiB (about $3.50), 5,000 accounts about 1.2 TiB (about $18). Requests are small (a save is one put and one list) and R2's free tier covers a million of each class a month. Downloads are free on R2.

## 10b. The published image

After a merge to `master`, CI builds the image and publishes it to `ghcr.io/zburtle/yaparr-cloud`, tagged `sha-<commit>` and `latest`. `deploy.sh` pulls the one for the commit it checked out, so the droplet no longer builds anything (and needs no spare memory or disk for it). If there is no such image (a branch, or `ghcr.io` cannot be reached) it builds the image itself, as before. Anyone can run the service without cloning this repository: `docker run -p 8080:8080 -v cloud-data:/data ghcr.io/zburtle/yaparr-cloud:latest`.

The first time the image is published GitHub makes the package. Open it (the repository's **Packages** section) and check its settings say Public. The package is tied to this repository by a label in the Dockerfile, which normally gives it the repository's visibility; if it is private, set it public by hand once (the API cannot do that).

## 10a. Automatic deploys (optional)

The `CI` workflow (`.github/workflows/ci.yml`) runs on every pull request and every push: the tests, a check that the compose file and the Caddyfile are valid, and a build of the image that starts the service in it and checks it comes up healthy and that the admin commands work. That needs nothing from you.

After a merge to `master` it can also **deploy** to the droplet, if you give it a way in. The way in is a key that can run `deploy.sh` and nothing else (a *forced command*: whatever the key asks for, the droplet runs the script, which accepts only `deploy` and `check`). Until you set it up, the deploy step says so and does nothing, and you deploy by hand with `./deploy.sh deploy`.

To turn it on, run these on your own computer (PowerShell), then on the droplet:

```powershell
# 1. Make a key for GitHub to use, with no passphrase (it is used by a script)
ssh-keygen -t ed25519 -N '""' -C "github-actions deploy (Yaparr.Cloud)" -f ci_deploy_key

# 2. Tell GitHub the key, where to go, and which machine it should trust
Get-Content -Raw ci_deploy_key | gh secret set DEPLOY_SSH_KEY --repo zburtle/Yaparr.Cloud
gh secret set DEPLOY_HOST --repo zburtle/Yaparr.Cloud --body "cloud.yaparr.com"
ssh-keyscan -t ed25519 cloud.yaparr.com | gh secret set DEPLOY_KNOWN_HOSTS --repo zburtle/Yaparr.Cloud

# 3. Show the public half, to put on the droplet
Get-Content ci_deploy_key.pub
```

On the droplet, as `deploy`, add one line to `~/.ssh/authorized_keys`, made of the restrictions and the public key from step 3:

```
command="/home/deploy/Yaparr.Cloud/deploy.sh",no-port-forwarding,no-X11-forwarding,no-agent-forwarding,no-pty ssh-ed25519 AAAA... github-actions deploy (Yaparr.Cloud)
```

`DEPLOY_HOST` has to lead to the droplet itself. **A name that goes through Cloudflare's proxy (the orange cloud) does not work**, because the proxy passes web traffic and not SSH: the deploy job then waits and fails with "Network is unreachable" or "Connection timed out". Set the DNS record to **DNS only** (grey cloud), or use the droplet's own address as `DEPLOY_HOST` (and `ssh-keyscan` that address for `DEPLOY_KNOWN_HOSTS`, checking the fingerprints against `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` on the droplet). The deploy step tries three times with a 20 second connect timeout, and prints what the name resolves to; an address that is not the droplet's is the sign of a proxied record.

Then delete `ci_deploy_key` and `ci_deploy_key.pub` from your computer (GitHub has its own copy as a secret). To check it, run `ssh -i ci_deploy_key deploy@cloud.yaparr.com check` before you delete it: it prints the branch and commit that are running and whether the service is healthy. To turn automatic deploys off, delete that line from `authorized_keys` (and the `DEPLOY_SSH_KEY` secret).

What this gives and what it costs: anyone who could run GitHub Actions in this repository (you) can deploy whatever is on `master` to the droplet, and nothing else; the key cannot open a shell. Since `deploy.sh` is part of the repository, whatever is merged into `master` runs there, so protect `master` the way you protect any branch that deploys.

Resetting a password loses nothing: what is stored is locked with each person's Sync key, which the server never has, so you cannot open anyone's data and a lost key cannot be recovered. Everything the server keeps is in the `cloud-data` volume (accounts with password hashes, signup codes as hashes, and the locked backups); the droplet backups cover it, and the `docker compose cp` line above makes a copy you can keep elsewhere.

## 11. When something is wrong

| What you see | Probably |
| --- | --- |
| Caddy logs say it could not get a certificate | The DNS name does not point at the droplet yet (check `nslookup`), or ports 80 or 443 are blocked (check the Cloud Firewall). Caddy retries by itself once that is fixed. |
| The app says "answered 502" | The proxy cannot reach the service: `docker compose ps` should show `cloud` as healthy; `docker compose logs cloud` says why if it is not. |
| "Couldn't reach the backup server" | The name does not resolve, the droplet is down, or the app has the wrong address. |
| An upload fails with "too big" | The backup is over 512 MiB; raise `Service__MaxBlobBytes` in `docker-compose.yml` (and `max_size` in the `Caddyfile`) and run the update command. |
| "This sign-up link does not work" | The code was revoked, used up or has ended (`codes list` shows which), or it was cut short when pasted. |
| A friend lost their password | `accounts password <email>`. Their backups are fine. |
| A friend lost their Sync key | Nothing can open what is already stored. They can start a new key (the app offers "I have lost it" when they sign in), and the old data stays unreadable. |

## 12. Costs and care

- The droplet is the only cost: roughly $12 a month, plus about 20% for backups.
- Keep the system updated (`sudo apt update && sudo apt upgrade`, and the `git pull` update above).
- Watch the disk (`df -h`): each person's backups keep the last five versions. `accounts list` shows who uses what.
- SSH takes keys only (the droplet's default), and `ufw limit` slows anyone who keeps trying. If your own address is steady, `ufw allow from <your address> to any port 22` and removing the general rule (`ufw delete limit 22/tcp`) is stricter, but you can lock yourself out if your address changes; the DigitalOcean web console is the way back in.
