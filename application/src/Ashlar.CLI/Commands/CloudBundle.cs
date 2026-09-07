using System.Text.Json;

namespace Ashlar.CLI.Commands;

/// <summary>The cloud targets the exporter can stage for.</summary>
public enum CloudTarget
{
    /// <summary>AWS — ECS Fargate one-shot task via ECR.</summary>
    Aws,

    /// <summary>Azure — Container Instances one-shot container via ACR.</summary>
    Azure,
}

/// <summary>
/// Stages a certified project into a one-command cloud deploy bundle: the governed app beside a
/// Dockerfile that layers it onto the published Ashlar runtime image, an entrypoint that
/// VERIFIES BEFORE IT RUNS (the cloud refuses a tampered app exactly like the native launcher
/// does), and a deploy script per target. Deterministic, offline, unit-testable — the deploy
/// script is where the cloud is actually touched, and only when the operator runs it.
/// </summary>
public static class CloudBundle
{
    /// <summary>The bundle format tag.</summary>
    public const string Format = "ashlar-cloud/v1";

    /// <summary>The published runtime image the Dockerfile layers the app onto.</summary>
    public const string RuntimeImage = "ghcr.io/ianfrelinger/nexo-cli:latest";

    /// <summary>Stages the full cloud bundle and returns the relative paths written.
    /// <paramref name="runtimeImage"/> overrides the default runtime image — pin a version or
    /// digest so the descriptor can attest which verifier will actually run the app.</summary>
    public static IReadOnlyList<string> Stage(string projectDir, string bundleDir, BundleInfo info, CloudTarget target, string? runtimeImage = null)
    {
        var image = string.IsNullOrWhiteSpace(runtimeImage) ? RuntimeImage : runtimeImage;
        Directory.CreateDirectory(bundleDir);
        var written = NativeBundle.StageApp(projectDir, bundleDir);

        // The container entrypoint: verify, then run the request passed as container args. A
        // container whose app or ledger was altered exits 65 at startup and never serves a run —
        // the self-proving contract holds in the cloud too.
        var readiness = info.Certified ? "certified and ready." : "verified and ready (unsigned).";
        var entrypoint =
            "#!/bin/sh\n"
            + "set -e\n"
            + "dotnet /app/Ashlar.CLI.dll verify --path /work/app\n"
            + "if [ \"$#\" -gt 0 ]; then\n"
            + "  exec dotnet /app/Ashlar.CLI.dll run \"$@\" --path /work/app\n"
            + "else\n"
            + "  echo\n"
            + "  echo \"" + readiness + " pass a request as the container command to run it.\"\n"
            + "fi\n";
        WriteText(Path.Combine(bundleDir, "entrypoint.sh"), entrypoint, written, bundleDir, unixExecutable: true);

        // The image: the published runtime plus THIS certified app. `USER root` only to place
        // files; execution returns to the base image's unprivileged user.
        var dockerfile =
            $"FROM {image}\n"
            + "USER root\n"
            + "COPY --chown=app:app app /work/app\n"
            + "COPY --chown=app:app entrypoint.sh /work/entrypoint.sh\n"
            + "RUN chmod +x /work/entrypoint.sh\n"
            + "USER $APP_UID\n"
            + "ENTRYPOINT [\"/work/entrypoint.sh\"]\n";
        WriteText(Path.Combine(bundleDir, "Dockerfile"), dockerfile, written, bundleDir);

        var name = Safe(info.Name);
        if (target == CloudTarget.Aws)
        {
            WriteText(Path.Combine(bundleDir, "deploy-aws.sh"), AwsScript(name), written, bundleDir, unixExecutable: true);
        }
        else
        {
            WriteText(Path.Combine(bundleDir, "deploy-azure.sh"), AzureScript(name), written, bundleDir, unixExecutable: true);
        }

        var descriptor = new
        {
            format = Format,
            target = target == CloudTarget.Aws ? "aws" : "azure",
            name = info.Name,
            verified = info.Verified,
            certified = info.Certified,
            signer = info.SignerFingerprint,
            ledgerEntries = info.LedgerEntries,
            runtimeImage = image,
            deploy = target == CloudTarget.Aws ? "./deploy-aws.sh \"<request>\"" : "./deploy-azure.sh \"<request>\"",
        };
        WriteText(Path.Combine(bundleDir, "bundle.json"),
            JsonSerializer.Serialize(descriptor, new JsonSerializerOptions { WriteIndented = true }), written, bundleDir);

        var certLine = info.Certified
            ? $"It is **certified**: signed {info.SignerFingerprint}, {info.LedgerEntries} signed ledger entr{(info.LedgerEntries == 1 ? "y" : "ies")}."
            : "It is **unsigned** — it verifies, but was not certified with an operator key.";
        var deployCmd = target == CloudTarget.Aws ? "./deploy-aws.sh \"<your request>\"" : "./deploy-azure.sh \"<your request>\"";
        var prereq = target == CloudTarget.Aws
            ? "- AWS CLI logged in (`aws sts get-caller-identity` works), Docker running, `AWS_REGION` set.\n"
              + "- Optional: `ASHLAR_SUBNET` — the subnet to run the task in, for accounts without a default VPC.\n"
            : "- Azure CLI logged in (`az account show` works), `AZURE_RESOURCE_GROUP` and `AZURE_LOCATION` set.\n"
              + "- Optional: `AZURE_ACR_NAME` — registry name override if the derived one is already taken (ACR names are global).\n";
        var readme =
            $"# {info.Name} — one-command cloud deploy ({(target == CloudTarget.Aws ? "AWS" : "Azure")})\n\n"
            + $"{certLine}\n\n"
            + "## Deploy and run\n\n"
            + $"```\n{deployCmd}\n```\n\n"
            + "The script builds the image (the published Ashlar runtime + this app), pushes it to your\n"
            + "registry, and runs it as a one-shot cloud task. **The container verifies the app against its\n"
            + "contract and signed ledger before running** — a tampered app exits 65 in the cloud, same as\n"
            + "it would on your desk.\n\n"
            + "## Prerequisites\n\n"
            + prereq
            + "\n"
            + "- `app/` — the project: contract, operator-owned policy, signed ledger.\n"
            + "- `Dockerfile` — the runtime image + this app.\n"
            + "- `entrypoint.sh` — verify-then-run.\n"
            + "- `bundle.json` — what is inside and what certifies it.\n"
            // The same disclosure the native bundle makes: the directories under app/ that the
            // export created rather than copied. The container entrypoint verifies before it runs,
            // so the sandbox root has to be in the image — and that has to be visible, not implied.
            + NativeBundle.PolicyDirectoryNote(projectDir);
        WriteText(Path.Combine(bundleDir, "README.md"), readme, written, bundleDir);

        return written;
    }

