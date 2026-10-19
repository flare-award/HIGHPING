using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LatencyStudio.Core.Configuration;
using LatencyStudio.Core.Latency;
using LatencyStudio.Core.Runtime;

namespace LatencyStudio.App.Infrastructure;

internal sealed record ProbeResult(
    double MeasuredTcpConnectMs,
    double? SourceBaselineRttMs,
    bool SourceBaselineAvailable,
    string Host,
    int Port);

/// <summary>
/// Measures a real TCP connect handshake to the selected test node. It deliberately
/// does not label this application-level TCP metric as a universal game/server ping.
/// </summary>
internal sealed class ProbeService
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public async Task<ProbeResult> MeasureAsync(
        string testNode,
        TrafficEngineController controller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (!ProbeEndpoint.TryParse(testNode, out var endpoint, out var parseError))
        {
            throw new ArgumentException(parseError, nameof(testNode));
        }

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(endpoint.Host, out var address)
                ? [address]
                : await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            throw new InvalidOperationException($"Не удалось разрешить тестовый узел {endpoint.Host}: {exception.Message}", exception);
        }

        if (addresses.Length == 0)
        {
            throw new InvalidOperationException($"Для тестового узла {endpoint.Host} не найдено IP-адресов.");
        }

        var errors = new List<string>();
        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            var stopwatch = Stopwatch.StartNew();
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), timeout.Token).ConfigureAwait(false);
                stopwatch.Stop();

                double? baseline = null;
                if (socket.LocalEndPoint is IPEndPoint local
                    && socket.RemoteEndPoint is IPEndPoint remote
                    && TcpFlowKey.TryFromEndpoints(local, remote, out var flow)
                    && controller.TryGetBaseline(flow, out var measuredBaseline))
                {
                    baseline = measuredBaseline;
                }
                else if (controller.Snapshot.State == TrafficEngineState.Disabled)
                {
                    // With the packet handler off this is the best available baseline
                    // for this exact TCP endpoint (not a measurement for other servers).
                    baseline = stopwatch.Elapsed.TotalMilliseconds;
                }

                return new ProbeResult(stopwatch.Elapsed.TotalMilliseconds, baseline, baseline.HasValue, endpoint.Host, endpoint.Port);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add($"{address}: тайм-аут {ConnectTimeout.TotalSeconds:0} с");
            }
            catch (SocketException exception)
            {
                errors.Add($"{address}: {exception.SocketErrorCode} ({exception.Message})");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(
            $"Не удалось установить TCP-соединение с {endpoint.Host}:{endpoint.Port}. " + string.Join("; ", errors));
    }
}
