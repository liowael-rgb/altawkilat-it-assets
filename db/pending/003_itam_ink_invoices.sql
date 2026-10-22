-- IT Assets build step 3a: ink requests approved by the manager the ink officer chooses, supplier orders, supplier invoices (toners, devices,
-- parts and maintenance), ink deliveries (no paper note: the request document with its approval goes in the payment pack),
-- and the monthly payment pack for Accounting.
-- No toner stock list (owner, 22 October 2026).

INSERT itam_data.counter(counter_code,next_value) VALUES(N'InkRequest',1),(N'SupplierOrder',1),(N'PaymentPack',1);

-- Ink request INK-n for an active printer: the ink officer (IT of the region) sends it to the approving manager chosen by IT
-- (a senior manager, not the Active Directory manager); that manager, or the manager's backup on leave, approves.
CREATE TABLE itam_data.ink_request(
 request_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 request_no nvarchar(20) NOT NULL CONSTRAINT UX_itam_ink_request_no UNIQUE,
 printer_asset_id int NOT NULL REFERENCES itam_data.asset(asset_id),
 employee_id uniqueidentifier NOT NULL REFERENCES itam_access.employee(employee_id),
 requested_by uniqueidentifier NOT NULL, requested_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
 toner nvarchar(100) NOT NULL, quantity int NOT NULL CHECK(quantity BETWEEN 1 AND 50), note nvarchar(500) NULL,
 region_code nvarchar(20) NOT NULL REFERENCES itam_ref.region(region_code),
 status nvarchar(20) NOT NULL CHECK(status IN(N'Submitted',N'WaitingApproval',N'Approved',N'Rejected',N'Ordered',N'Delivered',N'Cancelled')),
 manager_id uniqueidentifier NULL, sent_by uniqueidentifier NULL, sent_at_utc datetime2 NULL, decided_by uniqueidentifier NULL, decided_at_utc datetime2 NULL, reject_reason nvarchar(500) NULL,
 order_id int NULL, cancelled_reason nvarchar(500) NULL, version rowversion NOT NULL);
CREATE INDEX IX_itam_ink_request_status ON itam_data.ink_request(status,region_code);
CREATE INDEX IX_itam_ink_request_manager ON itam_data.ink_request(manager_id,status);

-- Supplier order ORD-n: several approved requests in one order (today's order email); printed or emailed to the supplier.
CREATE TABLE itam_data.supplier_order(
 order_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 order_no nvarchar(20) NOT NULL CONSTRAINT UX_itam_supplier_order_no UNIQUE,
 supplier_id int NOT NULL REFERENCES itam_ref.supplier(supplier_id), region_code nvarchar(20) NOT NULL REFERENCES itam_ref.region(region_code),
 note nvarchar(500) NULL, created_by uniqueidentifier NOT NULL, created_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
ALTER TABLE itam_data.ink_request ADD CONSTRAINT FK_itam_ink_request_order FOREIGN KEY(order_id) REFERENCES itam_data.supplier_order(order_id);

-- Payment pack PAY-n: one supplier and category; cover memo + every invoice with its order and signed notes.
CREATE TABLE itam_data.payment_pack(
 pack_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 pack_no nvarchar(20) NOT NULL CONSTRAINT UX_itam_payment_pack_no UNIQUE,
 supplier_id int NOT NULL REFERENCES itam_ref.supplier(supplier_id),
 category nvarchar(20) NOT NULL CHECK(category IN(N'Toners',N'Devices',N'PartsMaintenance')),
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
