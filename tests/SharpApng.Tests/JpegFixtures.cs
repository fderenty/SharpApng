using System.Diagnostics;

namespace SharpApng.Tests;

/// <summary>
/// Generates JPEG test files with Pillow, alongside a PNG of Pillow's own decoding of each one.
/// </summary>
/// <remarks>
/// <para>
/// Comparing against an independent decoder is the point: checking the decoder against itself
/// would prove nothing. Pillow writes the JPEG and also writes what it decodes back out as a
/// PNG, which this library's already-tested PNG decoder then reads. Nothing here compares
/// encoded bytes to a stored reference file.
/// </para>
/// <para>
/// Fixtures are generated rather than committed, so the suite carries no binary blobs. When
/// Python or Pillow is missing the JPEG tests report as inconclusive instead of failing, since
/// their absence says nothing about the decoder.
/// </para>
/// </remarks>
internal static class JpegFixtures
{
    private static readonly Lock Gate = new();
    private static string? _directory;
    private static string? _unavailableReason;

    /// <summary>The folder holding the generated fixtures, or null when Pillow is unavailable.</summary>
    public static string? Directory
    {
        get
        {
            lock (Gate)
            {
                if (_directory is null && _unavailableReason is null)
                    Generate();

                return _directory;
            }
        }
    }

    /// <summary>Why the fixtures could not be generated, when they could not.</summary>
    public static string UnavailableReason => _unavailableReason ?? "unknown";

    /// <summary>Skips the calling test when the fixtures are unavailable, returning the folder otherwise.</summary>
    public static string RequireDirectory()
    {
        string? directory = Directory;
        if (directory is null)
            Assert.Inconclusive($"JPEG fixtures unavailable: {UnavailableReason}");

        return directory!;
    }

    /// <summary>Loads a fixture's JPEG bytes.</summary>
    public static byte[] Jpeg(string name) => File.ReadAllBytes(Path.Combine(RequireDirectory(), name + ".jpg"));

    /// <summary>Loads Pillow's own decoding of a fixture, as a PNG.</summary>
    public static RgbaImage Reference(string name)
        => PngCodec.DecodeFile(Path.Combine(RequireDirectory(), name + ".ref.png"));

    private static void Generate()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sharpapng-jpeg-fixtures");

        try
        {
            System.IO.Directory.CreateDirectory(directory);
            string script = Path.Combine(directory, "generate.py");
            File.WriteAllText(script, GeneratorScript);

            foreach (string executable in new[] { "python", "python3", "py" })
            {
                var info = new ProcessStartInfo(executable, [script, directory])
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };

                try
                {
                    using Process? process = Process.Start(info);
                    if (process is null)
                        continue;

                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit(120_000);

                    if (process.ExitCode == 0)
                    {
                        _directory = directory;
                        return;
                    }

                    _unavailableReason = $"{executable} exited {process.ExitCode}: {error.Trim()}";
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
                {
                    // Try the next candidate name.
                }
            }

            _unavailableReason ??= "no Python interpreter with Pillow was found on PATH";
        }
        catch (Exception ex)
        {
            _unavailableReason = ex.Message;
        }
    }

    private const string GeneratorScript = """
        import sys
        from pathlib import Path

        try:
            from PIL import Image
        except ImportError:
            sys.exit("Pillow is not installed")

        out = Path(sys.argv[1])
        out.mkdir(parents=True, exist_ok=True)


        def photo(w, h, mode="RGB"):
            "Smooth gradients plus hard edges: the DCT has something to do either way."
            im = Image.new(mode, (w, h))
            px = im.load()
            for y in range(h):
                for x in range(w):
                    r = (x * 255) // max(1, w - 1)
                    g = (y * 255) // max(1, h - 1)
                    b = ((x + y) * 255) // max(1, w + h - 2)
                    if (x // 8 + y // 8) % 5 == 0:
                        r, g, b = 250, 20, 40
                    px[x, y] = (r, g, b) if mode == "RGB" else (r,)
            return im


        def noisy(w, h):
            "Deterministic pseudo-noise: every block compresses to a different length, which is"
            "what makes restart-marker alignment actually get exercised."
            im = Image.new("RGB", (w, h))
            px = im.load()
            seed = 12345
            for y in range(h):
                for x in range(w):
                    seed = (seed * 1103515245 + 12345) & 0x7FFFFFFF
                    px[x, y] = ((seed >> 16) & 255, (seed >> 8) & 255, seed & 255)
            return im


        def save(name, im, **kwargs):
            jpg = out / f"{name}.jpg"
            im.save(jpg, "JPEG", **kwargs)
            with Image.open(jpg) as decoded:
                decoded.convert("RGB").save(out / f"{name}.ref.png")


        save("baseline444", photo(64, 48), quality=92, subsampling=0)
        save("baseline422", photo(64, 48), quality=92, subsampling=1)
        save("baseline420", photo(64, 48), quality=92, subsampling=2)
        save("greyscale", photo(48, 32, "L"), quality=92)
        save("progressive", photo(64, 48), quality=92, progressive=True, subsampling=2)
        save("progressive444", photo(48, 48), quality=90, progressive=True, subsampling=0)
        save("restart", photo(64, 64), quality=90, restart_marker_blocks=4, subsampling=2)
        # Noise plus a restart after every single MCU. Smooth test images leave the bit reader
        # landing on the marker by luck; noise makes every interval a different length, so the
        # search for the marker has to be right rather than fortunate.
        save("restart_dense", noisy(160, 128), quality=95, restart_marker_blocks=1, subsampling=2)
        save("restart_noisy444", noisy(96, 96), quality=92, restart_marker_blocks=3, subsampling=0)
        save("odd_size", photo(37, 23), quality=90, subsampling=2)
        save("tiny1x1", photo(1, 1), quality=90)
        save("tiny3x5", photo(3, 5), quality=90)
        save("lowquality", photo(64, 48), quality=15, subsampling=2)
        save("highquality", photo(64, 48), quality=100, subsampling=0)
        save("large", photo(200, 150), quality=85, subsampling=2)

        # Every EXIF orientation, on an image whose corners are distinguishable.
        base = Image.new("RGB", (16, 8), (30, 30, 30))
        for x in range(6):
            for y in range(3):
                base.putpixel((x, y), (255, 0, 0))
        for orientation in range(1, 9):
            exif = base.getexif()
            exif[0x0112] = orientation
            name = f"orient{orientation}"
            jpg = out / f"{name}.jpg"
            base.save(jpg, "JPEG", quality=95, exif=exif)
            with Image.open(jpg) as decoded:
                decoded.convert("RGB").save(out / f"{name}.ref.png")

        # An image with no EXIF at all, to confirm the default path.
        save("noexif", photo(16, 16), quality=95)

        print("ok")
        """;
}
