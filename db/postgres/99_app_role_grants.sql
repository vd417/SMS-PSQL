-- Grants for the sms_app role (see 00_app_role.sql). Runs last (alphabetically after every
-- table/function-creating file) so ALL TABLES/ALL FUNCTIONS/ALL SEQUENCES IN SCHEMA catches
-- everything already created by this point, in both sms_dev and each disposable test database.
GRANT USAGE ON SCHEMA dbo, rls TO sms_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA dbo TO sms_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA dbo TO sms_app;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA dbo TO sms_app;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA rls TO sms_app;
