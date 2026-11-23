using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Channels;

namespace SettlementRecon.Parsing;

/// <summary>
/// Reads a stream with <see cref="PipeReader"/>, frames it into lines without copying (except for the rare
/// line that straddles two buffer segments), parses each line with an <see cref="IRecordParser{TRow}"/> and
/// writes full batches to a channel. Handles a UTF-8 BOM, CRLF or LF endings, blank lines and a missing final newline.
/// </summary>
public static class StreamRecordReader
{
    /// <summary>A single line longer than this is treated as a corrupt file rather than buffered forever.</summary>
    public const int MaxLineBytes = 1024 * 1024;

    public static async Task<ParseStats> ReadAsync<TRow>(
        Stream stream,
        IRecordParser<TRow> parser,
        RowBatchPool<TRow> pool,
        ChannelWriter<RowBatch<TRow>> output,
        string source,
        CancellationToken cancellationToken = default)
        where TRow : struct
    {
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: 128 * 1024, minimumReadSize: 32 * 1024, leaveOpen: true));
        var framer = new LineFramer<TRow>(parser, pool, new ParseStats(source));
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;

                // The framer stops as soon as a batch fills up, so at most one full batch waits here and the
                // bounded channel's backpressure reaches all the way back to the file read.
                var batchFull = framer.Process(ref buffer, result.IsCompleted);
                if (batchFull)
                {
                    reader.AdvanceTo(buffer.Start);
                }
                else
                {
                    reader.AdvanceTo(buffer.Start, buffer.End);
                }

                if (framer.TryTakeFull(out var full))
                {
                    await output.WriteAsync(full, cancellationToken).ConfigureAwait(false);
                }

                if (result.IsCompleted && !batchFull)
                {
                    break;
                }
            }

            if (framer.TakeRemainder() is { } last)
            {
                await output.WriteAsync(last, cancellationToken).ConfigureAwait(false);
            }

            return framer.Stats;
        }
        catch
        {
            framer.ReturnBatches();
            throw;
        }
        finally
        {
            framer.ReleaseScratch();
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The synchronous, span-based part. Kept out of the async method so spans never cross an await.</summary>
    private sealed class LineFramer<TRow>(IRecordParser<TRow> parser, RowBatchPool<TRow> pool, ParseStats stats)
        where TRow : struct
    {
        private RowBatch<TRow>? _full;
        private RowBatch<TRow> _current = pool.Rent();
        private byte[]? _scratch;
        private bool _headerBound = !parser.HasHeader;
        private int _lineNumber;

        public ParseStats Stats => stats;

        /// <summary>Consumes complete lines from <paramref name="buffer"/>. Returns true when it stopped early because a batch filled up.</summary>
        public bool Process(ref ReadOnlySequence<byte> buffer, bool isFinal)
        {
            var reader = new SequenceReader<byte>(buffer);
            while (reader.TryReadTo(out ReadOnlySequence<byte> line, (byte)'\n'))
            {
                ProcessLine(line);
                if (_full is not null)
                {
                    buffer = buffer.Slice(reader.Position);
                    return true;
                }
            }

            buffer = buffer.Slice(reader.Position);
            if (isFinal && !buffer.IsEmpty)
            {
                ProcessLine(buffer);
                buffer = buffer.Slice(buffer.End);
            }
            else if (buffer.Length > MaxLineBytes)
            {
                throw new InvalidDataException($"{stats.Source}: line {_lineNumber + 1} is longer than {MaxLineBytes} bytes.");
            }

            return false;
        }

        public bool TryTakeFull(out RowBatch<TRow> batch)
        {
            batch = _full!;
            _full = null;
            return batch is not null;
        }

        /// <summary>Hands over the last, partly filled batch. The framer must not be used afterwards.</summary>
        public RowBatch<TRow>? TakeRemainder()
        {
            var last = _current;
            _current = null!;
            if (last.Count > 0)
            {
                return last;
            }

            pool.Return(last);
            return null;
        }

        public void ReturnBatches()
        {
            if (_current is not null)
            {
                pool.Return(_current);
                _current = null!;
            }

            if (_full is not null)
            {
                pool.Return(_full);
                _full = null;
            }
        }

        public void ReleaseScratch()
        {
            if (_scratch is not null)
            {
                ArrayPool<byte>.Shared.Return(_scratch);
                _scratch = null;
            }
        }

        private void ProcessLine(in ReadOnlySequence<byte> line)
        {
            if (line.Length > MaxLineBytes)
            {
                throw new InvalidDataException($"{stats.Source}: line {_lineNumber + 1} is longer than {MaxLineBytes} bytes.");
            }

            if (line.IsSingleSegment)
            {
                ProcessLine(line.FirstSpan);
                return;
            }

            var length = (int)line.Length;
            if (_scratch is null || _scratch.Length < length)
            {
                if (_scratch is not null)
                {
                    ArrayPool<byte>.Shared.Return(_scratch);
                }

                _scratch = ArrayPool<byte>.Shared.Rent(Math.Max(length, 4096));
            }

            line.CopyTo(_scratch);
            ProcessLine(_scratch.AsSpan(0, length));
        }

        private void ProcessLine(ReadOnlySpan<byte> line)
        {
            _lineNumber++;
            stats.Lines = _lineNumber;
            stats.Bytes += line.Length + 1;

            if (!line.IsEmpty && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            if (_lineNumber == 1 && line.StartsWith("﻿"u8))
            {
                line = line[3..];
            }

            if (line.Trim(" \t"u8).IsEmpty)
            {
                return;
            }

            if (!_headerBound)
            {
                parser.BindHeader(line);
                _headerBound = true;
                return;
            }

            switch (parser.Parse(line, _lineNumber, _current.Arena, out var row, out var error))
            {
                case RecordParseStatus.Parsed:
                    _current.Add(row);
                    stats.Rows++;
                    if (_current.IsFull)
                    {
                        _full = _current;
                        _current = pool.Rent();
                    }

                    break;
                case RecordParseStatus.Skipped:
                    stats.Skipped++;
                    break;
                default:
                    stats.Reject(_lineNumber, error ?? "Malformed line.");
                    break;
            }
        }
    }
}
