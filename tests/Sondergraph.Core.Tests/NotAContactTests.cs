namespace Sondergraph.Core.Tests;

/// <summary>
/// Tests for the "not a contact" flag.
/// </summary>
/// <remarks>
/// Suppression is a judgement about a person, not about the accuracy of the data, so the
/// record must survive. These tests pin that distinction: marking hides, it never deletes.
/// </remarks>
public sealed class NotAContactTests
{
    private static Person P(string id, bool notAContact = false) =>
        new() { Id = id, Name = id, NotAContact = notAContact };

    [Fact]
    public void The_flag_defaults_to_false()
    {
        Assert.False(P("x").NotAContact);
    }

    [Fact]
    public void A_suppressed_person_still_carries_their_data()
    {
        // The point of the flag: the record is kept, including anything the user recorded.
        var person = P("x", notAContact: true) with
        {
            Phones = ["+1 555 0100"],
            Gender = "F",
            Tags = ["barista"],
        };

        Assert.True(person.NotAContact);
        Assert.Single(person.Phones);
        Assert.Equal("F", person.Gender);
        Assert.Equal(["barista"], person.Tags);
    }
}
