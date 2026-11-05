-- Least privilege for extraction (NCUA Part 748 App. A alignment): the extract login
-- KSEXTRACT can connect and read the KS schema, nothing else.
-- The operating-system user behind KSEXTRACT is created by custom/create_extract_user.sh,
-- because Db2 authenticates against OS accounts.
--
-- The Db2 Community image creates the database without granting any database authority
-- to PUBLIC (SYSCAT.DBAUTH lists only the instance owner), so no REVOKE is needed here.
-- If a database ever grants PUBLIC CREATETAB, BINDADD or IMPLICIT_SCHEMA, revoke them
-- before relying on these grants; AT-19 proves the extract login cannot write.

GRANT CONNECT ON DATABASE TO USER KSEXTRACT;
GRANT SELECTIN ON SCHEMA KS TO USER KSEXTRACT;
