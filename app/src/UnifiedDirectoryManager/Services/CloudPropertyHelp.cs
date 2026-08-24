namespace UnifiedDirectoryManager.Services;

/// <summary>
/// One short sentence per property, keyed by the property-row key the detail panes produce. Shown behind the
/// "?" beside a row's label, in every pane that builds <see cref="Models.CloudProperty"/> rows: Entra users,
/// groups and devices, Exchange mailboxes, and Exchange distribution groups.
///
/// These say what a setting IS. The reason a row cannot be edited is separate (<see cref="Models.CloudProperty.Tooltip"/>)
/// and is appended underneath, so one place answers both "what is this" and "why can't I change it".
///
/// A key with no entry simply shows no "?" — an absent explanation is better than a guessed one. Where a
/// setting has a trap an operator would otherwise walk into (a stored double negative, a one-way switch, a
/// pair of flags that must disagree) the sentence says so, because that is the moment they are reading it.
/// </summary>
public static class CloudPropertyHelp
{
    public static string? For(string key) => Text.TryGetValue(key, out var v) ? v : null;

    private static readonly IReadOnlyDictionary<string, string> Text =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // --- identity, shared across panes -----------------------------------------------------------
            ["displayName"] = "The name this object is shown under in the admin centres, and — for anything that receives mail — in address lists and Outlook.",
            ["name"] = "The directory object's name. Usually the same as the display name, but stored separately.",
            ["alias"] = "The Exchange alias, or mail nickname. It identifies the object to Exchange, and where an address policy applies it is what the primary address is built from — but it need not match the address.",
            ["id"] = "The Entra ID object identifier. Permanent, and the same value Microsoft Graph uses.",
            ["exchangeGuid"] = "Exchange's own identifier for this object. Unchanged by renames, so it is what writes address.",
            ["created"] = "When the object was created.",
            ["changed"] = "When the object was last modified, by anyone or anything.",
            ["dirSynced"] = "Whether this object is mastered in on-premises Active Directory. If it is, Exchange Online refuses every change.",
            ["isDirSynced"] = "Whether this object is mastered in on-premises Active Directory. If it is, Exchange Online refuses every change.",
            ["description"] = "Free text describing what this object is for. Some clients show it in the address book, so treat it as visible.",
            ["userPrincipalName"] = "The sign-in name. Usually, but not always, the same as the primary email address.",
            ["mail"] = "The primary email address recorded in the directory.",
            ["mailNickname"] = "The mail nickname, the part of the address before the @.",

            // --- distribution group ----------------------------------------------------------------------
            ["groupType"] = "What kind of group this is. A distribution list only receives mail; a mail-enabled security group also grants permissions; a Microsoft 365 group additionally owns its own mailbox and site.",
            ["roomList"] = "Marks a distribution list whose members are all room mailboxes, so Outlook can offer a building when booking. Cannot be undone.",
            ["emailAddressPolicy"] = "Whether an address policy builds this group's addresses. When it does, changing the alias also rewrites the primary address.",
            ["mailTip"] = "A note shown to anyone starting a message to this group, before they send it.",
            ["hiddenFromAddressLists"] = "Hides the object from the address book. It still receives mail at its addresses.",
            ["hiddenGroupMembership"] = "Hides the members from anyone who is not one. It can be turned on later, but Microsoft documents no way to turn it back off.",
            ["managedBy"] = "The group's owners. They approve join requests and receive delivery reports when those are sent to the owner.",
            ["joinRestriction"] = "Who may join. Open lets anyone add themselves; ApprovalRequired asks an owner; Closed means owners add members.",
            ["departRestriction"] = "Whether members may remove themselves, or only an owner can remove them.",
            ["grantSendOnBehalfTo"] = "Who may send as though on the group's behalf. Recipients see \"sent on behalf of\".",
            ["externalSenders"] = "Whether people outside the organization may send to this group. Exchange stores this inverted, as a requirement to authenticate.",
            ["acceptFrom"] = "If set, only these senders can reach the group. Everyone else is rejected.",
            ["rejectFrom"] = "Senders whose messages to this group are rejected.",
            ["bccBlocked"] = "Blocks delivery when the group is used in the Bcc line, and bounces the message back to the sender.",
            ["maxSendSize"] = "The largest message the group may send. Exchange Online takes this from the organization instead.",
            ["maxReceiveSize"] = "The largest message the group accepts. Exchange Online takes this from the organization instead.",
            ["deliveryReports"] = "Who is told when a message to the group cannot be delivered. One of the two must be chosen: neither, or both, leaves messages without a return path.",
            ["sendOof"] = "Whether a sender to the group sees the automatic replies of members who are away.",
            ["moderationEnabled"] = "Whether messages to this group wait for a moderator's approval before delivery.",
            ["moderatedBy"] = "Who approves messages when moderation is on.",
            ["moderationNotifications"] = "Who is told when their message is not approved: everyone, only people inside the organization, or nobody.",
            ["bypassModeration"] = "Senders whose messages skip moderation entirely.",

