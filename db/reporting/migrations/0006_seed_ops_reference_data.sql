-- Extract configuration: the three KeyStone tables copied by PL_ExtractKeyStone.
-- SourceColumns lists columns explicitly (never SELECT *) so a schema change in the source
-- surfaces as a failure rather than silent drift.
INSERT INTO ops.ExtractConfig
    (ExtractConfigId, SourceName, SourceSchema, SourceTable, SourceColumns, KeyColumns, DateColumn, TargetSchema, TargetTable, SortOrder, IsEnabled)
VALUES
    (1, 'Transactions', 'KS', 'POSTED_TXN',
        'TXN_ID, ACCOUNT_NBR, TXN_CODE, POSTING_DATE, EFFECTIVE_DATE, POSTED_TS, AMOUNT, DESCRIPTION, CHANNEL, CARD_PROCESSOR_REF, REVERSAL_OF_TXN_ID',
        'TXN_ID', 'POSTING_DATE', 'stg', 'KsPostedTxn', 1, 1),
    (2, 'Accounts', 'KS', 'ACCOUNT',
        'ACCOUNT_NBR, MEMBER_NBR, ACCOUNT_CATEGORY, PRODUCT_CODE, DESCRIPTION, OPEN_DATE, CLOSE_DATE, CURRENT_BALANCE, AVAILABLE_BALANCE, INTEREST_RATE, ACCRUED_INTEREST, STATUS, LAST_ACTIVITY_DATE, LAST_MAINT_TS',
        'ACCOUNT_NBR', 'LAST_ACTIVITY_DATE', 'stg', 'KsAccount', 2, 1),
    (3, 'GlEntries', 'KS', 'GL_ENTRY',
        'GL_ENTRY_ID, GL_ACCOUNT_NBR, POSTING_DATE, DEBIT_AMOUNT, CREDIT_AMOUNT, SOURCE_CODE, BATCH_REF, MEMO, CREATED_TS',
        'GL_ENTRY_ID', 'POSTING_DATE', 'stg', 'KsGlEntry', 3, 1);
GO

-- Card reconciliation sign rules: purchases add, refunds subtract, on both sides.
INSERT INTO ops.ReconSignRule (RuleSource, Code, Sign)
VALUES
    ('KeyStone', 'CPOS', 1),
    ('KeyStone', 'CRFD', -1),
    ('Settlement', 'SALE', 1),
    ('Settlement', 'RFND', -1);
GO

INSERT INTO ops.RetentionPolicy (ObjectName, RetentionPeriod, PolicyReference)
VALUES
    (N'ops.BatchRun', NULL, N'TBD - credit union records preservation program (NCUA Part 749)'),
    (N'ops.CardFileLog', NULL, N'TBD - credit union records preservation program (NCUA Part 749)'),
    (N'ops.ReconExceptions', NULL, N'TBD - credit union records preservation program (NCUA Part 749)'),
    (N'blob:card-archive', NULL, N'TBD - credit union records preservation program (NCUA Part 749)'),
    (N'blob:card-quarantine', NULL, N'TBD - credit union records preservation program (NCUA Part 749)'),
    (N'blob:evidence', NULL, N'TBD - credit union records preservation program (NCUA Part 749)');
GO
