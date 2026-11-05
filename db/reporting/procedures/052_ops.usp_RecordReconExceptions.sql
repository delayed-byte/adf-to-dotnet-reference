-- Records reconciliation exceptions for a business date and says whether an alert is due.
-- One exception row per out-of-tolerance item (card: ItemKey 'ALL'; GL: '<account>:<side>').
-- A rerun that finds the same difference adds no row; AlertRequired is true only while a
-- current exception has not been alerted, so each distinct exception is alerted exactly once.
-- Always returns one row.
CREATE OR ALTER PROCEDURE ops.usp_RecordReconExceptions
    @BusinessDate  date,
    @BatchRunId    uniqueidentifier,
    @Tolerance     decimal(19,2) = 0.00
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @current TABLE (
        CheckName       varchar(30)    NOT NULL,
        ItemKey         varchar(50)    NOT NULL,
        KeyStoneAmount  decimal(19,2)  NOT NULL,
        ComparedAmount  decimal(19,2)  NOT NULL,
        Difference      decimal(19,2)  NOT NULL,
        Description     nvarchar(400)  NOT NULL);

    INSERT INTO @current
    SELECT 'CardSettlement', 'ALL', KeyStoneTotal, SettlementTotal, Difference,
           N'Card settlement file total differs from KeyStone card transactions.'
    FROM ops.fn_CardReconTotals(@BusinessDate)
    WHERE ABS(Difference) > @Tolerance;

    INSERT INTO @current
    SELECT 'GeneralLedger', CONCAT(RTRIM(GlAccountNbr), ':', Side), KeyStoneAmount, ComparedAmount, Difference,
           CONCAT(N'Derived ', LOWER(Side), N' total for GL ', RTRIM(GlAccountNbr), N' differs from KeyStone GL entries.')
    FROM ops.fn_GlReconDetail(@BusinessDate)
    WHERE ABS(Difference) > @Tolerance;

    DECLARE @inserted TABLE (ReconExceptionId bigint NOT NULL);

    BEGIN TRANSACTION;

    INSERT INTO ops.ReconExceptions
        (BusinessDate, CheckName, ItemKey, KeyStoneAmount, ComparedAmount, Difference, Tolerance, Description, DetectedBatchRunId)
    OUTPUT inserted.ReconExceptionId INTO @inserted
    SELECT @BusinessDate, c.CheckName, c.ItemKey, c.KeyStoneAmount, c.ComparedAmount, c.Difference, @Tolerance, c.Description, @BatchRunId
    FROM @current AS c
    WHERE NOT EXISTS (
        SELECT 1 FROM ops.ReconExceptions AS e
        WHERE e.BusinessDate = @BusinessDate AND e.CheckName = c.CheckName
          AND e.ItemKey = c.ItemKey AND e.Difference = c.Difference);

    COMMIT TRANSACTION;

    SELECT
        CAST(CASE WHEN EXISTS (SELECT 1 FROM @current) THEN 1 ELSE 0 END AS bit) AS IsOutOfTolerance,
        (SELECT COUNT(*) FROM @current)                                           AS ExceptionCount,
        (SELECT COUNT(*) FROM @inserted)                                          AS NewExceptionCount,
        CAST(CASE WHEN EXISTS (
            SELECT 1 FROM ops.ReconExceptions AS e
            JOIN @current AS c ON e.CheckName = c.CheckName AND e.ItemKey = c.ItemKey AND e.Difference = c.Difference
            WHERE e.BusinessDate = @BusinessDate AND e.AlertSentUtc IS NULL) THEN 1 ELSE 0 END AS bit) AS AlertRequired,
        ISNULL((SELECT LEFT(STRING_AGG(CAST(CONCAT(CheckName, ' ', ItemKey, ' difference ', Difference) AS nvarchar(max)), N'; ')
                            WITHIN GROUP (ORDER BY CheckName, ItemKey), 1000)
                FROM @current), N'')                                               AS AlertSummary;
END;
GO

-- Marks the current unalerted exceptions for a business date as alerted (after the webhook
-- call succeeded). Only the alert columns change; the protect trigger enforces that.
CREATE OR ALTER PROCEDURE ops.usp_MarkReconAlertSent
    @BusinessDate  date,
    @BatchRunId    uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE ops.ReconExceptions
    SET AlertSentUtc = SYSUTCDATETIME(), AlertBatchRunId = @BatchRunId
    WHERE BusinessDate = @BusinessDate AND AlertSentUtc IS NULL;
END;
GO
