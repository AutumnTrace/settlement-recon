# 2. Hash join on reference, with the ledger as the build side

- **Status:** accepted
- **Date:** 2026-11-23

## Context

Reconciliation is a join: every settlement line must find the ledger entry with the same PSP reference, and anything left over on either side is a break. The two inputs arrive as streams in no useful order. Sorting both and merging would need either all rows in memory or an external sort on disk. Nested loops are out of the question at a million rows.

The join also has to answer more than "found or not": a reference claimed twice is a duplicate, a payout line may cover many ledger entries through a batch id, and ledger entries that nobody claimed are *missing at PSP*.

## Decision

Use a classic hash join with the ledger as the build side and the settlement files as the probe side:

- The ledger stream is routed by provider into one `LedgerIndex` per provider in the run. Each index is an open-addressing hash table (linear probing, XxHash3, load factor at most 0.75) over entries stored as structs in fixed 1 MiB chunks. Reference bytes are stored in a chunked arena. A second table maps batch ids to a linked list of their entries with a running total.
- Each entry carries a small state (`Open`, `Paired`, `PairedInBatch`, `Duplicate`) that the probe side updates in place. That is how duplicates are detected and unclaimed entries are swept at the end, without a second pass over either file.
- Settlement files stream through the index of their provider. Providers are independent, so each has its own channel and matcher, and they run in parallel.
- Only leftovers (lines with no reference match) and breaks are materialised as records. The amount-and-date fallback then runs on those leftovers alone, sorted by amount and searched with a binary search.

## Consequences

- Memory is bounded by the ledger index, which costs about 64 bytes of entry struct plus the reference bytes and a share of the table per ledger entry. Settlement files of any size add only the pooled batches in flight. BenchmarkDotNet measured 113.76 MB allocated for a whole run over the 1M-transaction set (1,000,014 ledger rows and 883,415 settlement lines), most of it the index itself. The naive baseline allocated 3,083.12 MB for the same work ([results](../benchmarks/reconciliation.md)).
- The index holds no per-entry objects. A ledger of a million rows is a few hundred large arrays, so gen-2 collections have little to trace.
- Settlement parsing can only run a few batches ahead while the ledger index is being built: the channel bound holds it back. Overlapping the whole settlement parse with the build would mean buffering the settlement files, which is the memory growth this design avoids.
- The ledger for the period has to fit in memory. For one day or one month of a single operator it does, by a wide margin. Beyond that, the next step is a partitioned (Grace) hash join: hash both inputs into N spill files by reference and join partition by partition. The index layout does not have to change for that.

## Alternatives considered

- **`Dictionary<string, LedgerEntry>`**: the obvious build side, and the one the baseline uses. It costs one string and one object per entry plus dictionary entries, and every lookup needs a `string` key built from the settlement line.
- **Sort-merge join**: needs both inputs sorted by reference. Settlement files are not, so it means an external sort.
- **Settlement as the build side**: also valid. Building on the ledger instead makes *missing at PSP* a cheap sweep over entry states, and puts all files of one provider against one index.
- **Doing it in the database** (bulk load both sides, `FULL OUTER JOIN`): a good fit when the data already lives in a warehouse. Here the inputs are files dropped by PSPs, and the explanations need domain rules that are awkward in SQL.
