using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CreditUnion.Batch.Core.Configuration;
using CreditUnion.Batch.Core.Reconciliation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CreditUnion.Batch.KeyStone.Reconciliation;

/// <summary>
/// The Web activity "Post Recon Alert" as a typed <see cref="HttpClient"/> (ADR 0013). One call is one HTTP request: there is no
/// retry handler, and the factory registration removes the HTTP-client loggers and the client's trace spans, because the
/// webhook URL carries a secret token in its path and neither the logger nor the span redacts a path. <see cref="HttpClient.Timeout"/>
/// is the activity's <c>httpRequestTimeout</c>.
/// <para>
/// Failures are classified for the orchestrator's <c>RetryPolicy</c>: no connection, 408, 429, 5xx and timeouts are
/// transient (<see cref="ReconAlertUnavailableException"/>, <see cref="TimeoutException"/>); every other status and bad
/// settings are not. No message, log line or exception carries the URL, a header or a response body, and the driver's own
/// exception is not attached.
/// </para>
/// </summary>
public sealed partial class HttpReconAlertClient(HttpClient http, IOptions<ReconAlertWebhookOptions> options, ILogger<HttpReconAlertClient> logger) : IReconAlertClient
{
    public async Task PostAsync(string jsonBody, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jsonBody);
        var url = WebhookUrl();
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        var content = new ByteArrayContent(new UTF8Encoding(false).GetBytes(jsonBody));
        content.Headers.ContentType = new MediaTypeHeaderValue(ReconAlertWebActivity.ContentType);
        request.Content = content;

        HttpResponseMessage response;
        try
        {
            // The body of the response is not read: the Web activity's output is not used.
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogNoResponse("timeout");
            throw new TimeoutException(string.Create(CultureInfo.InvariantCulture,
                $"The alert webhook did not answer within the Web activity's httpRequestTimeout of {http.Timeout}."));
        }
        catch (HttpRequestException e)
        {
            LogNoResponse(e.HttpRequestError.ToString());
            throw new ReconAlertUnavailableException($"The alert webhook could not be reached ({e.HttpRequestError}).");
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                LogAccepted(status);
                return;
            }

            LogFailedStatus(status);
            var text = string.Create(CultureInfo.InvariantCulture, $"The alert webhook answered HTTP {status} ({response.StatusCode}) to the POST");
            throw IsTransient(response.StatusCode)
                ? new ReconAlertUnavailableException(text + "; the Post Recon Alert policy retries it.")
                : new ReconAlertRejectedException(text + "; retrying the same request cannot succeed, so it is not retried.");
        }
    }

    /// <summary>408, 429 and 5xx.</summary>
    public static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    /// <summary>
    /// The webhook URL from configuration (Key Vault in Azure), checked to be absolute http or https. It is resolved here, inside
    /// the activity, and is never an activity input or output. Messages name the setting, not its value.
    /// </summary>
    internal Uri WebhookUrl()
    {
        Uri? url;
        try
        {
            url = options.Value.Url;
        }
        catch (Exception e) when (e is InvalidOperationException or OptionsValidationException or UriFormatException)
        {
            throw new ReconAlertConfigurationException("ReconAlertWebhook:Url cannot be read from configuration (it must be an absolute http or https URL).");
        }

        if (url is null)
        {
            throw new ReconAlertConfigurationException("ReconAlertWebhook:Url is not configured (Key Vault secret " + ReconAlertWebActivity.KeyVaultSecretName + ").");
        }

        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            throw new ReconAlertConfigurationException("ReconAlertWebhook:Url must be an absolute http or https URL.");
        }

        return url;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The alert webhook accepted the POST with HTTP {StatusCode}.")]
    private partial void LogAccepted(int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The alert webhook answered HTTP {StatusCode} to the POST.")]
    private partial void LogFailedStatus(int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The alert webhook POST got no response ({Reason}).")]
    private partial void LogNoResponse(string reason);
}
