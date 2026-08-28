using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Resolves pasted terms against ONE directory. Membership is owned by a particular service, so resolution
/// has to be too: a cloud-only user cannot join an on-prem group, and a user with no mailbox cannot join a
/// distribution list. Resolving against the wrong directory would find a person who cannot be added.
/// </summary>
public interface IMemberResolver
{
    /// <param name="progress">How many terms are done, for a paste that takes a while.</param>
    Task<IReadOnlyList<MemberResolution>> ResolveAsync(
        IReadOnlyList<PastedTerm> terms, IProgress<int>? progress, CancellationToken cancellationToken = default);
}

/// <summary>Which directory a paste is being resolved against.</summary>
public enum MemberBackend { ActiveDirectory, Entra, Exchange }

/// <summary>Exchange recipients. Batched by the service, because that channel serialises every call.</summary>
public sealed class ExchangeMemberResolver(IExchangeService exchange) : IMemberResolver
{
    public Task<IReadOnlyList<MemberResolution>> ResolveAsync(
        IReadOnlyList<PastedTerm> terms, IProgress<int>? progress, CancellationToken cancellationToken = default)
        => exchange.ResolveMembersAsync(terms, progress, cancellationToken);
}

/// <summary>
/// On-premises Active Directory, over LDAP. Exact first, then <c>(anr=…)</c> — ambiguous name resolution,
/// the directory's own answer to "I have part of a name": it matches common name, display name, first and
/// last name, and the logon name, which is exactly the set a pasted list arrives in.
/// </summary>
public sealed class AdMemberResolver(IDirectoryService directory) : IMemberResolver
{
    private static readonly string[] Columns =
        ["distinguishedName", "displayName", "sAMAccountName", "userPrincipalName", "objectClass"];

    public async Task<IReadOnlyList<MemberResolution>> ResolveAsync(
        IReadOnlyList<PastedTerm> terms, IProgress<int>? progress, CancellationToken cancellationToken = default)
    {
        var results = new List<MemberResolution>(terms.Count);
        var done = 0;

        foreach (var term in terms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = LdapFilter.EscapeValue(term.Term);
            var name = LdapFilter.EscapeValue(term.SearchText);

            // Exact on every identifier a paste can carry, in one round trip.
            var exact = await FindAsync(
                $"(&(objectCategory=person)(objectClass=user)(|(userPrincipalName={value})(sAMAccountName={value})"
                + $"(mail={value})(proxyAddresses=smtp:{value})(displayName={value})(displayName={name})))",
                cancellationToken);

            var resolution = PastedMemberParser.FromRung(term, MemberLookupKind.Address, exact);
            if (resolution is null)
            {
                // Ambiguous. anr is AD's own partial-name matcher; a wildcard on displayName alone would miss
                // anyone pasted as a logon name or a first name.
                var fuzzy = await FindAsync(
                    $"(&(objectCategory=person)(objectClass=user)(anr={name}))", cancellationToken);
                resolution = PastedMemberParser.FromRung(term, MemberLookupKind.Search, fuzzy)
                             ?? PastedMemberParser.NotFound(term);
            }
            results.Add(resolution);
            progress?.Report(++done);
        }
        return results;
    }

    private async Task<IReadOnlyList<MemberCandidate>> FindAsync(string filter, CancellationToken ct)
    {
        var rows = await directory.SearchAsync(
            new SearchQuery { ObjectType = AdObjectType.User, RawFilter = filter }, Columns, ct);

        var found = new List<MemberCandidate>();
        foreach (var r in rows)
        {
            // The DN is what an AD group membership is written with; without one there is nothing to add.
            var dn = r.Get("distinguishedName");
            if (string.IsNullOrWhiteSpace(dn)) continue;
            var display = r.Get("displayName");
            if (string.IsNullOrWhiteSpace(display)) display = r.Get("sAMAccountName");
            found.Add(new MemberCandidate(dn, display, r.Get("userPrincipalName"), "User"));
        }
        return found;
    }
}

/// <summary>
/// Entra ID, over Microsoft Graph. Graph has no ambiguous-name resolution, so the exact rung is filtered out
/// of a search result rather than asked for separately — one round trip per term either way.
/// </summary>
public sealed class EntraMemberResolver(IGraphService graph) : IMemberResolver
{
    public async Task<IReadOnlyList<MemberResolution>> ResolveAsync(
        IReadOnlyList<PastedTerm> terms, IProgress<int>? progress, CancellationToken cancellationToken = default)
    {
        var results = new List<MemberResolution>(terms.Count);
        var done = 0;

        foreach (var term in terms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await graph.ListUsersAsync(term.SearchText, null, cancellationToken);
            var all = page.Items.Select(ToCandidate).Where(c => c is not null).Select(c => c!).ToList();
            // Graph pages. Deciding "exactly one exact match" over the first page only would resolve a row
            // against a slice of the answer, so a further page means the row asks instead.
            var truncated = !string.IsNullOrWhiteSpace(page.NextLink);

            // An exact hit inside a search result is still an exact hit: the term matched a whole identifier
            // or a whole name, so it can resolve the row. Everything else asks.
            var exact = all.Where(c => Matches(c, term)).ToList();
            var resolution = exact.Count > 0 && !truncated
                ? PastedMemberParser.FromRung(term, MemberLookupKind.Address, exact)
                : PastedMemberParser.FromRung(term, MemberLookupKind.Search, all);

            results.Add(resolution ?? PastedMemberParser.NotFound(term));
            progress?.Report(++done);
        }
        return results;
    }

    private static bool Matches(MemberCandidate c, PastedTerm term) =>
        Same(c.Secondary, term.Term)                 // userPrincipalName
        || Same(c.DisplayName, term.Term)
        || Same(c.DisplayName, term.SearchText);     // the flipped "Last, First"

    private static bool Same(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static MemberCandidate? ToCandidate(CloudObjectRow row)
    {
        // The object id is what a Graph membership reference is written with.
        if (string.IsNullOrWhiteSpace(row.Id)) return null;
        return new MemberCandidate(row.Id, row.DisplayName, row.Get("userPrincipalName"), row.Kind.ToString());
    }
}
