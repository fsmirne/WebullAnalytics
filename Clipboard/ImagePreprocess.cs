using SkiaSharp;

namespace WebullAnalytics.Clipboard;

/// <summary>Prepares a UI screenshot for Tesseract, which binarizes poorly on dark trading themes:
/// colored text (Webull's green Buy / red Sell) sits near mid-gray luminance and vanishes, and 1px
/// decimal points die in downscaled antialiasing ("21.5" reads as "215" — a wrong-strike hazard).
///
/// Three steps fix all of it: 3x upscale (decimal points become multi-pixel), brightest-channel
/// grayscale (max(R,G,B) keeps saturated red/green text as bright as white text, where luminance
/// weighting would sink red to ~30%), and inversion (dark text on white — the polarity Tesseract's
/// binarization expects).</summary>
internal static class ImagePreprocess
{
	private const int Scale = 3;

	public static byte[] ForOcr(byte[] imageBytes)
	{
		using var src = SKBitmap.Decode(imageBytes);
		if (src == null) return imageBytes;   // not decodable — let tesseract try the original

		using var scaled = new SKBitmap(src.Width * Scale, src.Height * Scale, SKColorType.Bgra8888, SKAlphaType.Opaque);
		src.ScalePixels(scaled, new SKSamplingOptions(SKCubicResampler.Mitchell));

		var pixels = scaled.Pixels;
		for (var i = 0; i < pixels.Length; i++)
		{
			var p = pixels[i];
			var bright = Math.Max(p.Red, Math.Max(p.Green, p.Blue));
			var inv = (byte)(255 - bright);
			pixels[i] = new SKColor(inv, inv, inv);
		}
		scaled.Pixels = pixels;

		using var img = SKImage.FromBitmap(scaled);
		using var png = img.Encode(SKEncodedImageFormat.Png, 100);
		return png.ToArray();
	}
}
