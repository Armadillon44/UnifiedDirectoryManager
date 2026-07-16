namespace UnifiedDirectoryManager.Models;

/// <summary>
/// How a group's membership is applied — this decides which backend adds/removes the member, because the
/// three directories don't share one API:
/// <list type="bullet">
/// <item><see cref="OnPremAd"/> — on-prem Active Directory group; membership via LDAP, keyed by DN.</item>
/// <item><see cref="EntraGraph"/> — Entra ID security / Microsoft 365 group; membership via Microsoft Graph,
/// keyed by object id.</item>
/// <item><see cref="ExchangeOnline"/> — Exchange Online distribution list / mail-enabled security group;
/// Graph CAN'T modify these, so membership goes through the ExchangeOnlineManagement module
/// (Add/Remove-DistributionGroupMember), keyed by primary SMTP.</item>
/// </list>
/// </summary>
public enum GroupChannel { OnPremAd, EntraGraph, ExchangeOnline }

/// <summary>
/// A group selectable from any directory in the unified picker, and the currency the create/apply flows pass
/// around. <see cref="Channel"/> decides the apply backend: on-prem carries a <see cref="Dn"/>; Entra (Graph)
/// carries a <see cref="CloudId"/>; an Exchange distribution group carries a <see cref="Smtp"/> (and usually a
/// <see cref="CloudId"/> too, since it's also an Entra object). <see cref="Detail"/> is a short description for
/// the picker list (e.g. the parent OU on-prem, or the group kind/origin in the cloud).
/// </summary>
public sealed record GroupRef(
    GroupChannel Channel,
    string Name,
    string? Dn,
    string? CloudId,
    string? Smtp,
    string Detail)
{
    /// <summary>Short label for the directory/backend, shown in the picker and baskets.</summary>
    public string ChannelLabel => Channel switch
    {
        GroupChannel.OnPremAd => "On-prem",
        GroupChannel.EntraGraph => "Cloud",
        GroupChannel.ExchangeOnline => "Exchange",
        _ => "?",
    };

    /// <summary>Stable key for de-duplicating the basket (DN on-prem; Entra id or SMTP in the cloud).</summary>
    public string Key => Channel == GroupChannel.OnPremAd
        ? (Dn ?? Name)
        : (CloudId ?? Smtp ?? Name);
}

/// <summary>
/// A persisted reference to an Exchange Online distribution list / mail-enabled security group. Membership is
/// applied with Add-DistributionGroupMember (Graph can't), so the load-bearing identity is the primary
/// <see cref="Smtp"/>; <see cref="Id"/> (Entra object id) is kept for display and de-duplication.
/// </summary>
public sealed class DistributionGroupRef
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Smtp { get; set; } = string.Empty;
}
