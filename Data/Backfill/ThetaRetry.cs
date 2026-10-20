using System.Net.Sockets;
using Grpc.Core;
using WebullAnalytics.Api.ThetaData;

namespace WebullAnalytics.Data.Backfill;

/// <summary>Transient-failure retry around one ThetaData request (the Python backfill's <c>thetacall</c>): up to five attempts with
/// 2/4/6/8 s backoff on server/transport blips — 5xx and UNAVAILABLE, INTERNAL (a multi-hour server-side ProcessingError outage on
/// 2026-06-06 recovered on its own), dropped HTTP/2 streams, connection and DNS errors, and a call that stalled. Anything else, and
/// a lost session, propagates at once. A legitimately empty result is not an error here: the client yields no chunks for NOT_FOUND.</summary>
internal static class ThetaRetry
{
	private const int Attempts = 5;

	/// <summary>Runs <paramref name="attempt"/> until it completes or fails non-transiently. Each attempt must build its result from
	/// scratch: a retried request restarts its stream, so nothing from a failed attempt may survive into the next.</summary>
	internal static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> attempt, CancellationToken cancellation)
	{
		for (var i = 1; ; i++)
		{
			try
			{
				return await attempt(cancellation);
			}
			catch (Exception ex) when (i < Attempts && IsTransient(ex) && !cancellation.IsCancellationRequested)
			{
				await Task.Delay(TimeSpan.FromSeconds(2 * i), cancellation);
			}
		}
	}

	/// <summary>The whole response of a request, as one table.</summary>
	internal static Task<ThetaTable> CollectAsync(Func<CancellationToken, IAsyncEnumerable<ThetaTable>> request, CancellationToken cancellation) =>
		RunAsync(token => ThetaTable.CollectAsync(request(token)), cancellation);

	internal static bool IsTransient(Exception ex) => ex switch
	{
		ThetaStallException => true,
		RpcException rpc => rpc.StatusCode is StatusCode.Unavailable or StatusCode.Internal or StatusCode.DeadlineExceeded or StatusCode.Unknown,
		HttpRequestException or SocketException or IOException => true,
		_ => false
	};
}
