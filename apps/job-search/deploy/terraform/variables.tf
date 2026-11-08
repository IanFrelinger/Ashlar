variable "name" {
  description = "Name for every resource, the parameter-store path (/<name>/...) and the KMS alias."
  type        = string
  default     = "jobsearch-studio"
  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{2,30}$", var.name))
    error_message = "Lower-case letters, digits and hyphens, 3 to 31 characters, starting with a letter."
  }
}

variable "region" {
  description = "AWS region."
  type        = string
  default     = "us-east-1"
}

variable "domain" {
  description = "The studio's address, e.g. studio.example.com. Point its DNS A record at the elastic IP (or set route53_zone_id)."
  type        = string
  validation {
    condition     = can(regex("^([a-z0-9]([a-z0-9-]*[a-z0-9])?\\.)+[a-z]{2,}$", var.domain))
    error_message = "A lower-case DNS name such as studio.example.com."
  }
}

variable "acme_email" {
  description = "Contact address Let's Encrypt uses for certificate expiry notices."
  type        = string
  validation {
    condition     = can(regex("^[^@\\s'\"]+@[^@\\s'\"]+\\.[a-z]{2,}$", var.acme_email))
    error_message = "An email address."
  }
}

variable "instance_type" {
  description = "x86_64 instance type. The default runs an 8B private model on CPU (owner decision 2026-11-08): 4 vCPU, 16 GiB."
  type        = string
  default     = "m7i.xlarge"
}

variable "root_volume_gb" {
  description = "Root volume (operating system, Docker images)."
  type        = number
  default     = 40
}

variable "data_volume_gb" {
  description = "Data volume (database, step library, model weights, certificates). It outlives the instance."
  type        = number
  default     = 100
}

variable "private_model" {
  description = "The Ollama model that reads personal data on the server."
  type        = string
  default     = "llama3.1:8b"
  validation {
    condition     = can(regex("^[a-z0-9][a-z0-9._:/-]{0,79}$", var.private_model))
    error_message = "An Ollama model name such as llama3.1:8b."
  }
}

variable "image_tag" {
  description = "Image tag a new instance starts with. Empty: nothing runs until the first deploy.sh. Set it to the current release so a replaced instance comes back on its own."
  type        = string
  default     = ""
  validation {
    condition     = can(regex("^[A-Za-z0-9._-]{0,128}$", var.image_tag))
    error_message = "An image tag: letters, digits, dot, underscore, hyphen."
  }
}

variable "backup_retention_days" {
  description = "Days a backup is kept. Erased accounts remain in backups this long at most (PRIVACY.md)."
  type        = number
  default     = 35
}

variable "availability_zone" {
  description = "Availability zone for the instance and its data volume when subnet_id is empty; empty picks the region's first."
  type        = string
  default     = ""
}

variable "subnet_id" {
  description = "Public subnet for the instance. Empty: the default VPC's default subnet in availability_zone."
  type        = string
  default     = ""
}

variable "route53_zone_id" {
  description = "Hosted zone to create the domain's A record in. Empty: create the record yourself."
  type        = string
  default     = ""
}

variable "github_repository" {
  description = "owner/repo whose release workflow may push images and roll out (GitHub OIDC). Empty: no deploy role."
  type        = string
  default     = ""
}

variable "github_environment" {
  description = "GitHub environment the release job runs in; only jobs in it can assume the deploy role. Give it required reviewers."
  type        = string
  default     = "studio-production"
}

variable "create_github_oidc_provider" {
  description = "Create the account's GitHub OIDC provider. Set false if the account already has one."
  type        = bool
  default     = true
}
