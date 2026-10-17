# SharpApng

**Imaging for .NET, with no dependencies at all.**

Reads and writes animated PNG. Decodes PNG, JPEG, GIF and BMP. Resizes. Every image is RGBA —
8 or 16 bits per channel — because transparency is the point. One NuGet package, zero
`PackageReference` entries, **no native libraries**, identical behaviour on Windows, Linux and
macOS.

```csharp
using var file = File.Create("walk.png");
using var writer = new ApngWriter(file, width: 320, height: 240);

foreach (RgbaImage frame in frames)
    writer.AddFrame(frame, delayMilliseconds: 40);

writer.Complete();
```

---

## Why this exists

The .NET options for writing APNG all lean on an external imaging library — XNA, System.Drawing,
SkiaSharp — which means a native dependency, a different set of bugs per platform, and no control
over how the PNG itself is emitted. Several also parse PNG by *searching the byte stream for
chunk names*, which is the root problem: the four bytes spelling `IDAT` occur by chance inside
compressed pixel data, so the parser eventually latches onto a false match and reads a garbage
length.

Four decisions follow from that:

**Structural parsing.** Chunks are walked by their length/type/data/CRC framing, and every CRC is
verified on the way in. A corrupt file fails with a message naming the chunk instead of producing
a corrupt image.

**One pixel layout, always.** Frames are written as colour type 6 without exception. Handing each
frame to a general-purpose PNG encoder instead lets it choose freely — RGB for an opaque frame,
RGBA for a transparent one — while the `IHDR` written from frame one governs the whole file.
Mixing the two turns multi-frame output into noise.

**Cropping instead of blanking.** A common way to store only what changed is to blank unchanged
pixels to transparent and lean on `blend_op = OVER`. It does not hold up: OVER can never *lower*
the alpha already on the canvas, so a frame that erases something comes out wrong, and an image
with genuine transparency is unencodable in principle. SharpApng computes the changed rectangle
and writes that sub-image with `blend_op = SOURCE`, which is exact in every case.

**Pixels stay in memory.** No frame is round-tripped through an encoded PNG just to be parsed
back out. Data is compressed once, when the `IDAT` or `fdAT` is written.

---

## Installing

Targets .NET 10.

### Library

```bash
dotnet add package SharpApng
```

The package has **no dependencies** — the dependency group is genuinely empty, not merely small.
It ships XML documentation, symbols and Source Link, so stepping into the library from your own
debugger works without a local checkout.

### Command line tool

```bash
dotnet tool install --global SharpApng.Cli
sharpapng create -o walk.png -d 80 frame*.png
```

Or grab a standalone binary from the releases page if you would rather not install a runtime at
all — see below.

### From source

```bash
dotnet build
dotnet test
dotnet pack -c Release -o dist    # both packages
```

### Native binaries

Releases ship a standalone `sharpapng` executable per platform. **No .NET runtime to install** —
the binaries are ahead-of-time compiled, and zlib is linked in statically, so a release is one
file with no dependencies whatsoever.

| Platform | Runtime identifier | Build |
|---|---|---|
| Windows x64 | `win-x64` | NativeAOT |
| Windows x86 | `win-x86` | NativeAOT, cross-compiled from x64 |
| Windows ARM64 | `win-arm64` | NativeAOT, cross-compiled from x64 |
| Linux x64 | `linux-x64` | NativeAOT |
| Linux ARM64 | `linux-arm64` | NativeAOT |
| Linux ARM 32-bit | `linux-arm` | NativeAOT, cross-compiled from x64 |
| macOS Apple Silicon | `osx-arm64` | NativeAOT |
| macOS Intel | `osx-x64` | NativeAOT |

Two constraints are worth knowing before building these yourself.

**NativeAOT cannot cross-compile from one operating system to another.** Linking uses the
target platform's own toolchain, so a Linux binary must be built on Linux, a macOS binary on
macOS, and so on. The release workflow runs one job per operating system for exactly this
reason.

**Across architectures, it can.** On Windows that only takes the MSVC build tools for the
target — *MSVC ARM64 build tools* in the Visual Studio installer for `win-arm64`; x86 comes
with the x64 tools. On Linux it is how `linux-arm` is built, since no hosted runner is 32-bit
ARM. It needs the target's cross gcc and libc, its `objcopy`, and `lld`; the CLI project
selects the last two by itself, so on Ubuntu:

