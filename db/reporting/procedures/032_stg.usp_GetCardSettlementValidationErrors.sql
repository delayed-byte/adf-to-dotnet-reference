-- Source of the error manifest written next to a quarantined settlement file.
-- Card numbers never appear: the raw file only carries BIN and last 4.
CREATE OR ALTER PROCEDURE stg.usp_GetCardSettlementValidationErrors
    @BusinessDate  date,
    @SourceFile    nvarchar(400)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        CONVERT(char(10), BusinessDate, 23) AS businessDate,
        SourceFile                          AS sourceFile,
        CONVERT(varchar(36), BatchRunId)    AS batchRunId,
        RecordType                          AS recordType,
        ProcessorRefNbr                     AS processorRefNbr,
        ErrorCode                           AS errorCode,
        ErrorMessage                        AS errorMessage
    FROM stg.CardSettlementValidationError
    WHERE BusinessDate = @BusinessDate AND SourceFile = @SourceFile
    ORDER BY CASE RecordType WHEN N'H' THEN 0 WHEN N'D' THEN 1 WHEN N'T' THEN 2 ELSE 3 END, ProcessorRefNbr, ErrorCode;
END;
GO
