# 1. Stream settlement files instead of loading them

- **Status:** accepted
- **Date:** 2026-11-23

## Context

A busy operator receives one settlement file per provider per day. A card acquirer's daily file runs to hundreds of thousands of lines, and month-end or catch-up runs reach millions. The obvious implementation is `File.ReadAllLines`, then `string.Split` per line, then `decimal.Parse` per amount, then a list of objects. It allocates every line as a `string`, every field as another `string`, and every row as an object, and it holds the whole file in memory before matching starts. Memory and GC time grow with file size, and nothing can start until the slowest file has been read.

Settlement formats are also messy in ways that matter: a UTF-8 BOM, quoted free text that contains the delimiter, comma decimals with dot thousands separators, signs that differ per provider, JSON with escaped strings.

## Decision

Parse every input as a stream of UTF-8 bytes and never materialise the file:

- `System.IO.Pipelines` (`PipeReader`) reads the file in pooled buffers. `SequenceReader<byte>` finds line ends. A line is copied only in the rare case that it straddles two buffer segments.
- Parsers work on `ReadOnlySpan<byte>`: a quote-aware CSV tokenizer using vectorised `IndexOf`, `Utf8JsonReader` for JSON Lines, and an amount parser that goes straight from text to integer minor units (no `decimal`, no `double`).
- Parsed rows are small structs (`SettlementRow`, `LedgerRow`) whose text fields are slices into a reusable byte arena, so parsing a row allocates nothing.
- Rows travel in pooled batches of 4,096 through bounded `System.Threading.Channels`. The reader stops framing as soon as a batch fills, so a slow consumer slows the file read down (backpressure) instead of letting batches pile up.
- Malformed lines are rejected with a reason and a line number, counted exactly, and the rest of the file still parses.

## Consequences

- Memory for the settlement side is a few pooled batches per channel, whatever the file size.
- Parsing, index building and matching overlap on different cores.
- Parser code is lower level than `Split` plus `decimal.Parse`. It is covered by per-format tests for BOM, quoted fields, escaped quotes, comma decimals, thousands separators, negative refunds and timezone offsets, by a property-based test for amount round trips, and by a test that feeds files 1 to 7 bytes at a time.
- Newlines inside quoted CSV fields are not supported. No settlement export in scope uses them, and supporting them would complicate line framing for every file.
- The [benchmarks](../../README.md#-performance) put numbers on the difference against the naive version.

## Alternatives considered

- **`File.ReadAllLines` + `string.Split`**: simplest, and kept in `benchmarks/SettlementRecon.Baseline` as the baseline and test oracle. It allocates for every line and field and holds whole files in memory.
- **A CSV library (CsvHelper and similar)**: robust and convenient, but it reflects into objects and allocates per record. The formats here are few and well defined, so a span parser is small enough to own.
- **Memory-mapped files**: fast for random access, but harder to use with streams that do not come from a local disk (uploads, blob storage). `PipeReader` works on any `Stream`.
