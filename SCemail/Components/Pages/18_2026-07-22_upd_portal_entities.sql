-- Aggiunta colonna visibility a portal_webgis
ALTER TABLE idt.portal_webgis
ADD COLUMN IF NOT EXISTS visibility VARCHAR(30);

ALTER TABLE idt.portal_faqs
ADD COLUMN IF NOT EXISTS featured BOOLEAN;

DROP INDEX IF EXISTS idt.idx_portal_surveys_visibility_status_publish_date;

ALTER TABLE idt.portal_surveys
DROP COLUMN IF EXISTS visibility;