using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteConnect.Session.Discovery;

public sealed record LocalAddress(IPAddress Address, IPAddress? Mask, string InterfaceName)
{
    public IPAddress? Broadcast
    {
        get
        {
            if (Mask is null || Address.AddressFamily != AddressFamily.InterNetwork)
            {
                return null;
            }

            var a = Address.GetAddressBytes();
            var m = Mask.GetAddressBytes();
            var b = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                b[i] = (byte)(a[i] | ~m[i]);
            }

            return new IPAddress(b);
        }
    }
}

public static class NetworkInfo
{
    /// <summary>Usable IPv4 addresses of up, non-loopback, non-tunnel interfaces.</summary>
    public static IReadOnlyList<LocalAddress> GetLocalIPv4()
    {
        var result = new List<LocalAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    var ip = ua.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip))
                    {
                        continue;
                    }

                    var bytes = ip.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254)
                    {
                        continue; // link-local / APIPA
                    }

                    IPAddress? mask = null;
                    try
                    {
                        mask = ua.IPv4Mask;
                    }
                    catch (NotImplementedException)
                    {
                    }

                    result.Add(new LocalAddress(ip, mask, nic.Name));
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        return result;
    }

    public static string[] Endpoints(int port)
        => GetLocalIPv4().Select(a => $"{a.Address}:{port}").Distinct().Take(8).ToArray();
}
