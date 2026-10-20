using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using WebullAnalytics.Api.ThetaData.Proto;
using WebullAnalytics.Api.ThetaData.Proto.V3;

namespace WebullAnalytics.Api.ThetaData;

/// <summary>Thrown when ThetaData's single session is unavailable: another process holds <see cref="ThetaSessionLock"/>, or the
/// server ended our session (UNAUTHENTICATED — a sign-in elsewhere with the same login).</summary>
internal sealed class ThetaSessionException(string message) : Exception(message);

/// <summary>Direct gRPC client for ThetaData's market-data service — a port of the Python <c>thetadata</c> library (Apache 2.0)
/// pinned to <see cref="LibraryVersion"/>. ThetaData confirmed in writing (2026-10-20) that calling these endpoints from our own
/// client with our own credentials is permitted for our own use, UNSUPPORTED: there's no published spec and the protocol can
/// change with the library, so the protos are regenerated from the library by scripts/thetadata_proto_extract.py and every
/// request mirrors what that library version sends.
///
/// One instance = one ThetaData session. <see cref="ConnectAsync"/> takes <see cref="ThetaSessionLock"/> first and signs in only
/// if it's free; dispose to release both. Concurrent calls on one instance share the session (the library's
/// existing_authorized_client pattern) and are capped at the tier's in-flight limit. Every call carries a deadline: the library's
/// calls can hang with no error, which the Python backfill only escapes by killing its worker process.</summary>
internal sealed class ThetaDataClient : IDisposable
{
	/// <summary>The thetadata library version the protos and request shapes were taken from.</summary>
	internal const string LibraryVersion = "1.0.15";

	private const string AuthUrl = "https://nexus-api.thetadata.us/identity/terminal/auth_user";
	// The library sends this fixed key on every sign-in; it identifies the client software, not the account.
	private const string TerminalKey = "cf58ada4-4175-11f0-860f-1e2e95c79e64";
	private const string ServiceAddress = "https://mdds-01.thetadata.us:443";
	private const string ClientName = "csharp";

	// Price.value is a scaled int; Price.type indexes the scale. Same table and same double multiply as the library, so decoded
	// prices are bit-identical to what the Python backfill stored.
	private static readonly double[] PriceTypeFactors = [0.0, 0.000000001, 0.00000001, 0.0000001, 0.000001, 0.00001, 0.0001, 0.001, 0.01, 0.1, 1.0, 10.0, 100.0, 1000.0, 10000.0, 100000.0, 1000000.0, 10000000.0, 100000000.0, 1000000000.0];
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
	private static readonly HttpClient AuthHttp = new();

	private readonly ThetaSessionLock _lock;
	private readonly GrpcChannel _channel;
	private readonly BetaThetaTerminal.BetaThetaTerminalClient _stub;
	private readonly AuthToken _authToken;
	private readonly string _email;
	private readonly SemaphoreSlim _inFlight;
	private readonly TimeSpan _callTimeout;

	private ThetaDataClient(ThetaSessionLock sessionLock, string sessionId, string email, int maxConcurrent, TimeSpan callTimeout)
	{
		_lock = sessionLock;
		_authToken = new AuthToken { SessionUuid = sessionId };
		_email = email;
		_inFlight = new SemaphoreSlim(maxConcurrent, maxConcurrent);
		_callTimeout = callTimeout;
		_channel = GrpcChannel.ForAddress(ServiceAddress);
		_stub = new BetaThetaTerminal.BetaThetaTerminalClient(_channel);
	}

	/// <summary>The creds file the Python backfill uses: THETADATA_CREDENTIALS_FILE, else data/creds.txt (email line 1, password line 2).</summary>
	internal static string DefaultCredsPath() => Environment.GetEnvironmentVariable("THETADATA_CREDENTIALS_FILE") is { Length: > 0 } path ? path : Program.ResolvePath("data/creds.txt");

