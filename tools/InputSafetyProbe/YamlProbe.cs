using System.Text;
using Ashlar.Manifest;

namespace Ashlar.InputSafetyProbe;

internal static class YamlProbe
{
    // Exit 23 is a reasoned depth refusal. Other results (including a process-fatal stack
    // overflow in a deliberately regressed build) are distinguished by the parent test.
    internal static int Run(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[2], out var depth) || depth < 1 || depth > 100_000)
            return 2;
        var yaml = new StringBuilder();
        if (args[1] == "mapping")
        {
            for (var i = 0; i < depth; i++) yaml.Append("{a: ");
            yaml.Append('0').Append('}', depth);
        }
        else if (args[1] == "sequence") yaml.Append('[', depth).Append('0').Append(']', depth);
        else return 2;

        bool accepted;
        string reason;
        switch (args[0])
        {
            case "guard": accepted = YamlGuard.Check(yaml.ToString(), "probe", out reason); break;
            case "manifest": accepted = ManifestLoader.TryLoad(yaml.ToString(), out _, out reason); break;
            case "policy": accepted = PolicyLoader.TryLoad(yaml.ToString(), out _, out reason); break;
            default: return 2;
        }
        Console.WriteLine($"accepted={accepted}; {reason}");
        return !accepted && reason.Contains("nesting depth", StringComparison.Ordinal) ? 23 : 0;
    }
}
