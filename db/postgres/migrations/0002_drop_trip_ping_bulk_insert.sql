-- 0002: drop dbo.trip_ping_bulk_insert, the worked TVP example from
-- db/postgres/08_sample_procedure_conversions.sql. Nothing calls it: TransportModule calls
-- "dbo.TripPing_BulkInsert", which folds to dbo.tripping_bulkinsert (14_transport_procs.sql).
-- Not destructive: it removes an unused function and touches no data.

DROP FUNCTION IF EXISTS dbo.trip_ping_bulk_insert(uuid, uuid, jsonb);
