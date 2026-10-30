using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using GridSim.Core.Interpose;
using GridSim.Web.Sessions;
using GridSim.Web.Site;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N346 (CF3, October 2026), three parts:
/// <list type="number">
/// <item>the interposer's stop summary counts step-position and bitstring points on their own
///   (<see cref="RtuPointTally"/>), as well as inside <c>points</c>;</item>
/// <item>a replay-only web instance serves only the raw replays it is configured for (<c>GridSim:ReplayCases</c>,
///   default gb-full) and refuses any other <c>bridge:&lt;case&gt;@raw</c> by name, at the HTTP gate and in
///   <see cref="SessionManager.Create"/>;</item>
/// <item>key-ring certificate rotation: <c>Auth:KeyPreviousCertificatePath</c> names a decrypt-only certificate so keys
///   written before a rotation still read.</item>
/// </list>
/// Every web host is the real GridSim.Web app in memory with its own temporary key folder; certificates are throwaway
/// self-signed ones written to temporary files.
/// </summary>
public sealed partial class Cf3OpenCodeFindingsTests
{
    // ------------------------------------------------------------------------------------------- 1. stop summary

    private static RtuPoint Pt(string address, RtuPointType type, double value) => new(address, type, value, RtuQuality.Good, null);

    [Fact]
    public void N346_the_tally_counts_step_positions_and_bitstrings_inside_and_beside_the_total()
    {
        var t = new RtuPointTally();
        Assert.Equal("points 0, step-positions 0, bitstrings 0", t.ConsoleFields());
        t.Add(new[] { Pt("IOA:1", RtuPointType.Analog, 1.0), Pt("IOA:10", RtuPointType.StepPosition, 5.0) });
        t.Add(Array.Empty<RtuPoint>());
        t.Add(new[]
        {
            Pt("IOA:20", RtuPointType.Bitstring, 4294967295.0), Pt("IOA:10", RtuPointType.StepPosition, -64.0),
            Pt("IOA:2", RtuPointType.BinaryStatus, 1.0), Pt("IOA:3", RtuPointType.Counter, 7.0), Pt("IOA:4", RtuPointType.Setpoint, 0.5),
        });
        Assert.Equal((7L, 2L, 1L), (t.Points, t.StepPositions, t.Bitstrings));
        Assert.Equal("points 7, step-positions 2, bitstrings 1", t.ConsoleFields());
        Assert.Equal("points=7 step-positions=2 bitstrings=1", t.AuditFields());
        Assert.Throws<ArgumentNullException>(() => t.Add(null!));
    }

    [Fact]
    public void N346_the_interpose_stop_line_and_audit_outcome_carry_the_tally()
    {
        // CLI wiring (the interposer needs a live client, N230): the stop line and the audit outcome print the tally's
        // fields where the total used to be, and the old bare counter is gone.
        string program = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Cli", "Program.cs"));
        Assert.Contains("map-errors {pipeline.MapErrors}, {pointTally.ConsoleFields()}, ", program, StringComparison.Ordinal);
        Assert.Contains("map-errors={pipeline.MapErrors} {pointTally.AuditFields()} windows-written=", program, StringComparison.Ordinal);
        Assert.Contains("pointTally.Add(pts);", program, StringComparison.Ordinal);
        Assert.DoesNotContain("pointsSeen", program, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------- 2. replay cases

    private static WebApplicationFactory<Program> N346Factory(params (string Key, string Value)[] settings)
    {
        string keys = Path.Combine(Path.GetTempPath(), "gridsim-cf3-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keys);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:KeysDirectory", keys);
            foreach ((string k, string v) in settings) b.UseSetting(k, v);
        });
    }

