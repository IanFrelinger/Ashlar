# Deploying Job Search Studio on AWS

One EC2 server runs everything (owner decision 2026-11-08, [STUDIO.md](../docs/STUDIO.md) §1):

```
Internet ──443──▶ Caddy (TLS, no access log) ──pod:8080──▶ studio ──127.0.0.1:11434──▶ Ollama (private model)
                                                             │  └── certify-step / step-host child processes
                                                             ├──▶ Claude API (generic step designs only, PII-free frames)
                                                             └──▶ job boards, Google APIs
host: hourly backup ──▶ S3 (SSE-KMS, versioned, expiring)     secrets: SSM Parameter Store (SecureString, KMS)
```

The studio and Ollama share one network namespace, held by a small `pod` container, so the studio reaches the model
on loopback: the only address the app accepts for a model that may read personal data
([Models.cs](../src/JobSearch.Host/Studio/Models.cs)). Nothing else reaches the model. Caddy, in its own namespace,
reaches only the studio's port.

| File | What it is |
|---|---|
| [`Dockerfile`](Dockerfile) | The studio image: SDK build stage, ASP.NET runtime stage, non-root, `/data` volume. Build from the repository root. |
| [`docker-compose.yml`](docker-compose.yml), [`Caddyfile`](Caddyfile) | The stack on the server. |
| [`host/jobsearch`](host/jobsearch) | The operator command on the server: `up`, `rollout TAG`, `backup`, `health`, `env`. |
| [`host/*.service`, `host/*.timer`](host) | systemd: the stack at boot, the hourly backup. |
| [`terraform/`](terraform) | The AWS resources and the first-boot script. |
| [`deploy.sh`](deploy.sh) | Push an image and roll the server to it through SSM. |

## Cost

These are on-demand prices in us-east-1 as of 2026-11. Check the AWS pricing pages for current figures.

| Item | Monthly |
|---|---|
| m7i.xlarge (4 vCPU, 16 GiB), always on | about $147 |
| EBS gp3: 40 GB root and 100 GB data | about $11 |
| Public IPv4 address | about $4 |
| KMS key, S3 backups, ECR images | a few dollars |
| Claude API (step writing only, capped at 10 new steps per user per month) | usage |

A one-year Savings Plan lowers the instance cost by roughly a third. The private model costs nothing per call: it is
the instance's CPU.

## Before you start

- An AWS account and credentials that may create the resources in `terraform/main.tf`.
- Terraform 1.6 or newer, the AWS CLI, and Docker.
- A domain name for the studio, for example `studio.example.com`.
- A **Google OAuth client** of type *Web application* (Google Cloud console, APIs & Services, Credentials), with:
  - authorised redirect URI `https://<domain>/auth/google/callback`;
  - the Drive and Sheets APIs enabled on the project.
- An **Anthropic API key**, for writing new steps. The studio runs without one; the forge then says it cannot write
  steps.

## 1. Create the resources

```bash
cd apps/job-search/deploy/terraform
cp example.tfvars terraform.tfvars          # set domain and acme_email
terraform init
terraform apply
```

Terraform state holds no secrets, because none pass through Terraform. Even so, keep the state somewhere private;
an S3 backend with encryption is the usual choice.

Then point the domain's A record at `terraform output -raw public_ip`, or set `route53_zone_id` and Terraform creates
the record. Caddy obtains the certificate once DNS resolves.

## 2. Put the secrets in the parameter store

Every parameter under the path becomes one environment variable of the studio container, and nothing else reads
them. Use `read -s` so that no secret lands in your shell history:

```bash
P=$(terraform output -raw parameter_path); K=$(terraform output -raw kms_key_alias)
put() { aws ssm put-parameter --name "$P/$1" --type SecureString --key-id "$K" --overwrite --value "$2" >/dev/null; }

put GOOGLE_CLIENT_ID "1234-abc.apps.googleusercontent.com"
read -rsp "Google client secret: " s && put GOOGLE_CLIENT_SECRET "$s"; echo
read -rsp "Anthropic API key: " s && put ANTHROPIC_API_KEY "$s"; echo
put JOBSEARCH_SIGNING_KEY "$(openssl rand -base64 32)"
put JOBSEARCH_MASTER_KEY "$(openssl rand -base64 32)"
unset s
```

What the two generated keys do:

- **`JOBSEARCH_MASTER_KEY`** encrypts the stored Google tokens (AES-GCM). It also keys the erasure ledger's hashes.
  - Never replace it: there is no re-encryption yet (known limit). A new key orphans every stored connection, so users
    reconnect, and it orphans the ledgers of earlier erasures.
  - The parameter store keeps its history, so an overwrite can be undone.
- **`JOBSEARCH_SIGNING_KEY`** signs every step the studio certifies. To rotate it, add the old public key to a
  `JOBSEARCH_TRUSTED_KEYS` parameter first, or the library's existing steps stop loading.

## 3. Deploy

```bash
apps/job-search/deploy/deploy.sh            # builds, pushes <commit>, rolls the server to it
```

The first rollout pulls the private model, about 5 GB, which takes a few minutes. A rollout succeeds only when
`/health` and `/health/model` both answer. Otherwise the server goes back to the image it ran before.

Releases normally run through the workflow instead: [RELEASE.md](../docs/RELEASE.md). The workflow pushes the very image
its QA agents tested.

