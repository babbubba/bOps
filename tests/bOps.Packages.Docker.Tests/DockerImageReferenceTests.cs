// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Docker.Tests;

/// <summary>The image reference and name grammar (ADR-0033): strict, and normalized the way the daemon reports references.</summary>
public sealed class DockerImageReferenceTests
{
    private static readonly string Digest = "sha256:" + new string('a', 64);

    [Theory]
    [InlineData("alpine", "alpine:latest")]
    [InlineData("alpine:3.20", "alpine:3.20")]
    [InlineData("docker.io/library/alpine:3.20", "alpine:3.20")]
    [InlineData("index.docker.io/library/alpine", "alpine:latest")]
    [InlineData("registry-1.docker.io/library/alpine:3", "alpine:3")]
    [InlineData("library/alpine", "alpine:latest")]
    [InlineData("docker.io/myorg/app:v1", "myorg/app:v1")]
    [InlineData("myorg/app", "myorg/app:latest")]
    [InlineData("library/foo/bar", "library/foo/bar:latest")]
    [InlineData("ghcr.io/owner/app:v1", "ghcr.io/owner/app:v1")]
    [InlineData("localhost:5000/app", "localhost:5000/app:latest")]
    [InlineData("localhost/app:1", "localhost/app:1")]
    [InlineData("registry.example.com:5443/team/app:1.2.3", "registry.example.com:5443/team/app:1.2.3")]
    [InlineData("my_app.v2-x:tag_1.0-rc", "my_app.v2-x:tag_1.0-rc")]
    [InlineData("a__b/c--d:1", "a__b/c--d:1")]
    [InlineData("deadbeef", "deadbeef:latest")]
    public void ANameIsAcceptedAndNormalized(string value, string familiar)
    {
        Assert.True(DockerImageReference.TryParseName(value, out var reference, out var error), error);
        Assert.Equal(familiar, reference!.Familiar);
        Assert.Equal(familiar, reference.ToString());
        Assert.False(reference.IsId);
        Assert.Null(reference.Id);
        Assert.Equal(value, reference.Original);
    }

    [Fact]
    public void ADefaultTagIsLatest_ButNotWhenATagOrDigestIsGiven()
    {
        Assert.True(DockerImageReference.TryParseName("alpine", out var bare, out _));
        Assert.Equal("latest", bare!.Tag);
        Assert.Null(bare.Digest);

        Assert.True(DockerImageReference.TryParseName("alpine:3.20", out var tagged, out _));
        Assert.Equal("3.20", tagged!.Tag);

        Assert.True(DockerImageReference.TryParseName($"alpine@{Digest}", out var pinned, out _));
        Assert.Null(pinned!.Tag);
        Assert.Equal(Digest, pinned.Digest);
        Assert.Equal($"alpine@{Digest}", pinned.Familiar);
        Assert.Equal("alpine", pinned.Repository);
    }

