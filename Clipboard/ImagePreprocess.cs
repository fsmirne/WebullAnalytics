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
	public static byte[] ForOcr(byte[] imageBytes, int scale = 3, bool greenChannel = false, bool removeLines = true)
	{
		using var src = SKBitmap.Decode(imageBytes);
		if (src == null) return imageBytes;   // not decodable — let tesseract try the original

		using var scaled = new SKBitmap(src.Width * scale, src.Height * scale, SKColorType.Bgra8888, SKAlphaType.Opaque);
		src.ScalePixels(scaled, new SKSamplingOptions(SKCubicResampler.Mitchell));

		var pixels = scaled.Pixels;
		for (var i = 0; i < pixels.Length; i++)
		{
			var p = pixels[i];
			// greenChannel: ClearType puts the glyph's luminance backbone in G with red/blue fringes whose phase
			// shifts with window position — max-channel amplifies the fringes (why visually identical snips OCR'd
			// differently), while G alone is fringe-free. Its blind spot is RED text (dark in G); the max-channel
			// passes cover that, so the ensemble runs both.
			var bright = greenChannel ? p.Green : Math.Max(p.Red, Math.Max(p.Green, p.Blue));
			// HARD binarize instead of soft grayscale+invert: Webull's alternating row tints survive soft
			// inversion as shaded stripes, and Tesseract's layout analysis then discards whole tinted rows as
			// graphic regions BEFORE recognition (verified: perfect glyph contrast, zero words emitted). We
			// know what Tesseract can't: ticket text is ~246 bright in its channel, every background ≤ ~70 —
			// threshold at 140 yields pure black-on-white with no stripes left to misclassify.
			var inv = bright > 140 ? (byte)0 : (byte)255;
			pixels[i] = new SKColor(inv, inv, inv);
		}
		// Erase table grid lines: Webull outlines buy-side rows with SATURATED GREEN cell borders that stay
		// bright in every channel, so inversion turns them into heavy black boxes around exactly those rows —
		// and Tesseract's line segmentation shreds text enclosed by touching grid lines (verified via
		// tessedit_write_images: the glyphs binarize perfectly, the borders do too, recognition still fails).
		// A dark run far longer than any glyph stroke is structurally a line, never text: erase it.
		var w = scaled.Width;
		var h = scaled.Height;
		if (removeLines)
		{
		var vertThreshold = 25 * scale;    // glyph strokes reach ~15px pre-scale; row-height borders ~35px
		var horizThreshold = 60 * scale;   // no glyph is 60px wide pre-scale; row separators span the image
		var white = new SKColor(255, 255, 255);
		for (var x = 0; x < w; x++)
		{
			var run = 0;
			for (var y = 0; y <= h; y++)
			{
				if (y < h && pixels[y * w + x].Red < 128) { run++; continue; }
				if (run > vertThreshold) for (var k = y - run; k < y; k++) pixels[k * w + x] = white;
				run = 0;
			}
		}
		for (var y = 0; y < h; y++)
		{
			var run = 0;
			for (var x = 0; x <= w; x++)
			{
				if (x < w && pixels[y * w + x].Red < 128) { run++; continue; }
				if (run > horizThreshold) for (var k = x - run; k < x; k++) pixels[y * w + k] = white;
				run = 0;
			}
		}
		}
		scaled.Pixels = pixels;

		// NOTE: padding the image was tried (text flush against the snip's bottom edge degrades the last leg
		// row) and REVERTED: any margin — white or background-matched — destabilized other rows' segmentation
		// more than it helped. Edge-row casualties are instead recovered by the parser's multi-pass merge and
		// single-leg reconstruction.

		using var img = SKImage.FromBitmap(scaled);
		using var png = img.Encode(SKEncodedImageFormat.Png, 100);
		return png.ToArray();
	}
}
