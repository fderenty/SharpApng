# SharpApng

**Imaging for .NET, with no dependencies at all.**

Reads and writes animated PNG. Decodes PNG, JPEG, GIF and BMP. Resizes. Every image is RGBA —
8 or 16 bits per channel — because transparency is the point. Zero package dependencies, no
native libraries to deploy, identical behaviour on Windows, Linux and macOS. Trimming- and
NativeAOT-safe.

## Writing

```csharp
using SharpApng;

using var file = File.Create("walk.png");
using var writer = new ApngWriter(file, width: 320, height: 240);

foreach (RgbaImage frame in frames)
    writer.AddFrame(frame, delayMilliseconds: 40);

writer.Complete();
```

Frames go in at full canvas size. The writer works out which rectangle changed and stores only
that, and folds identical consecutive frames into the previous frame's delay. On a 320×320,
24-frame animation with a small moving sprite, that came out **91.9 % smaller** than storing
whole frames.

Options:

```csharp
var options = new ApngWriterOptions
{
    LoopCount = 0,          // 0 plays forever
    OptimizeFrames = true,  // crop to the changed region (default)
    CompressionLevel = CompressionLevel.SmallestSize,
};
```

## Reading

```csharp
AnimationDocument document = ApngReader.ReadFile("walk.png");

// Frames come back as the file stores them: cropped sub-images with offsets and blend modes.
// RenderFrames replays them into the full-canvas images a viewer actually shows.
foreach (RgbaImage rendered in document.RenderFrames())
    Process(rendered);
```

A still PNG reads as a one-frame animation, so callers need no special case.

## Pixels

```csharp
var image = new RgbaImage(320, 240);                        // 8 bits per channel
var deep  = new RgbaImage(320, 240, PixelFormat.Rgba64);    // 16 bits per channel

Span<Rgba32> pixels = image.AsRgba32();     // direct, allocation-free
pixels[0] = new Rgba32(255, 0, 0);

RgbaImage wrapped = RgbaImage.Wrap(myBuffer, 320, 240);     // no copy
```

`Rgba32` and `Rgba64` are sequential structs whose field order matches memory order, so a
`byte[]` can be reinterpreted as either with no conversion. Alpha is **never premultiplied**.

Decoding accepts any PNG the specification allows — every colour type and bit depth, palettes,
`tRNS` transparency, Adam7 interlacing — plus JPEG, GIF and BMP, and normalises everything to
RGBA. `ImageLoader` picks the decoder from the data itself rather than the file extension.

## Resizing

```csharp
RgbaImage thumbnail = RgbaImageOps.ResizeToFit(source, 128);
RgbaImage exact     = RgbaImageOps.Resize(source, 320, 240, ResampleFilter.Lanczos3);
```

Filtering runs on premultiplied alpha internally, so transparent pixels cannot bleed their
colour into the edge — no dark fringe around resized cut-outs. Works on any `RgbaImage`,
whatever format it came from.

## Decoding JPEG

```csharp
RgbaImage photo = JpegCodec.DecodeFile("upload.jpg");
JpegInfo info = JpegCodec.ReadInfo(bytes);   // size and orientation, no pixel decoding
```

Baseline and progressive, greyscale and YCbCr, any chroma subsampling. **EXIF orientation is
applied by default**, matching every browser and viewer. Arithmetic, lossless, hierarchical,
12-bit and CMYK files are refused with a message naming what was found rather than decoded
approximately. There is no JPEG encoder.

## Converting GIF

```csharp
AnimationDocument source = AnimationLoader.LoadFile("input.gif");
ApngWriter.WriteFile(source, "output.png");
```

GIF's animation model maps almost exactly onto APNG's, so timing and layering are preserved
rather than approximated: the three disposal methods match, index transparency behaves as
`BlendOp.Over` with binary alpha, and hundredths-of-a-second delays land on `delay_den = 100`
with no rounding.

Two deliberate departures from the letter of the GIF specification, both to match what browsers
actually do: delays of 0 or 1 hundredths are raised to 100 ms, and "restore to background" clears
to transparent. Both are configurable through `GifReaderOptions`.

Since the output is RGBA, `GifReaderOptions.TransparentColor` can erase a colour the GIF was
never able to mark transparent.

LZW carries no licensing burden: the Unisys patent expired in 2003–2004.

## Notes

Encoding always emits colour type 6, non-interlaced. Writing exactly one layout is deliberate: it
is what makes an animation's frames incapable of disagreeing with the header that governs them.

Frame optimisation uses `BlendOp.Source` on a cropped rectangle rather than blanking unchanged
pixels for `BlendOp.Over`. Over can never *lower* the alpha already on the canvas, so it cannot
express a frame that erases something — `Source` is exact in every case.

16 bits per channel buys precision, not dynamic range: it is not HDR, and browsers truncate it to
8 bits when compositing. It is worth it as a working or archival format.

APNG plays in Chrome 59+, Firefox 3+, Safari 8+ and Edge 79+. Viewers that do not understand it
show the default image, so a file degrades to a still rather than to nothing.

---

[Source, documentation and a command line tool](https://github.com/fderenty/SharpApng) · MIT