```bash
sudo apt install clang lld gcc-arm-linux-gnueabihf binutils-arm-linux-gnueabihf libc6-dev-armhf-cross
dotnet publish src/SharpApng.Cli -c Release -r linux-arm
qemu-arm -L /usr/arm-linux-gnueabihf src/SharpApng.Cli/bin/Release/net10.0/linux-arm/publish/SharpApng.Cli --help
```

The same works for `linux-arm64` with the `aarch64-linux-gnu` packages. Without the target's
`objcopy` the link succeeds and the publish then fails at its very last step, which looks
exactly like the architecture being unsupported. It is not: NativeAOT has built `linux-arm`
since .NET 9, and in testing its output matched the x64 build pixel for pixel.

To build what your machine can produce:

```powershell
./build/publish.ps1                     # everything this host supports
./build/publish.ps1 -Rid linux-x64      # one target
```

The script reports what it skipped and why, rather than failing partway through. On Windows,
NativeAOT additionally needs the **Desktop development with C++** workload; on Linux it needs
`clang` and `zlib1g-dev`, plus the cross packages above for the ARM targets.

For the complete set, push a `v*` tag and let
[`release.yml`](.github/workflows/release.yml) build and attach all eight.

---

## Library

### Writing

```csharp
using SharpApng;

var options = new ApngWriterOptions
{
    LoopCount = 0,          // 0 plays forever
    OptimizeFrames = true,  // store only the changed rectangle (default)
};

using var file = File.Create("out.png");
using var writer = new ApngWriter(file, 320, 240, PixelFormat.Rgba32, options);

writer.AddFrame(ImageLoader.LoadFile("a.png"), 100);
writer.AddFrame(ImageLoader.LoadFile("b.png"), 100);
writer.Complete();
```

Frames go in at full canvas size; the writer works out what changed. Identical consecutive frames
are folded into the previous frame's delay rather than stored twice.

Building a document up front works too:

```csharp
var document = new AnimationDocument(320, 240) { LoopCount = 3 };
document.Add(firstImage, 100);
document.Add(secondImage, 100);

ApngWriter.WriteFile(document, "out.png");
```

### Reading

```csharp
AnimationDocument document = ApngReader.ReadFile("out.png");

Console.WriteLine($"{document.Frames.Count} frames, {document.TotalDurationMilliseconds} ms");

// Frames come back as the file stores them: cropped sub-images with offsets and blend modes.
// RenderFrames replays them into the full-canvas images a viewer actually shows.
foreach (RgbaImage rendered in document.RenderFrames())
    Process(rendered);
```

A still PNG reads as a one-frame animation, so callers need no special case.
`ApngReader.IsAnimated(bytes)` tells the two apart when it matters.

### Pixels

```csharp
var image = new RgbaImage(320, 240);            // transparent, 8 bits per channel
var deep  = new RgbaImage(320, 240, PixelFormat.Rgba64);   // 16 bits per channel

// Direct, allocation-free access at native speed
Span<Rgba32> pixels = image.AsRgba32();
pixels[0] = new Rgba32(255, 0, 0);

// Or wrap a buffer you already have, with no copy
RgbaImage wrapped = RgbaImage.Wrap(myBuffer, 320, 240);
```

`Rgba32` and `Rgba64` are sequential structs whose field order matches memory order, so a
`byte[]` can be reinterpreted as either with no conversion and no endianness question. Alpha is
**never premultiplied**.

Inputs can also come from `PngCodec`, `JpegCodec`, `BmpCodec`, `GifReader`, or `ImageLoader`,
which sniffs the format from the data rather than trusting the file extension.

### Resizing

```csharp
RgbaImage thumbnail = RgbaImageOps.ResizeToFit(source, 128);          // preserves aspect ratio
RgbaImage exact     = RgbaImageOps.Resize(source, 320, 240, ResampleFilter.Lanczos3);
```

Filters are `Box` (the default, and the right choice for downscaling), `Triangle` and
`Lanczos3`. `ResizeToFit` never enlarges.

