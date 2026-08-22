using System.IO;
using System.Text.Json;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

/// <summary>
/// Executes the shared SemVer 2 conformance vectors from
/// tools/dsh-semver-vectors.json against the C# implementation. The web test
/// suite runs the same file against tools/dsh-semver.mjs so the PowerShell
/// toolchain and the client gate can never drift apart.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class DshSemanticVersionVectorsTests
{
    private static readonly string VectorsPath =
        Path.Combine(TestWorkspace.RepositoryRoot, "tools", "dsh-semver-vectors.json");

    private static JsonDocument LoadVectors()
        => JsonDocument.Parse(File.ReadAllText(VectorsPath));

    [TestMethod]
    public void Vectors_Chain_IsStrictlyAscending()
    {
        using var document = LoadVectors();
        var chain = document.RootElement.GetProperty("ordered").EnumerateArray()
            .Select(element => element.GetString()!)
            .ToArray();
        CollectionAssert.AllItemsAreNotNull(chain);
        for (var index = 1; index < chain.Length; index++)
        {
            Assert.IsTrue(
                DshSemanticVersion.TryParse(chain[index - 1], out var left),
                $"{chain[index - 1]} must parse");
            Assert.IsTrue(
                DshSemanticVersion.TryParse(chain[index], out var right),
                $"{chain[index]} must parse");
            // The C# implementation guarantees precedence sign, not a
            // normalized -1/0/1 (identifier comparison returns the raw
            // ordinal difference).
            Assert.AreEqual(
                -1,
                Math.Sign(left.CompareTo(right)),
                $"expected {chain[index - 1]} < {chain[index]}");
        }
    }

    [TestMethod]
    public void Vectors_Pairs_MatchExpectedPrecedence()
    {
        using var document = LoadVectors();
        foreach (var pair in document.RootElement.GetProperty("pairs").EnumerateArray())
        {
            var left = pair.GetProperty("a").GetString()!;
            var right = pair.GetProperty("b").GetString()!;
            var expect = pair.GetProperty("expect").GetInt32();
            Assert.IsTrue(DshSemanticVersion.TryParse(left, out var leftVersion), left);
            Assert.IsTrue(DshSemanticVersion.TryParse(right, out var rightVersion), right);
            Assert.AreEqual(expect, Math.Sign(leftVersion.CompareTo(rightVersion)), $"{left} vs {right}");
        }
    }

    [TestMethod]
    public void Vectors_BuildMetadata_IsIgnoredForPrecedence()
    {
        using var document = LoadVectors();
        foreach (var pair in document.RootElement.GetProperty("equalByPrecedence").EnumerateArray())
        {
            var left = pair.GetProperty("a").GetString()!;
            var right = pair.GetProperty("b").GetString()!;
            Assert.IsTrue(DshSemanticVersion.TryParse(left, out var leftVersion), left);
            Assert.IsTrue(DshSemanticVersion.TryParse(right, out var rightVersion), right);
            Assert.AreEqual(0, leftVersion.CompareTo(rightVersion), $"{left} vs {right}");
        }
    }

    [TestMethod]
    public void Vectors_InvalidStrings_FailToParse()
    {
        using var document = LoadVectors();
        foreach (var invalid in document.RootElement.GetProperty("invalid").EnumerateArray())
        {
            var value = invalid.GetString()!;
            Assert.IsFalse(
                DshSemanticVersion.TryParse(value, out _),
                $"'{value}' must be rejected");
        }
    }

    [TestMethod]
    public void Vectors_Latest_Selection_MatchesExpectation()
    {
        using var document = LoadVectors();
        foreach (var scenario in document.RootElement.GetProperty("latest").EnumerateArray())
        {
            var seed = scenario.GetProperty("seed").GetString()!;
            var candidates = scenario.GetProperty("candidates").EnumerateArray()
                .Select(element => element.GetString()!)
                .ToArray();
            Assert.IsTrue(DshSemanticVersion.TryParse(seed, out var seedVersion), seed);

            string? best = null;
            DshSemanticVersion bestVersion = default;
            foreach (var candidate in candidates)
            {
                if (!DshSemanticVersion.TryParse(candidate, out var parsed)
                    || parsed.CompareTo(seedVersion) <= 0)
                    continue;
                if (best is null || parsed.CompareTo(bestVersion) > 0)
                {
                    best = candidate;
                    bestVersion = parsed;
                }
            }

            var expected = scenario.TryGetProperty("expect", out var expect)
                && expect.ValueKind == JsonValueKind.String
                ? expect.GetString()
                : null;
            Assert.AreEqual(expected, best, $"latest > {seed} among [{string.Join(", ", candidates)}]");
        }
    }
}
