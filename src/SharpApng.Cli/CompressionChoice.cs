using System.IO.Compression;
using SharpApng;
using ZopfliSharp;

namespace SharpApng.Cli;

/// <summary>
/// How a command compresses what it writes: the runtime's zlib at the level --fast or --smallest
/// picks, or Zopfli through --zopfli.
/// </summary>
internal sealed class CompressionChoice
{
    /// <summary>What --zopfli alone uses, the default of the zopfli tool itself.</summary>
    private const int DefaultZopfliIterations = 15;

    private bool _levelGiven;
    private int _zopfliIterations;

    /// <summary>How hard zlib works when Zopfli is not in use.</summary>
    public CompressionLevel Level { get; private set; } = CompressionLevel.Optimal;

    /// <summary>Records --fast or --smallest.</summary>
    public void SetLevel(CompressionLevel level)
    {
        Level = level;
        _levelGiven = true;
    }

    /// <summary>Records --zopfli, keeping any iteration count already given.</summary>
    public void UseZopfli()
    {
        if (_zopfliIterations == 0)
            _zopfliIterations = DefaultZopfliIterations;
    }

    /// <summary>Records --zopfli-iterations, which implies --zopfli.</summary>
    public void UseZopfli(int iterations)
    {
        if (iterations < 1)
            throw new ArgumentException($"Option '--zopfli-iterations' needs at least 1, got {iterations}.");

        _zopfliIterations = iterations;
    }

    /// <summary>
    /// The compressor to give the library, or null to keep the runtime's zlib. Called before any
    /// output is created, so a contradictory command line leaves nothing behind.
    /// </summary>
    public ZlibCompressor? CreateCompressor()
    {
        if (_zopfliIterations == 0)
            return null;

        if (_levelGiven)
            throw new ArgumentException("--zopfli replaces zlib, so it cannot be combined with --fast or --smallest.");

        // The tool writes one image at a time, so a single compression may as well use every
        // core. Zopfli's output is the same whatever the thread count.
        var options = new ZopfliOptions
        {
            Iterations = _zopfliIterations,
            MaxDegreeOfParallelism = -1,
        };

        return data => Zopfli.Compress(data, ZopfliFormat.Zlib, options);
    }
}
