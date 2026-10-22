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
  (request and cost approvals as the direct manager, or as the IT Manager for every request (owner, 22 October 2026), or a
  backup on leave; never their own request; link to ink approvals), My assets (custody, with
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

## Permission matrix applied (owner, 22 October 2026)

The owner's edited matrix (IT Assets - Permission Matrix, 22 October 2026) is applied; the system rules are in `ItamScope`
and the applied matrix is regenerated as "IT Assets - Permission Matrix (applied 22-10-2026).xlsx".
- The **IT Administrator** and **Auditor** roles are removed: seven IT roles remain. The IT Manager gives roles, keeps
  master data and reads the audit log.
- The **System Administrator** and **IT Manager** work in every region: assets, keys, labels, POs, handover, ink handling,
  invoices of every category, suppliers and maintenance orders; the System Administrator confirms both AD and CUCM removals.
- The **IT Helpdesk Team Leader** also confirms AD removals, sends ink for approval, approves maintenance orders and sees and
  sets backups (with the System Administrator and the IT Manager).
- **IT Support Specialists**: keys and labels in their regions; all POs, receiving only into warehouses and branches of
  their regions; parts invoices; link or register printers; add suppliers; create and follow maintenance orders.
- Maintenance orders are approved by the Team Leader, the System Administrator or the IT Manager (or a backup on leave),
  never by the person who created the order.
- **Several roles per person** (owner, 22 October 2026: the IT Manager is the System Administrator; the Ink Officer and the
  IT Assets Officer are IT Support Specialists): the Roles page lists one row per person with all their roles; "Give roles"
  and "Give more roles" tick several roles and the regions of the per-region roles at once; each role is removed on its
  own. Rights add up; the region lists on the forms follow the widest role the person holds.

## Step 4 (gate passes, branch and warehouse transfers, discrepancies) — built 22 October 2026

- **Gate pass GP-n** (Movements, or "My gate passes and transfers" on My IT Assets): an employee takes their own assets
  out; IT can make a pass for any asset of their regions. Two kinds:
  - **Branch transfer**: from one branch to another, with a named receiver at the destination.
  - **Exit pass**: out of the building for a purpose (work from home, exhibition, repair, other with details);
    Returnable or Non-returnable.
- Every gate pass is approved by the **IT Helpdesk Team Leader, the System Administrator or the IT Manager** (owner,
  22 October 2026: like maintenance orders), or a backup on leave, never by the requester; the
  approver can shorten the validity. The pass prints only after approval (bilingual A4, logo, assets table, signatures
  for the sender, Security and the receiver, QR code to /g/GP-n). It is valid for dispatch for **3 days** from the exit
  date; an approved pass not dispatched in time expires.
- **Dispatch** by the named sender or IT: the assets become In Transit (transfer) or Out (exit pass). A non-returnable
  exit pass completes on dispatch; a returnable one completes when IT confirms the return.
- **Receipt** by the named receiver only, line by line: Received, Missing, Damaged or Incorrect.
  - Same holder: the asset's branch changes.
  - Different holder: the old delivery note is closed by the transfer (no receipt note) and the asset is In Store at the
    destination; a delivery starts only from In Store (owner, 22 October 2026), so IT of the destination delivers it to
    the receiver with a new delivery note signed on paper.
- **Warehouse transfer WT-n**: IT of the source region moves In Store stock to another IT warehouse; no approval; the
  receiver must be IT of the destination region. Lines can be dispatched in parts; the rest stays reserved or is
  cancelled. On receipt the assets are In Store at the destination, with no delivery note.
- **Discrepancies**: any Missing, Damaged or Incorrect line puts the movement in "Discrepancy pending". An approver
  resolves each line with an evidence file: Arrived later, Accepted at the destination, Returned to the source, Never
  dispatched or Confirmed lost (the asset becomes Lost). A movement with exceptions closes as "Closed with exceptions".
- An asset is on one open movement at a time; the picker shows only assets that can move. Several assets go on one pass or
  transfer at once (owner, 22 October 2026): tick them in the list, "Select all shown", or scan / paste several Asset Nos.
  or serials (one per line); at most 200 assets on one movement.
- To confirm: Movements rights are not yet in the permission matrix.
  Emails (IT-EM-19/20/32-38/57) come with the email step.

### Requester panel (owner, 22 October 2026)

Every request shows, on the right, all the information imported from Active Directory about the employee: name, username,
enabled or disabled, job description, department, company, branch (office), region, HR code, SAP ID, mobile and IP phone
(click to call), email and direct manager, with when it was last read from AD. Empty attributes show a dash, so gaps in AD
are visible. Device requests (PR-n) and gate passes and transfers (GP-n, WT-n: the employee responsible, or the requester)
show it beside the page; on the Ink page the employee's name opens the same panel in a side drawer. The IP phone
(AD ipPhone, as VTMS) is now kept on the employee copy. The national ID is never read.

## Step 5 (end of life and AD) — built 22 October 2026

New menu section **End of life**: Leavers, Loss cases, Disposals, Sales, AD comparison, Licenses. The asset page has
Report loss, Ask for disposal and Record sale.

- **Loss cases LC-n** (forms 22 to 25): the holder or IT of the region reports; the asset is Lost and the custody stays
  open (no receipt, no stock). The IT Manager assigns an investigator (never the holder); the System Administrator, the
  Team Leader or the IT Manager records security actions; the investigator records findings and a recommendation; the IT
  Manager (or a backup on leave) records the decision with the signed decision, never the reporter, the holder or the
  investigator: Not liable (responsibility cleared, custody closed by the loss decision, no receipt note) or Settlement
  (cleared only when the IT Assets Officer or IT Manager records the settlement evidence). The write-off is separate:
  asked by the IT Assets Officer or IT Manager, approved by the IT Manager; it never clears the employee. Recovery: IT
  receives the asset into their region, then the IT Assets Officer or IT Manager reviews it back to stock. A transfer line
  confirmed lost opens a loss case. A Lost asset cannot be received through the normal receipt.
- **Disposals DS-n** (forms 26 to 28): IT of the region asks for an asset In Store (or the technical assessment decides
  Dispose, which now opens the request itself); the asset is Pending disposal approval and blocked; the IT Manager (or a
  backup) approves or rejects, never their own request; approval alone never disposes; the IT Assets Officer or IT Manager
  records the completion (date, method, person, recipient, evidence file; for computers data wiped and licenses removed);
  only then is the asset Disposed. Rejection or cancel returns it In Store.
- **Sales SL-n** (form 21): any IT role, with the proof of payment, no approval; when the asset is in someone's custody
  only the holder buys it and the custody closes with the sale (no receipt note); computers get the AD/Endpoint removal
  task and IP phones the CUCM task (also when sold from stock). Sales list with buyers, prices and proofs.
- **Leavers CL-n** (form 29, form 30, Assets to collect): only the System Administrator marks an employee Resigned; every
  open delivery note becomes Owner Type Resigned (the old owner type is kept and restored if the clearance is cancelled)
  and appears on Assets to collect, grouped by employee with counts per region (specialists see their regions); a leaver
  gets no new deliveries; lost assets are listed apart. The clearance is completed by the System Administrator or the IT
  Manager only when no custody and no uncleared loss remain; then the System Administrator confirms the user is disabled
  in AD and kept.
- **AD computer comparison** (form 34): Compare now (System Administrator, IT Assets Officer, IT Manager) reads the
  computer accounts (read-only) and compares them with the computer names on active delivery notes (Owner Type Other left
  out), without case and spaces: Matched; In AD, no delivery note; Removal waiting; On a delivery note, not in AD (with
  the OU when elsewhere); Matched, needs a check (disabled or no sign-in for 90 days). Every run is kept; a line marked
  reviewed stays hidden until the computer changes. The demo uses an invented directory; the daily run and the settings
  (OUs, name patterns, inactive days) come with the AD connection and the settings page.
- **Licenses**: software licenses (type Software, new optional Seats field) and Windows keys of computers, with holders;
  keys never appear in the list (each opens from its asset page, logged).
- Confirmed by the owner (22 October 2026): the IT Manager, or a backup on leave, is the only person who decides losses,
  approves write-offs and approves disposals. Still to confirm: the System Administrator or IT Manager completing
  clearances. Emails (IT-EM-10/11/24/41-50/63/64)
  come with the email step.

## Alerts (owner, 22 October 2026)

Every role has **Alerts** at the top of the menu, with the total of what waits for the signed-in person. The page groups
the pending work by module, each with a count, the first references (PR-, GP-, delivery note numbers...) and a link to
the page where the work is done; items with a deadline or overdue are marked urgent:
- Decisions: device requests (direct manager or IT Manager), ink, maintenance, gate passes, disposals, loss cases.
- Movements: gate passes to dispatch, warehouse transfers to dispatch, assets to receive (named receiver), discrepancies
  to resolve, exit passes overdue for return. Employees see their own passes (linked to My IT Assets).
- Handover: delivery notes waiting for the signed copy (regions), AD / Endpoint Central / CUCM removals.
- Requests (IT): to review, accessories to price or buy, approved to order, to deliver.
- Ink (handlers): printers to link or register, requests to send for approval, approved ink to order or deliver.
- Maintenance: approved orders to send, assets overdue at the supplier.
- End of life: assets to collect, clearances ready to complete, users to disable in AD, disposals to complete, loss
  settlements and recovered assets to review, AD comparison differences.
Emails and the weekly summary (IT-EM-12, IT-EM-61) will use the same list when the email step is built.

## Look and table search before production (owner, 22 October 2026)

- Look: section labels in blue capitals, page and card titles in navy with a blue accent bar on each card heading, tinted
  table headings, row highlight on hover, the active menu item marked with a blue bar.
- Search on every list: a box above each table searches all columns, or one chosen column ("In Employee", "In Asset"...);
  every word must match (case ignored); it shows "n of m" rows and "No row matches" when nothing does; a click on a column
  heading sorts it (dates, amounts and numbers like PC-4816 in their real order), a second click reverses. The search is
  kept when the page redraws the list after an action. Lists that already have their own search (Assets with its full
  search panel, Handover) keep theirs; lists of fewer than three rows have no box.

## Step 6a: dashboard and reports — built 22 October 2026

- **Dashboard** (IT roles; each sees the assets of their own scope): tiles for assets in service, in use, in store, in
  repair, in transit, pending disposal, lost, delivery notes not signed and alerts (each opens the matching list); money
  tiles for maintenance cost and sales this year and toner delivered this month (for the roles of those modules); the most
  urgent alerts; charts of assets by type and by region (in use and in store; a bar opens the Assets list filtered), all
  assets by status, stock in store per location, ageing from the purchase date, and deliveries per month.
- **Available computers by computer type** (owner, 22 October 2026): computers In Store, minus those reserved for a
  device request, per computer type (Desktop, Notebook, All In One...): a dashboard tile and chart (a bar opens the Assets
  list filtered by computer type), a summary report by type and location with subtotals, and a list with each computer's
  processor, memory, storage and operating system.
- **Reports** (16, grouped by area): asset register; by type and status; by region and location; assets per employee;
  available computers by computer type; available computers (list);
  ageing; delivery notes not signed; leavers holding assets; maintenance cost; ink use; purchase orders received;
  gate passes and transfers; sales; disposals; loss cases. Filters: dates (where the report has a date) and region.
  Every report keeps the person's visibility (assets by type and region; unit costs only for the purchase roles; module
  reports only for that module's roles), opens on screen with search and sorting (total rows stay at the bottom), and
  exports to Excel (.xlsx: heading row frozen, filters, numbers as numbers). Opening a report is in the audit log.
- Still in step 6: the emails (IT-EM templates) and the SharePoint migration (on hold until the owner says).

## Step 6b: emails — built 22 October 2026 (local demo; no SMTP)

- **Layout like CMS** (owner, 22 October 2026): centred white card, embedded logo (cid:logo), "IT Assets Management System"
  underlined in navy, the automatic-notification line, "Dear ..." in red, the action sentence, a two-column details table
  with shaded labels and the record link, sections with red headings, "Best Regards", the person who acted and "ALTAWKILAT
  IT Team", and a footer with the IT-EM number. Values are HTML-encoded. Triggers, recipients and subjects follow the owner's
  notification manifest (IT Assets\Email Templates\notification_manifest.json); subject "[IT Assets] <name> - <record>".
- **Outbox**: each email is written in the same transaction as the action (once per event key), then a background sender
  sends it after commit through the mail service (interface `IItamMailer`; SMTP comes with the production set-up). Without
  a mail service nothing leaves the server and the emails stay Queued. Failures are retried 5 times, then Failed (never
  emailed); the IT Manager sends them again from the **Email outbox** page (Administration), which lists every email with
  its recipients and status and shows it exactly as sent. A **test mailbox** setting redirects every email to one address
  with an amber notice naming the real recipients (as CMS during its test phase).
- **Recipients**: approvers and their backups on leave; the person who acted is not asked to act on their own record;
  To/CC de-duplicated; a person without an email address is left out; an email with nobody to send to is kept as "No
  recipient" and the IT Manager receives IT-EM-56.
- **Events**: 01 delivery note to sign (paper), 02 receipt, 09/51/53 maintenance, 10/47/48/50 disposal, 11 leaver collection,
  13/23 AD and CUCM clean-up (return or sale), 15/18/26/27/28/31/54/55/58 requests, 19/20/57/32/33/34/35/36/38 movements,
  29/30/59 ink, 41-46 loss (no investigation details in emails), 62 technical assessment, 64 disable the AD user, 25 backup.
- **Reminders and weekly** (hourly job, each once per period): 04 signed copy waiting 3 days (issuer, every 3 days), 05 overdue
  7 days (IT Manager, weekly), 21 exit pass return overdue, 37 transfer receipt overdue, 49 disposal not completed in 14 days,
  52 maintenance overdue at the supplier, 63 loss case without action for 7 days, 11 collection reminder every 3 days, 61
  daily reminder of pending decisions, 12 weekly summary to the IT Manager and 24 weekly AD comparison report (Sunday 07:00).
  "Run reminders now" on the outbox page runs them on demand.
- Not sent (paper signing chosen): 03, 06, 07 (electronic acceptance); 60 (delegation returned) not built. Reminder timings
  above are defaults to confirm with the owner.
- **One-click approval from the email** (owner, 22 October 2026): the approval emails (IT-EM-31 request, 26/28 cost, 29 ink,
  09 maintenance, 19 gate pass, 10 disposal) carry green **Approve** and red **Reject** buttons (as in the owner's IT-EM-31
  template). They open the decision page of IT Assets with the record and the approval step. Opening the link never decides
  (a mail scanner or preview only reads). For the signed-in eligible approver (or backup on leave), Approve is recorded at
  once from the visible page, with no other click; Reject asks for the reason. The server accepts the decision only while
  the record is still at that step, so an old email, a second click or another step does nothing ("Already decided"),
  and anyone else sees "Not your decision". The same rules as on the record's page apply. Loss decisions need the signed
  decision file, so their email links to the case.
- **Replies reach the person who acted** (owner, 22 October 2026; the contact line was removed the same day): every email caused by a person's action (for example the
  delivery note email, from the IT person who issued it) has that person as Reply-To, and the line under the title reads
  "Questions? Reply to this email to reach <name>, or call <mobile> · ext <extension>" (tap-to-call). Automatic emails
  (reminders, weekly summary) have no Reply-To and keep "please don't reply". No shared mailbox.
  Those emails show no notice line (the "Questions? Reply ..." line was removed at the owner's request); the Reply-To stays.
- **Review pack** (22 October 2026): IT Assets\Email Templates - Review (22-10-2026): the Excel sheet of every email with
  its condition, recipients, Reply-To, subject, buttons and timing, an index page, and an example of each email made by the
  system with invented data (`ITAM_MAIL_SAMPLES=<folder>` makes the checks write the examples).

## Step 7a: production host — built 22 October 2026 (local; nothing installed on a server)

- New project `src/Itam.Host` (its own IIS site, .NET 10, Windows only). Settings in `appsettings.Production.json`
  (section ItamHost; template in the package) with **no password**: SQL Server (Windows authentication, encrypted
  connection), AD over LDAPS and the mail relay use the IIS application pool identity. The site refuses wrong settings
  (database other than ITAM_Test / ITAM_Production, http address, relative keys folder).
- **Windows sign-in**: IIS Windows authentication (anonymous off, in web.config); the enabled employee with the same
  account is let in; disabled or unknown accounts are not.
- **Active Directory** (read only, as VTMS: LDAPS 636, Negotiate, paged): the users OU every hour into the employee copy
  (a read that would disable more than half of the enabled employees is refused), the AD computer comparison every day at
  the configured hour. Attributes as the build plan; the national ID is never read.
- **Email**: SMTP through the company relay (as CMS): HTML with the embedded logo, plain-text copy, To/CC, Reply-To;
  sent only when Mail:Enabled; Mail:TestRecipient redirects every email during the test phase.
- **Keys**: ASP.NET data protection keys in their own folder, protected by DPAPI for the server; backed up with the
  database.
- **Database**: `Itam.Host.exe --install-database` (by an administrator) applies the scripts; the site refuses to start
  while a script is pending. The site's own identity gets the `itam_runtime` role only (deploy\sql\Grant-ItamRuntime.sql).
  The checks now run the store with that role only: two missing rights were found and granted (deleting PO lines and
  request cost lines). `/health` answers OK for monitoring.
- **Deploy**: `deploy\Build-ItamPackage.ps1` (checks, then the zip), `deploy\Install-ItamSite.ps1` (on the server: offline
  page, backup, copy, optional database install, health check), `deploy\README.md` (what IT and the DBA prepare).
- Tested locally against a throwaway ITAM_Test LocalDB database: refused before install, install 5/5 then 0/5, /health
  OK, the API refuses a request without Windows sign-in. AD and SMTP are tested with the checks (mapping, .eml output);
  the real AD, relay and IIS are tested on the test site.
- **Owner decision, 22 October 2026: production directly** (ITAM_Production), no separate test site. Until the owner
  declares it live it runs in a test phase: email off or to one test mailbox, access limited to an IT pilot AD group in
  IIS, backups of the database and the keys folder before every update; every production change approved by the owner.
- **Servers** (owner, 22 October 2026): the same as the shared platform: IIS on ATWK-WEB-1, SQL Server 2025 on ATWK-SQL-1,
  with IT Assets' own site, application pool, folders and database ITAM_Production. The SQL connection trusts the server
  certificate as the shared platform does (setting `SqlTrustServerCertificate`), and stays encrypted.
- **Site address** (owner, 22 October 2026): **https://it.altawkilat.com** on ATWK-WEB-1, with the installed wildcard
  certificate *.altawkilat.com; a DNS record it.altawkilat.com -> ATWK-WEB-1 (192.168.201.20) is needed.
