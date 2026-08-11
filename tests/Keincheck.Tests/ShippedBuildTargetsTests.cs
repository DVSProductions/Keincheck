using System.Xml.Linq;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// The <c>build/*.targets</c> files the packages ship.
///
/// These are never compiled, never referenced, and never executed by any test — the first thing
/// that evaluates them is a consumer's build, at which point a mistake is someone else's broken
/// build of a package they just installed. That happened: a `--` inside an XML comment (legal
/// prose, illegal XML) made <c>Keincheck.Client.targets</c> unloadable for every consumer that
/// opted into enrollment, and nothing in this repository noticed until a sample imported it.
/// </summary>
public sealed class ShippedBuildTargetsTests
{
    /// <summary>Every targets file shipped in a package's <c>build/</c> folder.</summary>
    public static TheoryData<string> ShippedTargets()
    {
        var root = RepositoryRoot();
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*.targets", SearchOption.AllDirectories))
        {
            // Only the ones that ship: build/ folders inside a package project, not obj/ copies.
            var dir = Path.GetFileName(Path.GetDirectoryName(path));
            if (string.Equals(dir, "build", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                data.Add(path);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ShippedTargets))]
    public void Is_Well_Formed_Xml(string path)
    {
        // XDocument.Load is exactly what MSBuild does first, and it is where `--` in a comment
        // fails: "An XML comment cannot contain '--'".
        var document = XDocument.Load(path);

        Assert.Equal("Project", document.Root!.Name.LocalName);
    }

    [Theory]
    [MemberData(nameof(ShippedTargets))]
    public void Declares_At_Least_One_Target(string path)
    {
        // A targets file that parses but defines nothing would import cleanly and do nothing at
        // all, which is indistinguishable from working right up until it matters.
        var document = XDocument.Load(path);

        Assert.Contains(document.Root!.Elements(), e => e.Name.LocalName == "Target");
    }

    [Fact]
    public void At_Least_One_Targets_File_Was_Found()
    {
        // Guards the discovery above: if the glob ever stops matching, every test in this class
        // would pass vacuously while checking nothing.
        Assert.NotEmpty(ShippedTargets());
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Keincheck.sln")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
