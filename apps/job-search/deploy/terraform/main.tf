# Job Search Studio on one EC2 server (owner decision 2026-11-08; docs/STUDIO.md §4, deploy/README.md).
#
# What this makes: a KMS key for backups and parameter-store secrets; a versioned, encrypted, private S3 bucket for
# backups that expire; an ECR repository; an instance role that can read only its own parameters, write and read
# backups but never delete them, and pull its own image; a data volume that outlives the instance; the instance with
# IMDSv2 and a hop limit of 1 (containers cannot reach the instance role); an elastic IP; an SSM document that rolls
# the studio to an image tag; and, optionally, a DNS record and a GitHub OIDC role for the release workflow.
#
# What it does not make: secrets. They never enter Terraform state; deploy/README.md puts them in the parameter store.

data "aws_caller_identity" "current" {}

data "aws_partition" "current" {}

data "aws_availability_zones" "available" {
  state = "available"
}

data "aws_subnet" "default" {
  count             = var.subnet_id == "" ? 1 : 0
  availability_zone = var.availability_zone != "" ? var.availability_zone : data.aws_availability_zones.available.names[0]
  default_for_az    = true
}

data "aws_subnet" "studio" {
  id = var.subnet_id != "" ? var.subnet_id : data.aws_subnet.default[0].id
}

# Canonical's current Ubuntu 24.04 LTS image: Docker, Compose and the SSM agent come from signed packages.
data "aws_ssm_parameter" "ubuntu" {
  name = "/aws/service/canonical/ubuntu/server/24.04/stable/current/amd64/hvm/ebs-gp3/ami-id"
}

locals {
  account_id    = data.aws_caller_identity.current.account_id
  partition     = data.aws_partition.current.partition
  param_path    = "/${var.name}"
  param_arn     = "arn:${local.partition}:ssm:${var.region}:${local.account_id}:parameter${local.param_path}"
  ecr_registry  = "${local.account_id}.dkr.ecr.${var.region}.amazonaws.com"
  initial_image = var.image_tag == "" ? "" : "${aws_ecr_repository.studio.repository_url}:${var.image_tag}"
}

# ── keys ───────────────────────────────────────────────────────────────────────────────────────────────────────────

resource "aws_kms_key" "studio" {
  description             = "${var.name}: backups and parameter-store secrets"
  enable_key_rotation     = true
  deletion_window_in_days = 30
}

resource "aws_kms_alias" "studio" {
  name          = "alias/${var.name}"
  target_key_id = aws_kms_key.studio.key_id
}

# ── backups ────────────────────────────────────────────────────────────────────────────────────────────────────────

resource "aws_s3_bucket" "backups" {
  bucket_prefix = "${var.name}-backups-"
}

resource "aws_s3_bucket_ownership_controls" "backups" {
  bucket = aws_s3_bucket.backups.id
  rule {
    object_ownership = "BucketOwnerEnforced"
  }
}

resource "aws_s3_bucket_public_access_block" "backups" {
  bucket                  = aws_s3_bucket.backups.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_versioning" "backups" {
  bucket = aws_s3_bucket.backups.id
  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "backups" {
  bucket = aws_s3_bucket.backups.id
  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm     = "aws:kms"
      kms_master_key_id = aws_kms_key.studio.arn
    }
    bucket_key_enabled = true
  }
}

# Backups expire, so an erased account is gone from them within backup_retention_days (PRIVACY.md). The host cannot
# delete or shorten anything: only this lifecycle removes backups.
resource "aws_s3_bucket_lifecycle_configuration" "backups" {
  bucket     = aws_s3_bucket.backups.id
  depends_on = [aws_s3_bucket_versioning.backups]

  rule {
    id     = "expire-backups"
    status = "Enabled"
    filter {
      prefix = "backups/"
    }
    expiration {
      days = var.backup_retention_days
    }
    noncurrent_version_expiration {
      noncurrent_days = 1
    }
  }

  rule {
    id     = "expire-replaced-ledgers"
    status = "Enabled"
    filter {
      prefix = "erasures/"
    }
    noncurrent_version_expiration {
      noncurrent_days = var.backup_retention_days
    }
  }

  rule {
    id     = "tidy"
    status = "Enabled"
    filter {}
    expiration {
      expired_object_delete_marker = true
    }
    abort_incomplete_multipart_upload {
      days_after_initiation = 1
    }
  }
}

