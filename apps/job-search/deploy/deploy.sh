#!/usr/bin/env bash
# Pushes a studio image to the ECR repository Terraform made and rolls the server to it through SSM. The server
# checks that the studio and its private model answer, and goes back to the previous image if they do not.
#
#   deploy.sh [--image LOCAL_IMAGE] [TAG]
#
#   TAG                  defaults to the commit (12 characters); tags are immutable, so an existing one is reused
#   --image LOCAL_IMAGE  push this already-built image (the release workflow pushes the image its QA agents tested)
#                        instead of building one here
#
# Where things are comes from `terraform output` in deploy/terraform, or from AWS_REGION, STUDIO_REPOSITORY_URL,
# STUDIO_INSTANCE_ID and STUDIO_ROLLOUT_DOCUMENT. Needs docker and the AWS CLI with credentials that may push to the
# repository and run the rollout document (the deploy role, or an operator's own).
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
repo=$(git -C "$here" rev-parse --show-toplevel)

image=""
if [[ "${1:-}" == "--image" ]]; then
  image=${2:?--image needs a local image}
  shift 2
fi
tag=${1:-$(git -C "$repo" rev-parse --short=12 HEAD)}
if [[ ! $tag =~ ^[A-Za-z0-9._-]{1,128}$ ]]; then
  echo "not an image tag: $tag" >&2
  exit 2
fi

output() { terraform -chdir="$here/terraform" output -raw "$1"; }
region=${AWS_REGION:-$(output region)}
repository_url=${STUDIO_REPOSITORY_URL:-$(output ecr_repository_url)}
instance=${STUDIO_INSTANCE_ID:-$(output instance_id)}
document=${STUDIO_ROLLOUT_DOCUMENT:-$(output rollout_document)}
registry=${repository_url%%/*}
repository=${repository_url#*/}

aws ecr get-login-password --region "$region" | docker login --username AWS --password-stdin "$registry" >/dev/null
if aws ecr describe-images --region "$region" --repository-name "$repository" --image-ids "imageTag=$tag" >/dev/null 2>&1; then
  echo "$repository_url:$tag is already in the repository; rolling out that image"
else
  if [[ -z "$image" ]]; then
    if [[ -n "$(git -C "$repo" status --porcelain)" && -z "${ALLOW_DIRTY:-}" ]]; then
      echo "refusing to build from uncommitted changes (ALLOW_DIRTY=1 builds them anyway)" >&2
      exit 2
    fi
    image="$repository_url:$tag"
    # The server is x86_64, whatever this machine is.
    docker build --platform linux/amd64 -f "$here/Dockerfile" --build-arg BUILD="$tag" -t "$image" "$repo"
  else
    docker tag "$image" "$repository_url:$tag"
  fi
  docker push "$repository_url:$tag"
fi

command_id=$(aws ssm send-command --region "$region" --instance-ids "$instance" --document-name "$document" \
  --parameters "tag=$tag" --comment "jobsearch rollout $tag" --query Command.CommandId --output text)
echo "rolling $instance to $tag (command $command_id)"
status=Pending
for _ in $(seq 1 200); do
  sleep 5
  status=$(aws ssm get-command-invocation --region "$region" --command-id "$command_id" --instance-id "$instance" \
    --query Status --output text 2>/dev/null || echo Pending)
  case "$status" in
    Pending | InProgress | Delayed) continue ;;
    *) break ;;
  esac
done
aws ssm get-command-invocation --region "$region" --command-id "$command_id" --instance-id "$instance" \
  --query '[StandardOutputContent, StandardErrorContent]' --output text || true
if [[ "$status" != Success ]]; then
  echo "rollout $status: the server is back on its previous image if it had one (see above)" >&2
  exit 1
fi
echo "rolled out $tag"
