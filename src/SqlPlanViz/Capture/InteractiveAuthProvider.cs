using Microsoft.Data.SqlClient;
using Microsoft.Identity.Client;

namespace SqlPlanViz.Capture;

/// <summary>
/// Custom <see cref="SqlAuthenticationProvider"/> for
/// <see cref="SqlAuthenticationMethod.ActiveDirectoryInteractive"/> that drives MSAL directly so
/// the Microsoft Entra MFA popup can be parented to the app window.
///
/// <para>
/// Microsoft.Data.SqlClient's bundled <c>ActiveDirectoryAuthenticationProvider</c> exposes no
/// parent-window hook at any version (verified by reflection over 6.1.2 and 7.0.1), so its popup
/// opens as an orphan top-level window. Calling MSAL ourselves lets us pass
/// <c>.WithParentActivityOrWindow(() =&gt; hwnd)</c>. The "no hand-rolled MSAL" ground rule was
/// explicitly lifted for this (see docs/interactive-connect-plan.md).
/// </para>
/// </summary>
public sealed class InteractiveAuthProvider : SqlAuthenticationProvider
{
    // The public client id Microsoft.Data.SqlClient itself registers for interactive auth.
    // SqlAuthenticationParameters carries no ClientId in 6.1.2, so this is always used.
    private const string SqlClientAppId = "2fd908ad-0664-4344-b9be-cd3e8b574c38";

    private readonly Func<IntPtr> _windowHandle;

    public InteractiveAuthProvider(Func<IntPtr> windowHandle) => _windowHandle = windowHandle;

    /// <summary>Registers this provider for the interactive method, anchored to the given HWND.</summary>
    public static void Register(Func<IntPtr> windowHandle) =>
        SetProvider(
            SqlAuthenticationMethod.ActiveDirectoryInteractive,
            new InteractiveAuthProvider(windowHandle));

    public override bool IsSupported(SqlAuthenticationMethod authenticationMethod) =>
        authenticationMethod == SqlAuthenticationMethod.ActiveDirectoryInteractive;

    public override async Task<SqlAuthenticationToken> AcquireTokenAsync(
        SqlAuthenticationParameters parameters)
    {
        var app = PublicClientApplicationBuilder
            .Create(SqlClientAppId)
            .WithAuthority(parameters.Authority)
            .WithRedirectUri("http://localhost")
            .Build();

        var scopes = new[] { parameters.Resource.TrimEnd('/') + "/.default" };

        AuthenticationResult result;
        try
        {
            var accounts = await app.GetAccountsAsync().ConfigureAwait(false);
            result = await app
                .AcquireTokenSilent(scopes, accounts.FirstOrDefault())
                .ExecuteAsync()
                .ConfigureAwait(false);
        }
        catch (MsalUiRequiredException)
        {
            var interactive = app.AcquireTokenInteractive(scopes)
                .WithParentActivityOrWindow(_windowHandle);

            if (!string.IsNullOrWhiteSpace(parameters.UserId))
            {
                interactive = interactive.WithLoginHint(parameters.UserId);
            }

            result = await interactive.ExecuteAsync().ConfigureAwait(false);
        }

        return new SqlAuthenticationToken(result.AccessToken, result.ExpiresOn);
    }
}
