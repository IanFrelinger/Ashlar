using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Ashlar.Infrastructure.Certification;

/// <summary>
/// Finds the concrete brick type in a PE image without loading or executing it.
/// Closes constructor <c>Environment.Exit</c> / constructor-hang during discovery:
/// nothing in this type calls <c>Assembly.Load*</c> or <c>Activator.CreateInstance</c>.
/// </summary>
internal static class MetadataBrickDiscovery
{
    /// <summary>Metadata name of the brick base type.</summary>
    public const string BrickMetadataName = "Ashlar.Core.Domain.Bricks.Brick";

    /// <summary>Returns the single concrete brick type's full name.</summary>
    public static string DiscoverBrickTypeName(byte[] assemblyBytes)
    {
        ArgumentNullException.ThrowIfNull(assemblyBytes);
        using var pe = new PEReader(new MemoryStream(assemblyBytes, writable: false));
        if (!pe.HasMetadata)
            throw new InvalidOperationException("gate-emitted artifact has no metadata");

        var reader = pe.GetMetadataReader();
        string? found = null;
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if ((type.Attributes & TypeAttributes.Abstract) != 0)
                continue;
            if ((type.Attributes & TypeAttributes.Interface) != 0)
                continue;
            // A nested or non-public type cannot be the shipped brick: the host resolves it
            // by name and constructs it. Reporting one would hand back a name that does not
            // round-trip through Assembly.GetType.
            if (!type.IsNested && (type.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public)
                continue;
            if (type.IsNested)
                continue;
            if (!InheritsBrick(reader, type))
                continue;

            var name = FullName(reader, type);
            if (found is not null)
            {
                throw new InvalidOperationException(
                    $"gate-emitted artifact contains more than one concrete brick type ({found}, {name})");
            }

            found = name;
        }

        return found
            ?? throw new InvalidOperationException(
                "gate-emitted artifact contains no public, non-nested, concrete type derived from "
                + BrickMetadataName);
    }

    private static bool InheritsBrick(MetadataReader reader, TypeDefinition type)
    {
        var current = type.BaseType;
        var hops = 0;
        while (!current.IsNil && hops++ < 16)
        {
            if (current.Kind == HandleKind.TypeReference)
            {
                var reference = reader.GetTypeReference((TypeReferenceHandle)current);
                var full = FullName(reader, reference);
                return string.Equals(full, BrickMetadataName, StringComparison.Ordinal);
            }

            if (current.Kind != HandleKind.TypeDefinition)
                return false;

            var def = reader.GetTypeDefinition((TypeDefinitionHandle)current);
            if (string.Equals(FullName(reader, def), BrickMetadataName, StringComparison.Ordinal))
                return true;
            current = def.BaseType;
        }

        return false;
    }

    private static string FullName(MetadataReader reader, TypeDefinition type)
    {
        var ns = reader.GetString(type.Namespace);
        var name = reader.GetString(type.Name);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static string FullName(MetadataReader reader, TypeReference reference)
    {
        var ns = reader.GetString(reference.Namespace);
        var name = reader.GetString(reference.Name);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }
}
