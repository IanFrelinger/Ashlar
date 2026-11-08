# Copy to terraform.tfvars and edit. Nothing in here is secret: secrets go in the parameter store (deploy/README.md).
region     = "us-east-1"
domain     = "studio.example.com"
acme_email = "ops@example.com"

# Optional:
# route53_zone_id   = "Z0123456789ABCDEFGHIJ"   # create the A record here
# github_repository = "your-org/your-repo"      # a deploy role for the release workflow
# image_tag         = "studio-v1.0.0"           # what a new or replaced server starts with
