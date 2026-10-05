namespace UnifiedDirectoryManager.Services;

/// <summary>What a silent check of the Entra ID sign-in found.</summary>
public enum CloudSignInState
{
    /// <summary>No tenant or client id has been entered, so cloud features were never set up here.</summary>
    NotConfigured,

    /// <summary>Configured, but nobody has signed in (or someone signed out).</summary>
    NotSignedIn,

    /// <summary>
    /// A sign-in was saved but no longer yields a token: the refresh token aged out, consent was revoked,
    /// the account was disabled, or a Conditional Access policy now blocks it.
    /// </summary>
    Expired,

    /// <summary>A token came back without prompting.</summary>
    SignedIn,

    /// <summary>
    /// The check itself could not be completed — the network was down, or the token endpoint was
    /// unreachable. NOT the same as being signed out, and never reported as if it were.
    /// </summary>
    CheckFailed,
}

/// <summary>The result of one silent sign-in check.</summary>
/// <param name="Account">Who the saved sign-in belongs to, when there is one.</param>
/// <param name="Message">The underlying error, for <see cref="CloudSignInState.CheckFailed"/>.</param>
public sealed record CloudSignInCheck(CloudSignInState State, string? Account, string? Message)
{
    public static CloudSignInCheck NotConfigured { get; } = new(CloudSignInState.NotConfigured, null, null);
}

/// <summary>
/// Turns a sign-in check into the sentence shown in the warning bar.
/// </summary>
/// <remarks>
/// Kept apart from both the Graph client and the view model so that the wording is testable without a
/// tenant: the states it has to tell apart are exactly the ones that are easy to conflate, and conflating
/// them is how an operator ends up re-authenticating to fix a network outage.
/// </remarks>
public static class CloudSignIn
{
    private const string Consequence = "Cloud (Entra ID) and Exchange Online features are unavailable until you sign in.";

    /// <summary>
    /// The warning-bar text, or empty when there is nothing worth saying.
    /// </summary>
    /// <remarks>
    /// Empty for <see cref="CloudSignInState.SignedIn"/>, obviously, and empty for
    /// <see cref="CloudSignInState.NotConfigured"/> on purpose: an operator doing only on-prem work has
    /// not got a problem, and a warning they cannot act on and do not need is one they learn to ignore —
    /// which costs the warnings that matter.
    /// </remarks>
    public static string Warning(CloudSignInCheck? check)
    {
        if (check is null) return string.Empty;

        // A switch EXPRESSION with no default arm, on purpose. Under -warnaserror a non-exhaustive one
        // is a build error, so a state added to CloudSignInState cannot reach an operator without
        // somebody deciding what it should say. The default arm this replaced made the NotConfigured
        // case inert -- mutation testing deleted that case and nothing changed, because both returned
        // empty -- and would have quietly swallowed any new state the same way.
        return check.State switch
        {
            // Nothing is wrong.
            CloudSignInState.SignedIn => string.Empty,

            // Also nothing is wrong: an operator doing only on-prem work has not got a problem, and a
            // warning they cannot act on and do not need is one they learn to ignore -- which costs the
            // warnings that matter.
            CloudSignInState.NotConfigured => string.Empty,

            CloudSignInState.NotSignedIn => "Not signed in to Entra ID. " + Consequence,

            CloudSignInState.Expired =>
                $"The saved Entra ID sign-in{For(check.Account)} has expired. " + Consequence,

            // Deliberately does NOT say "signed out". The app could not tell, and telling someone to
            // sign in again when the real problem is a dropped network sends them round a loop that
            // cannot fix it.
            CloudSignInState.CheckFailed =>
                "Could not check the Entra ID sign-in, so cloud features may not work." + Because(check.Message),
        };

        static string For(string? account) =>
            string.IsNullOrWhiteSpace(account) ? string.Empty : $" for {account!.Trim()}";

        static string Because(string? message) =>
            string.IsNullOrWhiteSpace(message) ? string.Empty : " " + message!.Trim();
    }
    /// <summary>Whether the bar should offer a Sign in… button, as opposed to only reporting.</summary>
    /// <remarks>
    /// Not offered for <see cref="CloudSignInState.CheckFailed"/>: signing in cannot fix a network that is
    /// down, and a button that does nothing useful is worse than no button.
    /// </remarks>
    public static bool CanSignIn(CloudSignInCheck? check) =>
        check?.State is CloudSignInState.NotSignedIn or CloudSignInState.Expired;
}