    [Fact]
    public void N346_replay_cases_setting_parses_and_refuses_by_name()
    {
        Assert.Equal(new[] { "gb-full" }, SessionManager.ReadReplayCases(null));
        Assert.Equal(new[] { "gb-full" }, SessionManager.ReadReplayCases("  "));
        Assert.Equal(new[] { "gb-full", "uk", "case_9.v2" }, SessionManager.ReadReplayCases(" gb-full , uk,case_9.v2 "));
        foreach ((string raw, string fragment) in new[]
        {
            ("gb-full,", "has an empty entry"),
            (",gb-full", "has an empty entry"),
            ("gb full", "is not a remote case name"),
            ("gb-full@raw", "is not a remote case name"),
            ("bridge:gb-full", "is not a remote case name"),
            ("a/b", "is not a remote case name"),
            ("gb-full,GB-FULL", "names 'GB-FULL' twice"),
        })
        {
            var ex = Assert.Throws<InvalidOperationException>(() => SessionManager.ReadReplayCases(raw));
            Assert.Contains("GridSim:ReplayCases", ex.Message, StringComparison.Ordinal);
            Assert.Contains(fragment, ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void N346_only_the_raw_replay_of_a_configured_case_is_allowed()
    {
        // The default (the one-argument form) is gb-full alone: another case's raw replay is refused now.
        Assert.True(SessionManager.IsAllowedOnReplayOnly("bridge:gb-full@raw"));
        Assert.False(SessionManager.IsAllowedOnReplayOnly("bridge:case9@raw"));
        Assert.False(SessionManager.IsAllowedOnReplayOnly("bridge:gb-full-2@raw"));
        string[] served = { "gb-full", "uk" };
        Assert.True(SessionManager.IsAllowedOnReplayOnly("bridge:UK@raw", served));   // names compare without regard to case
        Assert.True(SessionManager.IsAllowedOnReplayOnly("bridge:@raw", served));     // blank case means gb-full
        Assert.False(SessionManager.IsAllowedOnReplayOnly("bridge:case9@raw", served));
        Assert.False(SessionManager.IsAllowedOnReplayOnly("bridge:uk@presolved", served));
        Assert.False(SessionManager.IsAllowedOnReplayOnly("uk", served));
        Assert.False(SessionManager.IsAllowedOnReplayOnly("bridge:@raw", new[] { "uk" }));   // gb-full not served
        Assert.Equal("case9", SessionManager.RawReplayCase("bridge:case9@raw"));
        Assert.Null(SessionManager.RawReplayCase("bridge:case9@solves"));
        Assert.Null(SessionManager.RawReplayCase("bridge:case9@nonsense"));
    }

    [Fact]
    public void N346_create_refuses_an_unconfigured_raw_replay_by_name_and_lets_a_configured_one_through()
    {
        using var f = N346Factory(("GridSim:ReplayOnly", "true"), ("GridSim:ReplayCases", "gb-full,uk"));
        SessionManager m = f.Services.GetRequiredService<SessionManager>();
        Assert.Equal(new[] { "gb-full", "uk" }, m.ReplayCases);
        Assert.Equal("bridge:gb-full@raw", m.LandingCaseId);
        var ex = Assert.Throws<ReplayOnlyRefusedException>(() => m.Create("bridge:case9@raw"));
        Assert.Equal("This is a replay-only instance (GridSim:ReplayOnly=true): 'bridge:case9@raw' is the raw replay of case 'case9', "
            + "which this instance is not configured to serve (GridSim:ReplayCases: gb-full, uk), so it is refused.", ex.Message);
        // A non-raw id gets the general refusal, listing what can be opened.
        var general = Assert.Throws<ReplayOnlyRefusedException>(() => m.Create("case9"));
        Assert.Equal("This is a replay-only instance (GridSim:ReplayOnly=true): only the raw public-data replay of bridge:gb-full@raw, "
            + "bridge:uk@raw (GridSim:ReplayCases) can be opened here, so 'case9' is refused.", general.Message);
        // A configured raw replay passes the replay-only check and reaches the bridge code (no endpoint in the test).
        var noBridge = Assert.Throws<InvalidOperationException>(() => m.Create("bridge:uk@raw"));
        Assert.IsNotType<ReplayOnlyRefusedException>(noBridge);
        Assert.Contains("No GDA bridge endpoint is configured", noBridge.Message, StringComparison.Ordinal);
        Assert.Equal(0, m.ActiveSessions);
    }

    [Fact]
    public async Task N346_the_http_gate_answers_404_for_an_unconfigured_raw_replay()
    {
        await using var f = N346Factory(("GridSim:ReplayOnly", "true"));
        using HttpClient c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        using HttpResponseMessage refused = await c.GetAsync(new Uri("/dashboard/bridge:case9@raw", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(0, f.Services.GetRequiredService<SessionManager>().ActiveSessions);

        await using var g = N346Factory(("GridSim:ReplayOnly", "true"), ("GridSim:ReplayCases", "case9"));
        using HttpClient d = g.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        using HttpResponseMessage served = await d.GetAsync(new Uri("/dashboard/bridge:case9@raw", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Contains("No GDA bridge endpoint is configured", await served.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        // gb-full is no longer served by that instance, so the default landing id is refused there.
        using HttpResponseMessage landing = await d.GetAsync(new Uri("/dashboard/" + SessionManager.RawLandingCaseId, UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, landing.StatusCode);
    }

    [Fact]
    public void N346_a_bad_replay_cases_setting_stops_start_up()
    {
        using var f = N346Factory(("GridSim:ReplayCases", "gb-full,,uk"));
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("GridSim:ReplayCases has an empty entry", ex.ToString(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------- 3. rotation

    private static IConfiguration N346Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build();

    /// <summary>A throwaway self-signed certificate with (or without) its private key, as a password-less PKCS#12 file.</summary>
    private static string N346Pfx(string subject, bool withPrivateKey = true)
    {
        using RSA rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=" + subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-cf3-cert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "keyring.pfx");
        if (withPrivateKey) File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
        else
        {
            using X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificate(cert.RawData);
            File.WriteAllBytes(path, publicOnly.Export(X509ContentType.Pfx));
        }
        return path;
    }

    [Fact]
    public void N346_the_previous_certificate_is_loaded_checked_and_named_in_the_log()
    {
        string current = N346Pfx("GridSim rotation new"), previous = N346Pfx("GridSim rotation old");
        KeyRingProtection.Plan plan = KeyRingProtection.Resolve(N346Config(
            ("Auth:KeyProtection", "certificate"), ("Auth:KeyCertificatePath", current), ("Auth:KeyPreviousCertificatePath", previous)), isWindows: false);
        Assert.Equal(KeyRingProtection.Protector.Certificate, plan.Protector);
        Assert.NotNull(plan.PreviousCertificate);
        Assert.NotEqual(plan.Certificate!.Thumbprint, plan.PreviousCertificate!.Thumbprint);
        Assert.Contains($"Keys encrypted to the previous certificate in Auth:KeyPreviousCertificatePath (thumbprint {plan.PreviousCertificate.Thumbprint}) are still read",
            plan.LogLine, StringComparison.Ordinal);
        // No previous setting: no previous certificate (the plan is what it was before N346).
        Assert.Null(KeyRingProtection.Resolve(N346Config(("Auth:KeyProtection", "certificate"), ("Auth:KeyCertificatePath", current)), false).PreviousCertificate);
        // Honoured in a mode with no current certificate (a ring moved off certificates keeps reading its old keys).
        Assert.NotNull(KeyRingProtection.Resolve(N346Config(("Auth:KeyProtection", "none"), ("Auth:KeyPreviousCertificatePath", previous)), false).PreviousCertificate);

        void Refused(string fragment, params (string, string)[] s)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => KeyRingProtection.Resolve(N346Config(s), isWindows: false));
            Assert.Contains(fragment, ex.Message, StringComparison.Ordinal);
        }
        Refused("Auth:KeyPreviousCertificatePath names a file that does not exist",
            ("Auth:KeyProtection", "none"), ("Auth:KeyPreviousCertificatePath", Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".pfx")));
        Refused("the certificate in Auth:KeyPreviousCertificatePath has no private key",
            ("Auth:KeyProtection", "none"), ("Auth:KeyPreviousCertificatePath", N346Pfx("GridSim rotation public", withPrivateKey: false)));
        Refused("names the same certificate as Auth:KeyCertificatePath",
            ("Auth:KeyProtection", "certificate"), ("Auth:KeyCertificatePath", current), ("Auth:KeyPreviousCertificatePath", current));
    }

    [Fact]
    public void N346_after_a_rotation_keys_written_under_the_old_certificate_still_read()
    {
        // Host 1 writes a key encrypted to the OLD certificate and protects a payload with it. Host 2 has rotated: the
        // NEW certificate encrypts, the old one is the previous (decrypt-only) certificate. It reads the old payload
        // back. Host 3 has rotated but was not given the previous certificate: the old key cannot be decrypted, so the
        // same payload is refused (the setting is what makes the rotation lossless).
        string keys = Path.Combine(Path.GetTempPath(), "gridsim-cf3-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keys);
        string oldPfx = N346Pfx("GridSim rotation old"), newPfx = N346Pfx("GridSim rotation new");
        static WebApplicationFactory<Program> Host(string keysDir, params (string Key, string Value)[] s) =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting("Auth:KeysDirectory", keysDir);
                foreach ((string k, string v) in s) b.UseSetting(k, v);
            });

        string protectedText;
        using (var h1 = Host(keys, ("Auth:KeyProtection", "certificate"), ("Auth:KeyCertificatePath", oldPfx)))
            protectedText = h1.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("cf3-rotation").Protect("before the rotation");

        using (var h2 = Host(keys, ("Auth:KeyProtection", "certificate"), ("Auth:KeyCertificatePath", newPfx), ("Auth:KeyPreviousCertificatePath", oldPfx)))
            Assert.Equal("before the rotation", h2.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("cf3-rotation").Unprotect(protectedText));

        using var h3 = Host(keys, ("Auth:KeyProtection", "certificate"), ("Auth:KeyCertificatePath", newPfx));
        Assert.ThrowsAny<CryptographicException>(() =>
            h3.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("cf3-rotation").Unprotect(protectedText));
    }
}
