# 08 — Backup, restore and device replacement (Phase 1.5 prototype)

Goal: recover from a failed POS terminal or branch gateway **without silently losing or duplicating**
business operations. Recovery is **manual and operator-initiated**: there is no automatic failover, and
no part of the system guesses an outcome it cannot prove.

Everything below is a prototype. It is tested on one Windows PC with real processes (gateway, cloud API,
PostgreSQL, simulated terminals). It has **not** been tested on real separate machines, real network
shares or real printers (see §8).

## 1. What protects what

| Failure | Protected by | Not protected |
|---|---|---|
| Gateway process crash | WAL + restart (Phase 1.3) | — |
| Gateway database corrupt | Scheduled verified backups + cloud reconciliation | Operations after the last backup that never reached the cloud |
| Gateway disk/PC dead | Backups **on another machine/share** + cloud | Same as above; a backup on the same disk is lost with it |
| POS terminal lost/broken | Shared orders live on the gateway; revoke + re-pair | Operations the till sent with no gateway answer (kept pending, see §6) |
| Cloud unavailable | Gateway stays authoritative (Phase 1.4) | — |

**Recovery point:** the newer of the last off-machine backup and the last cloud-acknowledged event. Anything
done on the old gateway after both is known to nobody. The tools say this; they do not claim to recover it.

## 2. Automatic backups

```powershell
Pos.Gateway backup-config --dir \\nas\pos-backups\centre --interval-seconds 3600 --keep 48
Pos.Gateway backup                 # one immediate backup into the configured directory
Pos.Gateway backup --out D:\x.db   # one-off copy to an explicit file
Pos.Gateway status                 # last success, last failure, same-disk warning
```

- The running gateway (`BackupWorker`) re-reads the configuration at least every 30 s. Valid intervals are
  10 s to 7 days and `--keep` is 1–1000. A directory inside the gateway data folder is refused.
- **Consistent online copy.** The SQLite backup API writes to `<file>.partial`. Then `PRAGMA integrity_check`
  runs on the copy, the metadata is read **from the copy**, and SHA-256 and size are computed. Only then is the
  file renamed and its sidecar `<file>.db.json` written, last. A backup with no sidecar is incomplete and
  is refused.
- **Metadata:** format, branch id and name, cloud gateway id, creation time, gateway/POS schema versions,
  outbox last sequence, cloud-acknowledged sequence (the sync checkpoint), last operation position, SHA-256,
  size, source machine and whether the destination is off-disk.
- **Retention** keeps the newest N complete backups. Each run is recorded in `backup_runs`
  (ok/failed, error, sequence). A failure never stops the gateway; it shows in `status` and in the logs.
- **Same-disk warning.** The destination counts as "offsite" only if its path root differs from the data
  folder's (another drive letter or a UNC share). Otherwise every report and `status` says
  **"MÊME DISQUE — pas une protection contre une panne"**. This check is heuristic: a different drive letter
  can still be on the same physical disk or PC, so the operator must check.

## 3. Restore (gateway stopped)

```powershell
Pos.Gateway restore --from \\nas\...\gateway-<branch>-<stamp>.db --dry-run     # validate + compare, no change
Pos.Gateway restore --from <file> [--replace-newer] [--branch-id <guid>]
```

Validation (always, including dry-run):
1. The sidecar is present and in a supported format.
2. SHA-256 and size match.
3. The schema is not newer than this build.
4. `integrity_check` passes.
5. The metadata matches the content.
6. The branch matches the current database. On empty hardware, `--branch-id` is required and must match.

Safety:
- **Not while serving.** `run`, `restore` and `reconcile` take an exclusive lock (`gateway.lock`), and a restore
  is refused while a gateway is running on that data. A dry-run only reads, so it is allowed.
- **Newer data is never silently overwritten.** If the current database has more events than the backup,
  the restore is refused unless you pass `--replace-newer`.