    [Fact]
    public void ATagAndADigestTogetherReportTheDigestForm()
    {
        Assert.True(DockerImageReference.TryParseName($"ghcr.io/o/app:1@{Digest}", out var reference, out _));

        Assert.Equal("1", reference!.Tag);
        Assert.Equal($"ghcr.io/o/app@{Digest}", reference.Familiar);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Alpine")]
    [InlineData("alpine:")]
    [InlineData("alpine:-x")]
    [InlineData("alpine:.x")]
    [InlineData("alp ine")]
    [InlineData("alpine;rm")]
    [InlineData("alpine\nlatest")]
    [InlineData("a/../b")]
    [InlineData("-alpine")]
    [InlineData("/alpine")]
    [InlineData("alpine/")]
    [InlineData("a//b")]
    [InlineData("docker.io/")]
    [InlineData("http://host/x")]
    [InlineData("x:y:z")]
    [InlineData("alpine@sha256:short")]
    [InlineData("alpine@md5:0123456789abcdef0123456789abcdef")]
    [InlineData("alpine:1@")]
    [InlineData("Example.com/UPPER")]
    [InlineData("host..bad/app")]
    [InlineData("-host.com/app")]
    [InlineData("host.com:/app")]
    [InlineData("host.com:123456/app")]
    [InlineData("a_/b")]
    [InlineData("a.-b")]
    public void AMalformedNameIsRefused(string value)
    {
        Assert.False(DockerImageReference.TryParseName(value, out var reference, out var error));
        Assert.Null(reference);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ANullNameIsRefused()
    {
        Assert.False(DockerImageReference.TryParseName(null, out var reference, out var error));
        Assert.Null(reference);
        Assert.Contains("empty", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AReferenceIsLimitedToTwoHundredFiftyFiveCharacters()
    {
        var atLimit = new string('a', 255);
        var over = new string('a', 256);

        Assert.True(DockerImageReference.TryParseName(atLimit, out _, out var error), error);
        Assert.False(DockerImageReference.TryParseName(over, out _, out var overError));
        Assert.Contains("255", overError, StringComparison.Ordinal);
        Assert.False(DockerImageReference.TryParse(over, out _, out var overError2));
        Assert.Contains("255", overError2, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0123456789ab")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void AnIdIsRecognized_WithOrWithoutThePrefix(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        foreach (var value in new[] { hex, "sha256:" + hex, hex.ToUpperInvariant() })
        {
            Assert.True(DockerImageReference.TryParse(value, out var reference, out var error), error);
            Assert.True(reference!.IsId);
            Assert.Equal(hex, reference.Id);
            Assert.Equal(hex, reference.Familiar);
            Assert.Null(reference.Repository);
            Assert.Null(reference.Tag);
        }
    }

    [Theory]
    [InlineData("0123456789a")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    public void AnIdOutsideTwelveToSixtyFourHexCharactersIsNotAnId(string value)
    {
        Assert.True(DockerImageReference.TryParse(value, out var reference, out var error), error);
        Assert.False(reference!.IsId);
        Assert.Equal(value + ":latest", reference.Familiar);
    }

    [Theory]
    [InlineData("0123456789ab")]
    [InlineData("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void AnIdIsNeverAcceptedWhereANameIsRequired(string value)
    {
        Assert.False(DockerImageReference.TryParseName(value, out var reference, out var error));
        Assert.Null(reference);
        Assert.Contains("id", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameThatIsNotAnIdIsAcceptedByTheGeneralParser()
    {
        Assert.True(DockerImageReference.TryParse("alpine:3.20", out var reference, out var error), error);
        Assert.False(reference!.IsId);
        Assert.Equal("alpine:3.20", reference.Familiar);
        Assert.False(DockerImageReference.TryParse("Alpine", out _, out _));
        Assert.False(DockerImageReference.TryParse(null, out _, out _));
        Assert.False(DockerImageReference.TryParse(" ", out _, out _));
    }

    [Theory]
    [InlineData("ab", true)]
    [InlineData("my-volume_1.data", true)]
    [InlineData("0data", true)]
    [InlineData("a", false)]
    [InlineData("", false)]
    [InlineData("-data", false)]
    [InlineData("_data", false)]
    [InlineData(".data", false)]
    [InlineData("da ta", false)]
    [InlineData("da/ta", false)]
    [InlineData("da:ta", false)]
    [InlineData("da;ta", false)]
    [InlineData("da\nta", false)]
    [InlineData("dätä", false)]
    public void AVolumeNameFollowsTheDaemonsRule(string name, bool valid)
    {
        Assert.Equal(valid, DockerNames.TryValidateVolumeName(name, out var error));
        Assert.Equal(valid, error is null);
    }

    [Fact]
    public void AVolumeNameIsLimitedToOneHundredTwentyEightCharacters()
    {
        Assert.True(DockerNames.TryValidateVolumeName(new string('a', 128), out _));
        Assert.False(DockerNames.TryValidateVolumeName(new string('a', 129), out var error));
        Assert.Contains("128", error, StringComparison.Ordinal);
        Assert.False(DockerNames.TryValidateVolumeName(null, out _));
    }

    [Theory]
    [InlineData("linux/amd64", true)]
    [InlineData("linux/arm64", true)]
    [InlineData("linux/arm/v7", true)]
    [InlineData("windows/amd64", true)]
    [InlineData("linux", false)]
    [InlineData("linux/", false)]
    [InlineData("/amd64", false)]
    [InlineData("Linux/amd64", false)]
    [InlineData("linux/amd64/v1/x", false)]
    [InlineData("linux/amd64;x", false)]
    [InlineData("", false)]
    public void APlatformIsOsArchitectureAndOptionalVariant(string platform, bool valid) =>
        Assert.Equal(valid, DockerNames.TryValidatePlatform(platform, out _));

    [Fact]
    public void APlatformIsBoundedInLength()
    {
        Assert.True(DockerNames.TryValidatePlatform("linux/" + new string('a', 26), out _));
        Assert.False(DockerNames.TryValidatePlatform("linux/" + new string('a', 27), out _));
        Assert.False(DockerNames.TryValidatePlatform("linux/" + new string('a', 30), out _));
        Assert.False(DockerNames.TryValidatePlatform(null, out _));
    }
}