            // --- addresses ------------------------------------------------------------------------------
            ["primaryAddress"] = "The address replies are sent from. Changing it redirects mail and stops the old address working.",
            ["primarySmtpAddress"] = "The address replies are sent from. Changing it redirects mail and stops the old address working.",
            ["secondaryAddresses"] = "Extra addresses that also deliver here. Adding or removing one does not change where replies come from.",
            ["serviceAddresses"] = "Addresses Microsoft 365 maintains for routing. Removing one breaks mail flow, so they are not editable here.",
            ["otherAddresses"] = "Non-SMTP addresses such as X.500, kept so replies to old messages still resolve.",

            // --- mailbox --------------------------------------------------------------------------------
            ["recipientTypeDetails"] = "What kind of mailbox this is: a person's, a shared mailbox, a room, or equipment.",
            ["whenMailboxCreated"] = "When the mailbox itself was created, which can be later than the account.",
            ["forwardingAddress"] = "An internal recipient every message is forwarded to.",
            ["forwardingSmtpAddress"] = "An external address every message is forwarded to.",
            ["deliverToMailboxAndForward"] = "Whether forwarded mail is also kept in this mailbox, or only forwarded.",
            ["issueWarningQuota"] = "The size at which the owner is warned the mailbox is filling up.",
            ["prohibitSendQuota"] = "The size at which the mailbox can no longer send.",
            ["prohibitSendReceiveQuota"] = "The size at which the mailbox can no longer send or receive. Mail to it bounces.",
            ["useDatabaseQuotaDefaults"] = "Whether the quotas above come from the service defaults rather than from this mailbox.",
            ["litigationHoldEnabled"] = "Whether everything in the mailbox is preserved and cannot be permanently deleted.",
            ["litigationHoldDate"] = "When the hold was placed.",
            ["litigationHoldOwner"] = "Who placed the hold.",
            ["litigationHoldDuration"] = "How long items are held. Unlimited means for as long as the hold is on.",
            ["retentionPolicy"] = "The policy deciding how long items are kept and when they are archived or deleted.",
            ["hasArchive"] = "Whether an archive mailbox exists. Read from the archive's identifier, which is reliable, rather than its status.",
            ["archiveStatus"] = "Exchange's own archive status. Known to read None for a working archive after a licence change, so it is not trusted on its own.",
            ["archiveState"] = "Where the archive lives: locally, or in the cloud for an on-premises mailbox.",
            ["archiveGuid"] = "The archive mailbox's identifier. Present means an archive exists.",
            ["owaEnabled"] = "Whether this mailbox can be opened in Outlook on the web.",
            ["activeSyncEnabled"] = "Whether phones and tablets can sync this mailbox over Exchange ActiveSync.",
            ["mapiEnabled"] = "Whether the Outlook desktop app can connect to this mailbox.",
            ["ewsEnabled"] = "Whether applications can reach this mailbox through Exchange Web Services.",
            ["imapEnabled"] = "Whether IMAP clients can connect to this mailbox.",
            ["popEnabled"] = "Whether POP clients can connect to this mailbox.",
            ["protocolsError"] = "Why the protocol settings could not be read. The rest of this pane is unaffected.",
            ["totalItemSize"] = "How much space the mailbox uses.",
            ["itemCount"] = "How many items the mailbox holds.",
            ["totalDeletedItemSize"] = "Space used by deleted items still inside the recovery window.",
            ["deletedItemCount"] = "How many recoverable deleted items there are.",
            ["lastLogonTime"] = "When the mailbox was last opened. Service processes count as logons, so this is not proof a person signed in.",

