using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace WebullAnalytics.Sniff;

public static class HeaderSniffer
{
	private static readonly string[] HeaderKeys = ["access_token", "did", "lzone", "osv", "ph", "t_time", "t_token", "tz", "ver", "x-s", "x-sv"];
	private const int CdpPort = 9222;

	private record BrowserInfo(string ProcessName, string BinaryPath, ProcessStartInfo StartInfo);

	public static async Task<Dictionary<string, string>> CaptureAsync(string pin, bool autoCloseBrowser, CancellationToken cancellation = default)
	{
		var browser = DetectBrowser();

		if (Process.GetProcessesByName(browser.ProcessName).Length > 0 && !KillBrowser(browser.ProcessName, autoCloseBrowser))
			throw new InvalidOperationException($"Cannot proceed while {browser.ProcessName} is running.");

		Console.WriteLine($"Launching {browser.ProcessName}...");
		using var process = Process.Start(browser.StartInfo) ?? throw new InvalidOperationException($"Failed to start {browser.ProcessName}.");

		try
		{
			Console.WriteLine("Connecting to DevTools...");
			var wsUrl = await WaitForDebuggerUrl(cancellation);

			using var ws = new ClientWebSocket();
			await ws.ConnectAsync(new Uri(wsUrl), cancellation);

			int cmdId = 0;

			// Enable domains — Firefox requires explicit enablement before they work.
			await CdpSend(ws, ++cmdId, "Page.enable", null, cancellation);
			await CdpSend(ws, ++cmdId, "Runtime.enable", null, cancellation);
			await CdpSend(ws, ++cmdId, "Network.enable", null, cancellation);

			// Drive the unlock dialog with a C# retry loop of short, independent evals — NOT a single long-lived
			// Promise. On a cold launch the initial new-tab/blank document reports readyState=complete, then the
			// navigation to the account SPA tears down the execution context; a promise injected before that dies
			// and evaluate returns an error immediately. Each tick here is independent, so it just retries through
			// the navigation, then clicks 'unlock' and focuses the PIN field once the SPA has rendered the dialog.
			// The field is an <input type=number> in a styled-components Dialog/Modal — match on type, never class
			// name (Webull's classes are capitalized, so [class*=dialog] misses); type=tel is kept as a fallback.
			// We deliberately DO NOT match type=password: in the dedicated CDP profile the first run shows Webull's
			// LOGIN form (which has a password field), and typing the PIN blind into it would leak the code. The
			// trading PIN dialog is numeric, so number/tel alone identifies it. We re-click 'unlock' every tick to
			// cover the race where the anchor renders before React binds its handler.
			//
			// The deadline is generous (5 min) because on first run — or after the session cookies expire — the
			// user must log into Webull manually in the launched window; once they reach the account page the loop
			// picks up the unlock dialog automatically. Steady-state runs (cookies still valid) hit it in seconds.
			Console.WriteLine("Waiting for the Webull trading PIN dialog...");
			Console.WriteLine("If a login screen appears, log in in the browser window — sniffing continues automatically once the PIN dialog shows.");
			const string unlockTickJs = """
				(() => {
					const inp = document.querySelector('input[type=number], input[type=tel]');
					if (inp && inp.offsetParent) {
						inp.click(); inp.focus();
						return document.activeElement === inp ? 'focused' : 'found';
					}
					const a = Array.from(document.querySelectorAll('a')).find(a => a.textContent.trim().toLowerCase() === 'unlock');
					if (a) { a.click(); return 'clicked'; }
					return 'waiting';
				})()
				""";
			var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
			bool everClicked = false;
			string? state = null;
			while (DateTime.UtcNow < deadline)
			{
				// null = context torn down mid-navigation (or eval errored) — just retry on the next tick.
				state = await EvalForString(ws, ++cmdId, unlockTickJs, awaitPromise: false, TimeSpan.FromSeconds(3), cancellation);
				if (state is "focused" or "found") break;
				if (state == "clicked") everClicked = true;
				await Task.Delay(200, cancellation);
			}
			if (state is not ("focused" or "found"))
				throw new InvalidOperationException(everClicked
					? "Clicked 'unlock' but the PIN dialog never appeared — the Webull page layout may have changed."
					: "The trading PIN dialog never appeared. If a login screen was shown, the login may not have completed in time; otherwise the page layout may have changed.");
			await Task.Delay(300, cancellation);

			foreach (var c in pin)
			{
				var text = c.ToString();
				var keyCode = (int)c;
				await CdpSend(ws, ++cmdId, "Input.dispatchKeyEvent", new { type = "keyDown", text, key = text, windowsVirtualKeyCode = keyCode, nativeVirtualKeyCode = keyCode }, cancellation);
				await CdpSend(ws, ++cmdId, "Input.dispatchKeyEvent", new { type = "keyUp", key = text, windowsVirtualKeyCode = keyCode, nativeVirtualKeyCode = keyCode }, cancellation);
				await Task.Delay(80, cancellation);
			}

			Console.WriteLine("Waiting for API headers...");
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
			cts.CancelAfter(TimeSpan.FromSeconds(30));

			Dictionary<string, string> headers;
			try
			{
				headers = await ReadUntilHeaders(ws, cts.Token);
			}
			catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
			{
				throw new TimeoutException("Timed out waiting for a Webull API request. The unlock code may be incorrect, or the page did not trigger an API call.");
			}

			// Clear the page and close the browser gracefully so it persists about:blank as the last session.
			try
			{
				await CdpSend(ws, ++cmdId, "Page.navigate", new { url = "about:blank" }, CancellationToken.None);
				await DrainUntilResult(ws, cmdId, TimeSpan.FromSeconds(5), CancellationToken.None);
				await CdpSend(ws, ++cmdId, "Browser.close", null, CancellationToken.None);
				process.WaitForExit(5000);
			}
			catch { }

			return headers;
		}
		finally
		{
			try { if (!process.HasExited) process.Kill(); } catch { }
		}
	}