	/// <param name="maxConcurrent">Requests in flight at once — the subscription tier's limit (Value 2, Standard 4, Pro 8).</param>
	internal static async Task<ThetaDataClient> ConnectAsync(string credsPath, int maxConcurrent, TimeSpan callTimeout, CancellationToken cancellation)
	{
		var lines = File.Exists(credsPath) ? await File.ReadAllLinesAsync(credsPath, cancellation) : [];
		if (lines.Length < 2 || string.IsNullOrWhiteSpace(lines[0]) || string.IsNullOrWhiteSpace(lines[1]))
			throw new ThetaSessionException($"ThetaData credentials not found at {credsPath} (expected email on line 1, password on line 2)");

		var sessionLock = ThetaSessionLock.TryAcquire() ?? throw new ThetaSessionException($"ThetaData session is in use by another process (the backfill?) — {Program.ResolvePath(ThetaSessionLock.RelativePath)} is held; signing in now would end its session");
		try
		{
			var request = new { email = lines[0].Trim(), password = lines[1].Trim(), authEnv = new { envType = "PROD" } };
			using var message = new HttpRequestMessage(HttpMethod.Post, AuthUrl) { Content = JsonContent.Create(request) };
			message.Headers.Add("TD-TERMINAL-KEY", TerminalKey);
			using var response = await AuthHttp.SendAsync(message, cancellation);
			var body = await response.Content.ReadAsStringAsync(cancellation);
			if (!response.IsSuccessStatusCode)
				throw new ThetaSessionException($"ThetaData sign-in failed ({(int)response.StatusCode}): {body}");
			using var json = JsonDocument.Parse(body);
			var sessionId = json.RootElement.GetProperty("sessionId").GetString()!;
			var email = json.RootElement.GetProperty("user").GetProperty("email").GetString()!;
			return new ThetaDataClient(sessionLock, sessionId, email, maxConcurrent, callTimeout);
		}
		catch
		{
			sessionLock.Dispose();
			throw;
		}
	}

	/// <summary>option_list_expirations: every listed expiration of a root (columns symbol, expiration).</summary>
	internal Task<ThetaTable> OptionListExpirationsAsync(string symbol, CancellationToken cancellation)
	{
		var query = new OptionListExpirationsRequestQuery();
		query.Symbol.Add(symbol);
		var request = new OptionListExpirationsRequest { QueryInfo = Info(("symbol", symbol)), Params = query };
		return RunAsync(options => _stub.GetOptionListExpirations(request, options), cancellation);
	}

	/// <summary>option_history_quote: NBBO per <paramref name="interval"/> (e.g. "1m") for one expiration on one date, between start and end time of day ET.</summary>
	internal Task<ThetaTable> OptionHistoryQuoteAsync(OptionContract contract, string interval, DateOnly date, TimeOnly startTime, TimeOnly endTime, CancellationToken cancellation)
	{
		var query = new OptionHistoryQuoteRequestQuery { ContractSpec = contract.Spec(), Interval = interval, Date = Date(date), StartTime = Time(startTime), EndTime = Time(endTime) };
		var request = new OptionHistoryQuoteRequest { QueryInfo = Info([.. contract.Parameters(), ("interval", interval), ("date", Date(date)), ("start_time", Time(startTime)), ("end_time", Time(endTime))]), Params = query };
		return RunAsync(options => _stub.GetOptionHistoryQuote(request, options), cancellation);
	}

	/// <summary>option_at_time_quote: the last NBBO at <paramref name="timeOfDay"/> ET on each date in [start, end]. ThetaData warns
	/// that sub-minute times can make the request hang; pass minute boundaries.</summary>
	internal Task<ThetaTable> OptionAtTimeQuoteAsync(OptionContract contract, DateOnly startDate, DateOnly endDate, TimeOnly timeOfDay, CancellationToken cancellation)
	{
		var query = new OptionAtTimeQuoteRequestQuery { ContractSpec = contract.Spec(), StartDate = Date(startDate), EndDate = Date(endDate), TimeOfDay = Time(timeOfDay) };
		var request = new OptionAtTimeQuoteRequest { QueryInfo = Info([.. contract.Parameters(), ("start_date", Date(startDate)), ("end_date", Date(endDate)), ("time_of_day", Time(timeOfDay))]), Params = query };
		return RunAsync(options => _stub.GetOptionAtTimeQuote(request, options), cancellation);
	}

	/// <summary>option_snapshot_quote: the latest NBBO.</summary>
	internal Task<ThetaTable> OptionSnapshotQuoteAsync(OptionContract contract, CancellationToken cancellation)
	{
		var request = new OptionSnapshotQuoteRequest { QueryInfo = Info(contract.Parameters()), Params = new OptionSnapshotQuoteRequestQuery { ContractSpec = contract.Spec() } };
		return RunAsync(options => _stub.GetOptionSnapshotQuote(request, options), cancellation);
	}

	// The library's _fmt_if_date / _fmt_time formats.
	private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
	private static string Time(TimeOnly time) => time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

