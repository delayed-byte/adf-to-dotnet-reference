-- Examiner evidence for one batch run (NCUA alignment): files received, row counts,
-- checksums, reconciliation results and exceptions, as (Section, ItemName, ItemValue) rows.
-- PL_Master_NightlyBatch copies this result set to evidence/{businessDate}/ in blob storage.
--
-- Checksum: SHA-256 (hex) over the business columns of the table's rows for the business
-- date, one pipe-delimited line per row in primary-key order, NULL written as \N, lines
-- joined with LF and hashed as UTF-16LE (nvarchar). Load-audit columns are excluded, so an
-- identical rerun produces identical checksums.
CREATE OR ALTER PROCEDURE ops.usp_GetRunEvidence
    @BatchRunId    uniqueidentifier,
    @BusinessDate  date
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @lf nchar(1) = NCHAR(10);
    DECLARE @evidence TABLE (
        SortKey   int IDENTITY(1,1) NOT NULL,
        Section   varchar(30)    NOT NULL,
        ItemName  nvarchar(400)  NOT NULL,
        ItemValue nvarchar(max)  NULL);

    INSERT INTO @evidence (Section, ItemName, ItemValue) VALUES
        ('Run', N'batchRunId', CONVERT(nvarchar(36), @BatchRunId)),
        ('Run', N'businessDate', CONVERT(nvarchar(10), @BusinessDate, 23)),
        ('Run', N'generatedUtc', CONVERT(nvarchar(30), SYSUTCDATETIME(), 127));

    INSERT INTO @evidence (Section, ItemName, ItemValue)
    SELECT 'BatchRun',
           CONCAT(PipelineName, N'/', ISNULL(StepName, N'-'), N'/', ISNULL(ObjectName, N'-')),
           CONCAT(Status, N'; count=', ISNULL(CONVERT(nvarchar(20), RecordCount), N'-'),
                  N'; error=', ISNULL(ErrorCode, N'-'), N'; notes=', ISNULL(Notes, N'-'))
    FROM ops.BatchRun
    WHERE BatchRunId = @BatchRunId
    ORDER BY BatchRunEventId;

    INSERT INTO @evidence (Section, ItemName, ItemValue)
    SELECT 'Files', SourceFile,
           CONCAT(Outcome, N'; detailRows=', ISNULL(CONVERT(nvarchar(20), DetailRowCount), N'-'),
                  N'; trailerRows=', ISNULL(CONVERT(nvarchar(20), TrailerRowCount), N'-'),
                  N'; errors=', ISNULL(ErrorSummary, N'-'))
    FROM ops.CardFileLog
    WHERE BatchRunId = @BatchRunId
    ORDER BY CardFileLogId;

    INSERT INTO @evidence (Section, ItemName, ItemValue)
    SELECT 'RowCounts', v.TableName, CONVERT(nvarchar(20), v.Cnt)
    FROM (VALUES
        (N'core.PostedTransaction', (SELECT COUNT_BIG(*) FROM core.PostedTransaction WHERE PostingDate = @BusinessDate)),
        (N'core.Account',           (SELECT COUNT_BIG(*) FROM core.Account WHERE LastActivityDate = @BusinessDate)),
        (N'core.GlEntry',           (SELECT COUNT_BIG(*) FROM core.GlEntry WHERE PostingDate = @BusinessDate)),
        (N'core.CardSettlement',    (SELECT COUNT_BIG(*) FROM core.CardSettlement WHERE SettlementDate = @BusinessDate)),
        (N'core.DailyGlTotals',     (SELECT COUNT_BIG(*) FROM core.DailyGlTotals WHERE BusinessDate = @BusinessDate))
    ) AS v (TableName, Cnt);

    INSERT INTO @evidence (Section, ItemName, ItemValue)
    SELECT 'Checksums', N'core.PostedTransaction', CONVERT(nvarchar(64), HASHBYTES('SHA2_256', ISNULL(STRING_AGG(CAST(CONCAT_WS(N'|',
               TxnId, AccountNbr, TxnCode, CONVERT(nvarchar(10), PostingDate, 23), CONVERT(nvarchar(10), EffectiveDate, 23),
               CONVERT(nvarchar(27), PostedTs, 121), Amount, ISNULL(Description, N'\N'), Channel,
               ISNULL(CardProcessorRef, N'\N'), ISNULL(CONVERT(nvarchar(20), ReversalOfTxnId), N'\N')) AS nvarchar(max)), @lf)
               WITHIN GROUP (ORDER BY TxnId), N'')), 2)
    FROM core.PostedTransaction WHERE PostingDate = @BusinessDate;

    INSERT INTO @evidence (Section, ItemName, ItemValue)
    SELECT 'Checksums', N'core.Account', CONVERT(nvarchar(64), HASHBYTES('SHA2_256', ISNULL(STRING_AGG(CAST(CONCAT_WS(N'|',
               AccountNbr, MemberNbr, AccountCategory, ProductCode, ISNULL(Description, N'\N'), CONVERT(nvarchar(10), OpenDate, 23),
               ISNULL(CONVERT(nvarchar(10), CloseDate, 23), N'\N'), CurrentBalance, ISNULL(CONVERT(nvarchar(20), AvailableBalance), N'\N'),
               InterestRate, AccruedInterest, Status, CONVERT(nvarchar(10), LastActivityDate, 23), CONVERT(nvarchar(27), LastMaintTs, 121)) AS nvarchar(max)), @lf)
               WITHIN GROUP (ORDER BY AccountNbr), N'')), 2)
    FROM core.Account WHERE LastActivityDate = @BusinessDate;

    INSERT INTO @evidence (Section, ItemName, ItemValue)
    SELECT 'Checksums', N'core.GlEntry', CONVERT(nvarchar(64), HASHBYTES('SHA2_256', ISNULL(STRING_AGG(CAST(CONCAT_WS(N'|',
               GlEntryId, GlAccountNbr, CONVERT(nvarchar(10), PostingDate, 23), DebitAmount, CreditAmount, SourceCode,
               ISNULL(BatchRef, N'\N'), ISNULL(Memo, N'\N'), CONVERT(nvarchar(27), CreatedTs, 121)) AS nvarchar(max)), @lf)
               WITHIN GROUP (ORDER BY GlEntryId), N'')), 2)
    FROM core.GlEntry WHERE PostingDate = @BusinessDate;

    INSERT INTO @evidence (Section, ItemName, ItemValue)
    SELECT 'Checksums', N'core.CardSettlement', CONVERT(nvarchar(64), HASHBYTES('SHA2_256', ISNULL(STRING_AGG(CAST(CONCAT_WS(N'|',
               ProcessorRefNbr, CONVERT(nvarchar(10), SettlementDate, 23), CONVERT(nvarchar(10), TxnDate, 23), CardBin, CardLast4,
               Amount, TxnType, ISNULL(MerchantName, N'\N'), ISNULL(Mcc, N'\N'), CurrencyCode, SourceFile) AS nvarchar(max)), @lf)
               WITHIN GROUP (ORDER BY ProcessorRefNbr), N'')), 2)
    FROM core.CardSettlement WHERE SettlementDate = @BusinessDate;

    INSERT INTO @evidence (Section, ItemName, ItemValue)
    SELECT 'Checksums', N'core.DailyGlTotals', CONVERT(nvarchar(64), HASHBYTES('SHA2_256', ISNULL(STRING_AGG(CAST(CONCAT_WS(N'|',
               CONVERT(nvarchar(10), BusinessDate, 23), GlAccountNbr, DebitTotal, CreditTotal, NetAmount, TxnCount) AS nvarchar(max)), @lf)
               WITHIN GROUP (ORDER BY GlAccountNbr), N'')), 2)
    FROM core.DailyGlTotals WHERE BusinessDate = @BusinessDate;

    INSERT INTO @evidence (Section, ItemName, ItemValue)
    SELECT 'Reconciliation', N'CardSettlement',
           CONCAT(N'settlement=', SettlementTotal, N'; keystone=', KeyStoneTotal, N'; difference=', Difference)
    FROM ops.fn_CardReconTotals(@BusinessDate);

    INSERT INTO @evidence (Section, ItemName, ItemValue)
    SELECT 'Reconciliation', CONCAT(N'GeneralLedger/', RTRIM(GlAccountNbr), N':', Side),
           CONCAT(N'derived=', ComparedAmount, N'; keystone=', KeyStoneAmount, N'; difference=', Difference)
    FROM ops.fn_GlReconDetail(@BusinessDate)
    ORDER BY GlAccountNbr, Side;

    INSERT INTO @evidence (Section, ItemName, ItemValue)
    SELECT 'Exceptions', CONCAT(CheckName, N'/', ItemKey),
           CONCAT(N'difference=', Difference, N'; tolerance=', Tolerance,
                  N'; detectedUtc=', CONVERT(nvarchar(30), DetectedUtc, 127),
                  N'; alertSentUtc=', ISNULL(CONVERT(nvarchar(30), AlertSentUtc, 127), N'-'))
    FROM ops.ReconExceptions
    WHERE BusinessDate = @BusinessDate
    ORDER BY ReconExceptionId;

    SELECT Section AS section, ItemName AS item, ItemValue AS value
    FROM @evidence
    ORDER BY SortKey;
END;
GO
