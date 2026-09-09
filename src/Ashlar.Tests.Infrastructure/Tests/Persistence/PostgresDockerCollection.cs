using System.ComponentModel;
using System.Diagnostics;
using Npgsql;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Persistence;

[CollectionDefinition("PostgresDocker", DisableParallelization = true)]
public sealed class PostgresDockerCollection : ICollectionFixture<PostgresDockerFixture>;
