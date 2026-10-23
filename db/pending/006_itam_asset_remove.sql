-- IT Assets: the IT Manager removes an asset registered by mistake (owner, 23 October 2026), only while it was never used
-- (no delivery, transfer, maintenance, ink request, request, loss, disposal or sale). The audit log keeps a full copy of the
-- removed record and the reason. The site may delete the asset and its own details (fields, encrypted keys, history) only.
GRANT DELETE ON itam_data.asset TO itam_runtime;
GRANT DELETE ON itam_data.asset_attribute TO itam_runtime;
GRANT DELETE ON itam_data.asset_secret TO itam_runtime;
GRANT DELETE ON itam_data.asset_event TO itam_runtime;
