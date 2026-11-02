using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Dpt.AdHoc.Core
{
    /// <summary>
    /// Small helper around NetworkInterface: local IPv4 addresses and default gateways.
    /// Needed to spot NAT hairpin (a client whose source address is our own gateway).
    /// </summary>
    public static class LocalNetwork
    {
        private static readonly object Sync = new object();
        private static string[] _gateways = new string[0];
        private static string[] _addresses = new string[0];
        private static DateTime _refreshedUtc = DateTime.MinValue;

        public static string[] Gateways()
        {
            Refresh();
            lock (Sync) { return (string[])_gateways.Clone(); }
        }

        public static string[] Ipv4Addresses()
        {
            Refresh();
            lock (Sync) { return (string[])_addresses.Clone(); }
        }

        public static bool IsGateway(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return false;
            string[] gateways = Gateways();
            for (int i = 0; i < gateways.Length; i++)
                if (string.Equals(gateways[i], ip, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void Refresh()
        {
            lock (Sync)
            {
                if ((DateTime.UtcNow - _refreshedUtc).TotalSeconds < 60) return;
                _refreshedUtc = DateTime.UtcNow;

                List<string> gateways = new List<string>();
                List<string> addresses = new List<string>();
                try
                {
                    NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
                    for (int n = 0; n < nics.Length; n++)
                    {
                        NetworkInterface nic = nics[n];
                        if (nic.OperationalStatus != OperationalStatus.Up) continue;
                        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                        IPInterfaceProperties props;
                        try { props = nic.GetIPProperties(); }
                        catch { continue; }

                        try
                        {
                            GatewayIPAddressInformationCollection gws = props.GatewayAddresses;
                            for (int g = 0; g < gws.Count; g++)
                            {
                                IPAddress a = gws[g].Address;
                                if (a == null || a.AddressFamily != AddressFamily.InterNetwork) continue;
                                string s = a.ToString();
                                if (s == "0.0.0.0") continue;
                                if (!gateways.Contains(s)) gateways.Add(s);
                            }
                        }
                        catch { }

                        try
                        {
                            UnicastIPAddressInformationCollection ucs = props.UnicastAddresses;
                            for (int u = 0; u < ucs.Count; u++)
                            {
                                IPAddress a = ucs[u].Address;
                                if (a == null || a.AddressFamily != AddressFamily.InterNetwork) continue;
                                string s = a.ToString();
                                if (s.StartsWith("127.", StringComparison.Ordinal)) continue;
                                if (!addresses.Contains(s)) addresses.Add(s);
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                _gateways = gateways.ToArray();
                _addresses = addresses.ToArray();
            }
        }
    }
}