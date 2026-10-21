namespace Sondergraph.Core.Tests;

/// <summary>
/// Tests for guessing a gender from a name.
/// </summary>
/// <remarks>
/// The guesses are written into the field a recorded gender uses, so the two behaviours that
/// matter most are: never guessing on an ambiguous name, and never guessing on something that
/// is not a name at all.
/// </remarks>
public sealed class GenderGuessTests
{
    [Theory]
    [InlineData("Aaron Moniz", "M")]
    [InlineData("Abigail Loewen", "F")]
    [InlineData("Michael Chen", "M")]
    [InlineData("Sarah Jones", "F")]
    [InlineData("Abdullah Dabbagh", "M")]
    public void Common_names_produce_a_guess(string name, string expected)
    {
        var guess = GenderGuess.Guess(name);

        Assert.NotNull(guess);
        Assert.Equal(expected, guess.Gender);
    }

    [Theory]
    [InlineData("Alex Balfur")]
    [InlineData("Sam Taylor")]
    [InlineData("Jordan Lee")]
    [InlineData("Casey Morgan")]
    [InlineData("Jamie Fox")]
    [InlineData("Kim Park")]
    [InlineData("Ashley Smith")]
    [InlineData("Chris Wong")]
    public void Ambiguous_names_produce_no_guess(string name)
    {
        // The most important behaviour in the file. A confident wrong answer on a real person
        // is worse than a blank, which is visibly unfilled and prompts someone to look.
        Assert.Null(GenderGuess.Guess(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("_soup_can_2")]
    [InlineData("12345")]
    [InlineData("x")]
    public void Non_names_produce_no_guess(string? name)
    {
        Assert.Null(GenderGuess.Guess(name));
    }

    [Fact]
    public void A_handle_with_separators_still_yields_its_first_token()
    {
        // "abdullah_dabbagh_" is a real record here; its first token is a name.
        var guess = GenderGuess.Guess("abdullah_dabbagh_");

        Assert.NotNull(guess);
        Assert.Equal("M", guess.Gender);
        Assert.Equal("abdullah", guess.Source);
    }

    [Fact]
    public void An_unknown_name_produces_no_guess()
    {
        // Better to say nothing than to default to anything.
        Assert.Null(GenderGuess.Guess("Xzxzyq"));
    }

    [Fact]
    public void The_guess_confidence_is_reported()
    {
        var guess = GenderGuess.Guess("Michael");

        Assert.NotNull(guess);
        Assert.True(guess.Confidence > 0);
    }

    [Fact]
    public void The_table_covers_a_reasonable_vocabulary()
    {
        Assert.True(GenderGuess.KnownNameCount > 400, $"only {GenderGuess.KnownNameCount} names");
    }
}
