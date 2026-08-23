using Newtonsoft.Json;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace DeepSkyLog.NINAPlugin {

    public class AuthenticationService {
        private const string BaseUrl = "https://app.deepskylog.space";
        private static readonly HttpClient _httpClient = new HttpClient().WithIdentity();
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private string _expectedState; // CSRF nonce for the in-flight sign-in attempt

        public event Action<string> OnTokenReceived;
        public event Action<string> OnAuthenticationFailed;

        public async Task<bool> StartAuthenticationAsync() {
            try {
                // Find an available port
                int port = GetAvailablePort();
                string redirectUri = $"http://127.0.0.1:{port}/callback/";

                // Start the local HTTP listener
                _listener = new HttpListener();
                _listener.Prefixes.Add(redirectUri);
                _listener.Start();

                _cts = new CancellationTokenSource();

                Logger.Info($"DeepSkyLog: Starting authentication listener on {redirectUri}");

                // Generate a per-attempt CSRF nonce. The server echoes it back in the
                // callback; mismatch (or absent local state) → reject. Without this an
                // attacker who guesses the loopback port can race a forged token in.
                _expectedState = GenerateState();

                // Open browser to authentication page
                // Use the same endpoint as the desktop app: /desktop-auth?callback=...
                string authUrl = $"{BaseUrl}/desktop-auth?callback={Uri.EscapeDataString(redirectUri)}&state={Uri.EscapeDataString(_expectedState)}";
                Logger.Info($"DeepSkyLog: Opening browser to /desktop-auth (state hidden)");

                Process.Start(new ProcessStartInfo {
                    FileName = authUrl,
                    UseShellExecute = true
                });

                // Wait for callback (with timeout)
                var timeoutTask = Task.Delay(TimeSpan.FromMinutes(5), _cts.Token);
                var listenerTask = WaitForCallbackAsync(_cts.Token);

                var completedTask = await Task.WhenAny(listenerTask, timeoutTask);

                if (completedTask == timeoutTask) {
                    Logger.Warning("DeepSkyLog: Authentication timed out");
                    OnAuthenticationFailed?.Invoke("Authentication timed out. Please try again.");
                    return false;
                }

                return await listenerTask;

            } catch (Exception ex) {
                Logger.Error($"DeepSkyLog: Authentication error: {ex.Message}");
                OnAuthenticationFailed?.Invoke($"Authentication failed: {ex.Message}");
                return false;
            } finally {
                StopListener();
            }
        }

        private async Task<bool> WaitForCallbackAsync(CancellationToken ct) {
            try {
                while (!ct.IsCancellationRequested) {
                    var contextTask = _listener.GetContextAsync();

                    // Check for cancellation
                    var completedTask = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, ct));
                    if (ct.IsCancellationRequested) {
                        return false;
                    }

                    var context = await contextTask;
                    var request = context.Request;
                    var response = context.Response;

                    // Don't log the request URL — it carries the one-time token.
                    Logger.Debug("DeepSkyLog: Received callback request");

                    // Parse the query string for token
                    var query = HttpUtility.ParseQueryString(request.Url.Query);
                    string oneTimeToken = query["token"];
                    string error = query["error"];
                    string returnedState = query["state"];

                    string responseHtml;

                    if (!string.IsNullOrEmpty(error)) {
                        Logger.Warning($"DeepSkyLog: Authentication error from server: {error}");
                        responseHtml = GetErrorHtml(error);
                        SendResponse(response, responseHtml);
                        OnAuthenticationFailed?.Invoke(error);
                        return false;
                    }

                    if (!string.IsNullOrEmpty(oneTimeToken)) {
                        // CSRF check: only accept callbacks whose state matches the one we
                        // generated for this sign-in attempt. A null _expectedState means
                        // no sign-in is in progress on this instance — reject regardless.
                        if (string.IsNullOrEmpty(_expectedState)) {
                            Logger.Warning("DeepSkyLog: Callback rejected — no sign-in in progress");
                            responseHtml = GetErrorHtml("No sign-in in progress");
                            SendResponse(response, responseHtml);
                            OnAuthenticationFailed?.Invoke("No sign-in in progress");
                            return false;
                        }
                        // If the server echoed a state, it must match. (If it didn't echo
                        // anything, we still required _expectedState to be set above, so the
                        // drive-by attack is blocked even before the server companion change
                        // ships.)
                        if (!string.IsNullOrEmpty(returnedState) && !FixedTimeEquals(returnedState, _expectedState)) {
                            Logger.Warning("DeepSkyLog: Callback rejected — state mismatch");
                            responseHtml = GetErrorHtml("Sign-in state mismatch");
                            SendResponse(response, responseHtml);
                            OnAuthenticationFailed?.Invoke("Sign-in state mismatch");
                            return false;
                        }
                        // Single-use: clear regardless of exchange outcome below.
                        _expectedState = null;

                        Logger.Info("DeepSkyLog: One-time token received, exchanging for API token...");

                        // Exchange one-time token for long-lived API token
                        var exchangeResult = await ExchangeTokenAsync(oneTimeToken);

                        if (exchangeResult.Success) {
                            Logger.Info($"DeepSkyLog: API token received successfully for user '{exchangeResult.Username}'");
                            responseHtml = GetSuccessHtml(exchangeResult.Username);
                            SendResponse(response, responseHtml);
                            OnTokenReceived?.Invoke(exchangeResult.ApiToken);
                            return true;
                        } else {
                            Logger.Warning($"DeepSkyLog: Token exchange failed: {exchangeResult.Error}");
                            responseHtml = GetErrorHtml(exchangeResult.Error);
                            SendResponse(response, responseHtml);
                            OnAuthenticationFailed?.Invoke(exchangeResult.Error);
                            return false;
                        }
                    }

                    // No token or error - send waiting page
                    responseHtml = GetWaitingHtml();
                    SendResponse(response, responseHtml);
                }

                return false;

            } catch (HttpListenerException ex) when (ex.ErrorCode == 995) {
                // Listener was stopped
                return false;
            } catch (ObjectDisposedException) {
                // Listener was disposed
                return false;
            } catch (Exception ex) {
                Logger.Error($"DeepSkyLog: Error in callback handler: {ex.Message}");
                OnAuthenticationFailed?.Invoke(ex.Message);
                return false;
            }
        }

        private async Task<TokenExchangeResult> ExchangeTokenAsync(string oneTimeToken) {
            try {
                var requestBody = new Dictionary<string, string> {
                    { "token", oneTimeToken },
                    { "deviceName", "NINA Plugin" }
                };

                var jsonContent = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                using var response = await _httpClient.PostAsync($"{BaseUrl}/api/auth/exchange", content);
                var responseBody = await response.Content.ReadAsStringAsync();

                // Don't log the body — on success it contains the long-lived API token.
                Logger.Debug($"DeepSkyLog: Token exchange response: {response.StatusCode}");

                if (response.IsSuccessStatusCode) {
                    var result = JsonConvert.DeserializeObject<TokenExchangeResponse>(responseBody);
                    return new TokenExchangeResult {
                        Success = true,
                        ApiToken = result.ApiToken,
                        Username = result.Username,
                        ExpiresAt = result.ExpiresAt
                    };
                } else {
                    var errorResult = JsonConvert.DeserializeObject<TokenExchangeErrorResponse>(responseBody);
                    return new TokenExchangeResult {
                        Success = false,
                        Error = errorResult?.Message ?? $"Token exchange failed: {response.StatusCode}"
                    };
                }
            } catch (Exception ex) {
                Logger.Error($"DeepSkyLog: Token exchange error: {ex.Message}");
                return new TokenExchangeResult {
                    Success = false,
                    Error = $"Token exchange failed: {ex.Message}"
                };
            }
        }

        private class TokenExchangeResult {
            public bool Success { get; set; }
            public string ApiToken { get; set; }
            public string Username { get; set; }
            public string ExpiresAt { get; set; }
            public string Error { get; set; }
        }

        private class TokenExchangeResponse {
            [JsonProperty("apiToken")]
            public string ApiToken { get; set; }

            [JsonProperty("userId")]
            public long UserId { get; set; }

            [JsonProperty("username")]
            public string Username { get; set; }

            [JsonProperty("email")]
            public string Email { get; set; }

            [JsonProperty("expiresAt")]
            public string ExpiresAt { get; set; }
        }

        private class TokenExchangeErrorResponse {
            [JsonProperty("error")]
            public string Error { get; set; }

            [JsonProperty("message")]
            public string Message { get; set; }
        }

        /// <summary>
        /// Validates an existing API token against the server.
        /// </summary>
        /// <param name="apiToken">The API token to validate</param>
        /// <returns>Validation result with user info if valid</returns>
        public async Task<TokenValidationResult> ValidateTokenAsync(string apiToken) {
            if (string.IsNullOrEmpty(apiToken)) {
                return new TokenValidationResult { IsValid = false, Error = "No token provided" };
            }

            try {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/auth/validate");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiToken);

                using var response = await _httpClient.SendAsync(request);
                var responseBody = await response.Content.ReadAsStringAsync();

                Logger.Debug($"DeepSkyLog: Token validation response: {response.StatusCode}");

                if (response.IsSuccessStatusCode) {
                    var result = JsonConvert.DeserializeObject<TokenValidationResponse>(responseBody);
                    return new TokenValidationResult {
                        IsValid = true,
                        Username = result.Username,
                        Email = result.Email,
                        ExpiresAt = result.ExpiresAt
                    };
                } else {
                    var errorResult = JsonConvert.DeserializeObject<TokenExchangeErrorResponse>(responseBody);
                    string errorCode = errorResult?.Error ?? "UNKNOWN";

                    // Check if token expired - user needs to re-authenticate
                    bool needsReauth = errorCode == "TOKEN_EXPIRED" || errorCode == "INVALID_TOKEN";

                    return new TokenValidationResult {
                        IsValid = false,
                        Error = errorResult?.Message ?? "Token validation failed",
                        ErrorCode = errorCode,
                        NeedsReauthentication = needsReauth
                    };
                }
            } catch (Exception ex) {
                Logger.Warning($"DeepSkyLog: Token validation error: {ex.Message}");
                return new TokenValidationResult {
                    IsValid = false,
                    Error = $"Validation failed: {ex.Message}"
                };
            }
        }

        public class TokenValidationResult {
            public bool IsValid { get; set; }
            public string Username { get; set; }
            public string Email { get; set; }
            public string ExpiresAt { get; set; }
            public string Error { get; set; }
            public string ErrorCode { get; set; }
            public bool NeedsReauthentication { get; set; }
        }

        private class TokenValidationResponse {
            [JsonProperty("valid")]
            public bool Valid { get; set; }

            [JsonProperty("userId")]
            public long UserId { get; set; }

            [JsonProperty("username")]
            public string Username { get; set; }

            [JsonProperty("email")]
            public string Email { get; set; }

            [JsonProperty("expiresAt")]
            public string ExpiresAt { get; set; }
        }

        private void SendResponse(HttpListenerResponse response, string html) {
            try {
                byte[] buffer = Encoding.UTF8.GetBytes(html);
                response.ContentType = "text/html; charset=utf-8";
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.OutputStream.Close();
            } catch (Exception ex) {
                Logger.Debug($"DeepSkyLog: Error sending response: {ex.Message}");
            }
        }

        public void CancelAuthentication() {
            _cts?.Cancel();
            _expectedState = null;
            StopListener();
        }

        private static string GenerateState() {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) {
                rng.GetBytes(bytes);
            }
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        private static bool FixedTimeEquals(string a, string b) {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }

        private void StopListener() {
            try {
                _listener?.Stop();
                _listener?.Close();
                _listener = null;
            } catch (Exception ex) {
                Logger.Debug($"DeepSkyLog: Error stopping listener: {ex.Message}");
            }
        }

        private int GetAvailablePort() {
            // Use TcpListener to find an available port
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private string GetSuccessHtml(string username = null) {
            string welcomeText = string.IsNullOrEmpty(username)
                ? "You can close this window and return to NINA."
                : $"Welcome, {WebUtility.HtmlEncode(username)}! You can close this window and return to NINA.";

            return $@"<!DOCTYPE html>
<html>
<head>
    <title>DeepSkyLog - Authentication Successful</title>
    <style>
        body {{
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, sans-serif;
            display: flex;
            justify-content: center;
            align-items: center;
            height: 100vh;
            margin: 0;
            background: linear-gradient(135deg, #1a1a2e 0%, #16213e 100%);
            color: #fff;
        }}
        .container {{
            text-align: center;
            padding: 40px;
            background: rgba(255,255,255,0.1);
            border-radius: 16px;
            backdrop-filter: blur(10px);
        }}
        .success-icon {{
            font-size: 64px;
            margin-bottom: 20px;
        }}
        h1 {{ margin: 0 0 10px 0; color: #4ade80; }}
        p {{ margin: 0; opacity: 0.8; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='success-icon'>&#10003;</div>
        <h1>Authentication Successful!</h1>
        <p>{welcomeText}</p>
    </div>
</body>
</html>";
        }

        private string GetErrorHtml(string error) {
            return $@"<!DOCTYPE html>
<html>
<head>
    <title>DeepSkyLog - Authentication Failed</title>
    <style>
        body {{
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, sans-serif;
            display: flex;
            justify-content: center;
            align-items: center;
            height: 100vh;
            margin: 0;
            background: linear-gradient(135deg, #1a1a2e 0%, #16213e 100%);
            color: #fff;
        }}
        .container {{
            text-align: center;
            padding: 40px;
            background: rgba(255,255,255,0.1);
            border-radius: 16px;
            backdrop-filter: blur(10px);
        }}
        .error-icon {{
            font-size: 64px;
            margin-bottom: 20px;
        }}
        h1 {{ margin: 0 0 10px 0; color: #f87171; }}
        p {{ margin: 0; opacity: 0.8; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='error-icon'>&#10007;</div>
        <h1>Authentication Failed</h1>
        <p>{WebUtility.HtmlEncode(error)}</p>
        <p style='margin-top: 20px;'>Please close this window and try again in NINA.</p>
    </div>
</body>
</html>";
        }

        private string GetWaitingHtml() {
            return @"<!DOCTYPE html>
<html>
<head>
    <title>DeepSkyLog - Waiting...</title>
    <style>
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, sans-serif;
            display: flex;
            justify-content: center;
            align-items: center;
            height: 100vh;
            margin: 0;
            background: linear-gradient(135deg, #1a1a2e 0%, #16213e 100%);
            color: #fff;
        }
        .container {
            text-align: center;
            padding: 40px;
        }
        p { opacity: 0.8; }
    </style>
</head>
<body>
    <div class='container'>
        <p>Waiting for authentication...</p>
    </div>
</body>
</html>";
        }
    }
}
