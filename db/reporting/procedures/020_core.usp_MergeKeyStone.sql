-- Upserts the KeyStone staging tables into core on their source keys (PL_ExtractKeyStone).
-- Idempotent: rows are inserted when new and updated only when a business column differs,
-- so rerunning the same business date leaves core unchanged. Accounts are only updated
-- when the incoming LAST_MAINT_TS is not older than the stored one, so re-extracting an
-- earlier business date can never roll an account back.
CREATE OR ALTER PROCEDURE core.usp_MergeKeyStone
    @BusinessDate  date,
    @BatchRunId    uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now datetime2(3) = SYSUTCDATETIME();
    DECLARE @accountActions TABLE (MergeAction nvarchar(10) NOT NULL);
    DECLARE @txnActions     TABLE (MergeAction nvarchar(10) NOT NULL);
    DECLARE @glActions      TABLE (MergeAction nvarchar(10) NOT NULL);

    IF EXISTS (SELECT 1 FROM stg.KsAccount GROUP BY ACCOUNT_NBR HAVING COUNT(*) > 1)
        THROW 52000, 'Duplicate ACCOUNT_NBR values in stg.KsAccount.', 1;
    IF EXISTS (SELECT 1 FROM stg.KsPostedTxn GROUP BY TXN_ID HAVING COUNT(*) > 1)
        THROW 52001, 'Duplicate TXN_ID values in stg.KsPostedTxn.', 1;
    IF EXISTS (SELECT 1 FROM stg.KsGlEntry GROUP BY GL_ENTRY_ID HAVING COUNT(*) > 1)
        THROW 52002, 'Duplicate GL_ENTRY_ID values in stg.KsGlEntry.', 1;

    DECLARE @stagedAccounts bigint = (SELECT COUNT_BIG(*) FROM stg.KsAccount WHERE LAST_ACTIVITY_DATE = @BusinessDate);
    DECLARE @stagedTxns     bigint = (SELECT COUNT_BIG(*) FROM stg.KsPostedTxn WHERE POSTING_DATE = @BusinessDate);
    DECLARE @stagedGl       bigint = (SELECT COUNT_BIG(*) FROM stg.KsGlEntry WHERE POSTING_DATE = @BusinessDate);

    BEGIN TRANSACTION;

    MERGE core.Account WITH (HOLDLOCK) AS t
    USING (SELECT * FROM stg.KsAccount WHERE LAST_ACTIVITY_DATE = @BusinessDate) AS s
        ON t.AccountNbr = s.ACCOUNT_NBR
    WHEN MATCHED AND s.LAST_MAINT_TS >= t.LastMaintTs AND EXISTS (
            SELECT s.MEMBER_NBR, s.ACCOUNT_CATEGORY, s.PRODUCT_CODE, s.DESCRIPTION, s.OPEN_DATE, s.CLOSE_DATE,
                   s.CURRENT_BALANCE, s.AVAILABLE_BALANCE, s.INTEREST_RATE, s.ACCRUED_INTEREST, s.STATUS,
                   s.LAST_ACTIVITY_DATE, s.LAST_MAINT_TS
            EXCEPT
            SELECT t.MemberNbr, t.AccountCategory, t.ProductCode, t.Description, t.OpenDate, t.CloseDate,
                   t.CurrentBalance, t.AvailableBalance, t.InterestRate, t.AccruedInterest, t.Status,
                   t.LastActivityDate, t.LastMaintTs)
        THEN UPDATE SET
            MemberNbr = s.MEMBER_NBR, AccountCategory = s.ACCOUNT_CATEGORY, ProductCode = s.PRODUCT_CODE,
            Description = s.DESCRIPTION, OpenDate = s.OPEN_DATE, CloseDate = s.CLOSE_DATE,
            CurrentBalance = s.CURRENT_BALANCE, AvailableBalance = s.AVAILABLE_BALANCE,
            InterestRate = s.INTEREST_RATE, AccruedInterest = s.ACCRUED_INTEREST, Status = s.STATUS,
            LastActivityDate = s.LAST_ACTIVITY_DATE, LastMaintTs = s.LAST_MAINT_TS,
            LoadedBatchRunId = @BatchRunId, LoadedUtc = @now
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (AccountNbr, MemberNbr, AccountCategory, ProductCode, Description, OpenDate, CloseDate,
                CurrentBalance, AvailableBalance, InterestRate, AccruedInterest, Status,
                LastActivityDate, LastMaintTs, LoadedBatchRunId, LoadedUtc)
        VALUES (s.ACCOUNT_NBR, s.MEMBER_NBR, s.ACCOUNT_CATEGORY, s.PRODUCT_CODE, s.DESCRIPTION, s.OPEN_DATE, s.CLOSE_DATE,
                s.CURRENT_BALANCE, s.AVAILABLE_BALANCE, s.INTEREST_RATE, s.ACCRUED_INTEREST, s.STATUS,
                s.LAST_ACTIVITY_DATE, s.LAST_MAINT_TS, @BatchRunId, @now)
    OUTPUT $action INTO @accountActions;

    MERGE core.PostedTransaction WITH (HOLDLOCK) AS t
    USING (SELECT * FROM stg.KsPostedTxn WHERE POSTING_DATE = @BusinessDate) AS s
        ON t.TxnId = s.TXN_ID
    WHEN MATCHED AND EXISTS (
            SELECT s.ACCOUNT_NBR, s.TXN_CODE, s.POSTING_DATE, s.EFFECTIVE_DATE, s.POSTED_TS, s.AMOUNT,
                   s.DESCRIPTION, s.CHANNEL, s.CARD_PROCESSOR_REF, s.REVERSAL_OF_TXN_ID
            EXCEPT
            SELECT t.AccountNbr, t.TxnCode, t.PostingDate, t.EffectiveDate, t.PostedTs, t.Amount,
                   t.Description, t.Channel, t.CardProcessorRef, t.ReversalOfTxnId)
        THEN UPDATE SET
            AccountNbr = s.ACCOUNT_NBR, TxnCode = s.TXN_CODE, PostingDate = s.POSTING_DATE,
            EffectiveDate = s.EFFECTIVE_DATE, PostedTs = s.POSTED_TS, Amount = s.AMOUNT,
            Description = s.DESCRIPTION, Channel = s.CHANNEL, CardProcessorRef = s.CARD_PROCESSOR_REF,
            ReversalOfTxnId = s.REVERSAL_OF_TXN_ID, LoadedBatchRunId = @BatchRunId, LoadedUtc = @now
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (TxnId, AccountNbr, TxnCode, PostingDate, EffectiveDate, PostedTs, Amount, Description,
                Channel, CardProcessorRef, ReversalOfTxnId, LoadedBatchRunId, LoadedUtc)
        VALUES (s.TXN_ID, s.ACCOUNT_NBR, s.TXN_CODE, s.POSTING_DATE, s.EFFECTIVE_DATE, s.POSTED_TS, s.AMOUNT, s.DESCRIPTION,
                s.CHANNEL, s.CARD_PROCESSOR_REF, s.REVERSAL_OF_TXN_ID, @BatchRunId, @now)
    OUTPUT $action INTO @txnActions;

    MERGE core.GlEntry WITH (HOLDLOCK) AS t
    USING (SELECT * FROM stg.KsGlEntry WHERE POSTING_DATE = @BusinessDate) AS s
        ON t.GlEntryId = s.GL_ENTRY_ID
    WHEN MATCHED AND EXISTS (
            SELECT s.GL_ACCOUNT_NBR, s.POSTING_DATE, s.DEBIT_AMOUNT, s.CREDIT_AMOUNT, s.SOURCE_CODE,
                   s.BATCH_REF, s.MEMO, s.CREATED_TS
            EXCEPT
            SELECT t.GlAccountNbr, t.PostingDate, t.DebitAmount, t.CreditAmount, t.SourceCode,
                   t.BatchRef, t.Memo, t.CreatedTs)
        THEN UPDATE SET
            GlAccountNbr = s.GL_ACCOUNT_NBR, PostingDate = s.POSTING_DATE, DebitAmount = s.DEBIT_AMOUNT,
            CreditAmount = s.CREDIT_AMOUNT, SourceCode = s.SOURCE_CODE, BatchRef = s.BATCH_REF,
            Memo = s.MEMO, CreatedTs = s.CREATED_TS, LoadedBatchRunId = @BatchRunId, LoadedUtc = @now
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (GlEntryId, GlAccountNbr, PostingDate, DebitAmount, CreditAmount, SourceCode, BatchRef,
                Memo, CreatedTs, LoadedBatchRunId, LoadedUtc)
        VALUES (s.GL_ENTRY_ID, s.GL_ACCOUNT_NBR, s.POSTING_DATE, s.DEBIT_AMOUNT, s.CREDIT_AMOUNT, s.SOURCE_CODE, s.BATCH_REF,
                s.MEMO, s.CREATED_TS, @BatchRunId, @now)
    OUTPUT $action INTO @glActions;

    DECLARE @accIns bigint = (SELECT COUNT_BIG(*) FROM @accountActions WHERE MergeAction = 'INSERT');
    DECLARE @accUpd bigint = (SELECT COUNT_BIG(*) FROM @accountActions WHERE MergeAction = 'UPDATE');
    DECLARE @txnIns bigint = (SELECT COUNT_BIG(*) FROM @txnActions WHERE MergeAction = 'INSERT');
    DECLARE @txnUpd bigint = (SELECT COUNT_BIG(*) FROM @txnActions WHERE MergeAction = 'UPDATE');
    DECLARE @glIns  bigint = (SELECT COUNT_BIG(*) FROM @glActions WHERE MergeAction = 'INSERT');
    DECLARE @glUpd  bigint = (SELECT COUNT_BIG(*) FROM @glActions WHERE MergeAction = 'UPDATE');
    DECLARE @notes nvarchar(2000);

    SET @notes = CONCAT(N'staged=', @stagedAccounts, N'; inserted=', @accIns, N'; updated=', @accUpd, N'; unchanged=', @stagedAccounts - @accIns - @accUpd);
    EXEC ops.usp_LogBatchRunEvent @BatchRunId = @BatchRunId, @BusinessDate = @BusinessDate, @PipelineName = 'PL_ExtractKeyStone',
        @Status = 'Succeeded', @StepName = 'MergeKeyStone', @ObjectName = N'core.Account', @RecordCount = @stagedAccounts, @Notes = @notes;

    SET @notes = CONCAT(N'staged=', @stagedTxns, N'; inserted=', @txnIns, N'; updated=', @txnUpd, N'; unchanged=', @stagedTxns - @txnIns - @txnUpd);
    EXEC ops.usp_LogBatchRunEvent @BatchRunId = @BatchRunId, @BusinessDate = @BusinessDate, @PipelineName = 'PL_ExtractKeyStone',
        @Status = 'Succeeded', @StepName = 'MergeKeyStone', @ObjectName = N'core.PostedTransaction', @RecordCount = @stagedTxns, @Notes = @notes;

    SET @notes = CONCAT(N'staged=', @stagedGl, N'; inserted=', @glIns, N'; updated=', @glUpd, N'; unchanged=', @stagedGl - @glIns - @glUpd);
    EXEC ops.usp_LogBatchRunEvent @BatchRunId = @BatchRunId, @BusinessDate = @BusinessDate, @PipelineName = 'PL_ExtractKeyStone',
        @Status = 'Succeeded', @StepName = 'MergeKeyStone', @ObjectName = N'core.GlEntry', @RecordCount = @stagedGl, @Notes = @notes;

    COMMIT TRANSACTION;
END;
GO
