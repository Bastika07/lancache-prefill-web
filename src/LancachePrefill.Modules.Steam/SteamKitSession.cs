// Connection, login and license handling ported from SteamPrefill's Steam3Session and LicenseManager
// (tpill90/steam-lancache-prefill, MIT). See NOTICE.
using System.Text.Json;
using LancachePrefill.Core.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;

namespace LancachePrefill.Modules.Steam;

/// <summary>
/// The one Steam connection of the app: login (credentials + Steam Guard, QR, or stored refresh token),
/// account licenses, and the PICS / manifest / CDN calls behind <see cref="ISteamSession"/>.
/// Runs SteamKit2's callback pump on a background thread while the app runs.
/// </summary>
public sealed partial class SteamKitSession : ISteamSession, ISteamLibrary, ISteamLogin, IHostedService, IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly IAccountTokenStore _tokens;
    private readonly ILogger<SteamKitSession> _logger;
    private readonly SteamClient _client;
    private readonly CallbackManager _callbacks;
    private readonly SteamUser _user;
    private readonly SteamApps _apps;
    private readonly SteamContent _content;
    private readonly Client _cdn;
    private readonly SemaphoreSlim _signInLock = new(1, 1);
    private readonly Lock _stateLock = new();

    private CancellationTokenSource? _pump;
    private CancellationTokenSource? _loginCancellation;
    private TaskCompletionSource? _connected;
    private TaskCompletionSource<SteamUser.LoggedOnCallback>? _loggedOn;
    private TaskCompletionSource<SteamApps.LicenseListCallback>? _licenses;
    private TaskCompletionSource<string>? _pendingCode;
    private Licenses _owned = Licenses.None;
    private Dictionary<string, Server> _cdnServers = [];
    private SteamLoginState _state = new SteamLoginState.SignedOut();

    public SteamKitSession(IAccountTokenStore tokens, ILogger<SteamKitSession> logger)
    {
        _tokens = tokens;
        _logger = logger;
        _client = new SteamClient(SteamConfiguration.Create(c => c.WithConnectionTimeout(TimeSpan.FromSeconds(10))));
        _callbacks = new CallbackManager(_client);
        _user = _client.GetHandler<SteamUser>()!;
        _apps = _client.GetHandler<SteamApps>()!;
        _content = _client.GetHandler<SteamContent>()!;
        _cdn = new Client(_client);
        Client.RequestTimeout = TimeSpan.FromSeconds(60);

        _callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => _connected?.TrySetResult());
        _callbacks.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _callbacks.Subscribe<SteamUser.LoggedOnCallback>(cb => _loggedOn?.TrySetResult(cb));
        _callbacks.Subscribe<SteamApps.LicenseListCallback>(cb => _licenses?.TrySetResult(cb));
    }

    // ───────────── ISteamLogin ─────────────

    public SteamLoginState State
    {
        get { lock (_stateLock) { return _state; } }
        private set
        {
            lock (_stateLock) { _state = value; }
            StateChanged?.Invoke();
        }
    }

    public event Action? StateChanged;

    public Task StartCredentialsLoginAsync(string username, string password, CancellationToken ct) =>
        RunInteractiveLoginAsync(async (auth, token) =>
        {
            var session = await auth.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
            {
                Username = username,
                Password = password,
                IsPersistentSession = true,
                Authenticator = new StateAuthenticator(this),
            });
            return await session.PollingWaitForResultAsync(token);
        }, ct);

    public Task StartQrLoginAsync(CancellationToken ct) =>
        RunInteractiveLoginAsync(async (auth, token) =>
        {
            var session = await auth.BeginAuthSessionViaQRAsync(new AuthSessionDetails { IsPersistentSession = true });
            State = new SteamLoginState.ShowingQrCode(session.ChallengeURL);
            session.ChallengeURLChanged = () => State = new SteamLoginState.ShowingQrCode(session.ChallengeURL);
            return await session.PollingWaitForResultAsync(token);
        }, ct);

    public Task SubmitCodeAsync(string code)
    {
        _pendingCode?.TrySetResult(code);
        State = new SteamLoginState.Connecting();
        return Task.CompletedTask;
    }

    public void Cancel()
    {
        _loginCancellation?.Cancel();
        _pendingCode?.TrySetCanceled();
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        await _tokens.RemoveAsync(SteamPrefillModule.ModuleId, ct);
        if (_client.IsConnected)
        {
            _user.LogOff();
            _client.Disconnect();
        }
        _owned = Licenses.None;
        State = new SteamLoginState.SignedOut();
    }

    /// <summary>Runs one interactive login; any failure ends in SignedOut with a readable reason.</summary>
    private async Task RunInteractiveLoginAsync(Func<SteamAuthentication, CancellationToken, Task<AuthPollResult>> authenticate, CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loginCancellation = cancellation;
        State = new SteamLoginState.Connecting();
        try
        {
            await ConnectAsync(cancellation.Token);
            var result = await authenticate(_client.Authentication, cancellation.Token);
            var token = new StoredToken(result.AccountName, result.RefreshToken);
            await _tokens.SetAsync(SteamPrefillModule.ModuleId, JsonSerializer.Serialize(token), CancellationToken.None);
            await LogOnAsync(token, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            State = new SteamLoginState.SignedOut();
        }
        catch (AuthenticationException ex)
        {
            State = new SteamLoginState.SignedOut(ex.Result switch
            {
                EResult.InvalidPassword => "Invalid username or password.",
                EResult.RateLimitExceeded or EResult.AccountLoginDeniedThrottle => "Too many attempts. Wait a while and try again.",
                EResult.Expired or EResult.FileNotFound => "The login request expired. Please start again.",
                _ => $"Steam rejected the login ({ex.Result}).",
            });
        }
        catch (Exception ex)
        {
            LogLoginFailed(_logger, ex);
            State = new SteamLoginState.SignedOut($"Login failed: {ex.Message}");
        }
        finally
        {
            _loginCancellation = null;
        }
    }

    /// <summary>Bridges SteamKit2's authenticator callbacks to <see cref="State"/> and the dialog.</summary>
    private sealed class StateAuthenticator(SteamKitSession session) : IAuthenticator
    {
        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) =>
            session.AwaitCodeAsync(new SteamLoginState.AwaitingCode(SteamGuardCodeKind.Authenticator, null, previousCodeWasIncorrect));

        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) =>
            session.AwaitCodeAsync(new SteamLoginState.AwaitingCode(SteamGuardCodeKind.Email, email, previousCodeWasIncorrect));

        public Task<bool> AcceptDeviceConfirmationAsync()
        {
            session.State = new SteamLoginState.AwaitingDeviceConfirmation();
            return Task.FromResult(true); // wait for approval in the app; Steam falls back to a code if declined
        }
    }

    private Task<string> AwaitCodeAsync(SteamLoginState.AwaitingCode state)
    {
        _pendingCode = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        State = state;
        return _pendingCode.Task;
    }

    // ───────────── Connection + logon ─────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _pump = new CancellationTokenSource();
        var pump = _pump.Token;
        _ = Task.Factory.StartNew(() =>
        {
            while (!pump.IsCancellationRequested)
            {
                _callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
            }
        }, pump, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        // Sign back in with the stored token in the background; don't hold up app start.
        _ = Task.Run(async () =>
        {
            try
            {
                if (await _tokens.GetAsync(SteamPrefillModule.ModuleId, CancellationToken.None) is null)
                {
                    return; // never signed in: nothing to resume
                }
                await EnsureSignedInAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                LogResumeFailed(_logger, ex);
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_client.IsConnected)
        {
            _client.Disconnect();
        }
        await StopPumpAsync();
    }

    /// <summary>Stops the callback pump once, whichever of StopAsync/Dispose comes first.</summary>
    private async Task StopPumpAsync()
    {
        if (Interlocked.Exchange(ref _pump, null) is { } pump)
        {
            await pump.CancelAsync();
            pump.Dispose();
        }
    }

    /// <summary>Signs in with the stored token if not signed in yet. Throws if there is no usable token.</summary>
    private async Task EnsureSignedInAsync(CancellationToken ct)
    {
        if (State is SteamLoginState.SignedIn && _client.IsConnected)
        {
            return;
        }
        await _signInLock.WaitAsync(ct);
        try
        {
            if (State is SteamLoginState.SignedIn && _client.IsConnected)
            {
                return;
            }
            var json = await _tokens.GetAsync(SteamPrefillModule.ModuleId, ct)
                ?? throw new InvalidOperationException("Not signed in to Steam. Sign in on the Steam page first.");
            var token = JsonSerializer.Deserialize<StoredToken>(json)!;
            State = new SteamLoginState.Connecting();
            await ConnectAsync(ct);
            await LogOnAsync(token, ct);
        }
        finally
        {
            _signInLock.Release();
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        if (_client.IsConnected)
        {
            return;
        }
        _connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.Connect();
        await _connected.Task.WaitAsync(ConnectTimeout, ct);
    }

    private async Task LogOnAsync(StoredToken token, CancellationToken ct)
    {
        _loggedOn = new TaskCompletionSource<SteamUser.LoggedOnCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
        _licenses = new TaskCompletionSource<SteamApps.LicenseListCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
        _user.LogOn(new SteamUser.LogOnDetails
        {
            Username = token.AccountName,
            AccessToken = token.RefreshToken,
            ShouldRememberPassword = true,
        });
        var loggedOn = await _loggedOn.Task.WaitAsync(ConnectTimeout, ct);
        if (loggedOn.Result != EResult.OK)
        {
            if (loggedOn.Result is EResult.AccessDenied or EResult.InvalidPassword or EResult.Expired)
            {
                // Token revoked (e.g. password changed): forget it, a fresh login is needed.
                await _tokens.RemoveAsync(SteamPrefillModule.ModuleId, CancellationToken.None);
            }
            State = new SteamLoginState.SignedOut($"Steam refused the stored login ({loggedOn.Result}). Please sign in again.");
            throw new InvalidOperationException($"Steam logon failed: {loggedOn.Result}");
        }

        var licenses = await _licenses.Task.WaitAsync(ConnectTimeout, ct);
        _owned = await LoadLicensesAsync(licenses, ct);
        LogSignedIn(_logger, token.AccountName, _owned.Apps.Count, _owned.DepotIds.Count);
        State = new SteamLoginState.SignedIn(token.AccountName);
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        _connected?.TrySetException(new InvalidOperationException("Could not connect to Steam."));
        _loggedOn?.TrySetException(new InvalidOperationException("Disconnected from Steam while logging on."));
        if (State is SteamLoginState.SignedIn signedIn && !callback.UserInitiated)
        {
            // Reconnect lazily: the next ISteamSession call signs in again with the stored token.
            LogDisconnected(_logger, signedIn.AccountName);
        }
    }

    /// <summary>Owned apps and depots from the account's non-expired licenses (port of LicenseManager.LoadPackageInfo).</summary>
    private async Task<Licenses> LoadLicensesAsync(SteamApps.LicenseListCallback list, CancellationToken ct)
    {
        if (list.Result != EResult.OK)
        {
            throw new InvalidOperationException($"Could not load Steam licenses: {list.Result}");
        }
        var active = list.LicenseList.Where(l => !l.LicenseFlags.HasFlag(ELicenseFlags.Expired)).ToList();
        var requests = active.Select(l => new SteamApps.PICSRequest(l.PackageID, l.AccessToken)).ToList();
        var licensedAt = active.GroupBy(l => l.PackageID)
            .ToDictionary(g => g.Key, g => new DateTimeOffset(DateTime.SpecifyKind(g.Min(l => l.TimeCreated), DateTimeKind.Utc)));
        var result = await _apps.PICSGetProductInfo([], requests).ToTask().WaitAsync(TimeSpan.FromMinutes(2), ct);
        var apps = new Dictionary<uint, DateTimeOffset>();
        var depots = new HashSet<uint>();
        var now = DateTimeOffset.UtcNow;
        foreach (var package in result.Results!.SelectMany(r => r.Packages.Values))
        {
            var kv = package.KeyValues;
            var expiry = kv["extended"]["expirytime"].AsUnsignedLongNullable();
            if (kv["extended"]["freeweekend"].AsBoolean() && expiry is not null && DateTimeOffset.FromUnixTimeSeconds((long)expiry) < now)
            {
                continue; // expired free weekend
            }
            var packageTime = licensedAt.GetValueOrDefault(package.ID, DateTimeOffset.UnixEpoch);
            foreach (var appId in kv["appids"].Children.Select(c => uint.Parse(c.Value!, System.Globalization.CultureInfo.InvariantCulture)))
            {
                // Earliest license wins: an old game re-granted by a new bundle is not "recently purchased".
                apps[appId] = apps.TryGetValue(appId, out var known) && known < packageTime ? known : packageTime;
            }
            depots.UnionWith(kv["depotids"].Children.Select(c => uint.Parse(c.Value!, System.Globalization.CultureInfo.InvariantCulture)));
        }
        return new Licenses(apps, depots);
    }

    // ───────────── ISteamLibrary ─────────────

    public async Task<IReadOnlyDictionary<uint, DateTimeOffset>> GetOwnedAppsAsync(CancellationToken ct)
    {
        await EnsureSignedInAsync(ct);
        return _owned.Apps;
    }

    /// <summary>Port of AppInfoHandler.GetRecentlyPlayedGamesAsync: owned games with playtime in the last two weeks.</summary>
    public async Task<IReadOnlyList<uint>> GetRecentlyPlayedAppIdsAsync(CancellationToken ct)
    {
        await EnsureSignedInAsync(ct);
        var player = _client.GetHandler<SteamUnifiedMessages>()!.CreateService<SteamKit2.Internal.Player>();
        var response = await player.GetOwnedGames(new SteamKit2.Internal.CPlayer_GetOwnedGames_Request
        {
            steamid = _client.SteamID!.ConvertToUInt64(),
            include_appinfo = false,
            include_free_sub = true,
            include_played_free_games = true,
            skip_unvetted_apps = false,
        }).ToTask().WaitAsync(TimeSpan.FromMinutes(1), ct);
        if (response.Result != EResult.OK)
        {
            throw new InvalidOperationException($"Steam did not return the played games: {response.Result}");
        }
        return [.. response.Body.games.Where(g => g.playtime_2weeks > 0).Select(g => (uint)g.appid)];
    }

    // ───────────── ISteamSession ─────────────

    public async Task<IReadOnlyDictionary<uint, KeyValue>> GetAppInfoAsync(IReadOnlyCollection<uint> appIds, CancellationToken ct)
    {
        await EnsureSignedInAsync(ct);
        var tokens = await _apps.PICSGetAccessTokens(appIds, []).ToTask().WaitAsync(TimeSpan.FromMinutes(1), ct);
        var requests = appIds
            .Select(id => new SteamApps.PICSRequest(id, tokens.AppTokens.GetValueOrDefault(id)))
            .ToList();
        var result = await _apps.PICSGetProductInfo(requests, []).ToTask().WaitAsync(TimeSpan.FromMinutes(2), ct);
        return result.Results!
            .SelectMany(r => r.Apps.Values)
            .ToDictionary(app => app.ID, app => app.KeyValues);
    }

    public bool OwnsApp(uint appId) => _owned.Apps.ContainsKey(appId);

    public bool OwnsDepot(uint depotId) => _owned.DepotIds.Contains(depotId) || _owned.Apps.ContainsKey(depotId);

    public async Task<ulong> GetManifestRequestCodeAsync(uint depotId, uint appId, ulong manifestId, CancellationToken ct)
    {
        await EnsureSignedInAsync(ct);
        return await _content.GetManifestRequestCode(depotId, appId, manifestId, "public").WaitAsync(ct);
    }

    public async Task<SteamManifest> DownloadManifestAsync(uint depotId, ulong manifestId, ulong requestCode, CdnServer server, CancellationToken ct)
    {
        if (!_cdnServers.TryGetValue(server.Host, out var cdnServer))
        {
            throw new InvalidOperationException($"Unknown CDN server {server.Host}; call GetCdnServersAsync first.");
        }
        var manifest = await _cdn.DownloadManifestAsync(depotId, manifestId, requestCode, cdnServer).WaitAsync(ct);
        return new SteamManifest(depotId, manifestId, [.. (manifest.Files ?? []).Select(f => new SteamFile(
            [.. f.Chunks.Select(c => new SteamChunk(Convert.ToHexStringLower(c.ChunkID!), c.CompressedLength))]))]);
    }

    public async Task<IReadOnlyList<CdnServer>> GetCdnServersAsync(CancellationToken ct)
    {
        await EnsureSignedInAsync(ct);
        var servers = (await _content.GetServersForSteamPipe().WaitAsync(TimeSpan.FromSeconds(15), ct))
            .Where(s => (s.Type == "SteamCache" || s.Type == "CDN") && s.AllowedAppIds.Length == 0)
            .DistinctBy(s => s.Host)
            .ToList();
        _cdnServers = servers.ToDictionary(s => s.Host!);
        return [.. servers.Select(s => new CdnServer(s.Host!, s.Load))];
    }

    public void Dispose()
    {
        StopPumpAsync().GetAwaiter().GetResult(); // CancelAsync completes synchronously here
        _cdn.Dispose();
        _signInLock.Dispose();
    }

    private sealed record StoredToken(string AccountName, string RefreshToken);

    private sealed record Licenses(IReadOnlyDictionary<uint, DateTimeOffset> Apps, IReadOnlySet<uint> DepotIds)
    {
        public static Licenses None { get; } = new(new Dictionary<uint, DateTimeOffset>(), new HashSet<uint>());
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Signed in to Steam as {Account}: {Apps} owned apps, {Depots} depots")]
    private static partial void LogSignedIn(ILogger logger, string account, int apps, int depots);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Steam login failed")]
    private static partial void LogLoginFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not resume the stored Steam login")]
    private static partial void LogResumeFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Disconnected from Steam ({Account}); will reconnect on next use")]
    private static partial void LogDisconnected(ILogger logger, string account);
}
