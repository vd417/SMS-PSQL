using FluentAssertions;
using Sms.Application.Services.Comms;
using Sms.Modules.Comms;
using Xunit;

namespace Sms.Tests.Unit.Comms;

public class ChatContentFilterTests
{
    // A representative slice of the real seeded list (single words + a phrase + leet variants).
    private static readonly ChatBannedWord[] Words =
    [
        new("single", "sex"),
        new("single", "ass"),
        new("single", "anger"),
        new("single", "land"),
        new("single", "sendnudes"),
        new("single", "madarchod"),
        new("single", "a$$"),
        new("single", "f**k"),
        new("single", "d!ck"),
        new("single", "गाली"),
        new("phrase", "shut up"),
        new("phrase", "ullu ka pattha"),
    ];

    private static bool Blocked(string text) => ChatContentFilter.Match(Words, text).Blocked;

    [Theory]
    [InlineData("you are an ass")]
    [InlineData("ASS")]                       // case-insensitive
    [InlineData("stop sending sex stuff")]
    [InlineData("please sendnudes")]          // concatenated single token
    [InlineData("a$$")]                        // leet $ -> s
    [InlineData("f**k off")]                  // leet * stripped -> fk... see dedicated test
    [InlineData("you d!ck")]                  // leet ! -> i
    [InlineData("madarchod")]
    [InlineData("गाली मत दो")]                // Devanagari token
    [InlineData("just shut up now")]          // phrase (collapsed substring)
    [InlineData("tum ullu ka pattha ho")]     // phrase with words around it
    public void Blocks_banned_content(string text) => Blocked(text).Should().BeTrue();

    [Theory]
    [InlineData("the class starts at 9")]     // "class" must NOT trip "ass"
    [InlineData("please pass the book")]      // "pass" must NOT trip "ass"
    [InlineData("submit your assignment")]    // "assignment" must NOT trip "ass"
    [InlineData("there is danger ahead")]     // "danger" must NOT trip "anger"
    [InlineData("meet the manager")]          // "manager" must NOT trip "anger"
    [InlineData("we visited england and ireland")] // must NOT trip "land"
    [InlineData("the island is beautiful")]   // must NOT trip "land"
    [InlineData("class 18 has 21 students")]  // numbers must not be flagged
    [InlineData("good morning teacher")]
    [InlineData("")]
    [InlineData("   ")]
    public void Allows_normal_content(string text) => Blocked(text).Should().BeFalse();

    [Fact]
    public void Fstar_evasion_is_caught_as_token()
    {
        // "f**k" -> de-leet strips '*' -> "fk"; add "fk" to the list to catch this exact token.
        var words = new[] { new ChatBannedWord("single", "fk") };
        ChatContentFilter.Match(words, "f**k you").Blocked.Should().BeTrue();
        ChatContentFilter.Match(words, "fork the repo").Blocked.Should().BeFalse();
    }

    [Fact]
    public void Empty_word_list_allows_everything() =>
        ChatContentFilter.Match(System.Array.Empty<ChatBannedWord>(), "madarchod").Blocked.Should().BeFalse();
}
