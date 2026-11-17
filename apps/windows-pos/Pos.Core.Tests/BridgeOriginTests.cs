using Pos.Core.Bridge;

namespace Pos.Core.Tests;

public sealed class BridgeOriginTests
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase) { "https://pos-app.example" };

    [Theory]
    [InlineData("https://pos-app.example/index.html")]
    [InlineData("https://POS-APP.example/assets/x.js?q=1#h")]
    [InlineData("https://pos-app.example:443/")]
    public void Trusted_origin_is_allowed(string uri) => Assert.True(BridgeOrigin.IsAllowed(uri, Allowed));

    [Theory]
    [InlineData("http://pos-app.example/")]
    [InlineData("https://pos-app.example.evil.com/")]
    [InlineData("https://evil.com/?https://pos-app.example")]
    [InlineData("https://pos-app.example:8443/")]
    [InlineData("https://user@pos-app.example/")]
    [InlineData("file:///C:/app/index.html")]
    [InlineData("about:blank")]
    [InlineData("data:text/html,hi")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_origins_are_rejected(string? uri) => Assert.False(BridgeOrigin.IsAllowed(uri, Allowed));
}