    // ── deploy scripts ───────────────────────────────────────────────────────
    // These are the only place the cloud is touched, and only when the operator runs them. Both
    // are idempotent about the infrastructure they create and pass the request through as the
    // container command.

    private static string AwsScript(string name) =>
        """
        #!/usr/bin/env sh
        # One-command deploy: build → push to ECR → run as an ECS Fargate one-shot task.
        set -e
        : "${AWS_REGION:?set AWS_REGION, e.g. export AWS_REGION=us-east-1}"
        NAME="@name@"
        ACCOUNT=$(aws sts get-caller-identity --query Account --output text)
        REGISTRY="$ACCOUNT.dkr.ecr.$AWS_REGION.amazonaws.com"
        IMAGE="$REGISTRY/$NAME:latest"

        # The request travels inside JSON. A JSON string cannot carry raw control characters, and
        # quotes and backslashes must be escaped — otherwise a crafted request rewrites the task
        # definition instead of riding in it.
        json_escape() {
          if [ "$(printf '%s' "$1" | LC_ALL=C tr -dc '\000-\037' | wc -c)" -gt 0 ]; then
            echo "refusing: the request contains control characters — put it on one line and retry." >&2
            exit 64
          fi
          printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'
        }

        # Resolve networking FIRST, so an account that cannot place the task refuses before the
        # expensive build+push. ASHLAR_SUBNET overrides for accounts without a default VPC.
        SUBNET="${ASHLAR_SUBNET:-}"
        if [ -z "$SUBNET" ]; then
          VPC=$(aws ec2 describe-vpcs --filters Name=is-default,Values=true --query 'Vpcs[0].VpcId' --output text)
          if [ -z "$VPC" ] || [ "$VPC" = "None" ]; then
            echo "refusing: this account has no default VPC — export ASHLAR_SUBNET=subnet-xxxxxxxx (a subnet the task may run in) and re-run." >&2
            exit 64
          fi
          SUBNET=$(aws ec2 describe-subnets --filters Name=vpc-id,Values="$VPC" --query 'Subnets[0].SubnetId' --output text)
          if [ -z "$SUBNET" ] || [ "$SUBNET" = "None" ]; then
            echo "refusing: default VPC $VPC has no subnets — export ASHLAR_SUBNET=subnet-xxxxxxxx and re-run." >&2
            exit 64
          fi
        fi

        aws ecr describe-repositories --repository-names "$NAME" >/dev/null 2>&1 \
          || aws ecr create-repository --repository-name "$NAME" >/dev/null
        aws ecr get-login-password | docker login --username AWS --password-stdin "$REGISTRY"
        # Fargate here runs LINUX/X86_64 — build for that platform explicitly, or an arm64 host
        # (Apple Silicon) produces an image the task can pull but never exec.
        docker build --platform linux/amd64 -t "$IMAGE" "$(dirname "$0")"
        docker push "$IMAGE"

        aws ecs describe-clusters --clusters "$NAME" --query 'clusters[?status==`ACTIVE`]' --output text | grep -q . \
          || aws ecs create-cluster --cluster-name "$NAME" >/dev/null
        # Check-then-create, not create-and-swallow: a permission error here must stop the deploy,
        # or the task later dies on logging configuration with a far worse message.
        aws logs describe-log-groups --log-group-name-prefix "/ashlar/$NAME" --query "logGroups[?logGroupName=='/ashlar/$NAME'] | [0].logGroupName" --output text | grep -qv None \
          || aws logs create-log-group --log-group-name "/ashlar/$NAME"

        # Execution role (pull + logs); created once, reused after. The policy attach sits OUTSIDE
        # the create branch — attaching is idempotent, and this heals a role whose creation
        # succeeded but whose attach failed on an earlier run.
        ROLE="$NAME-exec"
        aws iam get-role --role-name "$ROLE" >/dev/null 2>&1 \
          || aws iam create-role --role-name "$ROLE" --assume-role-policy-document '{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"ecs-tasks.amazonaws.com"},"Action":"sts:AssumeRole"}]}' >/dev/null
        aws iam attach-role-policy --role-name "$ROLE" --policy-arn arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy
        ROLE_ARN=$(aws iam get-role --role-name "$ROLE" --query Role.Arn --output text)

        REQUEST="${1:-}"
        CMD='[]'
        [ -n "$REQUEST" ] && CMD="[\"$(json_escape "$REQUEST")\"]"
        # A just-created role can take a few seconds to become visible to ECS — retry with the real
        # error in hand instead of a blind sleep.
        n=0
        until ERR=$(aws ecs register-task-definition --family "$NAME" --requires-compatibilities FARGATE \
          --network-mode awsvpc --cpu 512 --memory 1024 --execution-role-arn "$ROLE_ARN" \
          --runtime-platform cpuArchitecture=X86_64,operatingSystemFamily=LINUX \
          --container-definitions "[{\"name\":\"$NAME\",\"image\":\"$IMAGE\",\"command\":$CMD,\"logConfiguration\":{\"logDriver\":\"awslogs\",\"options\":{\"awslogs-group\":\"/ashlar/$NAME\",\"awslogs-region\":\"$AWS_REGION\",\"awslogs-stream-prefix\":\"run\"}}}]" 2>&1 >/dev/null); do
          n=$((n+1))
          if [ "$n" -ge 6 ]; then
            printf '%s\n' "$ERR" >&2
            echo "registering the task definition failed after $n attempts — fix the error above and re-run." >&2
            exit 1
          fi
          echo "task definition not accepted yet (IAM propagation?) — retry $n/5 in 5s…"
          sleep 5
        done

        TASK=$(aws ecs run-task --cluster "$NAME" --task-definition "$NAME" --launch-type FARGATE \
          --network-configuration "awsvpcConfiguration={subnets=[$SUBNET],assignPublicIp=ENABLED}" \
          --query 'tasks[0].taskArn' --output text)
        # run-task can return HTTP 200 with an empty tasks[] and the reason in failures[] —
        # exit 0 with no task placed. Refuse to report a launch that did not happen.
        if [ -z "$TASK" ] || [ "$TASK" = "None" ]; then
          echo "refusing to report success: ECS accepted the request but did not place the task." >&2
          echo "re-run the aws ecs run-task command above with --output json and read failures[].reason (capacity, subnet, or ENI limits)." >&2
          exit 1
        fi
        echo "task launched: $TASK"
        echo "follow logs:   aws logs tail /ashlar/$NAME --follow"
        echo "exit code:     aws ecs describe-tasks --cluster $NAME --tasks $TASK --query 'tasks[0].containers[0].exitCode'   (65 = the app failed verify)"
        """.Replace("@name@", name) + "\n";

