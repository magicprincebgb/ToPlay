using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ToPlay.Host.Security;

/// <summary>
/// Sets up HTTPS that browsers actually trust on a LAN.
///
/// A plain self-signed server certificate always trips the "Your connection is
/// not private" / "Not secure" warning, because nothing vouches for it. Instead
/// we run a tiny private Certificate Authority (CA):
///
///   * A long-lived CA certificate is created once and installed into the PC's
///     Trusted Root store — so every browser ON THIS PC trusts ToPlay silently.
///   * The Kestrel server certificate is issued (signed) by that CA and covers
///     localhost plus every LAN IPv4 address, so it's valid for the phone URL.
///   * The CA's public certificate is written to <c>toplay-ca.crt</c> and served
///     at <c>/toplay-ca.crt</c>. Installing that one file on a phone/tablet once
///     makes ToPlay fully trusted there too (no warnings, PWA install allowed).
/// </summary>
public static class DevCertificate
{
    /// <summary>
    /// Returns the Kestrel server certificate (issued by our private CA),
    /// creating/persisting the CA + leaf and trusting the CA as needed.
    /// <paramref name="pfxPath"/> is the leaf .pfx; the CA files live beside it.
    /// </summary>
    public static X509Certificate2 LoadOrCreate(string pfxPath, string password)
    {
        var dir = Path.GetDirectoryName(pfxPath)!;
        var caPfxPath = Path.Combine(dir, "toplay-ca.pfx");
        var caCerPath = Path.Combine(dir, "toplay-ca.crt");

        var ca = LoadOrCreateCa(caPfxPath, caCerPath, password);
        TryTrust(ca);   // best effort: make this PC trust the CA silently

        // Reuse the existing leaf if it is still valid and was issued by our CA.
        if (File.Exists(pfxPath))
        {
            try
            {
                var existing = new X509Certificate2(pfxPath, password,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet);
                if (existing.NotAfter > DateTime.UtcNow.AddDays(20) &&
                    string.Equals(existing.Issuer, ca.Subject, StringComparison.OrdinalIgnoreCase))
                    return existing;
            }
            catch { /* regenerate below */ }
        }

        var leaf = CreateLeaf(ca);
        try { File.WriteAllBytes(pfxPath, leaf.Export(X509ContentType.Pfx, password)); }
        catch (Exception ex) { Console.WriteLine($"[cert] Could not persist server cert: {ex.Message}"); }
        return leaf;
    }

    // ---- CA ----------------------------------------------------------------

    private static X509Certificate2 LoadOrCreateCa(string caPfxPath, string caCerPath, string password)
    {
        if (File.Exists(caPfxPath))
        {
            try
            {
                var existing = new X509Certificate2(caPfxPath, password,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet);
                if (existing.NotAfter > DateTime.UtcNow.AddDays(30))
                {
                    ExportPublicCer(existing, caCerPath);
                    return existing;
                }
            }
            catch { /* recreate below */ }
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=ToPlay Local CA, O=ToPlay", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var now = DateTimeOffset.UtcNow;
        var caEphemeral = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));

        // Round-trip through PFX so the private key is usable for signing on Windows.
        var ca = new X509Certificate2(
            caEphemeral.Export(X509ContentType.Pfx, password), password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet);

