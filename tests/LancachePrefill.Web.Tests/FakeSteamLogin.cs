using LancachePrefill.Modules.Steam;

namespace LancachePrefill.Web.Tests;

/// <summary>Records what the dialog asks for; tests drive the state with <see cref="SetState"/>.</summary>
public sealed class FakeSteamLogin : ISteamLogin
{
    public SteamLoginState State { get; private set; } = new SteamLoginState.SignedOut();
    public event Action? StateChanged;

    public List<string> Calls { get; } = [];

    public void SetState(SteamLoginState state)
    {
        State = state;
        StateChanged?.Invoke();
    }

    public Task StartCredentialsLoginAsync(string username, string password, CancellationToken ct)
    {
        Calls.Add($"credentials:{username}:{password}");
        return Task.CompletedTask;
    }

    public Task SubmitCodeAsync(string code)
    {
        Calls.Add($"code:{code}");
        return Task.CompletedTask;
    }

    public Task StartQrLoginAsync(CancellationToken ct)
    {
        Calls.Add("qr");
        return Task.CompletedTask;
    }

    public void Cancel() => Calls.Add("cancel");

    public Task SignOutAsync(CancellationToken ct)
    {
        Calls.Add("signout");
        return Task.CompletedTask;
    }
}
