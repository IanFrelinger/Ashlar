using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests that deliberately reset <c>BsonMapper.Global</c> run alone.
/// </summary>
/// <remarks>
/// The mapper is process-wide static state shared by every LiteDB store in the assembly. Swapping it
/// out under a test that is running in parallel would hand that test the very race these tests exist
/// to prove is gone.
/// </remarks>
[CollectionDefinition("LiteDbMapper", DisableParallelization = true)]
public sealed class LiteDbMapperCollection;
