using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using WebullAnalytics.Api;

namespace WebullAnalytics.Schwab;

/// <summary>One-shot loopback listener that captures the Schwab OAuth redirect (e.g. https://127.0.0.1/?code=...)
/// so `wa schwab login` doesn't need the code pasted by hand. Schwab mandates an HTTPS callback, so this terminates
/// TLS with an ephemeral self-signed cert — the browser shows a one-time "connection isn't private" warning the user
/// clicks through, after which the redirect's GET reaches us and we read the code. Only auto-catches loopback hosts;
/// the caller falls back to manual paste when this returns null (bind failure, timeout, non-loopback redirect).</summary>
internal static class SchwabRedirectListener
{
	/// <summary>Waits for the browser to hit the configured redirect URI and returns the authorization code, or null
	/// if none arrives within <paramref name="timeout"/>. Throws on bind failure (port in use / permission) so the
	/// caller can distinguish "couldn't listen" from "timed out" and fall back to paste either way.
	///
	/// Connections are handled concurrently: dismissing the cert warning can leave an abandoned probe connection
	/// open while the real redirect arrives on a fresh one, so a blocking read on the probe must not stall the wait.</summary>
	public static async Task<string?> WaitForCodeAsync(SchwabConfig schwab, TimeSpan timeout, CancellationToken cancellation)
	{
		var uri = new Uri(schwab.RedirectUri);
		if (uri.Host is not ("127.0.0.1" or "localhost")) return null; // only loopback redirects are safe to self-host

		var useTls = uri.Scheme == "https";
		using var cert = useTls ? CreateSelfSignedCert(uri.Host) : null;

		var listener = new TcpListener(IPAddress.Loopback, uri.Port);
		listener.Start();

		using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
		cts.CancelAfter(timeout);
		var found = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
		await using var reg = cts.Token.Register(() => found.TrySetResult(null)); // wake on timeout/cancel

		try
		{
			_ = Task.Run(async () =>
			{
				while (!cts.IsCancellationRequested)
				{
					TcpClient tcp;
					try { tcp = await listener.AcceptTcpClientAsync(cts.Token); }
					catch { break; }
					_ = HandleConnectionAsync(tcp, uri, useTls, cert, found, cts.Token);
				}
			}, cts.Token);

			return await found.Task;
		}
		finally { listener.Stop(); }
	}

	/// <summary>Handles a single accepted connection: completes TLS, reads the request line, and — if it carries a
	/// <c>code</c> — completes <paramref name="found"/> with it. Swallows all errors (a failed handshake, a probe that
	/// sends nothing, a torn-down connection) so one bad connection never aborts the overall wait.</summary>
	private static async Task HandleConnectionAsync(TcpClient tcp, Uri uri, bool useTls, X509Certificate2? cert, TaskCompletionSource<string?> found, CancellationToken ct)
	{
		using (tcp)
		{
			try
			{
				Stream stream = tcp.GetStream();
				if (useTls)
				{
					var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
					await ssl.AuthenticateAsServerAsync(cert!, clientCertificateRequired: false, checkCertificateRevocation: false);
					stream = ssl;
				}

				var target = await ReadRequestTargetAsync(stream, ct);
				var code = target != null ? SchwabAuthClient.ExtractCode($"{uri.Scheme}://{uri.Host}:{uri.Port}{target}") : null;
				await WriteResponseAsync(stream, code != null, ct);
				if (code != null) found.TrySetResult(code);
			}
			catch { /* handshake abort / probe / partial request — another connection will carry the real redirect */ }
		}
	}

	/// <summary>Reads only the HTTP request line (first CRLF-terminated line) and returns its target — the
	/// "/path?query" token — or null. Byte-at-a-time so we never over-read into a subsequent request.</summary>
	private static async Task<string?> ReadRequestTargetAsync(Stream stream, CancellationToken ct)
	{
		var sb = new StringBuilder();
		var buf = new byte[1];
		while (sb.Length < 8192)
		{
			if (await stream.ReadAsync(buf.AsMemory(0, 1), ct) == 0) break;
			if (buf[0] == (byte)'\n') break;
			if (buf[0] != (byte)'\r') sb.Append((char)buf[0]);
		}
		var parts = sb.ToString().Split(' ');
		return parts.Length >= 2 ? parts[1] : null;
	}

	private static async Task WriteResponseAsync(Stream stream, bool ok, CancellationToken ct)
	{
		var body = ok
			? "<!doctype html><meta charset=utf-8><title>Schwab login</title><body style='font-family:sans-serif;margin:3rem'><h2>Authorized.</h2><p>You can close this tab and return to the terminal.</p></body>"
			: "<!doctype html><meta charset=utf-8><title>Schwab login</title><body style='font-family:sans-serif;margin:3rem'><h2>Waiting…</h2><p>No authorization code in this request.</p></body>";
		var bytes = Encoding.UTF8.GetBytes(body);
		var header = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
		await stream.WriteAsync(Encoding.UTF8.GetBytes(header), ct);
		await stream.WriteAsync(bytes, ct);
		await stream.FlushAsync(ct);
	}

	/// <summary>Builds a short-lived self-signed cert for the loopback host. Re-imported via PFX so SslStream sees an
	/// attached private key (a freshly created cert's key isn't persisted for server auth on Windows otherwise).</summary>
	private static X509Certificate2 CreateSelfSignedCert(string host)
	{
		using var rsa = RSA.Create(2048);
		var req = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		var san = new SubjectAlternativeNameBuilder();
		if (IPAddress.TryParse(host, out var ip)) san.AddIpAddress(ip); else san.AddDnsName(host);
		req.CertificateExtensions.Add(san.Build());
		using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
		return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
	}
}
