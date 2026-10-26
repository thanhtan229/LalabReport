-- Add bill contact/shipping snapshots used by cloud bill DTO and label SVG.
-- Split from 0007 because production applied 0007 before these columns were added.
ALTER TABLE cloud_bills ADD COLUMN phone_snapshot TEXT;
ALTER TABLE cloud_bills ADD COLUMN shipping_address_snapshot TEXT;
ALTER TABLE cloud_bills ADD COLUMN note TEXT;
