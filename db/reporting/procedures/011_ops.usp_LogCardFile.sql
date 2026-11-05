-- Appends the outcome for one settlement file to ops.CardFileLog.
CREATE OR ALTER PROCEDURE ops.usp_LogCardFile
    @BatchRunId          uniqueidentifier,
    @BusinessDate        date,
    @SourceFile          nvarchar(400),
    @Outcome             varchar(20),
    @DetailRowCount      int            = NULL,
    @TrailerRowCount     int            = NULL,
    @DetailTotalAmount   decimal(19,2)  = NULL,
    @TrailerTotalAmount  decimal(19,2)  = NULL,
    @ErrorSummary        nvarchar(2000) = NULL,
    @DestinationPath     nvarchar(1000) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO ops.CardFileLog
        (BatchRunId, BusinessDate, SourceFile, Outcome, DetailRowCount, TrailerRowCount,
         DetailTotalAmount, TrailerTotalAmount, ErrorSummary, DestinationPath)
    VALUES
        (@BatchRunId, @BusinessDate, @SourceFile, @Outcome, @DetailRowCount, @TrailerRowCount,
         @DetailTotalAmount, @TrailerTotalAmount, NULLIF(@ErrorSummary, N''), NULLIF(@DestinationPath, N''));
END;
GO