            // --- Entra user -----------------------------------------------------------------------------
            ["accountEnabled"] = "Whether this object may authenticate. Disabling a user does not remove their licences or mail; disabling a device stops it being used to sign in.",
            ["givenName"] = "First name.",
            ["surname"] = "Last name.",
            ["jobTitle"] = "Job title, as shown in the address book.",
            ["department"] = "Department, as shown in the address book.",
            ["companyName"] = "Company name, as shown in the address book.",
            ["employeeId"] = "The organization's own identifier for this person.",
            ["employeeType"] = "The kind of worker this is, such as employee or contractor.",
            ["employeeHireDate"] = "The recorded hire date.",
            ["officeLocation"] = "Office or desk location.",
            ["streetAddress"] = "Street address.",
            ["city"] = "City.",
            ["state"] = "State or province.",
            ["postalCode"] = "Postal or ZIP code.",
            ["country"] = "Country or region.",
            ["mobilePhone"] = "Mobile number. Not the same as the number used for multi-factor authentication.",
            ["businessPhones"] = "Work numbers shown in the address book.",
            ["faxNumber"] = "Fax number.",
            ["usageLocation"] = "The country whose service availability applies. A licence cannot be assigned until this is set.",
            ["preferredLanguage"] = "The language Microsoft 365 uses for this person.",
            ["userType"] = "Member for your own people, Guest for someone invited from outside.",
            ["externalUserState"] = "For a guest, whether the invitation has been accepted.",
            ["creationType"] = "How the account came to exist, such as invited as a guest or created directly.",
            ["ageGroup"] = "An age category that changes which consent rules apply.",
            ["otherMails"] = "Extra contact addresses in the directory. These do not receive mail.",
            ["imAddresses"] = "Instant messaging addresses in the directory.",
            ["manager"] = "This person's manager, as recorded in the directory.",
            ["createdDateTime"] = "When the account was created.",
            ["lastPasswordChangeDateTime"] = "When the password was last changed.",
            ["approximateLastSignInDateTime"] = "Roughly when the account last signed in. Approximate by design.",
            ["proxyAddresses"] = "Every address the directory holds for this object, including the primary.",

            // --- Entra group ----------------------------------------------------------------------------
            ["groupTypes"] = "The raw Entra classification. Unified means a Microsoft 365 group.",
            ["mailEnabled"] = "Whether the group can receive mail.",
            ["securityEnabled"] = "Whether the group can be used to grant permissions.",
            ["visibility"] = "Who can see the group and its content, and who may join unaided: Public (anyone), Private (an owner must approve), HiddenMembership (non-members cannot see the members either).",
            ["classification"] = "An organization-defined label, where one has been set up.",
            ["membershipRule"] = "The query deciding who belongs, for a dynamic group. Members cannot be added by hand.",
            ["membershipRuleProcessingState"] = "Whether the dynamic membership rule is currently being applied.",
            ["isAssignableToRole"] = "Whether Entra roles can be assigned to this group. Fixed when the group is created.",
            ["expirationDateTime"] = "When the group expires under an expiration policy, if one applies.",
            ["renewedDateTime"] = "When the group was last renewed against its expiration policy.",
            ["teams"] = "Whether the group is backing a team in Microsoft Teams.",

            // --- Entra device ---------------------------------------------------------------------------
            ["deviceId"] = "The device's own identifier, distinct from its directory object identifier.",
            ["operatingSystem"] = "The operating system the device reported.",
            ["operatingSystemVersion"] = "The operating system version the device reported.",
            ["manufacturer"] = "Device manufacturer.",
            ["model"] = "Device model.",
            ["trustType"] = "How the device is joined: to Entra, to your domain, or registered personally.",
            ["deviceOwnership"] = "Whether the device belongs to the company or to the person using it.",
            ["enrollmentType"] = "How the device was enrolled into management.",
            ["managementType"] = "What manages the device, such as Intune.",
            ["isCompliant"] = "Whether the device currently meets the compliance policies applied to it.",
            ["isManaged"] = "Whether the device is under management at all.",
            ["isRooted"] = "Whether the device is jailbroken or rooted, which compliance policies usually reject.",
            ["registrationDateTime"] = "When the device was registered in the directory.",

            // --- on-premises ----------------------------------------------------------------------------
            ["onPremisesSyncEnabled"] = "Whether this object comes from on-premises Active Directory. If it does, its attributes are mastered there.",
            ["onPremisesLastSyncDateTime"] = "When the last successful directory sync ran for this object.",
            ["onPremisesSamAccountName"] = "The pre-Windows 2000 account name from Active Directory.",
            ["onPremisesUserPrincipalName"] = "The sign-in name as it exists on-premises, which can differ from the cloud one.",
            ["onPremisesDistinguishedName"] = "The object's full path in Active Directory.",
            ["onPremisesDomainName"] = "The Active Directory domain this object comes from.",
            ["onPremisesNetBiosName"] = "The short NetBIOS name of that domain.",
            ["onPremisesImmutableId"] = "The value tying the cloud object to its on-premises one. Changing it breaks the link.",
            ["onPremisesSecurityIdentifier"] = "The Active Directory SID this object was created with.",
            ["origin"] = "Where this object was created.",
        };
}
