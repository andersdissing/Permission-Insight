using PermissionInsight.Api.Auditing;
using Xunit;

namespace PermissionInsight.Api.Tests;

/// <summary>
/// What may be written into the audit log.
///
/// The audit trail is the only record the backend keeps, and it lives in
/// Application Insights, which is read by a different and wider population than
/// the app role that gates this API. So the question these tests protect is not
/// "does the log work" but "does the log stay inside its own remit": who looked
/// at which site, never which documents were in it.
/// </summary>
public sealed class AuditQueryTests
{
    [Fact]
    public void An_item_path_is_never_logged()
    {
        // The one that matters. A library scan sends one of these per broken
        // item, so logging the value would deposit a browsable index of an
        // organisation's most exposed documents into the workspace.
        var redacted = AuditQuery.Redact("?siteId=abc&listId=def&path=%2Fsites%2Fhr%2FPersonalesager%2FOpsigelse.docx");

        Assert.DoesNotContain("Personalesager", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Opsigelse", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=[redacted]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Identifiers_and_search_terms_survive_because_they_are_the_target()
    {
        var redacted = AuditQuery.Redact("?q=finance&siteId=contoso.sharepoint.com,1,2&listId=abc&itemId=42");

        Assert.Contains("q=finance", redacted, StringComparison.Ordinal);
        Assert.Contains("listId=abc", redacted, StringComparison.Ordinal);
        Assert.Contains("itemId=42", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("[redacted]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrecognised_parameter_is_redacted_rather_than_passed()
    {
        // The allow-list has to fail closed. A parameter added later must not
        // start being logged because nobody remembered this file existed.
        var redacted = AuditQuery.Redact("?siteId=abc&somethingNew=a-document-name.docx");

        Assert.Contains("siteId=abc", redacted, StringComparison.Ordinal);
        Assert.Contains("somethingNew=[redacted]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void The_paging_token_is_redacted()
    {
        // SharePoint paging tokens encode the sort column's values, so a token
        // can carry a file name as a side effect of how paging works.
        var redacted = AuditQuery.Redact("?listId=abc&skipToken=Paged%3dTRUE%26p_FileLeafRef%3dSalary.xlsx");

        Assert.DoesNotContain("Salary", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("skipToken=[redacted]", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("?")]
    public void Nothing_to_redact_produces_nothing(string? query)
    {
        Assert.Equal(string.Empty, AuditQuery.Redact(query));
    }

    [Fact]
    public void A_valueless_parameter_does_not_leak_through_the_gap()
    {
        // Parses with a null name and the text as its value, which is the shape
        // most likely to slip past a naive rewrite.
        var redacted = AuditQuery.Redact("?%2Fsites%2Fhr%2FSalary.xlsx");

        Assert.DoesNotContain("Salary", redacted, StringComparison.OrdinalIgnoreCase);
    }
}
