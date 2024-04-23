using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Ydb.Sdk.Services.Auth;

namespace Ydb.Sdk.Auth;

public class StaticCredentialsProvider : ICredentialsProvider, IUseDriverConfig
{
    private readonly ILogger _logger;
    private readonly string _user;
    private readonly string? _password;

    private Driver? _driver;

    public int MaxRetries = 5;

    private readonly object _lock = new();

    private volatile TokenData? _token;
    private volatile Task? _refreshTask;

    public float RefreshRatio = .1f;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="user">User of the database</param>
    /// <param name="password">Password of the user. If user has no password use null </param>
    /// <param name="loggerFactory"></param>
    public StaticCredentialsProvider(string user, string? password, ILoggerFactory? loggerFactory = null)
    {
        _user = user;
        _password = password;
        loggerFactory ??= NullLoggerFactory.Instance;
        _logger = loggerFactory.CreateLogger<StaticCredentialsProvider>();
    }

    private async Task Initialize()
    {
        _token = await ReceiveToken();
    }

    public string GetAuthInfo()
    {
        var token = _token;

        if (token is null)
        {
            lock (_lock)
            {
                if (_token is not null) return _token.Token;
                _logger.LogWarning(
                    "Blocking for initial token acquirement, please use explicit Initialize async method.");

                Initialize().Wait();

                return _token!.Token;
            }
        }

        if (token.IsExpired())
        {
            lock (_lock)
            {
                if (!_token!.IsExpired()) return _token.Token;
                _logger.LogWarning("Blocking on expired token.");

                _token = ReceiveToken().Result;

                return _token.Token;
            }
        }

        if (!token.IsRefreshNeeded() || _refreshTask is not null) return _token!.Token;
        lock (_lock)
        {
            if (!_token!.IsRefreshNeeded() || _refreshTask is not null) return _token!.Token;
            _logger.LogInformation("Refreshing  token.");

            _refreshTask = Task.Run(RefreshToken);
        }

        return _token!.Token;
    }

    private async Task RefreshToken()
    {
        var token = await ReceiveToken();

        lock (_lock)
        {
            _token = token;
            _refreshTask = null;
        }
    }

    private async Task<TokenData> ReceiveToken()
    {
        var retryAttempt = 0;
        while (true)
        {
            try
            {
                _logger.LogTrace($"Attempting to receive token, attempt: {retryAttempt}");

                var token = await FetchToken();

                _logger.LogInformation($"Received token, expires at: {token.ExpiresAt}");

                return token;
            }
            catch (InvalidCredentialsException e)
            {
                _logger.LogWarning($"Invalid credentials, {e}");
                throw;
            }
            catch (Exception e)
            {
                _logger.LogDebug($"Failed to fetch token, {e}");

                if (retryAttempt >= MaxRetries)
                {
                    _logger.LogWarning($"Can't fetch token, {e}");
                    throw;
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));
                _logger.LogInformation($"Failed to fetch token, attempt {retryAttempt}");
                ++retryAttempt;
            }
        }
    }

    private async Task<TokenData> FetchToken()
    {
        if (_driver is null)
        {
            _logger.LogError("Driver in for static auth not provided");
            throw new NullReferenceException();
        }

        var client = new AuthClient(_driver);
        var loginResponse = await client.Login(_user, _password);
        if (loginResponse.Status.StatusCode == StatusCode.Unauthorized)
        {
            throw new InvalidCredentialsException(Issue.IssuesToString(loginResponse.Status.Issues));
        }

        loginResponse.Status.EnsureSuccess();
        var token = loginResponse.Result.Token;
        var jwt = new JwtSecurityToken(token);
        return new TokenData(token, jwt.ValidTo, RefreshRatio);
    }

    public async Task ProvideConfig(DriverConfig driverConfig)
    {
        _driver = await Driver.CreateInitialized(
            new DriverConfig(
                driverConfig.Endpoint,
                driverConfig.Database,
                new AnonymousProvider(),
                driverConfig.DefaultTransportTimeout,
                driverConfig.DefaultStreamingTransportTimeout,
                driverConfig.CustomServerCertificate));

        await Initialize();
    }

    private class TokenData
    {
        public TokenData(string token, DateTime expiresAt, float refreshInterval)
        {
            var now = DateTime.UtcNow;

            Token = token;
            ExpiresAt = expiresAt;

            if (expiresAt <= now)
            {
                RefreshAt = expiresAt;
            }
            else
            {
                RefreshAt = now + (expiresAt - now) * refreshInterval;

                if (RefreshAt < now)
                {
                    RefreshAt = expiresAt;
                }
            }
        }

        public string Token { get; }
        public DateTime ExpiresAt { get; }

        private DateTime RefreshAt { get; }

        public bool IsExpired()
        {
            return DateTime.UtcNow >= ExpiresAt;
        }

        public bool IsRefreshNeeded()
        {
            return DateTime.UtcNow >= RefreshAt;
        }
    }
}
