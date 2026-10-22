-- IT Assets build step 3a: ink requests approved by the manager the ink officer chooses, supplier orders, supplier invoices (toners, devices,
-- parts and maintenance), ink deliveries (no paper note: the request document with its approval goes in the payment pack),
-- and the monthly payment pack for Accounting.
-- No toner stock list (owner, 22 October 2026).

INSERT itam_data.counter(counter_code,next_value) VALUES(N'InkRequest',1),(N'SupplierOrder',1),(N'PaymentPack',1);

-- Ink request INK-n: the Ink Officer of the region sends it to the approving manager chosen by IT (a senior manager, not the
-- Active Directory manager); that manager, or the manager's backup on leave, approves. A printer not yet in IT Assets is
-- requested with its serial number and model (owner, 22 October 2026); the region comes from the employee's region in Active
-- Directory and the office is the printer location (unknown region: every Ink Officer sees it). The Ink Officer links or
-- registers the printer before sending for approval.
CREATE TABLE itam_data.ink_request(
 request_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 request_no nvarchar(20) NOT NULL CONSTRAINT UX_itam_ink_request_no UNIQUE,
 printer_asset_id int NULL REFERENCES itam_data.asset(asset_id),
 new_serial nvarchar(100) NULL, new_model_id int NULL REFERENCES itam_ref.model(model_id), new_model nvarchar(100) NULL, employee_location nvarchar(200) NULL,
 employee_id uniqueidentifier NOT NULL REFERENCES itam_access.employee(employee_id),
 requested_by uniqueidentifier NOT NULL, requested_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
 toner nvarchar(100) NOT NULL, quantity int NOT NULL CHECK(quantity BETWEEN 1 AND 50), note nvarchar(500) NULL,
 region_code nvarchar(20) NULL REFERENCES itam_ref.region(region_code),
 status nvarchar(20) NOT NULL CHECK(status IN(N'Submitted',N'WaitingApproval',N'Approved',N'Rejected',N'Ordered',N'Delivered',N'Cancelled')),
 manager_id uniqueidentifier NULL, sent_by uniqueidentifier NULL, sent_at_utc datetime2 NULL, decided_by uniqueidentifier NULL, decided_at_utc datetime2 NULL, reject_reason nvarchar(500) NULL,
 order_id int NULL, cancelled_reason nvarchar(500) NULL, version rowversion NOT NULL,
 CONSTRAINT CK_itam_ink_printer CHECK(printer_asset_id IS NOT NULL OR new_serial IS NOT NULL));
CREATE INDEX IX_itam_ink_request_status ON itam_data.ink_request(status,region_code);
CREATE INDEX IX_itam_ink_request_manager ON itam_data.ink_request(manager_id,status);

