using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ConnectIPN.Util;

public sealed record LocalAddress(IPAddress Address, string InterfaceName)
{
    public override string ToString() => $"{Address}  ({InterfaceName})";
}

internal static class LocalAddresses
{
    /// <summary>LAN IPv4 addresses, Wi-Fi first so the default pick is usually right.</summary>
    public static List<LocalAddress> Get()
    {
        var result = new List<(int rank, LocalAddress address)>();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            int rank = nic.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => 0,
                NetworkInterfaceType.Ethernet => 1,
                NetworkInterfaceType.GigabitEthernet => 1,
                _ => 2,
            };

            foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
            {
                if (info.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }
                byte[] bytes = info.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254)
                {
                    continue;
                }
                result.Add((rank, new LocalAddress(info.Address, nic.Name)));
            }
        }

        return result
            .OrderBy(x => x.rank)
            .ThenBy(x => x.address.Address.ToString(), StringComparer.Ordinal)
            .Select(x => x.address)
            .ToList();
    }

    public static string? Primary()
    {
        List<LocalAddress> addresses = Get();
        return addresses.Count > 0 ? addresses[0].Address.ToString() : null;
    }
}