        try { File.WriteAllBytes(caPfxPath, ca.Export(X509ContentType.Pfx, password)); }
        catch (Exception ex) { Console.WriteLine($"[cert] Could not persist CA: {ex.Message}"); }
        ExportPublicCer(ca, caCerPath);
        return ca;
    }

    private static void ExportPublicCer(X509Certificate2 cert, string path)
    {
        try
        {
            // DER-encoded public certificate (.crt) — the file phones install to trust ToPlay.
            File.WriteAllBytes(path, cert.Export(X509ContentType.Cert));
        }
        catch (Exception ex) { Console.WriteLine($"[cert] Could not write {Path.GetFileName(path)}: {ex.Message}"); }
    }

    // ---- leaf (Kestrel server cert) ----------------------------------------

    private static X509Certificate2 CreateLeaf(X509Certificate2 ca)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=ToPlay Local Host, O=ToPlay", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        try { san.AddDnsName(Dns.GetHostName()); } catch { }
        foreach (var ip in LocalIPv4Addresses())
            san.AddIpAddress(ip);
        request.CertificateExtensions.Add(san.Build());

        // Link the leaf to its issuer (helps iOS/Safari build the trust chain).
        try { request.CertificateExtensions.Add(
            X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false)); }
        catch { /* AKI is optional */ }

        var now = DateTimeOffset.UtcNow;
        // Apple limits trusted server certs to 825 days; keep well under that.
        var notAfter = now.AddDays(800);
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F; // positive serial

        var leafPublic = request.Create(ca, now.AddDays(-1), notAfter, serial);
        using var leafWithKey = leafPublic.CopyWithPrivateKey(rsa);

        return new X509Certificate2(
            leafWithKey.Export(X509ContentType.Pfx, "tmp"), "tmp",
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet);
    }

    // ---- trust the CA on this machine --------------------------------------

    private static void TryTrust(X509Certificate2 ca)
    {
        // Only the public cert goes into the trust store.
        using var pub = new X509Certificate2(ca.Export(X509ContentType.Cert));

        // Prefer the machine store (silent when elevated); fall back to the
        // per-user store (may show a one-time Windows confirmation dialog).
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            try
            {
                using var store = new X509Store(StoreName.Root, location);
                store.Open(OpenFlags.ReadWrite);
                var already = store.Certificates.Find(X509FindType.FindByThumbprint, pub.Thumbprint, false);
                if (already.Count == 0)
                {
                    store.Add(pub);
                    Console.WriteLine($"[cert] Installed ToPlay CA into {location} Trusted Root — this PC's browsers now trust ToPlay.");
                }
                return; // success at this location
            }
            catch { /* try the next location */ }
        }
        Console.WriteLine("[cert] Could not auto-trust the CA (run ToPlay as Administrator once, or install /toplay-ca.crt manually).");
    }

    // ---- network helpers (unchanged) ---------------------------------------

    public static IEnumerable<IPAddress> LocalIPv4Addresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        list.Add(ua.Address);
                }
            }
        }
        catch { /* best effort */ }
        return list.Distinct();
    }

    /// <summary>
    /// Best guess at the IP a phone should actually connect to. Prefers the
    /// physical adapter that owns a default gateway (the real Wi-Fi/Ethernet
    /// NIC) and skips Hyper-V / VirtualBox / VMware virtual adapters, whose
    /// addresses (e.g. 172.x from "vEthernet", 192.168.56.x from VirtualBox)
    /// are unreachable from other devices on the LAN.
    /// </summary>
    public static string? PrimaryLanIp()
    {
        IPAddress? best = null;
        int bestScore = int.MinValue;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var props = ni.GetIPProperties();
                bool hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !g.Address.Equals(IPAddress.Any));
                bool virt = LooksVirtual(ni);

                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (!IsPrivate(ua.Address)) continue;

                    int score = 0;
                    if (hasGateway) score += 100;   // the NIC with internet access wins
                    if (!virt) score += 50;         // real hardware beats virtual switches
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 20;
                    else if (ni.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                                                     or NetworkInterfaceType.GigabitEthernet) score += 15;

                    if (score > bestScore) { bestScore = score; best = ua.Address; }
                }
            }
        }
        catch { /* best effort */ }

        return best?.ToString()
            ?? LocalIPv4Addresses().FirstOrDefault(IsPrivate)?.ToString()
            ?? LocalIPv4Addresses().FirstOrDefault()?.ToString();
    }

    private static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10 ||
               (b[0] == 192 && b[1] == 168) ||
               (b[0] == 172 && b[1] >= 16 && b[1] <= 31);
    }

    private static bool LooksVirtual(NetworkInterface ni)
    {
        var s = (ni.Description + " " + ni.Name).ToLowerInvariant();
        return s.Contains("virtual") || s.Contains("hyper-v") || s.Contains("vmware")
            || s.Contains("virtualbox") || s.Contains("vethernet") || s.Contains("pseudo")
            || s.Contains("loopback") || s.Contains("tap") || s.Contains("tunnel");
    }
}
