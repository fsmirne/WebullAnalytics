using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace WebullAnalytics;

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

			// Wait for the page to actually finish loading instead of a fixed delay.
			Console.WriteLine("Waiting for page to load...");
			await WaitForCdpEvent(ws, "Page.loadEventFired", TimeSpan.FromSeconds(30), cancellation);

			// Poll for the unlock link — SPAs may render asynchronously after load.
			Console.WriteLine("Clicking unlock link...");
			const string pollClickJs = """
				new Promise(resolve => {
					let attempts = 0;
					const iv = setInterval(() => {
						const a = Array.from(document.querySelectorAll('a')).find(a => a.textContent.trim().toLowerCase() === 'unlock');
						if (a) { clearInterval(iv); a.click(); resolve('clicked'); }
						else if (++attempts > 50) { clearInterval(iv); resolve('not_found'); }
					}, 100);
				})
				""";
			await CdpSend(ws, ++cmdId, "Runtime.evaluate", new { expression = pollClickJs, awaitPromise = true }, cancellation);
			await DrainUntilResult(ws, cmdId, TimeSpan.FromSeconds(10), cancellation);
			await Task.Delay(1500, cancellation);

			// Focus the first input in the PIN dialog and type the code
			Console.WriteLine("Entering unlock code...");
			await CdpSend(ws, ++cmdId, "Runtime.evaluate", new { expression = "document.querySelector('.modal input, [class*=dialog] input, [class*=unlock] input, input[type=password], input[type=tel]')?.focus()" }, cancellation);
			await DrainUntilResult(ws, cmdId, TimeSpan.FromSeconds(5), cancellation);
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

		string userDataDir;
		if (OperatingSystem.IsWindows())
			userDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data");
		else if (OperatingSystem.IsMacOS())
			userDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Microsoft Edge");
		else
			userDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "microsoft-edge");

		if (!Directory.Exists(userDataDir))
			throw new InvalidOperationException($"Edge user data directory not found: {userDataDir}");

		return new("msedge", binaryPath, new ProcessStartInfo
		{
			FileName = binaryPath,
			Arguments = $"--remote-debugging-port={CdpPort} --user-data-dir=\"{userDataDir}\" --profile-directory=Default https://app.webull.com/account",
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

				if (captured.ContainsKey("access_token") && captured.ContainsKey("x-s"))
					return new Dictionary<string, string>(captured);
			}
			catch (JsonException) { }
		}

		throw new TimeoutException("WebSocket closed before headers were captured.");
	}
}
