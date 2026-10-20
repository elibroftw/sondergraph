namespace Sondergraph.Core.Tests;

/// <summary>
/// Tests for which addresses count as a person's.
/// </summary>
/// <remarks>
/// The real cases come from this workspace: GitHub thread notifications synced into contacts
/// produced entries named after repositories, each carrying one or more
/// reply+AFCPYIZ...@reply.github.com addresses. The broader risk in the other direction is
/// discarding a real person's email, which the user cannot see or recover from.
/// </remarks>
public sealed class ContactEmailsTests
{
    [Theory]
    [InlineData("reply+AFCPYIZVMEMNT5IZHDNUAKN27R4FNEVBNHHBVU4TMI@reply.github.com")]
    [InlineData("reply+AFCPYI7YEYJAVYG3M6KX3RF3GXSABEVBNHHBUTIRDQ@reply.github.com")]
    [InlineData("reply@reply.github.com")]
    [InlineData("noreply@github.com")]
    [InlineData("no-reply@github.com")]
    [InlineData("no_reply@example.com")]
    [InlineData("donotreply@example.com")]
    [InlineData("notifications@github.com")]
    [InlineData("Notifications@GitHub.com")]
    [InlineData("mailer-daemon@example.com")]
    [InlineData("postmaster@example.com")]
    [InlineData("bounces+jane@example.com")]
    public void Machine_and_reply_addresses_are_rejected(string email)
    {
        Assert.False(ContactEmails.IsPersonAddress(email));
    }

    [Theory]
    [InlineData("jane@example.com")]
    [InlineData("jane.doe@gmail.com")]
    [InlineData("elijah@hdsb.ca")]
    [InlineData("someone@github.com")]
    [InlineData("hi@replydesk.co")]
    [InlineData("Anitha.Ramesh@tdsecurities.com")]
    public void Ordinary_addresses_are_kept(string email)
    {
        // The last two matter: "replydesk" merely starts with "reply" in a different word,
        // and a bare github.com mailbox is not a notification address.
        Assert.True(ContactEmails.IsPersonAddress(email));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("@example.com")]
    [InlineData("jane@")]
    public void Malformed_input_is_rejected(string? email)
    {
        Assert.False(ContactEmails.IsPersonAddress(email));
    }

    [Fact]
    public void Keeping_filters_and_deduplicates()
    {
        var kept = ContactEmails
            .KeepPersonAddresses([
                "jane@example.com",
                "reply+abc@reply.github.com",
                "JANE@example.com",
            ])
            .ToList();

        Assert.Single(kept);
        Assert.Equal("jane@example.com", kept[0]);
    }

    [Fact]
    public void A_bare_github_mailbox_is_not_swept_up_by_the_subdomain_rule()
    {
        // Blocking github.com wholesale would look thorough and quietly drop a real person.
        Assert.True(ContactEmails.IsPersonAddress("person@github.com"));
        Assert.False(ContactEmails.IsPersonAddress("anything@reply.github.com"));
    }
}
