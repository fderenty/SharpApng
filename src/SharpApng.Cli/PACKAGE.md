# sharpapng

**Command line tool for building and inspecting animated PNGs.**

```bash
dotnet tool install --global SharpApng.Cli
```

```bash
sharpapng create -o walk.png -d 80 frame*.png   # build an animation
sharpapng convert logo.gif                      # GIF, JPEG or BMP to PNG
sharpapng resize photo.jpg -s 256               # thumbnail, aspect preserved
sharpapng info walk.png                         # frame list, delays, offsets
sharpapng extract walk.png ./frames             # split back into still PNGs
```

## create

```
sharpapng create --output <file> [options] <image>...

  -o, --output <file>   Where to write the APNG. Required.
  -p, --poster <file>   A still shown only by viewers without APNG support,
                        kept out of the animation. Must match the canvas.
  -d, --delay <ms>      Milliseconds per frame. Default 100.
  -l, --loop <n>        Play count; 0 loops forever. Default 0.
      --16-bit          Store 16 bits per channel instead of 8.
      --no-optimize     Store every frame whole instead of just what changed.
      --fast            Compress quickly rather than tightly.
      --smallest        Compress as tightly as zlib allows.
```

Inputs may be PNG, JPEG, GIF (its first frame) or BMP, and every frame must match the first one's
dimensions. By default only
the rectangle that changed between frames is stored, which on typical content is a large saving:
a 320×320, 24-frame animation with a small moving sprite came out **91.9 % smaller** than storing
whole frames.

`extract` writes *composited* frames — what a viewer displays — not the cropped sub-images the
file stores internally. When the file carries a separate poster, it comes out as `poster.png`,
and `create --poster` puts it back.

## Checking a deployed file

CDN optimisers, CMS thumbnail steps and build tasks such as `imagemin` or `pngquant` re-encode
PNGs and silently discard the chunks that carry the animation. The result is a valid PNG that no
longer moves, with no error anywhere to announce it. `info` reports it directly:

```
$ sharpapng info deployed.png
Container  : PNG
Animated   : no
Frames     : 1

This PNG carries no acTL chunk, so it is a still image. ...
```

## Standalone binaries

If you would rather not install a .NET runtime, each release also ships a single self-contained
executable for Windows x64/x86/ARM64, Linux x64/ARM64/ARM and macOS on both architectures.

---

Built on [SharpApng](https://www.nuget.org/packages/SharpApng), a dependency-free APNG library.

[Source and documentation](https://github.com/fderenty/SharpApng) · MIT
