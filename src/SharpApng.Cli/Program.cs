using System.Globalization;
using System.IO.Compression;
using SharpApng;

namespace SharpApng.Cli;

/// <summary>
/// Command line front end: build an APNG from images, inspect one, or split one back apart.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "create" => Create(args[1..]),
                "convert" => Convert(args[1..]),
                "resize" => Resize(args[1..]),
                "info" => Info(args[1..]),
                "extract" => Extract(args[1..]),
                _ => Fail($"Unknown command '{args[0]}'. Run 'sharpapng --help' for usage."),
            };
        }
        catch (Exception ex) when (ex is PngFormatException or BmpFormatException or GifFormatException
                                      or IOException or NotSupportedException or ArgumentException)
        {
            return Fail(ex.Message);
        }
    }

    private static int Convert(string[] args)
    {
        string? input = null;
        string? output = null;
        var options = new GifReaderOptions();
        int loopOverride = -1;
        CompressionLevel level = CompressionLevel.Optimal;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" or "--output":
                    output = RequireValue(args, ref i);
                    break;

                case "-t" or "--transparent":
                {
                    string value = RequireValue(args, ref i);
                    if (value.Equals("background", StringComparison.OrdinalIgnoreCase))
                        options.TreatBackgroundAsTransparent = true;
                    else
                        options.TransparentColor = ParseColor(value);
                    break;
                }

                case "--tolerance":
                    options.ColorTolerance = ParseInt(RequireValue(args, ref i), "--tolerance");
                    break;

                case "--literal-delays":
                    options.ClampShortDelays = false;
                    break;

                case "-l" or "--loop":
                    loopOverride = ParseInt(RequireValue(args, ref i), "--loop");
                    break;

                case "--fast":
                    level = CompressionLevel.Fastest;
                    break;

                case "--smallest":
                    level = CompressionLevel.SmallestSize;
                    break;

                default:
                    if (input is null)
                        input = args[i];
                    else
                        return Fail($"Unexpected argument '{args[i]}'. convert takes a single input file.");
                    break;
            }
        }

        if (input is null)
            return Fail("convert needs an input file.");

        output ??= Path.ChangeExtension(input, ".png");

        AnimationDocument document = AnimationLoader.LoadFile(input, options);
        if (loopOverride >= 0)
            document.LoopCount = loopOverride;

        ApngWriter.WriteFile(document, output, new ApngWriterOptions { CompressionLevel = level });

        var before = new FileInfo(input);
        var after = new FileInfo(output);
        double change = (double)after.Length / before.Length;

        Console.WriteLine(
            $"Wrote {output}: {document.Frames.Count} frames, {document.Width}x{document.Height}, " +
            $"{document.TotalDurationMilliseconds:N0} ms per loop.");
        Console.WriteLine($"  {before.Length:N0} bytes in, {after.Length:N0} bytes out ({change:P0} of the original).");
        return 0;
    }

    private static Rgba32 ParseColor(string value)
    {
        string text = value.TrimStart('#');

        if (text.Length == 3)
            text = string.Concat(text[0], text[0], text[1], text[1], text[2], text[2]);

        if (text.Length != 6 || !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            throw new ArgumentException($"'{value}' is not a colour. Use #RRGGBB, #RGB, or the word 'background'.");

        return new Rgba32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private static int Create(string[] args)
    {
        var inputs = new List<string>();
        string? output = null;
        int delay = 100;
        int loop = 0;
        bool optimize = true;
        bool sixteenBit = false;
        string? posterPath = null;
        CompressionLevel level = CompressionLevel.Optimal;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" or "--output":
                    output = RequireValue(args, ref i);
                    break;

                case "-p" or "--poster":
                    posterPath = RequireValue(args, ref i);
                    break;

                case "-d" or "--delay":
                    delay = ParseInt(RequireValue(args, ref i), "--delay");
                    break;

                case "-l" or "--loop":
                    loop = ParseInt(RequireValue(args, ref i), "--loop");
                    break;

                case "--no-optimize":
                    optimize = false;
                    break;

                case "--16-bit":
                    sixteenBit = true;
                    break;

                case "--fast":
                    level = CompressionLevel.Fastest;
                    break;

                case "--smallest":
                    level = CompressionLevel.SmallestSize;
                    break;

                default:
                    inputs.Add(args[i]);
                    break;
            }
        }

        if (output is null)
            return Fail("create needs an output path: --output result.png");
        if (inputs.Count == 0)
            return Fail("create needs at least one input image.");

        PixelFormat format = sixteenBit ? PixelFormat.Rgba64 : PixelFormat.Rgba32;
        RgbaImage first = ImageLoader.LoadFile(inputs[0]).ConvertTo(format);

        // Validate the poster before creating the output, so a bad one leaves no partial file.
        RgbaImage? poster = null;
        if (posterPath is not null)
        {
            poster = ImageLoader.LoadFile(posterPath).ConvertTo(format);
            if (poster.Width != first.Width || poster.Height != first.Height)
            {
                return Fail(
                    $"'{posterPath}' is {poster.Width}x{poster.Height} but the animation is " +
                    $"{first.Width}x{first.Height}. The poster must match the canvas.");
            }
        }

        var options = new ApngWriterOptions
        {
            LoopCount = loop,
            OptimizeFrames = optimize,
            CompressionLevel = level,
        };

        using (FileStream file = File.Create(output))
        using (var writer = new ApngWriter(file, first.Width, first.Height, format, options))
        {
            if (poster is not null)
                writer.SetPosterImage(poster);

            writer.AddFrame(first, delay);

            for (int i = 1; i < inputs.Count; i++)
            {
                RgbaImage image = ImageLoader.LoadFile(inputs[i]).ConvertTo(format);
                if (image.Width != first.Width || image.Height != first.Height)
                {
                    return Fail(
                        $"'{inputs[i]}' is {image.Width}x{image.Height} but the animation is " +
                        $"{first.Width}x{first.Height}. Every frame must match the canvas.");
                }

                writer.AddFrame(image, delay);
            }

            writer.Complete();
        }

        var info = new FileInfo(output);
        Console.WriteLine(
            $"Wrote {output}: {inputs.Count} frames{(poster is null ? "" : " plus a separate poster")}, " +
            $"{first.Width}x{first.Height}, {format}, {info.Length:N0} bytes.");
        return 0;
    }

    private static int Resize(string[] args)
    {
        string? input = null;
        string? output = null;
        int size = 128;
        int width = 0;
        int height = 0;
        ResampleFilter filter = ResampleFilter.Box;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" or "--output":
                    output = RequireValue(args, ref i);
                    break;

                case "-s" or "--size":
                    size = ParseInt(RequireValue(args, ref i), "--size");
                    break;

                case "-w" or "--width":
                    width = ParseInt(RequireValue(args, ref i), "--width");
                    break;

                case "-h" or "--height":
                    height = ParseInt(RequireValue(args, ref i), "--height");
                    break;

                case "-f" or "--filter":
                {
                    string value = RequireValue(args, ref i);
                    if (!Enum.TryParse(value, ignoreCase: true, out filter))
                        return Fail($"Unknown filter '{value}'. Use box, triangle or lanczos3.");
                    break;
                }

                default:
                    if (input is null)
                        input = args[i];
                    else
                        return Fail($"Unexpected argument '{args[i]}'. resize takes a single input file.");
                    break;
            }
        }

        if (input is null)
            return Fail("resize needs an input file.");

        output ??= Path.ChangeExtension(input, ".thumb.png");

        RgbaImage source = ImageLoader.LoadFile(input);
        RgbaImage result = width > 0 && height > 0
            ? RgbaImageOps.Resize(source, width, height, filter)
            : RgbaImageOps.ResizeToFit(source, size, filter);

        PngCodec.EncodeFile(result, output);

        Console.WriteLine(
            $"Wrote {output}: {source.Width}x{source.Height} -> {result.Width}x{result.Height} " +
            $"({filter}, {new FileInfo(output).Length:N0} bytes).");
        return 0;
    }

    private static int Info(string[] args)
    {
        if (args.Length == 0)
            return Fail("info needs a file path.");

        byte[] data = File.ReadAllBytes(args[0]);
        string container = ImageLoader.IsPng(data) ? "PNG"
            : GifReader.IsGif(data) ? "GIF"
            : ImageLoader.IsBmp(data) ? "BMP"
            : "unknown";

        // Detected from the data, not the extension, so a mislabelled file still reports honestly.
        bool animated = container switch
        {
            "PNG" => ApngReader.IsAnimated(data),
            "GIF" => true,
            _ => false,
        };

        AnimationDocument document = AnimationLoader.Load(data);

        Console.WriteLine($"File       : {args[0]}");
        Console.WriteLine($"Container  : {container}");
        Console.WriteLine($"Animated   : {(animated ? "yes" : "no")}");
        Console.WriteLine($"Canvas     : {document.Width}x{document.Height}");
        Console.WriteLine($"Format     : {document.Format}");
        Console.WriteLine($"Frames     : {document.Frames.Count}");

        if (animated)
        {
            Console.WriteLine($"Loops      : {(document.LoopCount == 0 ? "infinite" : document.LoopCount.ToString(CultureInfo.InvariantCulture))}");
            Console.WriteLine($"Duration   : {document.TotalDurationMilliseconds:N0} ms per loop");
        }

        if (container == "PNG" && animated)
            Console.WriteLine($"Poster     : {(document.PosterImage is null ? "none (frame 1 is the still fallback)" : "separate still image")}");

        if (container == "PNG" && !animated)
        {
            // The usual cause of a flattened APNG is an image pipeline that re-encoded it and
            // dropped the chunks it did not recognise. Nothing errors when that happens, so say
            // it out loud rather than leaving a bare "Frames: 1".
            Console.WriteLine();
            Console.WriteLine("This PNG carries no acTL chunk, so it is a still image.");
            Console.WriteLine("If it was animated when you built it, something re-encoded it in between:");
            Console.WriteLine("a CDN image optimiser, a CMS thumbnail step, or a build task such as");
            Console.WriteLine("imagemin or pngquant will silently discard acTL, fcTL and fdAT.");
            return 0;
        }

        Console.WriteLine();

        int index = 0;
        foreach (AnimationFrame frame in document.Frames)
        {
            Console.WriteLine(
                $"  [{index++,3}] {frame.Image.Width,5}x{frame.Image.Height,-5} " +
                $"at ({frame.X,5},{frame.Y,5})  {frame.DelayMilliseconds,6} ms  " +
                $"blend={frame.BlendOp,-6} dispose={frame.DisposeOp}");
        }

        return 0;
    }

    private static int Extract(string[] args)
    {
        if (args.Length < 2)
            return Fail("extract needs a source file and an output directory.");

        AnimationDocument document = ApngReader.ReadFile(args[0]);
        Directory.CreateDirectory(args[1]);

        int index = 0;
        foreach (RgbaImage rendered in document.RenderFrames())
        {
            string path = Path.Combine(args[1], $"frame_{index:D4}.png");
            PngCodec.EncodeFile(rendered, path);
            index++;
        }

        if (document.PosterImage is { } poster)
        {
            // The still that viewers without APNG support display. It sits outside the
            // animation, so it is not one of the numbered frames.
            PngCodec.EncodeFile(poster, Path.Combine(args[1], "poster.png"));
            Console.WriteLine($"Extracted {index} composited frames to {args[1]}, plus poster.png.");
        }
        else
        {
            Console.WriteLine($"Extracted {index} composited frames to {args[1]}.");
        }

        return 0;
    }

    private static string RequireValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"Option '{args[index]}' needs a value.");

        return args[++index];
    }

    private static int ParseInt(string value, string option)
        => int.TryParse(value, CultureInfo.InvariantCulture, out int result)
            ? result
            : throw new ArgumentException($"Option '{option}' needs a whole number, got '{value}'.");

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            sharpapng - build and inspect animated PNGs

            USAGE
              sharpapng create --output <file> [options] <image>...
              sharpapng convert <input> [--output <file>] [options]
              sharpapng resize <input> [--size <px>] [--output <file>]
              sharpapng info <file>
              sharpapng extract <file> <directory>

            CREATE OPTIONS
              -o, --output <file>   Where to write the APNG. Required.
              -p, --poster <file>   A still shown only by viewers without APNG support,
                                    kept out of the animation. Must match the canvas.
              -d, --delay <ms>      Milliseconds per frame. Default 100.
              -l, --loop <n>        Play count; 0 loops forever. Default 0.
                  --16-bit          Store 16 bits per channel instead of 8.
                  --no-optimize     Store every frame whole instead of just what changed.
                  --fast            Compress quickly rather than tightly.
                  --smallest        Compress as tightly as zlib allows.

            CONVERT OPTIONS
              -o, --output <file>      Where to write the APNG. Defaults to the input name.
              -t, --transparent <col>  Erase a colour: #RRGGBB, #RGB, or 'background' to use
                                       the one the GIF declares.
                  --tolerance <n>      How far a channel may differ and still be erased. Default 0.
              -l, --loop <n>           Override the play count.
                  --literal-delays     Copy the stated delays instead of clamping very short
                                       ones to 100 ms the way browsers do.
                  --fast, --smallest   Compression effort.

            RESIZE OPTIONS
              -o, --output <file>   Where to write the PNG. Defaults to <input>.thumb.png.
              -s, --size <px>       Bound on the longer side, preserving aspect. Default 128.
              -w, --width <px>      Exact width, used with --height.
              -h, --height <px>     Exact height, used with --width.
              -f, --filter <name>   box (default), triangle or lanczos3.

            EXAMPLES
              sharpapng create -o walk.png -d 80 frame*.png
              sharpapng create -o walk.png -d 80 --poster cover.png frame*.png
              sharpapng convert logo.gif -t background
              sharpapng convert banner.gif -o banner.png -t #ffffff --tolerance 8
              sharpapng resize photo.jpg -s 256 -o thumb.png
              sharpapng info walk.png
              sharpapng extract walk.png ./frames

            create takes PNG, JPEG, GIF (first frame) or BMP images, all matching the first one's dimensions.
            convert takes a GIF, a JPEG, a PNG or a BMP and writes a PNG.
            resize takes any of those and never enlarges when using --size.
            extract writes composited frames, plus poster.png when the file carries one.
            """);
    }
}