Filtering happens on **premultiplied alpha** internally, then unpremultiplies. Averaging
non-premultiplied channels lets the colour hiding behind transparent pixels bleed into their
neighbours, which is what produces the dark fringe around badly resized cut-outs. Callers still
see non-premultiplied pixels throughout; this is invisible except in the absence of the halo.

This is a plain operation on `RgbaImage`, independent of any format — it works the same on a
frame decoded from a PNG, a GIF, a JPEG, or a buffer you filled yourself.

### Decoding JPEG

```csharp
RgbaImage photo = JpegCodec.DecodeFile("upload.jpg");

// Dimensions and orientation without decoding any pixels
JpegInfo info = JpegCodec.ReadInfo(bytes);
```

Baseline and progressive Huffman coding, greyscale and YCbCr, and any chroma subsampling the
sampling factors can express. Output is always opaque `Rgba32`, since JPEG has no alpha channel.

**EXIF orientation is applied by default.** Phone cameras store a portrait photograph in the
sensor's landscape orientation with a rotation flag; ignoring that flag displays it lying on its
side. Every browser and viewer honours it, so this library does too. Set
`JpegDecoderOptions.ApplyExifOrientation` to false for the stored orientation, and use
`RgbaImageOps.ApplyOrientation` to apply one yourself.

Coding processes that are not implemented — arithmetic, lossless, hierarchical, 12-bit precision
and four-component CMYK — are **refused with a message naming what was found**, never decoded
approximately. That matters most for CMYK: Adobe writes those files with inverted channels, so a
naive decode produces a plausible-looking image in complementary colours, a bug that survives
review precisely because the output looks like an image.

There is no JPEG encoder. Write thumbnails as PNG.

---

## Converting GIF

GIF reading is built in, so an existing animation can be used as a source directly — in memory,
with no temporary file:

```csharp
AnimationDocument source = AnimationLoader.LoadFile("input.gif");
ApngWriter.WriteFile(source, "output.png");
```

Or from the command line:

```bash
sharpapng convert logo.gif                              # writes logo.png
sharpapng convert banner.gif -t background              # erase the declared background colour
sharpapng convert banner.gif -t #ffffff --tolerance 8   # erase white, near enough
```

**The two animation models line up almost exactly**, so a conversion preserves timing and
layering rather than approximating them. GIF's three disposal methods are APNG's, its index
transparency behaves as `BlendOp.Over` with all-or-nothing alpha, and its hundredths-of-a-second
delays land on `delay_den = 100` with no rounding at all.

Two places where matching the specification would give the wrong answer:

**Very short delays.** Countless GIFs declare a delay of 0 or 1 hundredths. Every browser has
clamped those to 100 ms since the 1990s, so the number in the file and the speed anyone actually
sees are different. Converting literally yields an animation several times faster than the
original appeared. SharpApng clamps by default; `--literal-delays` copies the stated values.

**"Restore to background".** The specification says to fill with the background colour; every
browser clears to transparent. `DisposeOp.Background` does the latter, so the conversion matches
what viewers show.

### Erasing a colour

Because the output is RGBA, the conversion is not limited to the transparency GIF could express.
`-t` turns a chosen colour transparent — useful for the very common case of artwork sitting on a
flat background that was never marked transparent.

This forces frames to be composited and re-emitted whole, and the reason is worth stating: the
stored sub-frames use `BlendOp.Over`, and Over cannot *lower* the alpha already on the canvas. A
pixel that was opaque in the GIF and becomes transparent would simply fail to erase what sits
beneath it. Compositing first and writing full frames with `BlendOp.Source` avoids that, and the
writer crops them back down afterwards, so little is lost in file size.

### On file size

An APNG is RGBA where a GIF is 256-colour indexed, so conversion is not automatically smaller —
but it is not automatically larger either, because per-frame cropping often more than pays for
the wider pixels. Measured on real files:

| Source | Result |
|---|---|
| 100×100, 3 frames | **78 %** of the GIF |
| 100×100, 4 frames | **86 %** |
| 320×320 synthetic, 5 frames | 143–147 % |
| 600×600 single dense image | 183 % |