	private static BrowserInfo DetectBrowser()
	{
		if (OperatingSystem.IsWindows())
			return CreateEdgeInfo();
		if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
		{
			// Prefer Edge on Linux/macOS (full CDP support), fall back to Firefox.
			try { return CreateEdgeInfo(); }
			catch (InvalidOperationException) { return CreateFirefoxInfo(); }
		}
		throw new InvalidOperationException("Unsupported platform for header sniffing.");
	}

	private static BrowserInfo CreateEdgeInfo()
	{
		var binaryPath = FindBinaryPath([
			// Windows
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
			// Linux
			"/opt/microsoft/msedge/msedge",
			"/usr/bin/microsoft-edge-stable",
			"/usr/bin/microsoft-edge",
			// macOS
			"/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
		], "Microsoft Edge");

		// Chromium 136+ (Edge included) silently disables remote debugging when --user-data-dir points at the
		// browser's DEFAULT profile — a security fix against malware attaching to a logged-in session over CDP.
		// So we cannot reuse the user's normal Edge profile; we run against a dedicated, persistent CDP profile.
		// It is empty on first run (the user logs into Webull once in the launched window), and its session
		// cookies persist across runs so later sniffs go straight to the PIN dialog. The dir must be created up
		// front — Edge won't create a --user-data-dir that doesn't exist when remote debugging is enabled.
		var userDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebullAnalytics", "edge-cdp");
		Directory.CreateDirectory(userDataDir);

		return new("msedge", binaryPath, new ProcessStartInfo
		{
			FileName = binaryPath,
			Arguments = $"--remote-debugging-port={CdpPort} --user-data-dir=\"{userDataDir}\" https://app.webull.com/account",
			UseShellExecute = false,
		});
	}

	private static BrowserInfo CreateFirefoxInfo()
	{
		var binaryPath = FindBinaryPath([
			"/usr/bin/firefox",
			"/snap/bin/firefox",
			"/usr/bin/firefox-esr",
			"/Applications/Firefox.app/Contents/MacOS/firefox",
		], "Firefox");

		return new("firefox", binaryPath, new ProcessStartInfo
		{
			FileName = binaryPath,
			ArgumentList = { "--remote-debugging-port", CdpPort.ToString(), "https://app.webull.com/account" },
			UseShellExecute = false,
		});
	}

	private static string FindBinaryPath(string[] candidates, string browserName)
	{
		// Check candidates first, then fall back to PATH lookup
		var found = candidates.FirstOrDefault(File.Exists);
		if (found != null) return found;

		// Try finding on PATH
		var name = browserName.ToLowerInvariant().Replace(" ", "");
		try
		{
			var psi = new ProcessStartInfo("which", name) { RedirectStandardOutput = true, UseShellExecute = false };
			if (!OperatingSystem.IsWindows())
			{
				var proc = Process.Start(psi);
				if (proc != null)
				{
					var path = proc.StandardOutput.ReadToEnd().Trim();
					proc.WaitForExit();
					if (proc.ExitCode == 0 && File.Exists(path)) return path;
				}
			}
		}
		catch { }

		throw new InvalidOperationException($"{browserName} not found.");
	}

	private static bool KillBrowser(string processName, bool autoClose)
	{
		var displayName = processName == "msedge" ? "Microsoft Edge" : "Firefox";

		if (!autoClose)
		{
			Console.Write($"{displayName} is running and must be closed to sniff headers. Close it now? [Y/n] ");
			var key = Console.ReadLine()?.Trim();
			if (!string.IsNullOrEmpty(key) && !key.Equals("y", StringComparison.OrdinalIgnoreCase))
				return false;
		}

		Console.WriteLine($"Closing {displayName}...");
		foreach (var p in Process.GetProcessesByName(processName))
		{
			try { p.Kill(); } catch { }
		}

		for (int i = 0; i < 20; i++)
		{
			if (Process.GetProcessesByName(processName).Length == 0) return true;
			Thread.Sleep(250);
		}

		Console.WriteLine($"Warning: {displayName} processes did not exit in time.");
		return false;
	}

