# IT Assets (ITAM) — Build Plan (standalone application)

**Date:** 22 October 2026 · **Status:** plan; nothing built yet
**Design input:** `New Platform\IT Assets` (Project Plan v1.9, Business Scenarios, CONFIRMED_RULES, Permission Matrix,
40 form workbooks, 31 email templates, master data workbook, document and label examples, clickable demo).

## Owner decision, 22 October 2026: a separate application, in its own repository

IT Assets is **a separate application, not part of the shared platform**, as CONFIRMED_RULES and Standalone_Hosting say
(an earlier choice the same day to host it in the hub was withdrawn). Its code lives in **its own GitHub repository**.
Everything in CONFIRMED_RULES stays authoritative.

| Area | IT Assets as a standalone application |
| --- | --- |
| Servers | The same ATWK-WEB-1 and ATWK-SQL-1, but nothing shared with the hub |
| Site | Its own IIS site and application pool; address, binding and certificate to configure (`ITAssetsBaseUrl`; not hub.altawkilat.com/itasm) |
| Sign-in | Its own Windows / Active Directory sign-in (the employee's own Windows account) |
| Database | Its own database `ITAM_Production` (own runtime login, migrations ledger, backups, restore runbook) |
| Directory | Its own read-only Active Directory reading of the users OU (OU=Users,OU=ALTAWKILAT Users) and of the computer OUs |
| Roles | Its own roles and regional scopes (IT Support Specialist per region, Team Leader, System Administrator, Voice Administrator, IT Assets Officer, IT Manager, IT Administrator, Auditor) |
| Email | Its own sender, templates IT-EM-xx and outbox, through the corporate SMTP relay with an independent connection |
| Jobs, files, PDFs | Its own scheduler, document store (folder per record) and PDF generation |
| Test | A separate IT Assets test site and test database on the same servers; test email only to a test mailbox (plan v1.9) |
| Code | Its own repository: `src/Itam.Web` (site), `src/Itam.Core` (rules and store), `db/` (migrations), `tools/Itam.Demo`, `tests/Itam.Checks`; written in the same style as the platform code but sharing no code at runtime |
| Releases | Its own install and update packages with preview, backup and rollback |
| Master data | Its own regions, branches and IT warehouses (the Shared Administration Center is postponed and IT Assets does not depend on it) |

## Owner decision, 22 October 2026: delivery notes are signed on paper by the employee himself

Replaces the electronic acceptance in plan v1.9 (Accept / Report a problem, stamped PDF, acceptance reminders):

1. IT issues the delivery in IT Assets; the bilingual delivery note PDF is generated and printed.
2. **The employee signs the printed note by hand, himself.** No one signs or accepts for him: not IT, not his manager,
   not a backup.
3. IT scans and uploads the signed copy to the delivery. The delivery shows **Waiting for signed copy** until then, and
   **Signed** after (who uploaded, when). The signed copy is kept unchanged; the unsigned PDF stays as issued.
4. Reminders go to the IT person who issued the delivery (and the Team Leader when overdue), not to the employee.
5. The employee still sees the note on My IT Assets (read-only) and may get it by email for information, without an
   Accept button.
6. Where the plan says "accepted delivery note" (procurement documents for Accounting, branch transfers that change
   custody), it now means the delivery note with the signed copy uploaded.

Ink has **no delivery note and no signed copy** (owner, 22 October 2026, replacing the earlier paper-note rule): IT records the
delivery (date, IT shelf or invoice line); the payment pack carries each invoice and the ink request documents it pays for,
each showing who sent it for approval, the approving manager and the decision. The ink notes described below are replaced.

## Purchase orders (PO) — added on the owner's request, 22 October 2026

There is no SAP: Procurement sends each PO to IT by email. IT records it in IT Assets and registers the devices by
receiving them against the PO (forms 12 Purchase Order and 13 PO Stock Receiving).

**Record a PO** (IT Assets Officer):

| Field | Rule |
| --- | --- |
| PO Number | As written on the PO document; unique; required |
| PO Date, Supplier | Required (supplier from master data) |
| Raised by | Procurement, or the IT procurement request it came from (PR-n) |
| PO lines | One row per product: asset type, manufacturer, model, specification, quantity, unit cost (SAR before VAT); line and PO totals with VAT 15% |
| Invoice number | Optional; added when the invoice arrives |
| PO document | The PDF from Procurement, attached; required |

**Receive devices against a PO** (IT Assets Officer, or regional IT for its own warehouse):

- Choose the PO line and the receiving IT warehouse or branch; enter each device on its own row with serial number,
  service tag, MAC address and the other fields of its type. Each row becomes an asset with its Asset No. (PC-n …),
  status In Store, the PO number, supplier, unit cost and PO date as purchase details.
- Partial receipts are allowed (several deliveries per PO); **receiving more than the remaining quantity of a line is
  refused**, and duplicate serial numbers, service tags and MAC addresses are refused.
- Optional delivery evidence (supplier's delivery note) and invoice reference per receipt.

**PO page and PO Delivery Report** (counts kept by the system):

| Count | Meaning |
| --- | --- |
| Ordered | Sum of the PO line quantities |
| Received by IT | Devices registered against the PO |
| Still to come | Ordered minus received |
| In IT stock | Received devices In Store and not reserved |
| Reserved, pending delivery | In Store and reserved for an approved request |
| Delivered, waiting for signed copy | In Use; the delivery note is not yet signed on paper and uploaded |
| Delivered and signed | In Use with the signed delivery note uploaded |

The **PO Delivery Report** is a printable bilingual PDF for Accounting: the PO, these counts, and each device with its
state, employee or location, request, delivery note and signed date; signed by the IT Assets Officer and received by
Accounting (the date sent is recorded).

**Goal (owner, 22 October 2026): print one PO pack with the delivery notes of all its items.** "Print PO pack" makes
one PDF, in this order:

1. Cover: the PO Delivery Report above (PO, counts, one line per device).
2. The PO document from Procurement.
3. The delivery note of every device on the PO, in Asset No. order: the **signed copy** uploaded by IT; if a device is
   not yet signed, the issued note marked "Not yet signed"; if not yet delivered, a line "In IT stock / reserved".

The cover lists anything not yet delivered or signed, so the IT Assets Officer can print now or wait until the PO is
complete. Each print is recorded (who, when, how many notes, which were unsigned) together with the date sent to Accounting.

Migration: existing assets keep the PO and invoice numbers typed in SharePoint as references; PO records start with the
new system (there is no PO list today).

## Ink invoices — ink is tracked by the supplier's invoice (owner, 22 October 2026)

Printer ink has no PO: the supplier invoice is the purchase record.

- **Record an ink invoice** (ink handler of the region or IT Assets Officer): invoice number (unique per supplier),
  date, supplier, region, invoice PDF attached, lines (toner model, quantity, unit cost before VAT; totals with VAT 15%).
- **Ink delivery from an invoice:** when the ink handler delivers ink for an approved ink request, he picks the
  invoice line the cartridge comes from. Delivering more than the line's remaining quantity is refused. The ink delivery
  note (printer Asset No. + ink sequence, e.g. PNT-2879-602) is printed, signed on paper by the employee and uploaded.
- **Invoice page:** invoiced, delivered (signed / waiting for signed copy), still in stock, per line.
- **Print invoice pack:** one PDF with a cover (invoice, lines, counts, one line per ink delivery: employee, printer,
  request INK-n, signed date), the invoice document, then every **signed ink delivery note** for that invoice (unsigned ones
  marked "Not yet signed"). Each print and the date sent to Accounting are recorded.
- Migration: the 589 SharePoint ink deliveries keep their cost and become ink delivery notes without an invoice link
  (no invoice list exists today); invoice tracking starts with the new system.

**Create the invoice from the uploaded file (owner request, 22 October 2026).** The handler uploads the supplier's
invoice PDF; the system reads it and fills the invoice form (number, date, supplier and VAT number, lines, totals). The
handler always reviews and corrects before saving; nothing is created without that check. Reading, from most to least
reliable:

1. **Saudi e-invoice (ZATCA / FATOORA):** PDF/A-3 with the invoice XML (UBL 2.1) embedded: exact header, lines and totals,
   read on our own server.
2. **Text PDF:** text read on our server with a small reading rule per supplier (one or two sample invoices per supplier
   needed to set it up).
3. **Scanned picture:** OCR on our server (weak on tables and Arabic) or, only if the owner and IT approve sending
   invoices outside the company, cloud AI reading.

Then: each line is matched to a toner model in master data (unmatched lines are flagged for the handler to choose, and
the choice is remembered for that supplier's wording); totals and VAT 15% are checked; an invoice number already
recorded for the supplier is refused. The same reading can later fill PO lines from the PO document.

To confirm: sample invoices from the main ink suppliers (to see which level applies), and whether cloud AI reading of
scanned invoices is allowed (default: no).

## Build order (demo first, every step reviewed by the owner)

| Step | Content | Result |
| --- | --- | --- |
| 1. Foundation + register + PO | Database, roles and regional scopes, audit, master data (types and short codes, statuses, regions, branches, IT warehouses, suppliers), asset register (fields per type, Asset No. by rule, duplicate serial/service tag/MAC checks, history), purchase orders (record the PO with its lines and document; receive devices against it, partial receipts, no over-receipt; PO page, PO Delivery Report and the PO pack with all delivery notes), My Backup | Demo: record a PO, receive devices, find assets, print the PO pack |
| 2. Handover | Delivery to an employee (one open custody per asset, computer-name rule), bilingual delivery note PDF printed and signed by the employee on paper, signed copy uploaded (Waiting for signed copy / Signed, reminders to IT), receipt note (-R), technical assessment on the delivery, AD/Endpoint and CUCM removal tasks, labels (Zebra ZPL 63.5 x 38.1 mm + PDF) | Demo: full handover cycle |
| 3. Self-service + requests | My IT Assets (and My Team), device and accessory requests with manager one-click approval and cost approval, ink requests (regional handler, manager approval), ink invoices and ink delivery notes from an invoice, invoice pack, request documents for Accounting | Demo: employee and manager pages |
| 4. Stock movements | Stock allocation, gate passes and branch transfers (approval, dispatch, per-asset receipt, discrepancies), warehouse transfers (no approval), maintenance orders | Demo: movements |
| 5. End of life + AD | Loss cases, disposal (approval then completion), sale, employee clearance, Assets to collect, AD computer comparison, licenses (encrypted keys) | Feature complete |
| 6. Reports + migration | Dashboards, Excel exports, SharePoint migration (read-only extract, cleaning, numbers kept, two rehearsals) | Ready for UAT |
| 7. Production | IT creates the IIS sites (test and production) and binding; install packages (database, site files), test site with test mailbox, UAT, final migration, go-live | Live |

Numbers stay as today: Asset No. = short code + one shared sequence (PC-822; continue after 4808), delivery note
PNT-2879-3060 (continue after 4132), receipt = delivery number + -R, TA-<delivery no.>, ink delivery = printer Asset No. +
ink sequence (after 601), PR-n, GP-n, WT-n.

## To confirm with the owner (not blocking step 1)

1. Address and binding of the IT Assets site (production and test), and its certificate; done by IT infrastructure.
2. Who holds IT Administrator (plan open question 20; proposed: the System Administrator and a backup).
3. Cairo location and UAE assets (open question 1); Zebra printer model and dpi; reminder timings.
4. Unfilled permission rows in the matrix (liability/write-off, disposal completion, branch discrepancy resolution) stay
   disabled until confirmed.

## Step 2 (handover) — built 22 October 2026, decisions to confirm

Built as a local demo with checks: delivery, paper-signed delivery note with uploaded scan, receipt back (-R), technical
assessment (TA-), AD/Endpoint Central and CUCM removal tasks, labels (PDF + ZPL), Handover page, delivery notes in the PO pack.
Permissions from the matrix: delivery, receipt and assessment by the regional IT Support Specialist (own regions), Team
Leader and IT Assets Officer; labels also by the IT Administrator; AD/Endpoint removal confirmed by the System
Administrator, CUCM removal by the Voice Administrator.

To confirm:
1. Overdue signed copy after 3 days (reminder email to the issuer, and the Team Leader when overdue, comes with the email service).
2. Receipt condition values: Working, Damaged, Faulty.
3. Owner type Resigned (System Administrator, IT-EM-11) and the Assets to collect page follow with employee clearance (step 5).
4. The label QR address: the final IT Assets site address (today's example uses hub.altawkilat.com/itasm).
5. Company Name on the delivery note comes from Active Directory (the employee's company), not from the asset.

## Step 3a (ink and invoices) — built 22 October 2026

Owner decisions (22 October 2026): the Ink Officer (a new role given per region in Administration → Roles;
the IT Assets Officer covers all regions; specialists and the Team Leader do not handle ink) sends each ink request to the approving manager he
chooses (a senior manager, not the Active Directory manager), who approves (prices come
from the supplier invoice, no per-request cost step); no toner stock list; ink and invoices before the requests part.
Computers have a service tag only (no serial number). No ServiceDesk ticket numbers in IT Assets.

Built as a local demo with checks: ink request INK-n for an In Use printer (toner code from the printer), approval by the
direct manager or the manager's backup on leave (no self-approval, rejection needs a reason), supplier order ORD-n (PDF and
an email draft; nothing sent automatically), supplier invoices (Toners, Devices, Parts and maintenance; number unique per
supplier; VAT 15%; toners linked to their order, devices to the PO receipts they cover, shown on the PO page and in the PO
pack), ink delivery (from the IT shelf or an invoice line, never more than the line, never before the approval date;
no note to sign), the ink request document PDF (request, sent for approval by, approver, decision, order, delivery), and the payment pack PAY-n (Inter-Office Memo cover with Date | Invoice No. | Amount | Total
and the three signatures, then each invoice followed by its ink request documents; sent-to-Accounting and paid dates).

To confirm: reading the uploaded invoice automatically (ZATCA XML / text PDF / OCR) comes next; names on the memo cover
(today: "Checked by (IT)", "Approved by (Head of Department)", "Accountant"); email of approvals with the email service.
Step 3b next: My IT Assets (and My Team), device and accessory requests with manager approval and cost approval.

Production note (owner, 22 October 2026): person search in production reads Active Directory directly (name, account or
email), for the approving manager of ink requests and the other person fields (deliver to, give a role, backups). A person
chosen from Active Directory is added to the employee copy (stable AD identity), so approvals and history stay linked.
The demo searches the invented employee copy instead.

### Ink for a printer not in IT Assets (owner, 22 October 2026)

When the employee's printer is not found, the employee still asks: serial number (required) and printer model (dropdown, or
"Not in the list" with the name typed). The region comes from the employee's region in Active Directory and the office is
the printer location. A serial number already registered links the printer by itself. Otherwise the request shows
"Not registered" to the Ink Officer of that region (unknown region: every Ink Officer), who either links it to the printer's
Asset No. or registers the printer (old stock, no PO, In Use where it is kept) and prints its label. Only then can it be
sent for approval. The Ink Officer may print labels in their regions.

### Active Directory attributes (same as VTMS; build at step 7)

IT Assets reads the employee details the shared platform's VTMS directory import reads, with the same configurable keys
(`Administration:LDAP.Region`, `.Branch`, `.JobTitle`, `.Company`):

| IT Assets field | AD attribute |
|---|---|
| Region | `postalCode` (matched to an IT Assets region code or name) |
| Branch / office (printer location, delivery note) | `physicalDeliveryOfficeName` |
| Job title | `description` |
| Company | `company` |
| Department | `department` |
| HR code | `postOfficeBox` |
| Mobile | `mobile` |
| Direct manager | `manager` |
| Extension (click-to-call later) | `ipPhone` |
| Account, name, email, enabled | `sAMAccountName`, `displayName`, `mail` / `userPrincipalName`, `userAccountControl` |

The national ID (`extensionAttribute6`) is not read: IT Assets does not need it.

### Documents: logo, black-and-white printing, Calibri (owner, 22 October 2026)

Every PDF (delivery, receipt and assessment notes, labels, PO report, toner order, ink request, payment cover memo) has
the company logo and uses grayscale only (black text, gray rules, light-gray section bands) for black-and-white printers.
English text is in Calibri; Arabic stays in Arial, because Calibri has no Arabic letters. Each PDF embeds only the letters
it uses, so a one-page note is about 90 KB instead of 575 KB.
Deployment: the server needs `calibri.ttf` (it comes with Microsoft Office, not always with Windows Server); without it the
English text falls back to Arial.

### Supplier module (owner, 22 October 2026)

A Suppliers page lists every supplier with VAT number, contact person and phone (shared click-to-call control), and how many
invoices are waiting for a payment pack. One supplier's page shows its details (VAT, CR, contact, phone, email, address),
invoices, payment packs, toner orders and POs. The Ink Officer, the IT Assets Officer and the IT Administrator add and change
suppliers (audited). Tick any of the supplier's invoices waiting for a pack and put them in one payment pack; one pack holds
invoices of one supplier, of one category or several together (Mixed), and the person needs the right for each category.
Invoices, the PO page and the PO list show where each stands with Accounting: not in a pack, in a pack not yet sent, sent
to Accounting (date), or paid (date); the PO list and page show whether the PO pack was sent to Accounting.
The payment pack cover follows the owner's Inter-Office Memorandum templates (Toners / Parts & Maintenance covers): left
column with Subject, To (Accounts Department), From (the IT Manager), Date, Reference # (PAY-n), Attachment, Status and
Action boxes and Pages; on the right "ALTAWKILAT <year>", the invoices table (Date | Invoice no. | Amount, Total SAR),
"Please arrange to pay the supplier", Regards, the received-by block, IT Department (checked by whoever prepared the pack)
and Account Department signatures. Suppliers have an Arabic name too (shown on the list, the page and the cover).

### Asset search and history (owner, 22 October 2026)

The Assets search adds Model, Computer type, Processor and Memory (dropdowns of the values in use), the employee's
Active Directory username and SAP ID (AD streetAddress, as VTMS); the list shows who has each asset now. Each asset page
has a History section with tabs: Delivery (delivery notes, employee with username and SAP ID, signed copy, returned),
Receipt (receipt notes, condition, received into, technical assessment), Maintenance and All events.
Maintenance follows form 07 Maintenance Order and plan v1.9 (owner, 22 October 2026, replacing the first MT-n draft): a
Maintenance page and orders MO-n for assets repaired by an outside supplier. The IT Assets Officer creates the order (Asset
No. fills the product fields read-only; supplier, quotation no. and file, cost, maintenance details, expected supplier
return; no ServiceDesk ticket). The IT Manager, or the backup on leave, approves or rejects (reason required; never the
creator). Only then "Sent to the supplier" with the dispatch date: the asset is In Repair. "Back from the supplier" closes
it with the end date, outcome (repaired / not repairable), repair details, final cost and invoice no. (required when
repaired; linked to the recorded invoice of that supplier when it exists); the asset goes back to its holder (In Use) or In
Store at the chosen location. Not repairable does not dispose: record a technical assessment. Overdue returns are shown.
Repairs IT does itself are not orders: "Record an IT repair" on the asset history. Emails IT-EM-09/51/52/53 come with the
email step.

## Step 3b (My IT Assets and device and accessory requests) — built 22 October 2026

- **My IT Assets** (every employee, first menu item; employees without an IT role land here): Waiting for your decision
  (request and cost approvals as the direct manager or the backup on leave; link to ink approvals), My assets (custody, with
  the delivery note PDF and whether the signed copy is in), My requests, My documents (delivery and receipt notes), My team
  (managers: each direct report's assets, open requests and notes not yet signed). No costs, keys or supplier details.
  Delivery notes stay paper-signed (no electronic Accept); gate passes and transfers come with step 4.
- **Device requests PR-n**: New or Replacement (the device replaced from the person's custody; its technical assessment is
  linked when recorded, otherwise attached); a manager can ask for a team member. The direct manager (or backup) approves;
  nobody approves their own request. The manager decides within 3 days (request approval, and again for the cost
  approval, counted from when it reached the manager); otherwise the request is cancelled automatically (a timer every 15
  minutes, and every page and decision checks first); the pages show the time left. The IT Assets Officer chooses the source:
  - From stock: reserves an In Store device (one request per device) and assigns an IT Support Specialist, who delivers it
    from the asset page and links the delivery note: Fulfilled.
  - New device: cost table (item, specification, quantity, unit cost before VAT; subtotal, VAT 15%, grand total) with the
    supplier and the quotation; the direct manager approves the cost; request document for Accounting (date recorded);
    Ordered (optional PO); delivered and linked: the request document and the delivery note print together.
  - IT accessory: assigned to a specialist, who prices it with the local shop; the manager approves the cost; bought with
    petty cash (shop, date, amount not above the approved total, receipt required): Fulfilled; no Asset No.
- **Request document** (bilingual A4 with logo): header, employee information with the cost center, request details, cost
  table with the amount in words, approvals recorded in IT Assets (who and when), IT Manager and Accounting signatures, and
  a QR code to /r/PR-n. No ServiceDesk ticket fields. Emails (IT-EM-15/18/26/27/28/31) come with the email step.
