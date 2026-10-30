using FluentAssertions;
using LalabAutoReport.Infrastructure.Services;
using Xunit;

namespace LalabAutoReport.Tests;

public class UpdaterChecksumTests
{
    private const string Hash = "19A3AB2A7FA92325D8134E16343344D99B5ED33564BF97EB7A7F5E28E5C2CB57";

    [Fact]
    public void ChecksumMatches_PublishedFormat_AcceptsMatchingHash()
    {
        GitHubUpdateService.ChecksumMatches(Hash, Hash.ToLowerInvariant() + "  LalabAutoReport.exe\n").Should().BeTrue();
    }

    [Fact]
    public void ChecksumMatches_IsCaseInsensitive()
    {
        GitHubUpdateService.ChecksumMatches(Hash.ToLowerInvariant(), Hash + "  LalabAutoReport.exe").Should().BeTrue();
    }

    [Fact]
    public void ChecksumMatches_DifferentHash_Rejects()
    {
        string other = "66F7D9D9F4EA2F2AA627624D15D9914566C302064CED76363438B6E533F95D4F";
        GitHubUpdateService.ChecksumMatches(other, Hash + "  LalabAutoReport.exe").Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-hash  LalabAutoReport.exe")]
    [InlineData("1234  LalabAutoReport.exe")]
    public void ChecksumMatches_EmptyOrMalformedContent_Rejects(string content)
    {
        GitHubUpdateService.ChecksumMatches(Hash, content).Should().BeFalse();
    }
}
