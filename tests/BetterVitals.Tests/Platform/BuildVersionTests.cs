using System.Globalization;
using BetterVitals.Platform;
using Xunit;

namespace BetterVitals.Tests.Platform;

public sealed class BuildVersionTests
{
    [Theory]
    // a higher version always replaces, a lower one never
    [InlineData("0.2.0+abc1234.20261020", "0.1.0+f3d5649.20261023", true)]
    [InlineData("0.1.0+f3d5649.20261023", "0.2.0+abc1234.20261020", false)]
    [InlineData("1.0.0+abc1234.20261101", "0.9.9+abc1234.20261101", true)]
    // same version: the later build replaces, an earlier one never
    [InlineData("0.1.0+abc1234.20261024", "0.1.0+f3d5649.20261023", true)]
    [InlineData("0.1.0+abc1234.20261022", "0.1.0+f3d5649.20261023", false)]
    // same version and day: another commit is offered (a test build), the very same build is not
    [InlineData("0.1.0+abc1234.20261023", "0.1.0+f3d5649.20261023", true)]
    [InlineData("0.1.0+f3d5649.20261023", "0.1.0+f3d5649.20261023", false)]
    [InlineData("0.1.0+F3D5649.20261023", "0.1.0+f3d5649.20261023", false)]
    // an installed copy without a readable version is replaced; a candidate without one never replaces
    [InlineData("0.1.0+abc1234.20261023", null, true)]
    [InlineData("0.1.0+abc1234.20261023", "", true)]
    [InlineData("0.1.0+abc1234.20261023", "garbage", true)]
    [InlineData(null, "0.1.0+f3d5649.20261023", false)]
    [InlineData("garbage", "0.1.0+f3d5649.20261023", false)]
    // a file version without metadata ("0.1.0.0") is an older build of the same version
    [InlineData("0.1.0+abc1234.20261023", "0.1.0", true)]
    [InlineData("0.1.0-beta.1+abc1234.20261023", "0.1.0+f3d5649.20261020", true)]
    public void Decides_which_build_to_install(string? candidate, string? installed, bool replace)
        => Assert.Equal(replace, BuildVersion.ShouldReplace(candidate, installed));

    [Fact]
    public void Shows_version_commit_and_date()
    {
        Assert.Equal("0.1.0 (f3d5649, 23.10.2026)", BuildVersion.Display("0.1.0+f3d5649.20261023", CultureInfo.GetCultureInfo("pl-PL")));
        Assert.Equal("0.1.0 (f3d5649, 10/23/2026)", BuildVersion.Display("0.1.0+f3d5649.20261023", CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("0.1.0", BuildVersion.Display("0.1.0", CultureInfo.InvariantCulture));
        Assert.Equal("?", BuildVersion.Display(null, CultureInfo.InvariantCulture));
        Assert.Equal("garbage", BuildVersion.Display("garbage", CultureInfo.InvariantCulture));
    }
}
