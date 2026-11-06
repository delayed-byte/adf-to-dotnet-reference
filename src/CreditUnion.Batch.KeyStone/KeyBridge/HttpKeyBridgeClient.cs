using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CreditUnion.Batch.Core.Configuration;
using CreditUnion.Batch.Core.Map;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CreditUnion.Batch.KeyStone.KeyBridge;

/// <summary>
/// The Web activity "Get GL Mapping From KeyBridge" as a typed <see cref="HttpClient"/> (ADR 0012). One call is one HTTP
/// request: there is no retry handler, and the factory registration removes the HTTP-client loggers, so nothing here
/// or underneath retries or logs headers. <see cref="HttpClient.Timeout"/> is the activity's <c>httpRequestTimeout</c>.
/// <para>
/// Failures are classified for the orchestrator's <c>RetryPolicy</c>: no connection, 408, 429, 5xx and timeouts are
/// transient (<see cref="KeyBridgeUnavailableException"/>, <see cref="TimeoutException"/>); every other status, an
/// unusable body and bad settings are not. No message carries the URL, a header, the credential or the body.
/// </para>
/// </summary>
public sealed partial class HttpKeyBridgeClient(HttpClient http, IOptions<KeyBridgeOptions> options, ILogger<HttpKeyBridgeClient> logger) : IKeyBridgeClient
{
    public async Task<KeyBridgeGlMappings> GetGlMappingsAsync(Guid batchRunId, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Get, RequestUri(settings));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add(KeyBridgeWebActivity.CorrelationHeader, batchRunId.ToString("D", CultureInfo.InvariantCulture));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Credential(settings));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogNoResponse("timeout");
            throw new TimeoutException(string.Create(CultureInfo.InvariantCulture,
                $"KeyBridge did not answer within the Web activity's httpRequestTimeout of {http.Timeout}."));
        }
        catch (HttpRequestException e)
        {
            LogNoResponse(e.HttpRequestError.ToString());
            throw new KeyBridgeUnavailableException($"KeyBridge could not be reached ({e.HttpRequestError}).");
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                LogFailedStatus(status);
                var text = string.Create(CultureInfo.InvariantCulture, $"KeyBridge answered HTTP {status} ({response.StatusCode}) to GET {KeyBridgeWebActivity.MappingsPath}");
                throw IsTransient(response.StatusCode)
                    ? new KeyBridgeUnavailableException(text + "; the Web activity policy retries it.")
                    : new KeyBridgeRejectedException(text + "; retrying the same request cannot succeed, so it is not retried.");
            }

            var body = await ReadBodyAsync(response.Content, cancellationToken);
            var result = Parse(body);
            LogReceived(status, body.Length);
            return result;
        }
    }

    /// <summary>408, 429 and 5xx (contracts/keybridge/README.md: transient failures are retried).</summary>
    public static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    /// <summary><c>@concat(keyBridgeBaseUrl, '/v1/reference/transaction-codes/gl-mappings')</c>, after checking the setting is safe to use.</summary>
    public static Uri RequestUri(KeyBridgeOptions settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var baseUrl = settings.BaseUrl ?? throw new KeyBridgeConfigurationException("KeyBridge:BaseUrl is not configured.");
        if (!baseUrl.IsAbsoluteUri || (baseUrl.Scheme != Uri.UriSchemeHttps && baseUrl.Scheme != Uri.UriSchemeHttp))
        {
            throw new KeyBridgeConfigurationException("KeyBridge:BaseUrl must be an absolute http or https URL.");
        }

        if (!string.IsNullOrEmpty(baseUrl.UserInfo))
        {
            throw new KeyBridgeConfigurationException("KeyBridge:BaseUrl must not contain credentials; set KeyBridge:Username and KeyBridge:Password instead.");
        }

        if (!string.IsNullOrEmpty(baseUrl.Query) || !string.IsNullOrEmpty(baseUrl.Fragment))
        {
            throw new KeyBridgeConfigurationException("KeyBridge:BaseUrl must not contain a query string or fragment.");
        }

        return new Uri(baseUrl.OriginalString + KeyBridgeWebActivity.MappingsPath, UriKind.Absolute);
    }

    private static string Credential(KeyBridgeOptions settings)
    {
        if (string.IsNullOrEmpty(settings.Username) || settings.Username.Contains(':', StringComparison.Ordinal))
        {
            throw new KeyBridgeConfigurationException("KeyBridge:Username must be set and must not contain ':'.");
        }

        if (string.IsNullOrEmpty(settings.Password))
        {
            throw new KeyBridgeConfigurationException("KeyBridge:Password is not configured (Key Vault secret keybridge-api-password).");
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.Username + ":" + settings.Password));
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > KeyBridgeWebActivity.MaxResponseBytes)
        {
            throw TooLarge();
        }

        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > KeyBridgeWebActivity.MaxResponseBytes)
                {
                    throw TooLarge();
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            throw new KeyBridgeUnavailableException("The KeyBridge response broke off while it was being read.");
        }
    }

    private static KeyBridgeGlMappings Parse(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("mappings", out var mappings))
            {
                throw new KeyBridgeResponseException("The KeyBridge response has no 'mappings' property (ADF: activity('Get GL Mapping From KeyBridge').output.mappings).");
            }

            string? version = null;
            if (root.TryGetProperty("version", out var v))
            {
                version = v.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => v.GetString(),
                    _ => v.GetRawText(),
                };
            }

            return new KeyBridgeGlMappings(version, mappings.GetRawText());
        }
        catch (JsonException)
        {
            throw new KeyBridgeResponseException("The KeyBridge response is not valid JSON.");
        }
    }

    private static KeyBridgeResponseException TooLarge() =>
        new(string.Create(CultureInfo.InvariantCulture, $"The KeyBridge response exceeds the Web activity output limit of {KeyBridgeWebActivity.MaxResponseBytes} bytes."));

    [LoggerMessage(Level = LogLevel.Information, Message = "KeyBridge GET gl-mappings answered HTTP {StatusCode} with {Bytes} bytes.")]
    private partial void LogReceived(int statusCode, int bytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "KeyBridge GET gl-mappings answered HTTP {StatusCode}.")]
    private partial void LogFailedStatus(int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "KeyBridge GET gl-mappings got no response ({Reason}).")]
    private partial void LogNoResponse(string reason);
}
