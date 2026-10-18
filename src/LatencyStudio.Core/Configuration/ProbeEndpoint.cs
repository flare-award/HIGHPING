using System.Globalization;
using System.Net;

namespace LatencyStudio.Core.Configuration;

public readonly record struct ProbeEndpoint(string Host, int Port)
{
    public static ProbeEndpoint Default => new("1.1.1.1", 443);

    public static bool TryParse(string? value, out ProbeEndpoint endpoint, out string error)
    {
        endpoint = default;
        error = string.Empty;
        var input = value?.Trim() ?? string.Empty;
        if (input.Length == 0)
        {
            error = "Укажите имя узла или IP-адрес.";
            return false;
        }

        string host;
        var port = 443;
        if (input[0] == '[')
        {
            var closingBracket = input.IndexOf(']');
            if (closingBracket <= 1)
            {
                error = "IPv6-адрес введите в квадратных скобках, например [2001:db8::1]:443.";
                return false;
            }

            host = input[1..closingBracket];
            var suffix = input[(closingBracket + 1)..];
            if (suffix.Length > 0 && (!suffix.StartsWith(':') || !TryParsePort(suffix[1..], out port)))
            {
                error = "Порт должен быть числом от 1 до 65535.";
                return false;
            }
        }
        else
        {
            var colonCount = input.Count(static character => character == ':');
            if (colonCount == 1)
            {
                var separator = input.LastIndexOf(':');
                host = input[..separator].Trim();
                if (!TryParsePort(input[(separator + 1)..], out port))
                {
                    error = "Порт должен быть числом от 1 до 65535.";
                    return false;
                }
            }
            else
            {
                // An unbracketed IPv6 literal is accepted with the default port.
                host = input;
            }
        }

        host = host.Trim();
        if (host.Length is 0 or > 253 || host.Any(char.IsWhiteSpace))
        {
            error = "Имя узла или IP-адрес указаны некорректно.";
            return false;
        }

        if (!IPAddress.TryParse(host, out _) && Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            error = "Укажите корректное DNS-имя или IP-адрес.";
            return false;
        }

        endpoint = new ProbeEndpoint(host, port);
        return true;
    }

    private static bool TryParsePort(string value, out int port)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is >= 1 and <= 65535;
    }
}
