using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Groups every UI test under one xUnit collection so they serialize against the
/// single shared <see cref="HeadlessSession"/> (one Avalonia Application per
/// process). New UI-touching test classes — including the future tool-level
/// tests — should be annotated with <c>[Collection(HeadlessCollection.Name)]</c>.
/// </summary>
[CollectionDefinition(Name)]
public sealed class HeadlessCollection : ICollectionFixture<HeadlessSession>
{
    public const string Name = "Avalonia headless UI";
}
