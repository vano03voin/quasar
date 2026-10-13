# Cluster input storage and retention

See the [deployed filesystem audit](ClusterStorageAudit.md) for measured storage,
the reversible legacy cleanup and remaining retention opportunities.

Deployment snapshots are pinned manifests plus filesystem references to content objects.
Payload bytes are keyed by SHA-256 and executable mode; identical files across releases,
dependency snapshots, mod versions and generated configurations share their objects.
Linux hard links preserve the existing package directory layout without following symbolic
links. The objects and references are read-only. Mutable runtime worlds and configuration
receive independent writable copies; saving a game cannot modify a deployment snapshot.

The content-store primitives also support Windows NTFS on Windows 10 1809 / Server 2019
or later. They create native hard links, inspect the volume and link count through file
handles, and keep the executable metadata as a separate object key even though Windows
does not have a Unix executable bit. Other Windows filesystems are rejected. A different
volume uses a local store; failure to create a reference never silently substitutes a copy.
Objects and references carry the shared read-only attribute. Reference deletion uses the
extended Windows unlink operation, and legacy adoption uses extended atomic rename;
neither clears read-only on the shared file. Unsupported native operations fail closed.
The native flags follow Microsoft's
[extended file disposition](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddk/ns-ntddk-_file_disposition_information_ex)
and [file rename](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information)
contracts.
Immutable snapshot/staging cleanup must use `ClusterContentStore.DeleteTree`/`DeleteFile`,
not clear attributes before `Directory.Delete`. Cleanup refuses reparse points. Writable
world copies clear read-only only after `File.Copy` has created independent files.

This is storage portability, not a Windows managed-cluster release. Package staging,
dependency provisioning and deployment readiness gates remain unchanged. Windows world
archive extraction retains its existing independent-copy behavior pending end-to-end
managed deployment validation. Linux storage calls retain their existing native behavior.

`dotnet run --project Tests/ClusterContentStorage/ClusterContentStorage.csproj -c Release`
runs native filesystem checks without starting Quasar or a game. An optional directory
on another NTFS volume exercises cross-volume fallback, for example append `-- D:/`.
The test creates and removes unique temporary subdirectories only. Coverage includes
write protection, shared attributes, independent mutable copies, executable keys, atomic
adoption, object corruption, ownership retention, last-reference pruning and junction
rejection. Installation collection also protects active descendants using canonical
native separators and Windows case-insensitive comparisons; sibling-prefix paths do
not retain an unrelated installation. A Windows 10.0.26200 run on distinct C:/D: NTFS volumes passed these checks;
this does not substitute for Server 2022 or power-failure acceptance.

Quasar uses `ManagedRuntime/Tools/.objects`; each Host uses
`state/conversions/.objects`. These are separate distribution endpoints. A different
filesystem gets a local object store because hard links cannot cross devices. Offline
Host commands use a store beside the requested installation root.

## Reference lifetime

Catalog selection, active deployment provenance, prepared changes, the previous rollback
revision and an interrupted update retain their inputs. Retained managed backups retain
their saved deployment inputs too. Historical operation receipts alone do not retain
payloads. Completed updates do not keep their old workflow reference indefinitely.

Recovery/export backups outside the managed backup format can include a
`deployment-references.json` file under the backup directory. Its value is an array of
cluster-definition storage roots: `uniqueName`, `dependencyManifestSha256`, and optionally
`preparation`. It records storage dependencies, not active runtime state. Removing the
backup also releases these roots. Unreadable backup/reference metadata or missing
provenance blocks collection rather than being interpreted as an empty reference set.

Catalog changes and completion of storage work trigger collection. There is no age-based
retention window for unused versions. Collection waits for in-progress workflows, including
the interval between uploading files and committing the prepared deployment. Once a
snapshot is released, its hard links are removed; an object whose last snapshot reference
has disappeared is unlinked immediately. Multiple clusters can retain the same world/mod
input. A Host independently protects the artifact and configuration roots of its active
attachments. Offline/unreachable Hosts defer cleanup with a bounded request and retry.

Existing dependency and Host installation copies are migrated by verified, atomic file
replacement, preserving the approved paths and hashes. Generated configurations are pruned
only after preparation registered ownership. Unregistered operator recovery directories,
runtime directories and standalone servers are never swept. An audit is still required
for old unregistered conversion/configuration directories.

Completed setup/conversion deletes temporary installation trees and transfer archives.
Abandoned `ClusterUpdates` work directories are removed when no storage workflow is active.
Published package download caches and deliberately retained runtime recovery copies have
separate lifetimes; this collector does not delete them.

## Combined updates and transfer

Staging a release with queued content builds one candidate using the selected release,
resolved profile, exact queued versions and preserved topology/world seed. One managed
apply performs the existing backup, shutdown, migration and restart. Queues are cleared
only when every Host prepares successfully and the queue/profile still match.

The UI reports release verification, individual mod reuse/download, plugin preparation,
changed-file packaging, transfer size and Host preparation. Workshop versions are cached
only with a version receipt and verified payload inventory. Versions are observed before
and after download; missing receipts require one download to establish provenance.
Magnetar's implicit Steam companion is included in the managed mod payload when crossplay
is disabled. Only references from retained deployment specifications keep mod versions.

An authenticated Host inventory permits sparse installation/mod archives. Received entries
and reused content are checked against the new pins before atomic publication. Reuse is
allowed only for matching path, SHA-256, byte count and executable mode. Removed files
are absent from the candidate. Old Hosts without inventory endpoints receive full archives
and retain their old storage behavior until upgraded. Manifests remain compatible with the
existing packaged deployment generator; its writable working copies are disposable.
