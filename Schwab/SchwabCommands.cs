using Spectre.Console;
using Spectre.Console.Cli;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using WebullAnalytics.Api;

namespace WebullAnalytics.Schwab;

/// <summary>Loads api-config.json and returns its <see cref="SchwabConfig"/>, erroring out with actionable
/// messages when the file or the Schwab credentials are missing.</summary>
internal static class SchwabConfigGate
{
	public static bool TryLoad(out ApiConfig config, out SchwabConfig schwab, out string configPath)
	{
		config = null!;
		schwab = null!;
		configPath = Program.ResolvePath(Program.ApiConfigPath);
		if (!File.Exists(configPath))
		{
			AnsiConsole.MarkupLine("[red]api-config.json not found[/] — run `wa sniff` to bootstrap it.");
			return false;
		}
		var loaded = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(configPath));
		if (loaded == null)
		{
			AnsiConsole.MarkupLine("[red]failed to parse api-config.json[/].");
			return false;
		}
		config = loaded;
		if (config.Schwab == null || string.IsNullOrWhiteSpace(config.Schwab.ClientId) || string.IsNullOrWhiteSpace(config.Schwab.ClientSecret) || string.IsNullOrWhiteSpace(config.Schwab.RedirectUri))
		{
			AnsiConsole.MarkupLine("[red]Schwab credentials missing[/] — add a \"schwab\" block with clientId, clientSecret, and redirectUri to api-config.json (from your app at developer.schwab.com).");
			return false;
		}
		schwab = config.Schwab;
		return true;
	}
}

internal sealed class SchwabLoginSettings : CommandSettings { }

/// <summary>Three-legged OAuth login: opens the authorize URL, captures the post-login redirect on a local loopback
/// listener (falling back to a pasted URL), exchanges the code for tokens, and stores the refresh token (7 days).</summary>
internal sealed class SchwabLoginCommand : AsyncCommand<SchwabLoginSettings>
{
	protected override async Task<int> ExecuteAsync(CommandContext context, SchwabLoginSettings settings, CancellationToken cancellation)
	{
		if (!SchwabConfigGate.TryLoad(out _, out var schwab, out var configPath)) return 1;

		var authorizeUrl = SchwabAuthClient.BuildAuthorizeUrl(schwab);
		AnsiConsole.MarkupLine("[bold]Schwab login[/] — a browser will open for you to log in and authorize.");
		AnsiConsole.MarkupLine("If it doesn't open, visit this URL manually:");
		AnsiConsole.WriteLine();
		AnsiConsole.WriteLine(authorizeUrl);
		AnsiConsole.WriteLine();
		TryOpenBrowser(authorizeUrl);

		string? code = null;
		try
		{
			AnsiConsole.MarkupLine($"Waiting for the redirect to [dim]{Markup.Escape(schwab.RedirectUri)}[/] …");
			AnsiConsole.MarkupLine("[yellow]Your browser will warn the connection isn't private (self-signed cert) — click Advanced → Proceed to continue.[/]");
			code = await SchwabRedirectListener.WaitForCodeAsync(schwab, TimeSpan.FromMinutes(5), cancellation);
		}
		catch (Exception ex) when (ex is SocketException or AuthenticationException or IOException)
		{
			// Port already in use, no permission to bind, or a TLS setup problem — the paste fallback still works.
			AnsiConsole.MarkupLine($"[yellow]Automatic capture unavailable ({Markup.Escape(ex.Message)}).[/]");
		}

		if (code == null)
		{
			AnsiConsole.MarkupLine("[dim]Falling back to manual entry.[/] Copy the [bold]entire URL[/] from the address bar and paste it here:");
			Console.Write("> ");
			var pasted = Console.ReadLine();
			if (string.IsNullOrWhiteSpace(pasted))
			{
				AnsiConsole.MarkupLine("[red]No URL entered.[/]");
				return 1;
			}
			code = SchwabAuthClient.ExtractCode(pasted);
			if (code == null)
			{
				AnsiConsole.MarkupLine("[red]Could not find a 'code' parameter in that URL.[/] Paste the full redirect URL including '?code=...'.");
				return 1;
			}
		}

		try
		{
			await SchwabAuthClient.ExchangeCodeAsync(schwab, code, configPath, cancellation);
		}
		catch (SchwabAuthException ex)
		{
			AnsiConsole.MarkupLine($"[red]Token exchange failed:[/] {Markup.Escape(ex.Message)}");
			return 1;
		}

		AnsiConsole.MarkupLine("[green]Logged in.[/] Refresh token stored — valid for [bold]7 days[/]. Re-run `wa schwab login` weekly.");
		return 0;
	}

	/// <summary>Best-effort open of the authorize URL in the default browser; the URL is printed too, so a failure
	/// here just means the user clicks it themselves.</summary>
	private static void TryOpenBrowser(string url)
	{
		try
		{
			if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
			else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
			else Process.Start("xdg-open", url);
		}
		catch { /* fall back to the printed URL */ }
	}
}

internal sealed class SchwabStatusSettings : CommandSettings { }

/// <summary>Reports whether tokens are present and how long until the access/refresh tokens expire.</summary>
internal sealed class SchwabStatusCommand : AsyncCommand<SchwabStatusSettings>
{
	protected override Task<int> ExecuteAsync(CommandContext context, SchwabStatusSettings settings, CancellationToken cancellation)
	{
		if (!SchwabConfigGate.TryLoad(out _, out var schwab, out _)) return Task.FromResult(1);

		if (string.IsNullOrEmpty(schwab.RefreshToken))
		{
			AnsiConsole.MarkupLine("[yellow]Not logged in.[/] Run `wa schwab login`.");
			return Task.FromResult(0);
		}

		var days = SchwabAuthClient.RefreshTokenDaysRemaining(schwab);
		var refreshMsg = days is { } d
			? (d <= 0 ? "[red]EXPIRED — run `wa schwab login`[/]" : d < 1 ? $"[red]{d * 24:F1}h left — re-login soon[/]" : $"[green]{d:F1} days left[/]")
			: "[dim]unknown (no issued timestamp)[/]";
		var accessMsg = schwab.AccessTokenExpiresUtc is { } exp
			? (exp <= DateTime.UtcNow ? "[dim]expired (auto-refreshes on next use)[/]" : $"{(exp - DateTime.UtcNow).TotalMinutes:F0} min left")
			: "[dim]none cached[/]";

		AnsiConsole.MarkupLine($"Refresh token: {refreshMsg}");
		AnsiConsole.MarkupLine($"Access token:  {accessMsg}");
		return Task.FromResult(0);
	}
}
