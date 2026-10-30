using Xunit;

// The API bootstrap and persistence fixtures share one ephemeral PostgreSQL database.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
