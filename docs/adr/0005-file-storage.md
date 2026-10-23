# ADR 0005: Local disk file storage behind `IFileStorage`, with opaque keys

- **Status:** Accepted
- **Date:** 2026-10-23
- **Deciders:** project owner (human), drafted with Claude Opus

## Context

Clients upload documents that are, by definition, sensitive: passports, payslips, bank statements.
The prototype must store them somewhere, serve them to staff, and never serve them to anyone else.

Hosting is Azure App Service F1 with a persistent `/home` volume (ADR 0007). Blob Storage would be
the production answer, but adding it now means a storage account, credentials and an SDK in a
prototype that already has an object-storage-shaped seam available for free.

Upload handling is where a file-collection product gets attacked: oversized files, type confusion,
path traversal through the filename, and files served from a guessable public URL.

## Decision

**An `IFileStorage` abstraction with a local-disk implementation, opaque storage keys, and no
public path.**

### The seam

```csharp
public interface IFileStorage
{
    Task<string> SaveAsync(Stream content, CancellationToken ct);  // returns a storage key
    Task<Stream> OpenReadAsync(string key, CancellationToken ct);
    Task<bool> ExistsAsync(string key, CancellationToken ct);
}
```

`LocalDiskFileStorage` writes under `Storage:RootPath` — a gitignored folder locally, `/home/files`
in production.

### Rules

1. **Opaque keys.** A key is `yyyy/MM/{guid}` with no extension and no part of the original
   filename. The original name, content type, size and SHA-256 live in the `UploadedFile` row and
   are reapplied on download. A client-supplied filename never touches the filesystem, so path
   traversal has nothing to traverse.
2. **No public path.** The storage root is never registered as static files. Downloads go through
   `Features/Review/DownloadFile.cs`, which requires the staff policy and writes a
   `FileDownloaded` audit event.
3. **No client download at all.** There is no portal download endpoint; clients get `404`.
4. **Size enforced while streaming.** The 20 MB limit is counted server-side as bytes are written
   and aborts the write when exceeded. `Content-Length` is advisory and never trusted.
5. **Type checked twice.** Extension must be in the allow-list (`.pdf .jpg .jpeg .png .docx
   .xlsx`), *and* the leading bytes must match that type. DOCX and XLSX are ZIP containers, so the
   sniffer checks the ZIP header plus the expected internal entry.
6. **Hash on write.** SHA-256 is computed while streaming and stored, giving integrity checking
   and duplicate detection for free.
7. **Nothing is deleted.** A re-upload after rejection supersedes the previous file; the previous
   row and blob stay. Deletion exists only in the GDPR erasure path (ADR 0006).
8. **Partial writes are cleaned up.** A failed or aborted upload removes its temporary file and
   commits no database row.

## Consequences

### What this buys

- Zero infrastructure: clone and run.
- The attack surface a file-upload feature normally has is closed by construction — no
  client-controlled path component, no guessable URL, no unauthenticated route.
- Every download is attributable, because the only way out is through an audited endpoint.
- Swapping in Blob Storage later is one new class and one registration line.

### What it costs

- **Files are tied to one instance's volume.** Another reason the deployment is single-instance
  (ADR 0001).
- **F1's 1 GB cap** is shared with the database. At 20 MB per file, the prototype fills up fast.
  The runbook notes it; no quota enforcement is built.
- **No virus scanning.** Out of scope and stated as such. A real system scans before the file is
  reachable by staff.
- **Backups are manual**, alongside the database file.
- **Content sniffing is a heuristic.** It stops a renamed executable; it does not stop a genuine
  PDF with a malicious payload. Downloads are served as attachments with the stored content type,
  never inline, so the browser does not render them in the app's origin.
- **No deduplication** despite storing the hash. Deliberate: two clients uploading the same file
  must remain independently deletable.

### Upgrade path (not built now)

`AzureBlobFileStorage` implementing the same interface, with keys unchanged, plus short-lived SAS
URLs for download if direct-to-blob serving is ever wanted. The audit requirement means the
application must still mediate, so the endpoint stays.

## Alternatives considered

| Option | Why not |
|---|---|
| **Azure Blob Storage now** | The right production answer and the documented upgrade path. Adds a storage account and credentials to a prototype that does not need them yet; the interface keeps the cost of waiting near zero. |
| **Files as blobs in SQLite** | Transactional with the metadata, which is tempting for the audit rule. But it inflates the database, makes backups heavy, and streams badly. The metadata row is what must be transactional, and it is. |
| **Keep the original filename on disk** | Readable in a file browser, but it hands an attacker control of a path component and leaks client data to anyone who can list the folder. |
| **Serve files from a static path with an unguessable name** | Security by URL secrecy, with no audit trail and no revocation. Rejected against `PLAN.md` §4. |
| **Extension check only** | A renamed executable passes. Sniffing is a few lines. |