    private static string AzureScript(string name) =>
        """
        #!/usr/bin/env sh
        # One-command deploy: build → push to ACR → run as an Azure Container Instances one-shot.
        set -e
        : "${AZURE_RESOURCE_GROUP:?set AZURE_RESOURCE_GROUP}"
        : "${AZURE_LOCATION:?set AZURE_LOCATION, e.g. export AZURE_LOCATION=eastus}"
        NAME="@name@"
        # ACR registry names are GLOBALLY unique across all Azure tenants (name.azurecr.io) and
        # must be 5-50 alphanumerics. Default: the project name plus a subscription-derived suffix —
        # deterministic per subscription, so re-runs reuse the registry. AZURE_ACR_NAME overrides
        # when even that collides, or to reuse an existing registry.
        ACR="${AZURE_ACR_NAME:-}"
        if [ -z "$ACR" ]; then
          SUB=$(az account show --query id --output tsv | tr -cd '0-9a-f' | cut -c1-8)
          ACR="$(printf '%s' "$NAME" | tr -cd 'a-z0-9' | cut -c1-38)${SUB}acr"
        fi

        # Values bound for the deployment spec ride inside double-quoted YAML scalars. Quotes and
        # backslashes must be escaped and control characters refused — otherwise a crafted value
        # rewrites the spec instead of riding in it.
        yaml_escape() {
          if [ "$(printf '%s' "$1" | LC_ALL=C tr -dc '\000-\037' | wc -c)" -gt 0 ]; then
            echo "refusing: a value bound for the deployment spec contains control characters — put it on one line and retry." >&2
            exit 64
          fi
          printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'
        }

        az group show --name "$AZURE_RESOURCE_GROUP" >/dev/null 2>&1 \
          || az group create --name "$AZURE_RESOURCE_GROUP" --location "$AZURE_LOCATION" >/dev/null
        az acr show --name "$ACR" >/dev/null 2>&1 \
          || az acr create --name "$ACR" --resource-group "$AZURE_RESOURCE_GROUP" --sku Basic --admin-enabled true >/dev/null

        # Build in the registry (no local Docker daemon needed on this path).
        az acr build --registry "$ACR" --image "$NAME:latest" "$(dirname "$0")"

        REQUEST="${1:-}"
        SERVER=$(az acr show --name "$ACR" --query loginServer --output tsv)
        USERNAME=$(az acr credential show --name "$ACR" --query username --output tsv)
        PASSWORD=$(az acr credential show --name "$ACR" --query 'passwords[0].value' --output tsv)
        # Escape OUTSIDE the heredoc: a substitution that fails during heredoc expansion is
        # swallowed (the statement's status is cat's), so the control-character refusal inside
        # yaml_escape would fail OPEN there. As standalone assignments, set -e fires.
        LOCATION_ESC=$(yaml_escape "$AZURE_LOCATION")
        SERVER_ESC=$(yaml_escape "$SERVER")
        USERNAME_ESC=$(yaml_escape "$USERNAME")
        PASSWORD_ESC=$(yaml_escape "$PASSWORD")
        az container delete --name "$NAME" --resource-group "$AZURE_RESOURCE_GROUP" --yes >/dev/null 2>&1 || true

        # The registry password and the request travel in a 0600 temp spec, never on a command
        # line — command lines are world-readable in a process listing; this file is not. The
        # request rides as one exec-style argument: no shell re-parsing, no word-splitting.
        SPEC=$(mktemp)
        chmod 600 "$SPEC"
        trap 'rm -f "$SPEC"' EXIT INT TERM
        COMMAND=''
        [ -n "$REQUEST" ] && COMMAND="
              command: [\"/work/entrypoint.sh\", \"$(yaml_escape "$REQUEST")\"]"
        cat > "$SPEC" <<EOF
        apiVersion: '2021-10-01'
        location: "$LOCATION_ESC"
        name: $NAME
        type: Microsoft.ContainerInstance/containerGroups
        properties:
          osType: Linux
          restartPolicy: Never
          imageRegistryCredentials:
          - server: "$SERVER_ESC"
            username: "$USERNAME_ESC"
            password: "$PASSWORD_ESC"
          containers:
          - name: $NAME
            properties:
              image: "$SERVER_ESC/$NAME:latest"$COMMAND
              resources:
                requests:
                  cpu: 1.0
                  memoryInGB: 1.5
        EOF
        az container create --resource-group "$AZURE_RESOURCE_GROUP" --file "$SPEC" >/dev/null
        echo "container launched — follow it with:  az container logs --follow --name $NAME --resource-group $AZURE_RESOURCE_GROUP"
        echo "exit code after it stops:  az container show --name $NAME --resource-group $AZURE_RESOURCE_GROUP --query 'containers[0].instanceView.currentState.exitCode'   (65 = the app failed verify)"
        """.Replace("@name@", name) + "\n";

    private static void WriteText(string dest, string content, List<string> written, string bundleRoot, bool unixExecutable = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        // Raw string literals inherit the SOURCE file's line endings — CRLF on a typical Windows
        // checkout — and a CRLF deploy script is unrunnable under POSIX sh. Emit LF, always.
        File.WriteAllText(dest, content.Replace("\r\n", "\n"));
        written.Add(Path.GetRelativePath(bundleRoot, dest));
        if (unixExecutable && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dest,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static string Safe(string name)
    {
        // The result must be a valid ECR repository, ECS cluster/family, IAM role prefix and ACI
        // container-group name, and its alphanumeric residue plus suffixes must clear ACR's 5-50
        // rule: lowercase alphanumerics with single interior hyphens, clamped to 32 characters.
        var chars = new List<char>();
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                chars.Add(c);
            }
            else if (c is '-' && chars.Count > 0 && chars[^1] != '-')
            {
                chars.Add('-');
            }
            if (chars.Count == 32)
            {
                break;
            }
        }
        while (chars.Count > 0 && chars[^1] == '-')
        {
            chars.RemoveAt(chars.Count - 1);
        }
        return chars.Count(char.IsAsciiLetterOrDigit) >= 2 ? new string(chars.ToArray()) : "app";
    }
}
