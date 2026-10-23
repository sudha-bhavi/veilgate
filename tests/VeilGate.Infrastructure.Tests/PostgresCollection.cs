using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture> { }
