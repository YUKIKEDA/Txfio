# Contention breakdown (2026-09-27)

On Windows on one PC, the Release build of `tests/Txfio.Stress` was run one test at a time. The file tests and the directory tests were not run at the same time.

Conditions: `TXFIO_STRESS_SEED=1`, 15 transactions per process. The counts are the final result of each transaction, and the percentages are of the number of transactions in that cell (processes × 15). Whether a lock is taken depends on timing, so the counts of the next run do not match even with the same seed.

The no-wait version does not retry on lock contention. The retry version waits a little and retries the same transaction on contention (up to 50 times). No lock contention remained in the final results of the retry version. The attempts column is the total number of attempts, including retries.

Discarded is not a conflict. About once in five times, the child closes a transaction without committing it.

## Files, no wait

| Processes | Files | Succeeded | Lock contention | External conflict | Discarded |
| -------: | -------: | --------: | ---------: | -------: | --------: |
|        2 |        4 |  9 (30%) |  20 (67%) |        0 |   1 (3%) |
|        2 |        8 | 18 (60%) |   8 (27%) |        0 |  4 (13%) |
|        2 |       16 | 22 (73%) |   4 (13%) |        0 |  4 (13%) |
|        4 |        4 | 16 (27%) |  36 (60%) |  2 (3%) |  6 (10%) |
|        4 |        8 | 21 (35%) |  31 (52%) |        0 |  8 (13%) |
|        4 |       16 | 32 (53%) |  22 (37%) |        0 |  6 (10%) |
|        8 |        4 | 24 (20%) |  94 (78%) |        0 |   2 (2%) |
|        8 |        8 | 31 (26%) |  82 (68%) |        0 |   7 (6%) |
|        8 |       16 | 42 (35%) |  63 (53%) |        0 | 15 (13%) |

The number of transactions is 30 for 2 processes, 60 for 4 processes, and 120 for 8 processes. The number of attempts equals the number of transactions.

## Directories, no wait

| Processes | Directories | Succeeded | Lock contention | External conflict | Discarded |
| -------: | -----------: | --------: | ---------: | -------: | --------: |
|        2 |            4 | 17 (57%) |   5 (17%) |        0 |  8 (27%) |
|        2 |            8 | 21 (70%) |    2 (7%) |        0 |  7 (23%) |
|        2 |           16 | 23 (77%) |    1 (3%) |  2 (7%) |  4 (13%) |
|        4 |            4 | 20 (33%) |  30 (50%) |  2 (3%) |  8 (13%) |
|        4 |            8 | 33 (55%) |  19 (32%) |        0 |  8 (13%) |
|        4 |           16 | 38 (63%) |   8 (13%) |        0 | 14 (23%) |
|        8 |            4 | 34 (28%) |  68 (57%) |  4 (3%) | 14 (12%) |
|        8 |            8 | 46 (38%) |  55 (46%) |  4 (3%) | 15 (13%) |
|        8 |           16 | 66 (55%) |  37 (31%) |  2 (2%) | 15 (13%) |

The number of attempts equals the number of transactions.

## Files, retry

Lock contention in the final results: 0.

| Processes | Files | Succeeded | External conflict | Discarded | Attempts |
| -------: | -------: | ---------: | -------: | --------: | -------: |
|        2 |        4 |  22 (73%) |        0 |  8 (27%) |       36 |
|        2 |        8 |  22 (73%) |  1 (3%) |  7 (23%) |       37 |
|        2 |       16 |  25 (83%) |        0 |  5 (17%) |       37 |
|        4 |        4 |  44 (73%) |  1 (2%) | 15 (25%) |      132 |
|        4 |        8 |  48 (80%) |        0 | 12 (20%) |      109 |
|        4 |       16 |  45 (75%) |        0 | 15 (25%) |       83 |
|        8 |        4 |  95 (79%) |  2 (2%) | 23 (19%) |      461 |
|        8 |        8 |  93 (78%) |  2 (2%) | 25 (21%) |      317 |
|        8 |       16 | 100 (83%) |        0 | 20 (17%) |      243 |

## Directories, retry

Lock contention in the final results: 0.

| Processes | Directories | Succeeded | External conflict | Discarded | Attempts |
| -------: | -----------: | --------: | -------: | --------: | -------: |
|        2 |            4 | 24 (80%) |        0 |  6 (20%) |       35 |
|        2 |            8 | 21 (70%) |  1 (3%) |  8 (27%) |       32 |
|        2 |           16 | 25 (83%) |  1 (3%) |  4 (13%) |       33 |
|        4 |            4 | 44 (73%) |  1 (2%) | 15 (25%) |       88 |
|        4 |            8 | 43 (72%) |  1 (2%) | 16 (27%) |       77 |
|        4 |           16 | 42 (70%) |        0 | 18 (30%) |       65 |
|        8 |            4 | 94 (78%) |  5 (4%) | 21 (18%) |      230 |
|        8 |            8 | 92 (77%) |  4 (3%) | 24 (20%) |      208 |
|        8 |           16 | 94 (78%) |  1 (1%) | 25 (21%) |      160 |

For 8 processes × 8 directories and 8 processes × 16 directories, a child crashed with `DirectoryNotFoundException` in the first run: another child had deleted the directory just before the check for whether it was empty. Those two rows are from a second run with the same conditions.
