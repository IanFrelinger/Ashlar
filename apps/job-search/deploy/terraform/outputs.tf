output "region" {
  value = var.region
}

output "instance_id" {
  description = "Connect with: aws ssm start-session --target <id>"
  value       = aws_instance.studio.id
}

output "public_ip" {
  description = "Point the domain's A record here (unless route53_zone_id is set)."
  value       = aws_eip.studio.public_ip
}

output "ecr_repository_url" {
  value = aws_ecr_repository.studio.repository_url
}

output "backup_bucket" {
  value = aws_s3_bucket.backups.id
}

output "parameter_path" {
  description = "Put the studio's secrets under this path as SecureString parameters (deploy/README.md)."
  value       = local.param_path
}

output "kms_key_alias" {
  value = aws_kms_alias.studio.name
}

output "rollout_document" {
  value = aws_ssm_document.rollout.name
}

output "deploy_role_arn" {
  description = "For the release workflow's AWS_DEPLOY_ROLE_ARN variable; empty without github_repository."
  value       = var.github_repository == "" ? "" : aws_iam_role.deploy[0].arn
}