data "aws_iam_policy_document" "backups_bucket" {
  statement {
    sid     = "DenyInsecureTransport"
    effect  = "Deny"
    actions = ["s3:*"]
    principals {
      type        = "*"
      identifiers = ["*"]
    }
    resources = [aws_s3_bucket.backups.arn, "${aws_s3_bucket.backups.arn}/*"]
    condition {
      test     = "Bool"
      variable = "aws:SecureTransport"
      values   = ["false"]
    }
  }
}

resource "aws_s3_bucket_policy" "backups" {
  bucket     = aws_s3_bucket.backups.id
  policy     = data.aws_iam_policy_document.backups_bucket.json
  depends_on = [aws_s3_bucket_public_access_block.backups]
}

# ── images ─────────────────────────────────────────────────────────────────────────────────────────────────────────

resource "aws_ecr_repository" "studio" {
  name                 = var.name
  image_tag_mutability = "IMMUTABLE"
  image_scanning_configuration {
    scan_on_push = true
  }
}

resource "aws_ecr_lifecycle_policy" "studio" {
  repository = aws_ecr_repository.studio.name
  policy = jsonencode({
    rules = [
      {
        rulePriority = 1
        description  = "Untagged layers go after a week"
        selection    = { tagStatus = "untagged", countType = "sinceImagePushed", countUnit = "days", countNumber = 7 }
        action       = { type = "expire" }
      },
      {
        rulePriority = 2
        description  = "Keep the 30 newest images to roll back to"
        selection    = { tagStatus = "any", countType = "imageCountMoreThan", countNumber = 30 }
        action       = { type = "expire" }
      },
    ]
  })
}

# ── the instance's identity ────────────────────────────────────────────────────────────────────────────────────────

data "aws_iam_policy_document" "ec2_assume" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["ec2.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "studio" {
  name               = "${var.name}-instance"
  assume_role_policy = data.aws_iam_policy_document.ec2_assume.json
}

# Session Manager instead of SSH: no port 22, and every session is logged by AWS.
resource "aws_iam_role_policy_attachment" "ssm_core" {
  role       = aws_iam_role.studio.name
  policy_arn = "arn:${local.partition}:iam::aws:policy/AmazonSSMManagedInstanceCore"
}

data "aws_iam_policy_document" "studio" {
  statement {
    sid       = "ReadOwnParameters"
    actions   = ["ssm:GetParametersByPath", "ssm:GetParameters", "ssm:GetParameter"]
    resources = [local.param_arn, "${local.param_arn}/*"]
  }
  statement {
    sid       = "UseTheStudioKey"
    actions   = ["kms:Decrypt", "kms:GenerateDataKey"]
    resources = [aws_kms_key.studio.arn]
  }
  statement {
    sid       = "WriteAndReadBackupsButNeverDelete"
    actions   = ["s3:PutObject", "s3:GetObject"]
    resources = ["${aws_s3_bucket.backups.arn}/backups/*", "${aws_s3_bucket.backups.arn}/erasures/*"]
  }
  statement {
    sid       = "ListBackups"
    actions   = ["s3:ListBucket"]
    resources = [aws_s3_bucket.backups.arn]
  }
  statement {
    sid       = "PullOwnImage"
    actions   = ["ecr:BatchGetImage", "ecr:GetDownloadUrlForLayer", "ecr:BatchCheckLayerAvailability"]
    resources = [aws_ecr_repository.studio.arn]
  }
  statement {
    sid       = "RegistryLogin"
    actions   = ["ecr:GetAuthorizationToken"]
    resources = ["*"]
  }
}

resource "aws_iam_role_policy" "studio" {
  name   = "${var.name}-instance"
  role   = aws_iam_role.studio.id
  policy = data.aws_iam_policy_document.studio.json
}

