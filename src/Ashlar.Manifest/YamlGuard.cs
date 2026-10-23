using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Ashlar.Manifest;

/// <summary>
/// Pre-parse guards both loaders apply before YAML ever reaches the deserializer.
///
/// <para>Four rules, all fail-closed. SIZE: a manifest or policy over 1 MB is rejected —
/// no honest configuration document is that large, and the cap bounds parser work on
/// garbage. ALIASES: YAML anchors/aliases are rejected outright, because YamlDotNet
/// expands them and a small document can be crafted to expand exponentially (the classic
/// billion-laughs shape); ashlar documents never need them. The scan is textual and
/// deliberately a little over-eager — a scalar that genuinely needs a token shaped like
/// <c>&amp;name</c> or <c>*name</c> in anchor position does not belong in these files, and
/// the rejection says exactly what to change. DUPLICATE KEYS: a mapping with the same key
/// twice is refused rather than resolved last-one-wins. The safety documents are the one
/// place an ambiguity must never quietly pick the more permissive reading — a duplicate
/// <c>mode:</c> in a merge-conflicted or tampered policy would otherwise silently arm the
/// second value. DEPTH: at most 64 nested mappings/sequences are accepted, including
/// the root container, before recursive deserialization can exhaust the process stack.</para>
/// </summary>
public static partial class YamlGuard
{
    /// <summary>1 MB. Configuration, not cargo.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>Maximum simultaneously open YAML mappings and sequences, including the root.</summary>
    public const int MaxNestingDepth = 64;

    [GeneratedRegex(@"(^|[\s\[,{])[&*][A-Za-z0-9_]", RegexOptions.Multiline)]
    private static partial Regex AnchorOrAlias();

    /// <summary>
    /// Returns false with a reason when the raw document violates a guard.
    /// </summary>
    public static bool Check(string yaml, string documentName, out string reason)
    {
        if (yaml.Length > MaxBytes)
        {
            reason = $"REJECTED: {documentName} is {yaml.Length:N0} characters; the limit is {MaxBytes:N0}. "
                   + "These are configuration documents — if something this large seems necessary, it belongs elsewhere.";
            return false;
        }

        var match = AnchorOrAlias().Match(yaml);
        if (match.Success)
        {
            reason = $"REJECTED: {documentName} contains a YAML anchor or alias ('{match.Value.Trim()}…'). "
                   + "Anchors and aliases are not permitted — they enable exponential-expansion attacks and "
                   + "ashlar documents never need them. Write the value out literally.";
            return false;
        }

        if (HasUnsafeStructure(yaml, out var duplicateKey, out var tooDeep))
        {
            if (tooDeep)
            {
                reason = $"REJECTED: {documentName} exceeds the YAML nesting limit of {MaxNestingDepth}. "
                       + "Reduce nested mappings and sequences before loading this configuration.";
                return false;
            }

            reason = $"REJECTED: {documentName} defines the key '{duplicateKey}' more than once in the same mapping. "
                   + "The safety envelope must be unambiguous — a duplicate key is refused, not silently resolved to "
                   + "the last value. Remove the duplicate (a stray one is usually a merge-conflict artifact).";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// True when the document exceeds the depth limit or a mapping repeats a scalar key. Walks the YAML event
    /// stream so it sees every nesting level, and fails closed: if the document cannot be
    /// scanned at all, we say "no duplicate found" and let the deserializer produce the real
    /// parse error (a malformed document is rejected downstream regardless).
    /// </summary>
    private static bool HasUnsafeStructure(string yaml, out string duplicateKey, out bool tooDeep)
    {
        duplicateKey = string.Empty;
        tooDeep = false;
        try
        {
            var parser = new Parser(new StringReader(yaml));
            // One frame per open container. IsMapping distinguishes mappings (alternating
            // key/value) from sequences; ExpectingKey tracks whether the next scalar in a
            // mapping is a key. Keys is the set of keys already seen in that mapping.
            var stack = new Stack<Frame>();

            while (parser.MoveNext())
            {
                if (parser.Current is MappingStart or SequenceStart && stack.Count >= MaxNestingDepth)
                {
                    tooDeep = true;
                    return true;
                }

                switch (parser.Current)
                {
                    case MappingStart:
                        ConsumeValueSlot(stack);
                        stack.Push(new Frame(isMapping: true));
                        break;
                    case SequenceStart:
                        ConsumeValueSlot(stack);
                        stack.Push(new Frame(isMapping: false));
                        break;
                    case MappingEnd:
                    case SequenceEnd:
                        if (stack.Count > 0) stack.Pop();
                        break;
                    case Scalar scalar:
                        if (stack.Count > 0 && stack.Peek() is { IsMapping: true } frame && frame.ExpectingKey)
                        {
                            if (!frame.Keys.Add(scalar.Value))
                            {
                                duplicateKey = scalar.Value;
                                return true;
                            }
                            frame.ExpectingKey = false;   // the next scalar/container is this key's value
                        }
                        else
                        {
                            ConsumeValueSlot(stack);       // a scalar value, or a sequence item
                        }
                        break;
                }
            }
        }
        catch (YamlException)
        {
            return false;   // let the deserializer report the malformed-document error
        }

        return false;
    }

    // After a mapping value (scalar, or a whole nested container) is consumed, the parent
    // mapping expects a key again.
    private static void ConsumeValueSlot(Stack<Frame> stack)
    {
        if (stack.Count > 0 && stack.Peek() is { IsMapping: true } frame && !frame.ExpectingKey)
        {
            frame.ExpectingKey = true;
        }
    }

    private sealed class Frame
    {
        public Frame(bool isMapping)
        {
            IsMapping = isMapping;
            ExpectingKey = isMapping;
        }

        public bool IsMapping { get; }
        public bool ExpectingKey { get; set; }
        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);
    }
}