Animations with limited palettes and localised motion tend to shrink; single dense images grow.
Quality never suffers either way, since a GIF's 256 colours always fit inside RGBA exactly.

### Licensing

None. The Unisys patent on LZW — the reason PNG was invented in 1995 — expired on 20 June 2003
in the United States and by 7 July 2004 everywhere else. Reading and writing GIF has been
unencumbered for two decades.

---

## Command line

```
sharpapng create --output <file> [options] <image>...
sharpapng convert <input> [--output <file>] [options]
sharpapng resize <input> [--size <px>] [--output <file>]
sharpapng info <file>
sharpapng extract <file> <directory>
```

```bash
sharpapng create -o walk.png -d 80 frame*.png
sharpapng create -o walk.png -d 80 --poster cover.png frame*.png
sharpapng convert logo.gif -t background
sharpapng resize photo.jpg -s 256 -o thumb.png
sharpapng info walk.png
sharpapng extract walk.png ./frames    # composited frames, plus poster.png if there is one
```

`--16-bit` stores 16 bits per channel, `--no-optimize` stores every frame whole, `--smallest`
compresses as tightly as zlib allows. `--poster` adds a still that only viewers without APNG
support display, kept out of the animation itself; `extract` writes it back out as `poster.png`,
so the two commands round-trip a file completely.

---

## Serving APNG on the web

`.png` with `Content-Type: image/png` works everywhere. Browsers identify an animation by the
file's own signature and its `acTL` chunk, never by the extension or the media type, so nothing
special is required. An `image/apng` media type exists and has been registered with IANA since
2017, but it has never been necessary to make an animation play.

One failure mode is worth knowing about in advance, because nothing reports it.

### Image pipelines flatten APNG silently

CDN optimisers (Cloudflare Polish, Imgix, Cloudinary), CMS thumbnail generation, and build steps
such as `imagemin`, `oxipng` or `pngquant` read the PNG, re-encode it, and **discard the `acTL`,
`fcTL` and `fdAT` chunks they do not recognise**. The output is a perfectly valid PNG. It simply
no longer moves.

There is no error, no warning and no broken request — just a still image where an animation used
to be, usually noticed long after deployment. To check a deployed file in one command:

```
$ sharpapng info deployed.png
Container  : PNG
Animated   : no
Frames     : 1

This PNG carries no acTL chunk, so it is a still image.
If it was animated when you built it, something re-encoded it in between: ...
```

The fix is to exclude animations from optimisation rather than to rename them: `polish: off` on
the route at Cloudflare, a glob exclusion in the `imagemin` step, or the `--keep-chunks` option
on optimisers that offer one.

### If you would rather serve `.apng`

Renaming does dodge pipelines that only match `*.png`, and some people use it deliberately as a
guard rail. If you do, declare the media type or the server falls back to
`application/octet-stream` and the browser offers a download instead of rendering the image:

```nginx
# nginx, in mime.types or the http block
types { image/apng apng; }
```

```apache
# Apache, in .htaccess
AddType image/apng .apng
```

```xml
<!-- IIS, in web.config -->
<staticContent><mimeMap fileExtension=".apng" mimeType="image/apng" /></staticContent>
```

The trade-off: a CDN that does not know the extension may skip caching or compression for those
files. Note also that this is only about serving. **On disk, keep the `.png` extension** — an
APNG is a valid PNG, which is exactly what lets a viewer without animation support fall back to
the default image, and what keeps the file associated with an image application.

### A note on 2019

Before Edge moved to Chromium in January 2020, EdgeHTML did not display APNG at all, and the
format was not yet standardised — the PNG group had rejected it in 2007, and it was only folded
into the specification with PNG Third Edition in 2025. Tooling and server support of that era
reflected that limbo. Both problems are now behind us; the pipeline flattening described above is
the one that remains.

---

## Design notes

### No dependencies is achievable because zlib is in the BCL

`System.IO.Compression.ZLibStream` produces exactly the zlib framing PNG requires inside `IDAT`.
That is the single heaviest piece of a PNG codec, and it ships with the runtime. What remains —
CRC-32, the five scanline filters, chunk framing, the animation model — is a few hundred lines.

