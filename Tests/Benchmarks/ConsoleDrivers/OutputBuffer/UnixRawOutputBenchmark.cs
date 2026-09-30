using BenchmarkDotNet.Attributes;
using Terminal.Gui.Drivers;

namespace Terminal.Gui.Benchmarks.ConsoleDrivers.OutputBuffer;

/// <summary>Measures the complete UnixRaw frame writer without terminal I/O.</summary>
[MemoryDiagnoser]
[InvocationCount (1)]
[BenchmarkCategory ("Output", "Latency")]
public class UnixRawOutputBenchmark
{
    private OutputBufferImpl _small = null!;
    private OutputBufferImpl _large = null!;
    private AnsiOutput _output = null!;
    private long _bytes;
    private int _writes;

    /// <summary>Builds and warms the two rendered frame sizes.</summary>
    [GlobalSetup]
    public void Setup ()
    {
        _small = CreateBuffer (80, 25);
        _large = CreateBuffer (240, 70);
        _output = new (bytes =>
        {
            _writes++;
            _bytes += bytes.Length;
            return true;
        }) { CaptureOutput = false };

        _output.Write (_small);

        if (_writes == 0 || _bytes == 0)
        {
            throw new InvalidOperationException ("The UnixRaw benchmark did not write the warmup frame.");
        }

        _output.Write (_large);
    }

    /// <summary>Marks the 80×25 frame dirty before each measurement.</summary>
    [IterationSetup (Target = nameof (SmallFrame))]
    public void PrepareSmall ()
    {
        MarkDirty (_small);
        _writes = 0;
        _bytes = 0;
    }

    /// <summary>Marks the 240×70 frame dirty before each measurement.</summary>
    [IterationSetup (Target = nameof (LargeFrame))]
    public void PrepareLarge ()
    {
        MarkDirty (_large);
        _writes = 0;
        _bytes = 0;
    }

    /// <summary>Flushes a fully dirty 80×25 frame.</summary>
    [Benchmark]
    public long SmallFrame ()
    {
        _output.Write (_small);

        if (_writes != 1)
        {
            throw new InvalidOperationException ($"Expected one UnixRaw write, got {_writes}.");
        }

        return _bytes;
    }

    /// <summary>Flushes a fully dirty 240×70 frame.</summary>
    [Benchmark]
    public long LargeFrame ()
    {
        _output.Write (_large);

        if (_writes != 1)
        {
            throw new InvalidOperationException ($"Expected one UnixRaw write, got {_writes}.");
        }

        return _bytes;
    }

    private static OutputBufferImpl CreateBuffer (int cols, int rows)
    {
        OutputBufferImpl buffer = new ();
        buffer.SetSize (cols, rows);
        string text = new ('A', cols);

        for (int row = 0; row < rows; row++)
        {
            buffer.Move (0, row);
            buffer.AddStr (text);
        }

        return buffer;
    }

    private static void MarkDirty (OutputBufferImpl buffer)
    {
        for (int row = 0; row < buffer.Rows; row++)
        {
            buffer.DirtyLines [row] = true;

            for (int col = 0; col < buffer.Cols; col++)
            {
                buffer.Contents! [row, col].IsDirty = true;
            }
        }
    }
}
