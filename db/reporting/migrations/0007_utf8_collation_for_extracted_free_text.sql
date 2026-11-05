-- ADR 0005 (Db2 type handling): free-text columns copied from KeyStone keep every character.
-- KeyStone's Db2 database uses code set UTF-8 with string_units SYSTEM, so VARCHAR(n) holds n UTF-8
-- bytes. Under this database's default collation (SQL_Latin1_General_CP1_CI_AS) SqlBulkCopy silently
-- best-fits characters outside Windows-1252 ('Ω' becomes 'O', '✓' becomes '?'); a UTF-8 collation
-- stores them exactly, and varchar(n) then counts bytes just as Db2 does.
-- Codes and identifiers (CHAR columns, CARD_PROCESSOR_REF, BATCH_REF) stay ASCII under the default
-- collation, so joins with ops and ref tables need no COLLATE clauses.
ALTER TABLE stg.KsAccount ALTER COLUMN DESCRIPTION varchar(60) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
ALTER TABLE stg.KsPostedTxn ALTER COLUMN DESCRIPTION varchar(60) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
ALTER TABLE stg.KsGlEntry ALTER COLUMN MEMO varchar(80) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
GO

ALTER TABLE core.Account ALTER COLUMN Description varchar(60) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
ALTER TABLE core.PostedTransaction ALTER COLUMN Description varchar(60) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
ALTER TABLE core.GlEntry ALTER COLUMN Memo varchar(80) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
GO
