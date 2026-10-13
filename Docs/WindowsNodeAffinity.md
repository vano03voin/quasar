# Windows node CPU affinity

`Quasar.Host` can apply an opt-in, local Windows CPU policy to specific cluster
node slots. This controls scheduling of an already supported node executable;
it does not enable Windows cluster packages, Gateway or transport support. The
existing cluster platform and package checks still apply.

Add `windowsNodeAffinities` to the enrolled machine's existing Host JSON config.
Use the exact cluster ID and slot keys from the deployment. Preserve all existing
Host, enrollment, command and attachment settings. For example:

```json
{
  "windowsNodeAffinities": [
    { "clusterId": "example", "slotKey": "regular-1", "cpuMask": "0x000000FF" },
    { "clusterId": "example", "slotKey": "regular-2", "cpuMask": "0x0000FF00" }
  ]
}
```

This is a JSON fragment, not a complete Host config. The example assigns logical
processors 0–7 to the first node and 8–15 to the second. Identify the machine's actual CCD/core/SMT topology
before using the example: Windows logical processor numbers are not a portable
chiplet identifier. Both ranges must be confirmed to belong to the intended CCD,
and SMT sibling placement must be checked before splitting it. To let both nodes
share processors 0–15, use `0x0000FFFF` for both. Affinity does not reserve those
processors exclusively. The Host, Gateway, World Authority and
unmatched node slots retain their existing scheduling unless explicitly covered
by their own applicable policy. In particular, no executable-name wildcard or
machine-wide process enumeration is used.

The policy is local to this Host, read at Host startup, and deliberately absent
from portable execution bundles and Registry wire contracts. Missing or empty
`windowsNodeAffinities` preserves existing behavior. Cluster ID and slot key
matching is case-sensitive and exact. Duplicate entries, blank IDs, surrounding
ID whitespace, zero masks, malformed hex masks and unavailable processor bits
are rejected before the Host run loop. Masks require the `0x` prefix.

The initial implementation requires Windows x64 and exactly one active processor
group (up to 64 logical processors). It checks Windows' system mask rather than
`Environment.ProcessorCount`, which may reflect inherited affinity or job CPU
limits. Windows remains authoritative for job restrictions and access rights;
an unsuccessful set/readback fails the node launch. A configured policy can
deliberately widen a child's inherited affinity within the validated system mask.
It does not alter the Host process's own affinity.

## Launch and adoption

Host validates the policy before launch, applies it immediately after
`Process.Start`, verifies the resulting process mask and records it alongside the
launch identity. If this fails, Host uses its retained handle to stop the child it
just started and reports a launch failure. This is **not** suspended creation:
the first instructions can run under inherited affinity before the policy is
applied. Do not use this implementation when a guarantee from the first
instruction is required.

On adoption, Host first verifies the existing PID/start identity, executable path
and SHA-256, and deployment identity. A proven owned node with an older record
without `appliedCpuMask` receives the policy once. Otherwise every reconciliation
checks the recorded policy and current mask before reporting node readiness.
Changed configuration requires a node restart to apply a different mask.

If another program changes the mask, Host reports `node_affinity_failed` without
repeatedly resetting it or stopping the running node. Restoring the expected mask
allows a subsequent observation to recover. Removing a policy does not restore
or widen an already running process's mask. Explicit, correctly fenced stop
commands remain available when affinity is wrong.

Inspect existing affinity managers before deployment. A manager that periodically
rewrites a process mask can conflict with this setting; a manager that applies
individual thread affinity can restrict threads even while the process mask
matches. Readback here verifies process affinity, not all thread masks or the
physical core placement of every game worker. Keep test executables in their own
deployment paths so a production executable path rule cannot match them.

## Offline verification

```powershell
dotnet build Quasar.Host/Quasar.Host.csproj
& ./Quasar.Host/bin/Debug/net10.0/Quasar.Host.exe --self-test-affinity
```

The focused test validates malformed/duplicate/out-of-range policies, exact
scope, legacy configuration and 64-bit mask handling. On Windows it copies Host
to a temporary directory and launches only its inert `--self-test-child` mode,
exercising real node launch, readback, restart adoption, drift, unchanged defaults,
deployment/executable fences and fenced cleanup. No web service, Gateway,
Magnetar, game, Steam authentication or Registry connection is started. On a
machine restricted to one available CPU, it explicitly skips checks that need an
alternative CPU. Non-Windows execution runs deterministic validation only.