-- Supplier order ORD-n: several approved requests in one order (today's order email); printed or emailed to the supplier.
CREATE TABLE itam_data.supplier_order(
 order_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 order_no nvarchar(20) NOT NULL CONSTRAINT UX_itam_supplier_order_no UNIQUE,
 supplier_id int NOT NULL REFERENCES itam_ref.supplier(supplier_id), region_code nvarchar(20) NOT NULL REFERENCES itam_ref.region(region_code),
 note nvarchar(500) NULL, created_by uniqueidentifier NOT NULL, created_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
ALTER TABLE itam_data.ink_request ADD CONSTRAINT FK_itam_ink_request_order FOREIGN KEY(order_id) REFERENCES itam_data.supplier_order(order_id);

-- Payment pack PAY-n: picked invoices of one supplier (one category, or Mixed); cover memo + each invoice with its ink request documents.
CREATE TABLE itam_data.payment_pack(
 pack_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 pack_no nvarchar(20) NOT NULL CONSTRAINT UX_itam_payment_pack_no UNIQUE,
 supplier_id int NOT NULL REFERENCES itam_ref.supplier(supplier_id),
 category nvarchar(20) NOT NULL CHECK(category IN(N'Toners',N'Devices',N'PartsMaintenance',N'Mixed')), -- Mixed: invoices of several categories
 created_by uniqueidentifier NOT NULL, created_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
 sent_on date NULL, sent_by uniqueidentifier NULL, paid_on date NULL, paid_note nvarchar(200) NULL,
 signed_cover_file_id bigint NULL REFERENCES itam_data.stored_file(file_id));

-- Supplier invoice: number unique per supplier; PDF attached; lines with VAT 15%. Toner invoices link to their order;
-- device invoices link to the PO receipts they cover.
CREATE TABLE itam_data.invoice(
 invoice_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 supplier_id int NOT NULL REFERENCES itam_ref.supplier(supplier_id),
 invoice_no nvarchar(60) NOT NULL, invoice_date date NOT NULL,
 category nvarchar(20) NOT NULL CHECK(category IN(N'Toners',N'Devices',N'PartsMaintenance')),
 file_id bigint NOT NULL REFERENCES itam_data.stored_file(file_id),
 order_id int NULL REFERENCES itam_data.supplier_order(order_id), po_id int NULL REFERENCES itam_data.po(po_id),
 net decimal(18,2) NOT NULL, vat decimal(18,2) NOT NULL, total decimal(18,2) NOT NULL, note nvarchar(500) NULL,
 pack_id int NULL REFERENCES itam_data.payment_pack(pack_id),
 created_by uniqueidentifier NOT NULL, created_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
 CONSTRAINT UX_itam_invoice UNIQUE(supplier_id,invoice_no));
CREATE TABLE itam_data.invoice_line(
 line_id int IDENTITY(1,1) NOT NULL PRIMARY KEY, invoice_id int NOT NULL REFERENCES itam_data.invoice(invoice_id),
 line_no int NOT NULL, description nvarchar(200) NOT NULL, quantity int NOT NULL CHECK(quantity BETWEEN 1 AND 10000),
 unit_cost decimal(18,2) NOT NULL CHECK(unit_cost>=0), CONSTRAINT UX_itam_invoice_line UNIQUE(invoice_id,line_no));
ALTER TABLE itam_data.po_receipt ADD invoice_id int NULL CONSTRAINT FK_itam_receipt_invoice REFERENCES itam_data.invoice(invoice_id);

-- Ink delivery: from the IT shelf or from an invoice line. No delivery note and no signed copy (owner, 22 October 2026):
-- the payment pack carries the invoice and the ink request document with its approval.
CREATE TABLE itam_data.ink_delivery(
 ink_delivery_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 request_id int NOT NULL CONSTRAINT UX_itam_ink_delivery_request UNIQUE REFERENCES itam_data.ink_request(request_id),
 delivered_on date NOT NULL, source nvarchar(20) NOT NULL CHECK(source IN(N'Shelf',N'Invoice')),
 invoice_line_id int NULL REFERENCES itam_data.invoice_line(line_id), unit_cost decimal(18,2) NULL,
 issued_by uniqueidentifier NOT NULL, issued_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME());

-- Maintenance order MO-n (form 07, plan v1.9): an asset repaired by an outside supplier. The IT Assets Officer creates it
-- with the supplier, quotation (number and file), cost, maintenance details and expected return; the IT Manager (or the
-- backup on leave; never the creator) approves or rejects with a reason; only then is the asset sent (actual dispatch date,
-- asset In Repair); on return the order is closed with the outcome, repair details, final cost, invoice and end date, and the
-- asset goes back to its holder or In Store. No ServiceDesk ticket (owner, 22 October 2026). Repairs by IT are not orders:
-- they are recorded on the asset history. One open order per asset.
INSERT itam_data.counter(counter_code,next_value) VALUES(N'MaintenanceOrder',1);
CREATE TABLE itam_data.maintenance_order(
 order_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 order_no nvarchar(20) NOT NULL CONSTRAINT UX_itam_maintenance_order_no UNIQUE,
 asset_id int NOT NULL REFERENCES itam_data.asset(asset_id),
 supplier_id int NOT NULL REFERENCES itam_ref.supplier(supplier_id),
 quotation_no nvarchar(60) NOT NULL, quotation_file_id bigint NOT NULL REFERENCES itam_data.stored_file(file_id),
 cost decimal(18,2) NOT NULL CHECK(cost>=0), details nvarchar(1000) NOT NULL, expected_return date NOT NULL,
 status nvarchar(20) NOT NULL CHECK(status IN(N'WaitingApproval',N'Approved',N'Rejected',N'WithSupplier',N'Closed',N'Cancelled')),
 decided_by uniqueidentifier NULL, decided_at_utc datetime2 NULL, rejection_reason nvarchar(500) NULL,
 dispatched_on date NULL, dispatched_by uniqueidentifier NULL, previous_status nvarchar(20) NULL,
 returned_on date NULL, return_location_id int NULL REFERENCES itam_ref.location(location_id),
 outcome nvarchar(20) NULL CHECK(outcome IN(N'Repaired',N'NotRepairable')), repair_details nvarchar(1000) NULL,
 final_cost decimal(18,2) NULL CHECK(final_cost>=0), invoice_no nvarchar(60) NULL, invoice_id int NULL REFERENCES itam_data.invoice(invoice_id),
 closed_by uniqueidentifier NULL, cancelled_reason nvarchar(500) NULL,
 created_by uniqueidentifier NOT NULL, created_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(), version rowversion NOT NULL);
CREATE UNIQUE INDEX UX_itam_maintenance_order_open ON itam_data.maintenance_order(asset_id) WHERE status IN(N'WaitingApproval',N'Approved',N'WithSupplier');

-- IT procurement request PR-n (plan v1.9 step 3, decisions of 20 October 2026): the employee (or the manager for a team
-- member) asks for a device, New or Replacement (the device replaced and its technical assessment); the direct manager (or
-- the backup on leave) approves with one click. The IT Assets Officer chooses the source: From stock (reserve an In Store
-- device, assign an IT Support Specialist who delivers it), New device (cost table, the manager approves the cost, purchase
-- request PDF for Accounting, ordered on a PO, delivered) or IT accessory (priced in the region by a specialist, the
-- manager approves the cost, bought with petty cash, receipt required; no Asset No.). No ServiceDesk ticket (owner).
CREATE TABLE itam_data.request(
 request_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 request_no nvarchar(20) NOT NULL CONSTRAINT UX_itam_request_no UNIQUE,
 employee_id uniqueidentifier NOT NULL REFERENCES itam_access.employee(employee_id),
 requested_by uniqueidentifier NOT NULL, requested_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
 request_type nvarchar(20) NOT NULL CHECK(request_type IN(N'New',N'Replacement')),
 replaced_asset_id int NULL REFERENCES itam_data.asset(asset_id), assessment_file_id bigint NULL REFERENCES itam_data.stored_file(file_id),
 assessment_no nvarchar(40) NULL, description nvarchar(2000) NOT NULL,
 region_code nvarchar(20) NULL REFERENCES itam_ref.region(region_code), department nvarchar(200) NULL,
 status nvarchar(30) NOT NULL CHECK(status IN(N'WaitingRequestApproval',N'Rejected',N'InReview',N'StockAssigned',N'PricingInRegion',
   N'WaitingCostApproval',N'Approved',N'Ordered',N'CashPurchase',N'Fulfilled',N'Cancelled')),
 manager_id uniqueidentifier NULL, approved_by uniqueidentifier NULL, approved_at_utc datetime2 NULL, reject_reason nvarchar(500) NULL, rejected_by uniqueidentifier NULL,
 source nvarchar(20) NULL CHECK(source IN(N'Stock',N'NewDevice',N'Accessory')), reviewed_by uniqueidentifier NULL, reviewed_at_utc datetime2 NULL,
 assigned_to uniqueidentifier NULL, reserved_asset_id int NULL REFERENCES itam_data.asset(asset_id),
 supplier_id int NULL REFERENCES itam_ref.supplier(supplier_id), shop nvarchar(200) NULL, quotation_file_id bigint NULL REFERENCES itam_data.stored_file(file_id),
 priced_by uniqueidentifier NULL, priced_at_utc datetime2 NULL, cost_approved_by uniqueidentifier NULL, cost_approved_at_utc datetime2 NULL,
 po_id int NULL REFERENCES itam_data.po(po_id), ordered_at_utc datetime2 NULL,
 paid_amount decimal(18,2) NULL CHECK(paid_amount>=0), purchase_date date NULL, receipt_file_id bigint NULL REFERENCES itam_data.stored_file(file_id),
 delivery_id int NULL REFERENCES itam_data.delivery(delivery_id), fulfilled_at_utc datetime2 NULL, fulfilled_by uniqueidentifier NULL,
 accounting_sent_on date NULL, accounting_sent_by uniqueidentifier NULL, cancelled_reason nvarchar(500) NULL, version rowversion NOT NULL);
CREATE INDEX IX_itam_request_status ON itam_data.request(status);
-- A device reserved on one open request only.
CREATE UNIQUE INDEX UX_itam_request_reserved ON itam_data.request(reserved_asset_id) WHERE reserved_asset_id IS NOT NULL AND status=N'StockAssigned';
-- Cost table: item, specification, quantity and unit cost before VAT (line total, subtotal, VAT 15% and grand total computed).
CREATE TABLE itam_data.request_line(
 line_id int IDENTITY(1,1) NOT NULL PRIMARY KEY, request_id int NOT NULL REFERENCES itam_data.request(request_id),
 line_no int NOT NULL, item nvarchar(200) NOT NULL, specification nvarchar(300) NULL,
 quantity int NOT NULL CHECK(quantity BETWEEN 1 AND 1000), unit_cost decimal(18,2) NOT NULL CHECK(unit_cost>=0),
 CONSTRAINT UX_itam_request_line UNIQUE(request_id,line_no));

-- Stock movements (plan v1.9 step 4). One engine for three kinds:
--  Branch:    branch-to-branch transfer on a gate pass GP-n: the employee (own custody) or IT asks; the IT Helpdesk Team Leader
--             (or backup; never their own) approves; the named sender dispatches within the dispatch validity (exit date + 3
--             days); the named receiving employee confirms each asset; custody passes to the receiver with a new delivery note.
--  Exit:      other building exits on a gate pass (customer site, work from home, repair outside...): returnable passes close on
--             the confirmed return.
--  Warehouse: IT warehouse-to-warehouse transfer WT-n: no approval (owner, 20 October 2026); source IT submits and the stock is
--             reserved on an issued pass; partial dispatch and receipt; the named destination IT receiver confirms each asset.
-- Discrepancies (missing, damaged, incorrect) are resolved per asset by the Team Leader (or backup) with evidence; a movement
-- closes Completed, or Closed with Exceptions. Expiry of the dispatch validity never completes a dispatched movement.
ALTER TABLE itam_data.delivery ADD closed_by_movement_id int NULL; -- custody passed on a branch transfer (no receipt note)
CREATE TABLE itam_data.movement(
 movement_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 movement_no nvarchar(20) NOT NULL CONSTRAINT UX_itam_movement_no UNIQUE, -- GP-n (branch, exit) or WT-n (warehouse)
 kind nvarchar(20) NOT NULL CHECK(kind IN(N'Branch',N'Exit',N'Warehouse')),
 pass_no nvarchar(20) NOT NULL, pass_version int NOT NULL DEFAULT 1,
 requested_by uniqueidentifier NOT NULL, requested_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(), employee_id uniqueidentifier NULL,
 purpose nvarchar(60) NOT NULL, purpose_details nvarchar(1000) NULL, pass_type nvarchar(20) NOT NULL CHECK(pass_type IN(N'Returnable',N'NonReturnable')),
 from_location_id int NOT NULL REFERENCES itam_ref.location(location_id), to_location_id int NULL REFERENCES itam_ref.location(location_id), destination nvarchar(300) NULL,
 exit_date date NOT NULL, valid_until date NOT NULL, expected_date date NULL,
 carried_by nvarchar(20) NOT NULL CHECK(carried_by IN(N'Employee',N'Other')), carrier_name nvarchar(200) NULL, carrier_mobile nvarchar(30) NULL,
 sender_id uniqueidentifier NULL, receiver_id uniqueidentifier NULL, maintenance_order_id int NULL REFERENCES itam_data.maintenance_order(order_id),
 status nvarchar(30) NOT NULL CHECK(status IN(N'Submitted',N'Approved',N'Rejected',N'Cancelled',N'Expired',N'PartiallyDispatched',N'InTransit',N'Out',
   N'PartiallyReceived',N'ReceiptDisputed',N'AwaitingCustody',N'Completed',N'ClosedWithExceptions')),
 decided_by uniqueidentifier NULL, decided_at_utc datetime2 NULL, reject_reason nvarchar(500) NULL, cancelled_reason nvarchar(500) NULL,
 dispatched_at_utc datetime2 NULL, dispatched_by uniqueidentifier NULL, completed_at_utc datetime2 NULL, version rowversion NOT NULL);
CREATE INDEX IX_itam_movement_status ON itam_data.movement(status);
CREATE TABLE itam_data.movement_line(
 line_id int IDENTITY(1,1) NOT NULL PRIMARY KEY, movement_id int NOT NULL REFERENCES itam_data.movement(movement_id),
 asset_id int NOT NULL REFERENCES itam_data.asset(asset_id), previous_status nvarchar(20) NOT NULL, holder_id uniqueidentifier NULL,
 state nvarchar(20) NOT NULL CHECK(state IN(N'Pending',N'Dispatched',N'Received',N'Missing',N'Damaged',N'Incorrect',N'Returned',N'Lost',N'NeverDispatched',N'Cancelled')),
 dispatched_at_utc datetime2 NULL, received_at_utc datetime2 NULL, received_by uniqueidentifier NULL, condition_note nvarchar(500) NULL,
 issue_reported_by uniqueidentifier NULL, issue_reported_at_utc datetime2 NULL,
 resolution nvarchar(30) NULL CHECK(resolution IN(N'ArrivedLater',N'NeverDispatched',N'ReturnedToSource',N'ConfirmedLost',N'AcceptedAtDestination')),
 resolution_note nvarchar(1000) NULL, resolved_by uniqueidentifier NULL, resolved_at_utc datetime2 NULL, evidence_file_id bigint NULL REFERENCES itam_data.stored_file(file_id),
 new_delivery_id int NULL REFERENCES itam_data.delivery(delivery_id));
-- An asset is on one open movement at a time (lines still pending, dispatched or disputed).
CREATE UNIQUE INDEX UX_itam_movement_line_open ON itam_data.movement_line(asset_id) WHERE state IN(N'Pending',N'Dispatched',N'Missing',N'Damaged',N'Incorrect');

-- The cost table of a request is replaced as a whole when IT edits it.
GRANT DELETE ON itam_data.request_line TO itam_runtime;
