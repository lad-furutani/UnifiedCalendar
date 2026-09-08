using System.Diagnostics;
using Serilog;

namespace UnifiedCalendar.App.Services;

public interface IExternalUriLauncher
{
    Task<bool> OpenAsync(Uri uri, CancellationToken cancellationToken = default);
}

public sealed class ExternalUriLauncher : IExternalUriLauncher
{
    public Task<bool> OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Only absolute HTTP and HTTPS URIs can be opened.", nameof(uri));
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return Task.FromResult(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Warning("ExternalUriOpenFailed {ErrorCategory}", "ShellLaunch");
            return Task.FromResult(false);
        }
    }
}
