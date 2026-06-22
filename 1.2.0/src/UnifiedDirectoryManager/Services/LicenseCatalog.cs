namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Maps Microsoft 365 SKU part numbers (the native ids returned by Graph, e.g. "ENTERPRISEPACK") to
/// friendly product names (e.g. "Office 365 E3"). This is the well-known curated subset of Microsoft's
/// "Product names and service plan identifiers for licensing" reference; unknown SKUs fall back to the
/// part number unchanged, so the column is always populated.
/// </summary>
public static class LicenseCatalog
{
    private static readonly IReadOnlyDictionary<string, string> Names =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Microsoft 365 / Office 365 suites
            ["SPE_E3"] = "Microsoft 365 E3",
            ["SPE_E5"] = "Microsoft 365 E5",
            ["SPE_F1"] = "Microsoft 365 F3",
            ["SPE_F5_SEC"] = "Microsoft 365 F5 Security",
            ["ENTERPRISEPACK"] = "Office 365 E3",
            ["ENTERPRISEPREMIUM"] = "Office 365 E5",
            ["ENTERPRISEPREMIUM_NOPSTNCONF"] = "Office 365 E5 (without Audio Conferencing)",
            ["ENTERPRISEPACKPLUS"] = "Office 365 E4",
            ["STANDARDPACK"] = "Office 365 E1",
            ["DESKLESSPACK"] = "Office 365 F3",
            ["O365_BUSINESS_ESSENTIALS"] = "Microsoft 365 Business Basic",
            ["O365_BUSINESS_PREMIUM"] = "Microsoft 365 Business Standard",
            ["SPB"] = "Microsoft 365 Business Premium",
            ["O365_BUSINESS"] = "Microsoft 365 Apps for Business",
            ["OFFICESUBSCRIPTION"] = "Microsoft 365 Apps for Enterprise",
            ["M365EDU_A3_FACULTY"] = "Microsoft 365 A3 for Faculty",
            ["M365EDU_A3_STUDENT"] = "Microsoft 365 A3 for Students",
            ["M365EDU_A5_FACULTY"] = "Microsoft 365 A5 for Faculty",
            ["STANDARDWOFFPACK_FACULTY"] = "Office 365 A1 for Faculty",
            ["STANDARDWOFFPACK_STUDENT"] = "Office 365 A1 for Students",

            // Exchange / Teams / SharePoint standalone
            ["EXCHANGESTANDARD"] = "Exchange Online (Plan 1)",
            ["EXCHANGEENTERPRISE"] = "Exchange Online (Plan 2)",
            ["EXCHANGEDESKLESS"] = "Exchange Online Kiosk",
            ["EXCHANGEARCHIVE_ADDON"] = "Exchange Online Archiving for Exchange Online",
            ["SHAREPOINTSTANDARD"] = "SharePoint Online (Plan 1)",
            ["SHAREPOINTENTERPRISE"] = "SharePoint Online (Plan 2)",
            ["MCOSTANDARD"] = "Skype for Business Online (Plan 2)",
            ["TEAMS_EXPLORATORY"] = "Microsoft Teams Exploratory",
            ["Microsoft_Teams_Premium"] = "Microsoft Teams Premium",
            ["MCOEV"] = "Microsoft Teams Phone Standard",
            ["MCOMEETADV"] = "Microsoft 365 Audio Conferencing",
            ["MEETING_ROOM"] = "Microsoft Teams Rooms Standard",

            // EMS / Entra ID / security
            ["EMS"] = "Enterprise Mobility + Security E3",
            ["EMSPREMIUM"] = "Enterprise Mobility + Security E5",
            ["AAD_PREMIUM"] = "Microsoft Entra ID P1",
            ["AAD_PREMIUM_P2"] = "Microsoft Entra ID P2",
            ["RMSBASIC"] = "Azure Rights Management",
            ["RIGHTSMANAGEMENT"] = "Azure Information Protection Plan 1",
            ["INTUNE_A"] = "Intune",
            ["Microsoft_Intune_Suite"] = "Microsoft Intune Suite",
            ["IDENTITY_THREAT_PROTECTION"] = "Microsoft 365 E5 Security",
            ["INFORMATION_PROTECTION_COMPLIANCE"] = "Microsoft 365 E5 Compliance",
            ["ATP_ENTERPRISE"] = "Microsoft Defender for Office 365 (Plan 1)",
            ["THREAT_INTELLIGENCE"] = "Microsoft Defender for Office 365 (Plan 2)",
            ["WIN_DEF_ATP"] = "Microsoft Defender for Endpoint",

            // Power Platform / Dynamics / Project / Visio
            ["FLOW_FREE"] = "Microsoft Power Automate Free",
            ["POWER_BI_STANDARD"] = "Power BI (free)",
            ["POWER_BI_PRO"] = "Power BI Pro",
            ["POWERAPPS_VIRAL"] = "Microsoft Power Apps Plan 2 Trial",
            ["PROJECTPROFESSIONAL"] = "Project Plan 3",
            ["PROJECTPREMIUM"] = "Project Plan 5",
            ["PROJECT_P1"] = "Project Plan 1",
            ["VISIOCLIENT"] = "Visio Plan 2",
            ["VISIO_PLAN1_DEPT"] = "Visio Plan 1",
            ["DYN365_ENTERPRISE_PLAN1"] = "Dynamics 365 Customer Engagement Plan",

            // Common add-ons / free
            ["WINDOWS_STORE"] = "Windows Store for Business",
            ["MCOPSTNC"] = "Communications Credits",
            ["POWER_BI_INDIVIDUAL_USER"] = "Power BI Premium Per User",
            ["CCIBOTS_PRIVPREV_VIRAL"] = "Power Virtual Agents Viral Trial",
        };

    /// <summary>Friendly product name for a SKU part number, or the part number itself if unknown.</summary>
    public static string Friendly(string? skuPartNumber)
    {
        if (string.IsNullOrWhiteSpace(skuPartNumber)) return "(unknown)";
        return Names.TryGetValue(skuPartNumber, out var name) ? name : skuPartNumber;
    }
}
