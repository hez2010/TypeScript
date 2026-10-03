# C# project services

The C# project system owns configured, inferred and synthetic projects, immutable editor overlays, retained snapshots, automatic type acquisition, content-mapper processes and checker leases. The language server and both API transports use this implementation. The original project-test audit and its [97-test disposition inventory](../csharp/compatibility/evidence/phase7-project-test-inventory.json) distinguish observable contracts from Go-specific cache, queue and reference-count structures.

`ProjectSnapshotHost` creates independent snapshots. `ProjectSession` serializes editor updates and publishes a snapshot only after a successful build. A request lease keeps the selected snapshot and its programs alive through asynchronous work; canceled publication leaves the previous snapshot usable. Unchanged programs retain identity. Synthetic projects remain separate from editor default-project selection, and API sessions can mutate only the synthetic projects they own. Numeric synthetic handles accept the reference's positive signed and zero-padded aliases.

Each program has separate diagnostic, query and API checker ownership. Query checkers share request/file affinity and expire after inactivity; API checkers retain identity for the program's lifetime. An interrupted checker is replaced, while canceling a waiter or canceling after a completed query preserves the active checker's identity. Changing a request's checker category replaces its affinity, including for nested queries. Discarding a program stops idle cleanup while retained snapshots remain usable.

Snapshot filesystems cache owned source handles and directory results. Overlay edits preserve earlier versions, deleted directories invalidate descendants, and reads through package symlinks record the real paths needed to invalidate every alias. Auto-import discovery does not retain unrelated source handles. Ordinary parsed trees use weak cache entries and are bound before publication; mapped canonical and supplemental trees remain owned by their programs. These are managed ownership contracts, without Go's private parse-cache reference counters or key-reconstruction machinery.

Mapper contributions can introduce inferred-project roots. Mapper package-manifest changes reload configuration, dynamic mapper dependencies refresh their project, and excessive watch batches also refresh active mapper dependencies. Opening an unchanged mapped file compares its original text and preserves program identity. Reference searches finish the current breadth-first level so sibling projects contribute their mapper extensions consistently.

| Validation | Result |
| --- | --- |
| Original project tests, with bundled libraries | All 97 functions ran; 383 original test/subtest passes and one inherited ATA subtest skip |
| Original snapshot replay | 200 original sessions and 2 authored sessions; 444 transitions; zero unclassified differences |
| Original typings and overlay operations | 29 typings operations and 30 overlay operations from 12 cases; zero differences |
| Managed project, checker and filesystem checks | 177 assertions, including cancellation, concurrent ownership, disposal, retained snapshots and collection of retired syntax/program graphs |
| Synthetic-ID API comparisons | 23 requests, including shared LSP ownership; zero differences |
| Complete API regression | 193 scenarios and 6,358 requests; zero differences |
| Live LSP project/session comparisons | 66 scenarios and 188 requests; zero unclassified differences; two previously recorded reference integer-overflow differences |

The snapshot comparison preserves source text and kinds, roots, project/default ownership, program identity and diagnostic publications. Go's single-file clone/update counter has a documented representation policy. Two original tests have unordered overlay roots: twenty unchanged Go runs produced both orders for each test. Their [ordering evidence](../csharp/compatibility/evidence/phase7-project-ordering.json) retains the captures, and twenty corruption controls verify that normalization still rejects changed content, identities, diagnostics and missing elements. Other source/root arrays retain their order.

The inventory is exhaustive, but it does not claim an assertion-for-assertion translation of Go's private structures. Snapshot transitions from its cache-ownership tests run through C#; managed ownership and collection checks cover their lifetime requirements. Go's dirty-map proxy and background queue use snapshot-owned dictionaries, serialized publication, BCL tasks and channels in C#. One original logging test is empty and another checks Go interface conformance; LSP logging is validated through the protocol instead.

The [project-service evidence](../csharp/compatibility/evidence/phase7-project-services.json) records artifact hashes, the initial failing reproductions and subsequent checks. Correctness runs use Release CoreCLR and the default GC. Performance work remains on hold. The [API transport](csharp-api-transport.md) and [language-service](csharp-language-services.md) reports cover the surrounding editor and client contracts.