resource "aws_iam_instance_profile" "studio" {
  name = "${var.name}-instance"
  role = aws_iam_role.studio.name
}

# ── network ────────────────────────────────────────────────────────────────────────────────────────────────────────

resource "aws_security_group" "studio" {
  name        = var.name
  description = "Job Search Studio: HTTPS in (and HTTP for the redirect and certificate challenges); no SSH."
  vpc_id      = data.aws_subnet.studio.vpc_id
}

resource "aws_vpc_security_group_ingress_rule" "web" {
  for_each          = { http = ["tcp", 80], https = ["tcp", 443], http3 = ["udp", 443] }
  security_group_id = aws_security_group.studio.id
  ip_protocol       = each.value[0]
  from_port         = each.value[1]
  to_port           = each.value[1]
  cidr_ipv4         = "0.0.0.0/0"
  description       = each.key
}

# Job boards, Google, Anthropic, Let's Encrypt, ECR, SSM, Ubuntu mirrors and the model registry are all outbound HTTPS.
resource "aws_vpc_security_group_egress_rule" "all" {
  security_group_id = aws_security_group.studio.id
  ip_protocol       = "-1"
  cidr_ipv4         = "0.0.0.0/0"
  description       = "outbound"
}

# ── the server ─────────────────────────────────────────────────────────────────────────────────────────────────────

resource "aws_ebs_volume" "data" {
  availability_zone = data.aws_subnet.studio.availability_zone
  size              = var.data_volume_gb
  type              = "gp3"
  encrypted         = true
  tags = {
    Name = "${var.name}-data"
  }
  # The users' data. Remove this guard deliberately, after a final backup, to destroy it.
  lifecycle {
    prevent_destroy = true
  }
}

resource "aws_instance" "studio" {
  ami                    = data.aws_ssm_parameter.ubuntu.insecure_value
  instance_type          = var.instance_type
  subnet_id              = data.aws_subnet.studio.id
  vpc_security_group_ids = [aws_security_group.studio.id]
  iam_instance_profile   = aws_iam_instance_profile.studio.name

  # IMDSv2 only, one hop: the host reads its role; a container (one more hop) cannot.
  metadata_options {
    http_endpoint               = "enabled"
    http_tokens                 = "required"
    http_put_response_hop_limit = 1
  }

  root_block_device {
    volume_type = "gp3"
    volume_size = var.root_volume_gb
    encrypted   = true
  }

  user_data_base64 = base64gzip(templatefile("${path.module}/cloud-init.sh.tftpl", {
    data_volume_serial = replace(aws_ebs_volume.data.id, "-", "")
    region             = var.region
    param_path         = local.param_path
    bucket             = aws_s3_bucket.backups.id
    ecr_registry       = local.ecr_registry
    repository         = aws_ecr_repository.studio.name
    initial_image      = local.initial_image
    domain             = var.domain
    acme_email         = var.acme_email
    private_model      = var.private_model
    host_script        = file("${path.module}/../host/jobsearch")
    unit_studio        = file("${path.module}/../host/jobsearch.service")
    unit_backup        = file("${path.module}/../host/jobsearch-backup.service")
    unit_backup_timer  = file("${path.module}/../host/jobsearch-backup.timer")
    compose_file       = file("${path.module}/../docker-compose.yml")
    caddyfile          = file("${path.module}/../Caddyfile")
  }))

  tags = {
    Name = var.name
  }

  # A newer Ubuntu image or edited host files do not replace the server by surprise; replace it on purpose with
  # `terraform apply -replace=aws_instance.studio` (the data volume and the elastic IP move to the new one).
  lifecycle {
    ignore_changes = [ami, user_data_base64]
  }
}

resource "aws_volume_attachment" "data" {
  device_name                    = "/dev/sdf"
  volume_id                      = aws_ebs_volume.data.id
  instance_id                    = aws_instance.studio.id
  stop_instance_before_detaching = true
}

resource "aws_eip" "studio" {
  domain   = "vpc"
  instance = aws_instance.studio.id
  tags = {
    Name = var.name
  }
}