- **Forensic copy.** Current files are **moved** (never deleted) to `forensic/<stamp>/`.
- **Crash-safe.** `restore-in-progress.json` is written first. While it exists, every command (including
  `run`) refuses to start, except re-running the **same** restore, which resumes from that point. Debug
  builds can inject a crash with `POS_GATEWAY_FAULT_RESTORE=after-forensic|after-copy`.
- The copied file is re-verified, migrations run, and the state is set to **`restored`**.
- Every `queued`/`printing`/`failed` print job becomes **`uncertain`**. Nothing is reprinted automatically.

## 4. Recovery state: shared writes are blocked until verified

`recovery_state` is `normal | restored | cloud_ahead | reconciled | fenced`. In any state except `normal`:
- Terminals can connect and **read** the orders.
- Every shared write (create, add, quantity, submit) returns **423 `recovery_in_progress`**. This is a
  definitive "not applied", so the till does not queue or retry it.
- Resolving an interrupted operation that the gateway does not know is **not** answered "abandoned": it might be
  a cloud-confirmed operation that has not been imported yet. The till keeps it pending.
- Cloud sync is idle: an older history is never pushed over the cloud's.

`Pos.Gateway status` shows the state, the reason and the quarantine counts.

## 5. Cloud reconciliation (gateway stopped)

```powershell
Pos.Gateway reconcile --dry-run
Pos.Gateway reconcile
Pos.Gateway quarantine [--resolve <id> --note "<decision>"]
Pos.Gateway recovery-verify            # from 'reconciled'
Pos.Gateway recovery-verify --without-cloud   # only for an unlinked gateway, explicit
```

Let A = the local acknowledged sequence, L = the local last sequence and C = the cloud's last sequence for the
branch (`GET /v1/sync/state`, signed).

1. **Lost acknowledgments, in (A, min(L, C)].** Each local event is compared **by content hash** with the
   cloud's (`GET /v1/sync/events`). If they are equal, it is acknowledged, so it is not re-sent.
2. **Contradiction.** If the content differs at the same sequence, the event is **quarantined (blocking)**
   with the local payload preserved. Nothing is imported and nothing is acknowledged. Recovery cannot be
   verified.
3. **Cloud ahead, in (L, C].** Events are imported in order, **with their original ids**. Each one gets the
   order snapshot, the operation row (so a till retrying the same operation id gets a **replay**, never a
   second order) and an outbox row already acknowledged. Each event runs in a savepoint. One that cannot be
   applied is rolled back, quarantined (blocking) and stops the import, because later events depend on it.
   - **Submissions:** their print intents become **`uncertain`** print jobs, so the kitchen ticket may or may
     not have printed. The `print_intents` primary key prevents duplicates.
   - **Unknown terminal** (paired after the backup): a revoked placeholder is created, because its key is
     unknown and the till must re-pair. A non-blocking note is recorded.
   - **Catalog published after the backup:** the restored catalog stays in force and a non-blocking note asks
     you to check prices.
   - **Payments:** the prototype has no payment records. Order events are the only shared write. The same
     id-preserving import is the rule for payments when they exist.
4. **Atomicity.** The run is a single SQLite transaction. A dry-run rolls back. The result is `reconciled`,
   or `cloud_ahead` if anything blocking was found. Running it again is idempotent.
5. **Local-only events** (L > C) stay in the outbox and are sent after verification.

**What reconciliation cannot do:** recover operations that the old gateway committed after the last backup
and that never reached the cloud. Nobody has them. The command says so in its output. The paper tickets
already printed are the only remaining trace.

`recovery-verify` requires:
- `integrity_check` passes;
- no unresolved **blocking** quarantine item;
- state `reconciled` (or `restored`/`cloud_ahead` with `--without-cloud` **and** no cloud link).

It then sets `normal`.

