namespace LancachePrefill.Modules.Steam;

/// <summary>
/// The interactive Steam login (spec → Logins), as a state machine the UI renders.
/// Implemented by <see cref="SteamKitSession"/>; the UI is tested against a fake.
/// </summary>
public interface ISteamLogin
{
    SteamLoginState State { get; }

    /// <summary>Raised on any state change, possibly from a background thread.</summary>
    event Action? StateChanged;

    /// <summary>Username + password; may continue with <see cref="SteamLoginState.AwaitingCode"/> or device confirmation.</summary>
    Task StartCredentialsLoginAsync(string username, string password, CancellationToken ct);

    /// <summary>Answers <see cref="SteamLoginState.AwaitingCode"/>.</summary>
    Task SubmitCodeAsync(string code);

    /// <summary>Starts a QR login: shows <see cref="SteamLoginState.ShowingQrCode"/> until scanned in the Steam app.</summary>
    Task StartQrLoginAsync(CancellationToken ct);

    /// <summary>Abandons a login in progress.</summary>
    void Cancel();

    /// <summary>Signs out and forgets the stored token.</summary>
    Task SignOutAsync(CancellationToken ct);
}

public enum SteamGuardCodeKind
{
    /// <summary>Code from the Steam Mobile authenticator.</summary>
    Authenticator,

    /// <summary>Code Steam sent by e-mail.</summary>
    Email,
}

public abstract record SteamLoginState
{
    private SteamLoginState() { }

    /// <param name="Error">Why the last attempt failed, if it did.</param>
    public sealed record SignedOut(string? Error = null) : SteamLoginState;

    public sealed record Connecting : SteamLoginState;

    /// <param name="EmailHint">Masked address Steam sent the code to (e-mail codes only).</param>
    /// <param name="PreviousCodeWasWrong">The code just submitted was rejected.</param>
    public sealed record AwaitingCode(SteamGuardCodeKind Kind, string? EmailHint = null, bool PreviousCodeWasWrong = false) : SteamLoginState;

    /// <summary>Waiting for the user to approve the login in the Steam Mobile app.</summary>
    public sealed record AwaitingDeviceConfirmation : SteamLoginState;

    /// <param name="ChallengeUrl">Encode as QR code; Steam rotates it every ~30 s.</param>
    public sealed record ShowingQrCode(string ChallengeUrl) : SteamLoginState;

    public sealed record SignedIn(string AccountName) : SteamLoginState;
}
