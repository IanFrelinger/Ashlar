using System.Text.Json;
using Ashlar.Abstractions;
using Ashlar.Abstractions.Paths;

namespace Ashlar.Policies;

/// <summary>
/// Policy that enforces output path sandboxing.
///
/// Ensures that tool calls that write files only write within a specified sandbox directory.
/// Prevents path traversal attacks and unauthorized file system access.
///
/// Implements IPolicy for use with PolicyEngine.
/// Checks for "output" property in tool call arguments and validates it's within the sandbox.
///
/// <para>Containment is <see cref="PathContainment.IsWithin(string, string)"/>: the output must be
/// the root itself or lie under the root FOLLOWED BY A SEPARATOR, so OutputRoot <c>.../out</c> no
/// longer admits the sibling <c>.../out-evil</c>, and letter case is compared the way the platform's
/// default file system does. The check is lexical: it does not follow symbolic links. A relative
/// output resolves against the process working directory, not against OutputRoot.</para>
/// </summary>
public sealed class OutputPathSandboxed : IPolicy
{
    public bool Approve(ToolCall call, WorldSnapshot s, out string reason)
    {
        reason = "OK";
        if (!s.Data.TryGetValue("OutputRoot", out var rootObj) || rootObj is not string root)
            return true;

        if (call.Arguments.ValueKind != JsonValueKind.Object)
            return true;

        if (call.Arguments.TryGetProperty("output", out var outProp))
        {
            try
            {
                // Inside the try: GetString throws on a non-string "output", and a policy refuses what
                // it cannot read rather than throwing out of Approve.
                var output = outProp.GetString() ?? "";
                var full = Path.GetFullPath(output);
                var baseDir = Path.GetFullPath(root);
                if (!PathContainment.IsWithin(full, baseDir))
                {
                    reason = $"Output '{full}' escapes sandbox '{baseDir}'";
                    return false;
                }
            }
            catch
            {
                reason = $"Invalid output path";
                return false;
            }
        }
        return true;
    }
}