	private static async Task<string> WaitForDebuggerUrl(CancellationToken cancellation)
	{
		using var http = new HttpClient();
		for (int attempt = 0; attempt < 30; attempt++)
		{
			await Task.Delay(500, cancellation);
			try
			{
				var json = await http.GetStringAsync($"http://localhost:{CdpPort}/json", cancellation);
				foreach (var page in JsonDocument.Parse(json).RootElement.EnumerateArray())
				{
					if (page.TryGetProperty("webSocketDebuggerUrl", out var wsUrl))
						return wsUrl.GetString()!;
				}
			}
			catch (HttpRequestException) { }
		}
		throw new TimeoutException("Timed out waiting for DevTools to become available.");
	}

	private static async Task<JsonElement?> CdpReceive(ClientWebSocket ws, CancellationToken cancellation)
	{
		var buffer = new byte[65536];
		var sb = new StringBuilder();
		WebSocketReceiveResult result;
		do
		{
			result = await ws.ReceiveAsync(buffer, cancellation);
			sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
		} while (!result.EndOfMessage);

		if (result.MessageType == WebSocketMessageType.Close) return null;
		try { return JsonDocument.Parse(sb.ToString()).RootElement; }
		catch (JsonException) { return null; }
	}

	private static async Task WaitForCdpEvent(ClientWebSocket ws, string eventName, TimeSpan timeout, CancellationToken cancellation)
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
		cts.CancelAfter(timeout);
		try
		{
			while (true)
			{
				var msg = await CdpReceive(ws, cts.Token);
				if (msg == null) continue;
				if (msg.Value.TryGetProperty("method", out var m) && m.GetString() == eventName) return;
			}
		}
		catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
		{
			throw new TimeoutException($"Timed out waiting for {eventName}.");
		}
	}

	private static async Task DrainUntilResult(ClientWebSocket ws, int expectedId, TimeSpan timeout, CancellationToken cancellation)
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
		cts.CancelAfter(timeout);
		try
		{
			while (true)
			{
				var msg = await CdpReceive(ws, cts.Token);
				if (msg == null) continue;
				if (msg.Value.TryGetProperty("id", out var id) && id.GetInt32() == expectedId) return;
			}
		}
		catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
	}

	// Evaluate an expression and return its string result value, draining intervening events until the
	// matching command id arrives. Returns null on timeout or a non-string/absent result.
	private static async Task<string?> EvalForString(ClientWebSocket ws, int id, string expression, bool awaitPromise, TimeSpan timeout, CancellationToken cancellation)
	{
		await CdpSend(ws, id, "Runtime.evaluate", new { expression, awaitPromise, returnByValue = true }, cancellation);
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
		cts.CancelAfter(timeout);
		try
		{
			while (true)
			{
				var msg = await CdpReceive(ws, cts.Token);
				if (msg == null) continue;
				if (msg.Value.TryGetProperty("id", out var rid) && rid.GetInt32() == id)
				{
					if (msg.Value.TryGetProperty("result", out var res) && res.TryGetProperty("result", out var inner) && inner.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
						return v.GetString();
					return null;
				}
			}
		}
		catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return null; }
	}

	private static async Task CdpSend(ClientWebSocket ws, int id, string method, object? parameters, CancellationToken cancellation)
	{
		var msg = parameters != null
			? JsonSerializer.Serialize(new { id, method, @params = parameters })
			: JsonSerializer.Serialize(new { id, method });
		await ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, cancellation);
	}

	private static async Task<Dictionary<string, string>> ReadUntilHeaders(ClientWebSocket ws, CancellationToken cancellation)
	{
		var captured = new Dictionary<string, string>();
		var buffer = new byte[65536];
		var sb = new StringBuilder();

		while (ws.State == WebSocketState.Open && !cancellation.IsCancellationRequested)
		{
			sb.Clear();
			WebSocketReceiveResult result;
			do
			{
				result = await ws.ReceiveAsync(buffer, cancellation);
				sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
			} while (!result.EndOfMessage);

			if (result.MessageType == WebSocketMessageType.Close) break;

			try
			{
				var msg = JsonDocument.Parse(sb.ToString()).RootElement;
				if (!msg.TryGetProperty("method", out var method) || method.GetString() != "Network.requestWillBeSent") continue;
				if (!msg.TryGetProperty("params", out var prms) || !prms.TryGetProperty("request", out var req)) continue;
				if (!req.TryGetProperty("url", out var urlEl) || urlEl.GetString()?.Contains("ustrade.webullfinance.com") != true) continue;
				if (!req.TryGetProperty("headers", out var headers)) continue;

				foreach (var key in HeaderKeys)
				{
					if (headers.TryGetProperty(key, out var val))
					{
						var v = val.GetString();
						if (!string.IsNullOrEmpty(v)) captured[key] = v;
					}
				}

				// t_token is the trade token Webull mints only after a successful PIN unlock, and is exactly
				// what the trading endpoints require. Gating on it (not just access_token/x-s, which the locked
				// page already sends) prevents capturing a pre-unlock header set and closing the browser early.
				if (captured.ContainsKey("access_token") && captured.ContainsKey("x-s") && captured.ContainsKey("t_token"))
					return new Dictionary<string, string>(captured);
			}
			catch (JsonException) { }
		}

		throw new TimeoutException("WebSocket closed before headers were captured.");
	}
}
