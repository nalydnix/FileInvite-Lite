# ADR 0006: GDPR erasure against an append-only audit log — pseudonymise, don't delete

- **Status:** Accepted
- **Date:** 2026-10-23
- **Deciders:** project owner (human), drafted with Claude Opus
- **Scope note:** this ADR records the decision and the design. The erasure endpoint is **not
  built in the MVP**; `PLAN.md` §4 asks for the conflict to be resolved in an ADR, which is what
  this is.

## Context

Two requirements point in opposite directions.

**Append-only audit (SOC 2 mindset).** `PLAN.md` §4: every state change writes an `AuditEvent` in
the same transaction, and audit events are never updated or deleted by application code. An audit
log you can edit is not an audit log.

**Erasure (GDPR Art. 17).** A client can ask for their personal data to be deleted. Here that
means their name, email, the documents they uploaded, and any identifying trace left in audit
metadata.

The conflict is real and cannot be resolved by picking a side: deleting audit rows destroys the
integrity property, and keeping client names and emails in them indefinitely ignores a lawful
request.

## Decision

**Erase the personal data; keep the audit skeleton; make the subject pseudonymous.**

When a request is erased:

1. **Domain rows are pseudonymised, not deleted.**
   `DocumentRequest.ClientName` → `"[erased]"`, `ClientEmail` → `"[erased]"`. The row, its id, its
   dates and its statuses survive, so the history stays coherent.
2. **Uploaded files are destroyed.** Blobs are deleted from storage. The `UploadedFile` rows keep
   `Id`, `ChecklistItemId`, `SizeBytes`, `UploadedAt` and a `Erased` marker; `OriginalName`,
   `ContentType`, `Sha256` and `StorageKey` are cleared. The original filename is personal data
   (`jane-smith-passport.pdf`) and the hash is a fingerprint of the content, so both go.
3. **Audit events are not deleted and not rewritten in place.** Instead:
   - Client actors were **already pseudonymous**: an `AuditEvent` for a client records
     `ActorType = Client` and `ActorId = <request id>`, never a name or an email. Nothing to erase
     in the actor field.
   - `Metadata` is the only place client personal data can appear. A single **redaction pass**
     clears the personal fields of the affected events, writes a `DataErased` audit event
     recording who requested it, when, and how many events were touched, and is the **one**
     code path permitted to modify an audit row.
   - That path lives in its own service, is not callable from feature handlers, and its existence
     is called out here so a reviewer reading "audit events are never updated" knows the single
     exception and where it is.
4. **The event skeleton survives**: what happened, to which entity, when, by which actor type.
   That is what an audit log is for, and none of it is personal data once the metadata is
   redacted.
5. **Metadata hygiene comes first.** The real defence is that audit metadata carries as little
   personal data as possible to begin with (S6.6 forbids tokens, passwords, storage keys and file
   contents). Erasure is then a narrow operation on a few known fields.

## Consequences

### What this buys

- An erasure request can be honoured: the name, the email, the files and the filenames are gone.
- The audit log keeps its integrity property for everything except one declared, audited,
  single-purpose redaction path.
- The erasure itself is auditable — a `DataErased` event records that it happened.
- Because client audit actors are pseudonymous by design, most of the audit trail needs no
  erasure at all.

### What it costs

- **"Append-only" is now "append-only, with one audited exception."** That sentence must appear
  wherever the property is claimed, including the security review checklist. A property with an
  undisclosed exception is worse than one with a disclosed exception.
- **The redaction path is a high-value target.** It must be unreachable from feature code, covered
  by tests, and on the human review list whenever it changes.
- **Pseudonymisation is not anonymisation.** A request with an erased name but intact dates and
  item titles could, in principle, be re-identified by someone with outside knowledge. Honest
  framing for a prototype; a production system would assess re-identification risk properly.
- **Nothing enforces erasure of backups.** The runbook's manual database copies would still hold
  the data. Noted as a known gap.
- **Not built in the MVP**, so the design is unproven. The data model is already shaped for it —
  pseudonymous client actors, no personal data in storage keys — which is the part that would be
  expensive to retrofit.

## Alternatives considered

| Option | Why not |
|---|---|
| **Hard-delete everything, audit rows included** | Satisfies erasure, destroys the audit property outright, and leaves gaps that look identical to tampering. |
| **Keep audit rows untouched, delete only domain rows** | Leaves names and emails in audit metadata indefinitely — an incomplete erasure that quietly fails the obligation. |
| **Encrypt personal data per client, then destroy the key ("crypto-shredding")** | Elegant: erasure becomes a key deletion and no row is ever modified. Rejected for the prototype as a lot of key-management machinery, but it is the right answer at scale and the upgrade path if this ever matters. |
| **Never write personal data into audit metadata at all** | The ideal, and the direction S6.6 already pushes. Kept as a rule, but an absolute guarantee is not achievable while metadata is free-form JSON written by many handlers — hence the redaction path as a backstop. |
