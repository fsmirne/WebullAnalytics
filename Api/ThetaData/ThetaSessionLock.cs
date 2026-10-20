namespace WebullAnalytics.Api.ThetaData;

/// <summary>Cross-process guard for ThetaData's one-active-session-per-login rule: a newer sign-in silently ends the older
/// session, so an in-app quote pull during the nightly backfill would kill the backfill's session (it then fails with
/// UNAUTHENTICATED "Invalid session ID") — and vice versa. Every process that signs in holds <c>data/thetadata.session.lock</c>
/// open exclusively for as long as its session is in use; the Python backfill (scripts/backfill_thetadata.py) honors the same
/// file. Windows: FileShare.None makes any second open fail with a sharing violation; Unix: .NET maps FileShare.None to an
/// advisory flock, which the Python side takes with fcntl.flock.</summary>
internal sealed class ThetaSessionLock : IDisposable
{
	internal const string RelativePath = "data/thetadata.session.lock";

	private readonly FileStream _handle;

	private ThetaSessionLock(FileStream handle) => _handle = handle;

	/// <summary>The lock, or null when another process holds it (its session is live — signing in now would end it).</summary>
	internal static ThetaSessionLock? TryAcquire()
	{
		var path = Program.ResolvePath(RelativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		try { return new ThetaSessionLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
		catch (IOException) { return null; }
	}

	public void Dispose() => _handle.Dispose();
}
