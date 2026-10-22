# IT Assets (ITAM)

Standalone IT Assets Management application for ALTAWKILAT, replacing the SharePoint 2016 IT Assets site.
It runs on ATWK-WEB-1 and ATWK-SQL-1 with its own IIS site, database, Windows sign-in, email and jobs; it is not part
of the shared platform.

- Build plan: [docs/it-assets-build-plan-2026-10-22.md](docs/it-assets-build-plan-2026-10-22.md)
- Design input (not in this repository): `New Platform\IT Assets` (Project Plan v1.9, CONFIRMED_RULES, Permission Matrix,
  forms, email templates, master data, examples).

## Status

Step 1 of 7 built as a local demo (not deployed): employees from Active Directory (read-only copy), roles with regional
scope, master data, the asset register (fields per type, Asset No. continuing after 4808, duplicate checks, encrypted
keys with logged views, history), purchase orders (PO document, lines, receiving devices with partial receipts and no
over-receipt), the PO Delivery Report and the PO pack (report + PO document; delivery notes join in step 2), My Backup,
Administration Center and audit log.

## Run

```
dotnet run --project tools/Itam.Demo -- --reset      # http://127.0.0.1:5340/demo (LocalDB, invented data)
dotnet run --project tests/Itam.Checks -c Release      # throwaway LocalDB database, dropped at the end
```

| Folder | Holds |
| --- | --- |
| `src/Itam.App` | Rules, data store, API, pages (embedded), PDF writer |
| `db/pending` | Database migrations (SQL Server 2014 syntax), applied in order with a ledger |
| `tools/Itam.Demo` | Local demo host (never deployed) |
| `tests/Itam.Checks` | Checks on a throwaway database |

No real personal data, passwords or keys in this repository. PDFsharp (MIT) is used only to join PDFs.
