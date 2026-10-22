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
over-receipt), the PO Delivery Report and the PO pack, My Backup, Administration Center and audit log. Desktop design
approved 22 October 2026.

Step 2 (handover) built as a local demo: delivery to an active employee (one open custody per asset, computer-name rule,
owner type), the bilingual delivery note PDF signed on paper by the employee himself and its scan uploaded by IT
(Waiting for signed copy / Signed, overdue after 3 days), receipt back (-R) with condition and where it is kept, the
technical assessment (TA-; Dispose starts Pending disposal approval), the AD/Endpoint Central and CUCM removal tasks after
a return, asset labels (PDF at 63.5 x 38.1 mm with QR code, and Zebra ZPL), the Handover page, and the PO pack with every
delivery note (signed copy, or the issued note marked "Not yet signed"). Email reminders come with the email service.

Step 3a (ink and invoices) built as a local demo: ink requests sent by the ink officer to the approving manager, supplier orders, supplier
invoices (toners, devices linked to PO receipts, parts and maintenance), ink delivery notes signed on paper, and the monthly
payment pack (Inter-Office Memo cover + invoices + orders + signed notes). See the build plan for the details.

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

No real personal data, passwords or keys in this repository. PDFsharp (MIT) joins PDFs; QRCoder (MIT) draws the QR code on
the PDF label.
