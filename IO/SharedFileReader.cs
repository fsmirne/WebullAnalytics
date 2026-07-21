namespace WebullAnalytics.IO;

/// <summary>
/// Line readers that open with <c>FileShare.ReadWrite</c> so files a live process holds open for
/// append (proposal JSONL logs from `wa ai watch`, whose sinks share ReadWrite) can be read
/// concurrently. <c>File.ReadLines</c>/<c>ReadAllLines</c> open with <c>FileShare.Read</c>, which
/// denies write sharing and fails against an active writer's handle.
/// </summary>
internal static class SharedFileReader
{
	public static IEnumerable<string> ReadLines(string path)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using var reader = new StreamReader(stream);
		for (string? line; (line = reader.ReadLine()) != null;)
			yield return line;
	}

	public static string[] ReadAllLines(string path) => ReadLines(path).ToArray();
}
