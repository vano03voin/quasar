# Native Windows managed clusters: port status

This fork starts the Windows Host work needed for two regular simulation nodes
in one cluster. It does **not** yet provide a runnable Windows cluster runtime.
The existing package, dependency and guided-installer platform checks remain in
place. A successful Host build or storage test does not prove game/Steam support.

## Component boundary

Quasar is the management and process-supervision layer. It obtains a separate
runtime release from `CometWorks/cluster`; see
[`ClusterPackageService.RequiredFiles`](../Quasar/Services/ClusterPackageService.cs)
for the required package contents.

| Component | Available here | Remaining Windows work |
| --- | --- | --- |
| Quasar UI, API, Host executor, deployment coordination | Source | Native installation and end-to-end validation against a compatible runtime. |
| Registry admin/executor contracts | Vendored source, implementation-free | Preserve compatibility with the actual runtime; these DTOs do not implement coordination. |
| Cluster Registry | External package | Obtain source or inspect a supplied managed assembly; validate its Windows dependencies and lifecycle. |
| Cluster Gateway | External native `gateway-rs` binary | Obtain source or a compatible Windows build; validate Steam networking and routing. |
| ClusterNode and WorldAuthority | External managed plugin assemblies | Obtain source or inspect supplied assemblies; validate partition ownership, transfer, shared state and platform dependencies. |
| MagnetarWorld and deployment generator | External package | Validate world conversion, generated launch specifications, paths, quoting and Windows process supervision. |
| Magnetar, game files, DirectTransport, Steam libraries | Separate dependencies | Pin mutually compatible Windows builds and validate plugin/transport loading. |

The inspected development installation contained standalone Magnetar and Quasar,
plus `ClusterGateway.AdminContract.dll`. Its installed cluster directory was empty;
no Registry, Gateway, ClusterNode, WorldAuthority or converter implementation was
found in the inspected runtime/cache locations. This is a bounded inventory, not
proof that the package cannot exist on another machine. The public management
contracts cannot reconstruct implementation that is absent from them.

Managed .NET binaries, if supplied, can provide useful material for source
reconstruction. Record version, SHA-256, provenance and license alongside that
work. A native Rust Gateway is not a managed assembly and cannot be recovered by
the same C# decompilation workflow. A Linux runtime package would still be useful
for inspecting the managed components; it is not itself a native Windows release.

## Two nodes on the first chiplet

The intended topology is two **regular nodes of one world**, alongside the
Gateway, Registry and World Authority roles. This is not two independently
advertised servers. Keeping one client/Steam entry is an end-to-end acceptance
requirement for the Gateway and transport.

The new optional [Windows node affinity policy](WindowsNodeAffinity.md) selects
exact cluster/slot identities in local Host configuration. On the inspected
32-logical-processor Ryzen machine, native L3 topology identified the first CCD
as CPUs 0–15 and the second as CPUs 16–31, with adjacent SMT siblings. A proposed
split is `0x000000FF` for regular node 1 and `0x0000FF00` for regular node 2:
four physical cores/eight logical processors per node, on the same CCD. Recheck
topology before applying these masks to another machine.

The inspected existing affinity controller selects its production Magnetar by
exact executable path. It puts that executable on the second CCD. For other
processes it intersects their current mask with `0x0000FFFF`, preserving either
proposed node mask. New cluster runtimes therefore require a **separate absolute
executable path** from the production runtime. Reusing the production path would
match that controller and defeat the placement policy. Its single main-thread
identity and production MCP endpoint must not be reused for multiple nodes.

The policy sets and reads back affinity immediately after `Process.Start`; it
does not guarantee placement from the first instruction. It detects later
process-mask changes without repeatedly overwriting another controller. It does
not reserve CPUs or constrain every individual game thread. Operating-system and
management work can share the first CCD. No throughput improvement has yet been
measured with two game nodes.

## Verified scope of this change

Local verification used Windows build 26200 and .NET SDK 10.0.401. Diagnostics
was built from the upstream-documented source pin
`4629caff434e35312e8c99c776e364e392142d5d`. The Quasar worker and test build used
the installed dedicated-server references and Magnetar PluginSdk 2.4.3.3.

| Check | Result |
| --- | --- |
| Host build | Passed, no warnings or errors. |
| Existing Host `--self-test` | Passed, including Windows configuration retention and runtime containment checks. |
| Host `--self-test-affinity` | 55 checks passed with owned inert child processes. |
| Native content-storage fixture | 45 checks passed on two distinct NTFS volumes, including corruption, readonly references, Unicode adoption, cross-volume fallback and protection of active installations during cleanup. |
| Selected Quasar cluster tests | 77 passed, 21 explicitly skipped by existing Linux requirements; no failures. Some older Linux-only test bodies also return early on Windows, so this count is not equivalent to native filesystem coverage. |

The Quasar build retained existing dependency/nullability warnings outside the
changed code. No Quasar web service, game or Steam session was started by this
verification. Windows Server 2022, Linux runtime behavior, power failure and an
unsupported Windows filesystem were not exercised. The dedicated native
fixtures establish the Windows behavior covered here; they do not validate
the missing cluster runtime.

## Remaining acceptance work

1. Obtain the actual runtime and establish its source/build provenance. Produce
   or validate Windows Registry, Gateway, plugins, converter and transport.
2. Define the real Windows package layout and manifests, then adapt package
   selection, Steam dependencies, deployment generation and Host installation.
   Do not remove Linux-only checks before their replacements are supported.
3. On a disposable world, start World Authority and two regular nodes from
   isolated runtime/configuration paths and apply their exact slot policies.
4. Connect an ordinary client through one advertised server. Verify routing and
   a player/grid transfer across node ownership, then global state and mods.
5. Verify coordinated save/reload, interrupted transfer, node failure and restart
   without duplicate ownership or lost entities. Confirm actual thread placement
   and compatibility with the existing affinity controller under load.

Reimplementing the missing components from contracts alone is a separate cluster
engine project. A useful first feasibility test is one client connection routed
to two nodes with a controlled grid handover. Windows Host plumbing alone cannot
establish that feasibility or replace the missing simulation implementation.