resource "aws_route53_record" "studio" {
  count   = var.route53_zone_id == "" ? 0 : 1
  zone_id = var.route53_zone_id
  name    = var.domain
  type    = "A"
  ttl     = 300
  records = [aws_eip.studio.public_ip]
}

# ── rollouts ───────────────────────────────────────────────────────────────────────────────────────────────────────

# The only command the deploy role may run on the server: roll the studio to a tag that passes the pattern.
resource "aws_ssm_document" "rollout" {
  name            = "${var.name}-rollout"
  document_type   = "Command"
  document_format = "JSON"
  content = jsonencode({
    schemaVersion = "2.2"
    description   = "Roll Job Search Studio to an image tag; rolls back if the studio or its private model does not answer."
    parameters = {
      tag = {
        type           = "String"
        description    = "Image tag in the studio's ECR repository"
        allowedPattern = "^[A-Za-z0-9._-]{1,128}$"
      }
    }
    mainSteps = [{
      action = "aws:runShellScript"
      name   = "rollout"
      inputs = {
        timeoutSeconds = "900"
        runCommand     = ["/opt/jobsearch/bin/jobsearch rollout '{{ tag }}'"]
      }
    }]
  })
}

resource "aws_iam_openid_connect_provider" "github" {
  count          = var.github_repository != "" && var.create_github_oidc_provider ? 1 : 0
  url            = "https://token.actions.githubusercontent.com"
  client_id_list = ["sts.amazonaws.com"]
}

data "aws_iam_openid_connect_provider" "github" {
  count = var.github_repository != "" && !var.create_github_oidc_provider ? 1 : 0
  url   = "https://token.actions.githubusercontent.com"
}

locals {
  github_oidc_arn = var.github_repository == "" ? "" : (var.create_github_oidc_provider
    ? aws_iam_openid_connect_provider.github[0].arn
  : data.aws_iam_openid_connect_provider.github[0].arn)
}

data "aws_iam_policy_document" "deploy_assume" {
  count = var.github_repository == "" ? 0 : 1
  statement {
    actions = ["sts:AssumeRoleWithWebIdentity"]
    principals {
      type        = "Federated"
      identifiers = [local.github_oidc_arn]
    }
    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:aud"
      values   = ["sts.amazonaws.com"]
    }
    # Only jobs that run in the protected environment (required reviewers) get the role.
    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:sub"
      values   = ["repo:${var.github_repository}:environment:${var.github_environment}"]
    }
  }
}

resource "aws_iam_role" "deploy" {
  count              = var.github_repository == "" ? 0 : 1
  name               = "${var.name}-deploy"
  assume_role_policy = data.aws_iam_policy_document.deploy_assume[0].json
}

data "aws_iam_policy_document" "deploy" {
  count = var.github_repository == "" ? 0 : 1
  statement {
    sid       = "RegistryLogin"
    actions   = ["ecr:GetAuthorizationToken"]
    resources = ["*"]
  }
  statement {
    sid = "PushOwnImage"
    actions = [
      "ecr:BatchCheckLayerAvailability", "ecr:InitiateLayerUpload", "ecr:UploadLayerPart", "ecr:CompleteLayerUpload",
      "ecr:PutImage", "ecr:BatchGetImage", "ecr:DescribeImages",
    ]
    resources = [aws_ecr_repository.studio.arn]
  }
  statement {
    sid     = "RunOnlyTheRollout"
    actions = ["ssm:SendCommand"]
    resources = [
      aws_ssm_document.rollout.arn,
      "arn:${local.partition}:ec2:${var.region}:${local.account_id}:instance/${aws_instance.studio.id}",
    ]
  }
  statement {
    sid       = "ReadRolloutResults"
    actions   = ["ssm:GetCommandInvocation", "ssm:ListCommandInvocations"]
    resources = ["*"]
  }
}

resource "aws_iam_role_policy" "deploy" {
  count  = var.github_repository == "" ? 0 : 1
  name   = "${var.name}-deploy"
  role   = aws_iam_role.deploy[0].id
  policy = data.aws_iam_policy_document.deploy[0].json
}