## 4. Check

- Open `https://<domain>/`, sign in with Google, and read and accept the notice.
- `https://<domain>/health` and `https://<domain>/health/model` each answer `{"status":"ok"}`.
- Set `image_tag` in `terraform.tfvars` to the deployed tag. A replaced server then comes back on its own.

## Operating it

Connect with Session Manager. There is no SSH and no port 22. AWS logs every session.

```bash
aws ssm start-session --target "$(terraform output -raw instance_id)"
sudo -i
cd /opt/jobsearch
docker compose ps
docker compose logs --tail 100 studio
journalctl -u jobsearch -u jobsearch-backup --since today
/opt/jobsearch/bin/jobsearch health && echo ok
docker compose exec studio dotnet jobsearch.dll library list     # the operator CLI (dotnet jobsearch.dll --help)
```

| Task | How |
|---|---|
| Change a secret | `put NAME "value"` as above, then `systemctl restart jobsearch`: the start re-reads the parameter store. |
| Roll back | `deploy.sh <older tag>`. Tags are immutable and ECR keeps the newest 30. |
| Quarantine a library step | `docker compose exec studio dotnet jobsearch.dll library quarantine <id> --reason "..."` |
| Replace the server (new Ubuntu image, edited host files) | Set `image_tag`, then `terraform apply -replace=aws_instance.studio`. The data volume and the elastic IP move to the new server. |
| Bigger model or faster drafting | A larger `instance_type`. A GPU instance needs the NVIDIA container toolkit, which this setup does not install (known limit). |

## Backups and restore

Every hour, `jobsearch backup`:

1. has the studio copy its own data while it runs, using SQLite's online backup, so the copy is one consistent
   snapshot ([Backup.cs](../src/JobSearch.Host/Studio/Backup.cs));
2. ships the copy to `s3://<bucket>/backups/<time>.tar.gz`;
3. writes the newest erasure ledger to `erasures/latest.json`.

The backup bucket:

- encrypts with the studio's KMS key;
- keeps versions;
- expires backups after `backup_retention_days` (35 by default);
- gives the server write and read access, but never delete.

`keys/` is never in a backup. The stored tokens in a backup are encrypted, and the master key that opens them lives
only in the parameter store.

**Restore.** On the server, as root:

```bash
cd /opt/jobsearch && B=$(sed -n 's/^BACKUP_BUCKET=//p' host.conf)
aws s3 ls "s3://$B/backups/" | tail                           # pick a backup
systemctl stop jobsearch-backup.timer && docker compose stop studio
mv /srv/jobsearch/data /srv/jobsearch/data.before-restore      # keep until the restore is checked
mkdir /srv/jobsearch/data
aws s3 cp "s3://$B/backups/<time>.tar.gz" - | tar -xz -C /srv/jobsearch/data
aws s3 cp "s3://$B/erasures/latest.json" /srv/jobsearch/data/erasures-latest.json
chown -R 1654:1654 /srv/jobsearch/data && chmod 700 /srv/jobsearch/data
docker compose run --rm --no-deps studio erasures apply --file /data/erasures-latest.json
docker compose up -d && systemctl start jobsearch-backup.timer
```

`erasures apply` is not optional. An older backup still holds accounts that were erased after it was taken. The
newest ledger names those accounts by keyed hash, and the command erases them again and keeps their tombstones
([PRIVACY.md](../docs/PRIVACY.md)).

## What each part may do

| Part | May | May not |
|---|---|---|
| Instance role | Read `/<name>/*` parameters; use the studio key; put and get backups; pull its own image; Session Manager | Delete backups or shorten their retention; read other parameters; push images |
| Containers | Reach the internet; the studio reaches the model on loopback | Reach the instance role (IMDSv2, hop limit 1); write the studio image's files (read-only root, no capabilities, non-root) |
| Deploy role (optional, GitHub OIDC) | Push to the studio's repository; run the rollout document on this instance | Run any other command; be assumed outside the protected `studio-production` environment |
| Caddy | Terminate TLS and proxy to the studio | Reach the model; log requests |

## Verified, and not

**Verified in this repository's environment:**

- **The image:**
  - it builds from the repository root;
  - certification runs inside it, read-only, as a non-root user with no capabilities;
  - the UI suite passes against it;
  - the scripted QA fleet passes against it;
  - `backup` works inside it.
- **The compose stack**, with a stand-in for Ollama's API:
  - the studio reaches the model on loopback;
  - Caddy serves TLS with the security headers and cannot reach the model;
  - after a crash, the studio restarts and rejoins the model's namespace.
- **The host script**, against that stack, with a fake AWS CLI:
  - a rollout succeeds;
  - a broken image is rolled back;
  - a malformed tag is refused;
  - a backup lands in the bucket without `keys/`.
- **Terraform:** `terraform validate` and `fmt` pass (AWS provider 6.66). The first-boot script renders, passes
  `bash -n` and shellcheck, and is 5 KB compressed (the limit is 16 KB).

**Not verified:**

- `terraform plan` or `apply` against an AWS account;
- the first boot on a real Ubuntu 24.04 instance (packages, snap, the NVMe device path);
- Let's Encrypt issuance;
- Ollama with a real model on this CPU;
- the GitHub OIDC deploy role.

The first deploy is where these get checked. Watch `/var/log/jobsearch-init.log` and `journalctl -u jobsearch`.
