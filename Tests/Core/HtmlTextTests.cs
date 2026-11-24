using UnVault.Core.Util;

namespace UnVault.Core.Tests;

/// <summary>Fab's descriptions, which are HTML, as the plain text an item's preview shows.</summary>
public sealed class HtmlTextTests
{
    [Theory]
    [InlineData("Bazaar", "Bazaar")]
    [InlineData("<p>A tree pack.</p><p></p><p>Wind &amp; seasons&nbsp;included.</p>", "A tree pack.\n\nWind & seasons included.")]
    [InlineData("<p><span><em>Note:</em> rendered</span><br/>images</p>", "Note: rendered\nimages")]
    [InlineData("<ul><li>15 trees</li><li class=\"x\">2 seedlings</li></ul><p>Done</p>", "• 15 trees\n• 2 seedlings\n\nDone")]
    [InlineData("<p>See <a target=\"_blank\" href=\"https://youtu.be/x\">the tutorial</a>.</p>", "See the tutorial.")]
    [InlineData("<script>alert(1)</script><p>Text</p>", "alert(1)Text")] // shown as text, never run
    [InlineData("", "")]
    public void Html_becomes_plain_text(string html, string text) => Assert.Equal(text, HtmlText.ToPlain(html));
}