### Why `BlendOp.Source` by default

`Over` compresses slightly better on sprite-like content, because unchanged pixels inside the
changed rectangle can be left transparent. It also has two problems: it cannot reduce alpha, and
decoders round its intermediate arithmetic differently, so an encoder that simulates the canvas
can drift from what a viewer shows. `Source` is exact and implementation-independent. The
optimiser gets its compression from a tight bounding box instead, which on typical content is
where nearly all the saving lives — a 320×320, 24-frame animation with a small moving sprite came
out **91.9 % smaller** than storing whole frames.

### 16 bits per channel is precision, not HDR

PNG has allowed 16-bit samples since forever, and SharpApng supports them end to end. Be clear
about what that buys: more levels per channel, not more dynamic range. Real HDR in PNG arrived
through the `cICP` chunk (PNG Third Edition, 2025), which declares a PQ or HLG transfer function
— and works at 8 bits too. Browsers also truncate 16-bit PNGs to 8 bits when compositing, so
16-bit output is worth it as a working or archival format, not for what a page displays.

### Browser support for APNG

Universal in anything current: Chrome 59+, Firefox 3+, Safari 8+, Edge 79+. The only gap was
Internet Explorer and the old EdgeHTML engine. A viewer that does not understand APNG shows the
default image, so a file always degrades to a still rather than to nothing —
see `ApngWriter.SetPosterImage` to control which still that is.

---

## What is not here

**Animated WebP.** Not planned. APNG plays in every current browser (see
[Browser support for APNG](#browser-support-for-apng)), so a second animated format would reach
no viewer that APNG does not; its one advantage is somewhat smaller files. Against that, a
self-contained WebP writer needs a **VP8L** lossless codec — prefix Huffman coding, LZ77 backward
references, a colour cache — on the order of 1,500 lines of genuinely difficult code. Should the
trade ever change, the animation model (`AnimationDocument`, `AnimationFrame`) is
container-neutral, and `PixelRect.SnapToEven` already covers WebP's requirement for even frame
offsets.

**A JPEG encoder.** Not planned. Decoding is here; writing is not. Thumbnails come out as PNG,
which at small sizes costs little, and JPEG cannot carry the transparency this library is built
around. A baseline encoder is the easier half of the pair — the quantisation tables are yours to
choose and the specification's annex K supplies standard Huffman tables — should a measurement
ever justify it.

**Uncommon JPEG coding processes.** Arithmetic coding, lossless, hierarchical, 12-bit precision
and four-component CMYK are refused with a clear message rather than approximated. All are rare;
12-bit is essentially confined to medical imaging.

**`cICP` / HDR metadata.** Not written. Straightforward to add if it becomes useful.

---

## Testing

125 tests covering PNG round-trips at both depths, every colour type and bit depth on decode,
Adam7 interlacing, palette and `tRNS` transparency, filter reversibility, BMP variants, GIF
decoding (LZW, interlacing, disposal, transparency, colour keying), JPEG decoding (progressive
scans, chroma subsampling, restart markers, EXIF orientation, and the formats it refuses),
resampling, and APNG round-trips — including a frame that erases what came before it, the case
`blend_op = OVER` cannot express.

Output is additionally checked against **Pillow**, an independent APNG implementation, rather
than only against this library's own reader. Tests never compare encoded bytes against a stored
reference file: zlib output is not guaranteed stable across runtime versions, so a byte-for-byte
comparison tests the compressor rather than the codec.

```bash
dotnet test
```

---

## Prior art

Two projects covered parts of this ground and are worth knowing about:
[xupefei/APNG.NET](https://github.com/xupefei/APNG.NET) reads APNG, and
[cmksoftdev/AnimatedPngCreator](https://github.com/cmksoftdev/AnimatedPngCreator) writes it. I
used both before writing this, and wanting one library that does both without a native
dependency is what started SharpApng. It shares no source with either — everything here was
written against the specifications:

- [PNG Specification, Third Edition](https://www.w3.org/TR/png-3/) (W3C), which as of 2025 also
  standardises APNG and adds `cICP` for HDR
- The original [APNG specification](https://wiki.mozilla.org/APNG_Specification) (Mozilla)
