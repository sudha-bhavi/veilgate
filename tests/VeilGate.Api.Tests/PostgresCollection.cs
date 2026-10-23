using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Api.Tests;

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture> { }