	/// <summary>QueryInfo as the library builds it: the session token, the account email as a hint, and the call's parameters echoed
	/// as strings (server-side request logging) under a "client" tag naming the caller.</summary>
	private QueryInfo Info(params (string Key, string Value)[] parameters)
	{
		var info = new QueryInfo { AuthToken = _authToken, EmailHint = _email };
		info.QueryParameters.Add("client", ClientName);
		foreach (var (key, value) in parameters)
			info.QueryParameters[key] = value;
		return info;
	}

	/// <summary>Runs one server-streaming call under the in-flight cap and deadline and decodes every streamed chunk into one table.
	/// NOT_FOUND is ThetaData's "no data for this request" (the library's NoDataFoundError) and returns an empty table.</summary>
	private async Task<ThetaTable> RunAsync(Func<CallOptions, AsyncServerStreamingCall<ResponseData>> start, CancellationToken cancellation)
	{
		await _inFlight.WaitAsync(cancellation);
		try
		{
			using var call = start(new CallOptions(deadline: DateTime.UtcNow + _callTimeout, cancellationToken: cancellation));
			IReadOnlyList<string> headers = [];
			var rows = new List<object?[]>();
			await foreach (var chunk in call.ResponseStream.ReadAllAsync(cancellation))
			{
				var table = DecodeChunk(chunk);
				headers = table.Headers;
				rows.AddRange(table.Rows);
			}
			return new ThetaTable(headers, rows);
		}
		catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
		{
			return ThetaTable.Empty;
		}
		catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
		{
			throw new ThetaSessionException($"ThetaData ended this session ({ex.Status.Detail}) — another sign-in with the same login replaced it");
		}
		finally
		{
			_inFlight.Release();
		}
	}

	/// <summary>One streamed chunk -> its table (the library's per-chunk step of _convert_response_stream).</summary>
	internal static ThetaTable DecodeChunk(ResponseData chunk)
	{
		var table = DataTable.Parser.ParseFrom(Decompress(chunk));
		return new ThetaTable(table.Headers, [.. table.DataTable_.Select(row => row.Values.Select(Decode).ToArray())]);
	}

	private static byte[] Decompress(ResponseData chunk) => chunk.CompressionDescription?.Algo switch
	{
		null or CompressionAlgo.None => chunk.CompressedData.ToByteArray(),
		CompressionAlgo.Zstd => new ZstdSharp.Decompressor().Unwrap(chunk.CompressedData.Span).ToArray(),
		var algo => throw new NotSupportedException($"ThetaData response compression {algo}")
	};

	private static object? Decode(DataValue value) => value.DataTypeCase switch
	{
		DataValue.DataTypeOneofCase.Text => value.Text,
		DataValue.DataTypeOneofCase.Number => value.Number,
		DataValue.DataTypeOneofCase.Price => value.Price.Type != 0 ? value.Price.Value * PriceTypeFactors[value.Price.Type] : double.NaN,
		DataValue.DataTypeOneofCase.Boolean => value.Boolean,
		DataValue.DataTypeOneofCase.Timestamp => DecodeTimestamp(value.Timestamp),
		_ => null
	};

	private static DateTimeOffset DecodeTimestamp(ZonedDateTime timestamp)
	{
		var utc = DateTimeOffset.FromUnixTimeMilliseconds((long)timestamp.EpochMs);
		return timestamp.Zone == Proto.TimeZone.NewYork ? TimeZoneInfo.ConvertTime(utc, NyTz) : utc;
	}

	public void Dispose()
	{
		_channel.Dispose();
		_inFlight.Dispose();
		_lock.Dispose();
	}
}

/// <summary>A ThetaData contract selector: root + expiration, and optionally one strike and one side. Null strike = every strike
/// ("*"), null right = both sides — the library's defaults.</summary>
internal sealed record OptionContract(string Symbol, DateOnly Expiration, decimal? Strike = null, bool? IsCall = null)
{
	/// <summary>One contract from an OCC symbol (SPY261021C00670000), or null when it isn't one.</summary>
	internal static OptionContract? FromOcc(string occ) => ParsingHelpers.ParseOptionSymbol(occ) is { } p ? new(p.Root, DateOnly.FromDateTime(p.ExpiryDate), p.Strike, p.CallPut == "C") : null;

	private string ExpirationText => Expiration.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
	private string StrikeText => Strike?.ToString(CultureInfo.InvariantCulture) ?? "*";
	private string RightText => IsCall switch { true => "call", false => "put", null => "both" };

	internal ContractSpec Spec() => new() { Symbol = Symbol, Expiration = ExpirationText, Strike = StrikeText, Right = RightText };

	internal (string, string)[] Parameters() => [("symbol", Symbol), ("expiration", ExpirationText), ("strike", StrikeText), ("right", RightText)];
}