**Resolving a quarantine item.** `--resolve` only records the operator's decision; the item is never deleted. A
`sequence_conflict` means the backup and the cloud hold different histories. In the prototype the safe
way out is to restore a different backup, or to escalate. There is no merge tool.

## 6. POS terminal replacement

Lost, stolen or broken till:

```powershell
Pos.Gateway replace-terminal --terminal <old id> --name "Caisse A (remplacement)"
```

This revokes the old identity (its live session is closed and it can never reconnect), prints a one-time code
plus the gateway fingerprint, and writes an audit line. On the new device:
**Passerelle → Appairer ce poste**. It gets a **new identity and key**, and the shared orders and branch
settings are all still there.

Reassigning a till that still works: **Passerelle → Dissocier ce poste…** (bridge `gateway.unpair`). This deletes the
link **and** the identity, so the next pairing creates a new one. Revoke the old id on the gateway.
If operations are still waiting for a gateway answer, unpairing is **refused** unless you explicitly
tick "abandon" (they may have been applied; this is logged). Reconnecting first resolves them safely.

## 7. Gateway replacement procedure (new PC)

1. **Make sure the old gateway is off and unplugged from the network.** There is no automatic failover.
2. Install the gateway on the new PC (`install-gateway-service.ps1`) **without** starting it.
3. `Pos.Gateway restore --from <latest offsite backup> --branch-id <branch guid> --dry-run`, then run it again
   without `--dry-run`.
4. On the cloud: `Pos.Cloud gateway-code --branch <guid> --replace`. A normal code is **refused with 409
   `gateway_already_active`** while the old credential is active: one active gateway per branch (unique
   index). `--replace` revokes the old credential in the same transaction as the new registration.
5. On the new PC: `Pos.Gateway cloud-relink --url … --code … --pin …`, then `reconcile` (it registers first),
   check its report, `quarantine`, then `recovery-verify`.
6. Start the service. The new PC has a **new TLS certificate**, so every till must re-pair:
   *Dissocier* on the till (or `replace-terminal`), then pair with a new code. Revoke ids that are not
   accounted for.
7. If the old gateway is switched on again, its next sync gets **403 `gateway_revoked`** and it moves to
   **`fenced`**: shared writes are blocked, so it cannot fork the branch. Its data stays readable for
   forensics.
8. Check the trays of every printer for tickets marked uncertain (`print_jobs.status = 'uncertain'`). Reprint
   only by an explicit decision.

## 8. Audit

`<data>/audit/recovery.jsonl` (append-only JSON lines, outside the database so that it survives a restore)
records:
- `restore_started` / `restore_completed`;
- every `recovery_state` change;
- `reconcile` (counts, quarantined items, notes);
- `quarantine_resolved`;
- `recovery_verified`;
- `terminal_replaced`;
- `cloud_relink`.

The cloud logs `gateway_already_active` refusals and `replaced` revocations.

## 9. Limitations and remaining risks

- **Not tested on separate hardware.** The tests do not cover a real network share, a dead disk, a second PC or
  DPAPI keys on another machine. The "offsite" check is a path heuristic.
- **Backups are not encrypted** and the share's access control is the operator's job. They hold
  order data and the gateway's DPAPI-protected keys, which cannot be used on another machine and
  user account.
- **RPO is the backup interval for unsynced data.** Reconciliation only narrows it to the cloud checkpoint
  when the cloud got the events.
- **Fencing needs the old gateway to reach the cloud.** An old gateway that is offline from the cloud but
  still on the LAN would keep serving tills that pin its certificate. The physical "unplug it" step
  is the real control.
- **The cloud has no merge.** A `sequence_conflict` needs a human, and no merge tool exists.
- **Catalog after the backup** is not re-imported (only flagged).
- **Same-machine tests for fencing.** The old and new gateway run on one PC with separate data folders.
- **No payment records yet,** so payment duplication is covered by design only (original operation ids).
- **No dashboard.** Status is CLI plus logs. Backup failures are not alerted.
