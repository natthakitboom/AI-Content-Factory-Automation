-- Content Factory PostgreSQL bootstrap.
--
-- This file intentionally does not create or drop test databases.
-- Integration tests create databases named cf_test_<runid> through an explicit
-- CF_TEST_POSTGRES maintenance connection and drop only those databases.
-- The app database and its volume must survive normal make targets.

SELECT 'content factory init: per-run test databases are managed by tests' AS notice;
