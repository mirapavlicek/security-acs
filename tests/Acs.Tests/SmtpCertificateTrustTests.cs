using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Acs.Infrastructure.Notifications;
using Xunit;

namespace Acs.Tests;

/// <summary>
/// Interní SMTP relay se self-signed certifikátem: ověření se uvolní jen pro server, který správce
/// výslovně povolil, a jen tam, kde se STARTTLS navazovalo (podle cílového jména SslStreamu).
/// </summary>
public sealed class SmtpCertificateTrustTests
{
    private static X509Certificate2 SelfSigned(string cn)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // Na Linuxu potřebuje SslStream certifikát s exportovatelným klíčem načtený z PFX.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    /// <summary>TLS handshake klient ↔ loopback server s daným certifikátem; vrací, zda klient certifikát přijal.</summary>
    private static async Task<bool> HandshakeAsync(string targetHost, X509Certificate2 serverCert)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serverTask = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var ssl = new SslStream(client.GetStream(), false);
                try
                {
                    await ssl.AuthenticateAsServerAsync(serverCert, false, false);
                }
                catch (Exception)
                {
                    // klient certifikát odmítl — pro test v pořádku
                }
            });

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            await using var stream = new SslStream(tcp.GetStream(), false, SmtpCertificateTrust.Validate);
            try
            {
                await stream.AuthenticateAsClientAsync(targetHost).WaitAsync(TimeSpan.FromSeconds(15));
                return true;
            }
            catch (AuthenticationException)
            {
                return false;
            }
            finally
            {
                await serverTask.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Self_signed_certifikat_projde_jen_u_vyslovne_povoleneho_serveru()
    {
        using var cert = SelfSigned("srlsmtp01.test");

        SmtpCertificateTrust.SetRelaxed("smtpcl.test", false);
        Assert.False(await HandshakeAsync("smtpcl.test", cert));

        SmtpCertificateTrust.SetRelaxed("smtpcl.test", true);
        try
        {
            Assert.Contains("smtpcl.test", SmtpCertificateTrust.Relaxed);
            // Jiné jméno (CN=srlsmtp01) i self-signed — povolený host projde…
            Assert.True(await HandshakeAsync("smtpcl.test", cert));
            // …jiný cílový server se stejným certifikátem ne.
            Assert.False(await HandshakeAsync("other.test", cert));
        }
        finally
        {
            SmtpCertificateTrust.SetRelaxed("smtpcl.test", false);
        }
    }

    [Fact]
    public void Platny_certifikat_projde_vzdy_a_cizi_sender_se_neuvolni()
    {
        Assert.True(SmtpCertificateTrust.Validate(new object(), null, null, SslPolicyErrors.None));
        SmtpCertificateTrust.SetRelaxed("relay.test", true);
        try
        {
            Assert.False(SmtpCertificateTrust.Validate(new object(), null, null, SslPolicyErrors.RemoteCertificateChainErrors));
        }
        finally
        {
            SmtpCertificateTrust.SetRelaxed("relay.test", false);
        }
    }
}
