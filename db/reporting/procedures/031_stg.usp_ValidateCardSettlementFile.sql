-- Validates one settlement file that has been landed in stg.CardSettlementRaw:
-- header (exactly one H record, processor id present, settlement date = business date),
-- row schema (every D record has well-formed fields), and trailer (exactly one T record whose
-- record count and amount total match the detail records).
-- Format: contracts/card-processor/settlement-file-format.md
-- Writes every problem found to stg.CardSettlementValidationError (the source of the error
-- manifest) and returns a single summary row. Validation failures are data, not errors:
-- the procedure never throws for a bad file, so ADF/.NET do not retry it.
CREATE OR ALTER PROCEDURE stg.usp_ValidateCardSettlementFile
    @BusinessDate  date,
    @SourceFile    nvarchar(400),
    @BatchRunId    uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @errors TABLE (
        RecordType       nvarchar(10)   NULL,
        ProcessorRefNbr  nvarchar(200)  NULL,
        ErrorCode        varchar(50)    NOT NULL,
        ErrorMessage     nvarchar(1000) NOT NULL);

    DECLARE @raw TABLE (
        RecordType nvarchar(10) NULL,
        Field01 nvarchar(200) NULL, Field02 nvarchar(200) NULL, Field03 nvarchar(200) NULL,
        Field04 nvarchar(200) NULL, Field05 nvarchar(200) NULL, Field06 nvarchar(200) NULL,
        Field07 nvarchar(200) NULL, Field08 nvarchar(200) NULL, Field09 nvarchar(200) NULL);

    INSERT INTO @raw
    SELECT LTRIM(RTRIM(RecordType)), Field01, Field02, Field03, Field04, Field05, Field06, Field07, Field08, Field09
    FROM stg.CardSettlementRaw
    WHERE BusinessDate = @BusinessDate AND SourceFile = @SourceFile;

    DECLARE @headerCount int = (SELECT COUNT(*) FROM @raw WHERE RecordType = N'H');
    DECLARE @trailerCount int = (SELECT COUNT(*) FROM @raw WHERE RecordType = N'T');
    DECLARE @detailCount int = (SELECT COUNT(*) FROM @raw WHERE RecordType = N'D');

    -- Record types
    INSERT INTO @errors (RecordType, ProcessorRefNbr, ErrorCode, ErrorMessage)
    SELECT RecordType, NULL, 'RECORD_TYPE_INVALID',
           CONCAT(N'Unknown record type ''', ISNULL(RecordType, N'<empty>'), N'''; expected H, D or T.')
    FROM @raw
    WHERE ISNULL(RecordType, N'') NOT IN (N'H', N'D', N'T');

    -- Header
    IF @headerCount = 0
        INSERT INTO @errors VALUES (N'H', NULL, 'HEADER_MISSING', N'File has no header (H) record.');
    ELSE IF @headerCount > 1
        INSERT INTO @errors VALUES (N'H', NULL, 'HEADER_DUPLICATE', CONCAT(N'File has ', @headerCount, N' header (H) records; expected 1.'));
    ELSE
    BEGIN
        DECLARE @processorId nvarchar(200), @headerDateText nvarchar(200);
        SELECT @processorId = Field01, @headerDateText = Field02 FROM @raw WHERE RecordType = N'H';

        IF NULLIF(LTRIM(RTRIM(@processorId)), N'') IS NULL
            INSERT INTO @errors VALUES (N'H', NULL, 'HEADER_INVALID', N'Header processor id is empty.');

        IF LEN(ISNULL(@headerDateText, N'')) <> 8 OR TRY_CONVERT(date, @headerDateText, 112) IS NULL
            INSERT INTO @errors VALUES (N'H', NULL, 'HEADER_INVALID', N'Header settlement date is not a valid yyyyMMdd date.');
        ELSE IF TRY_CONVERT(date, @headerDateText, 112) <> @BusinessDate
            INSERT INTO @errors VALUES (N'H', NULL, 'HEADER_DATE_MISMATCH',
                CONCAT(N'Header settlement date ', @headerDateText, N' does not match business date ', CONVERT(char(8), @BusinessDate, 112), N'.'));
    END;

    -- Detail row schema: one error row per bad record, listing every invalid field.
    INSERT INTO @errors (RecordType, ProcessorRefNbr, ErrorCode, ErrorMessage)
    SELECT N'D', d.Field01, 'ROW_SCHEMA_INVALID', CONCAT(N'Invalid field(s): ', STUFF(v.BadFields, 1, 2, N''))
    FROM @raw AS d
    CROSS APPLY (SELECT CONCAT(
        CASE WHEN NULLIF(LTRIM(RTRIM(d.Field01)), N'') IS NULL OR LEN(d.Field01) > 24 THEN N', ProcessorRefNbr' END,
        CASE WHEN LEN(ISNULL(d.Field02, N'')) <> 8 OR TRY_CONVERT(date, d.Field02, 112) IS NULL THEN N', TxnDate' END,
        CASE WHEN ISNULL(d.Field03, N'') NOT LIKE N'[0-9][0-9][0-9][0-9][0-9][0-9]' THEN N', CardBin' END,
        CASE WHEN ISNULL(d.Field04, N'') NOT LIKE N'[0-9][0-9][0-9][0-9]' THEN N', CardLast4' END,
        CASE WHEN d.Field05 IS NULL
                  OR d.Field05 LIKE N'%[^0-9.]%'
                  OR d.Field05 NOT LIKE N'%[0-9]%'
                  OR LEN(d.Field05) - LEN(REPLACE(d.Field05, N'.', N'')) > 1
                  OR (CHARINDEX(N'.', d.Field05) > 0 AND LEN(d.Field05) - CHARINDEX(N'.', d.Field05) NOT BETWEEN 1 AND 2)
                  OR TRY_CONVERT(decimal(15,2), d.Field05) IS NULL
                  OR TRY_CONVERT(decimal(15,2), d.Field05) <= 0
             THEN N', Amount' END,
        CASE WHEN ISNULL(d.Field06, N'') NOT IN (N'SALE', N'RFND') THEN N', TxnType' END,
        CASE WHEN LEN(ISNULL(d.Field07, N'')) > 60 THEN N', MerchantName' END,
        CASE WHEN ISNULL(d.Field08, N'') <> N'' AND d.Field08 NOT LIKE N'[0-9][0-9][0-9][0-9]' THEN N', Mcc' END,
        CASE WHEN ISNULL(d.Field09, N'') <> N'USD' THEN N', CurrencyCode' END) AS BadFields) AS v
    WHERE d.RecordType = N'D' AND v.BadFields <> N'';

    INSERT INTO @errors (RecordType, ProcessorRefNbr, ErrorCode, ErrorMessage)
    SELECT N'D', Field01, 'DUPLICATE_PROCESSOR_REF', CONCAT(N'Processor reference appears ', COUNT(*), N' times in the file.')
    FROM @raw
    WHERE RecordType = N'D' AND NULLIF(LTRIM(RTRIM(Field01)), N'') IS NOT NULL
    GROUP BY Field01
    HAVING COUNT(*) > 1;

    -- Trailer
    DECLARE @trailerRowCount int, @trailerTotal decimal(19,2);
    DECLARE @detailTotal decimal(19,2) =
        (SELECT SUM(TRY_CONVERT(decimal(15,2), Field05)) FROM @raw WHERE RecordType = N'D');

    IF @trailerCount = 0
        INSERT INTO @errors VALUES (N'T', NULL, 'TRAILER_MISSING', N'File has no trailer (T) record.');
    ELSE IF @trailerCount > 1
        INSERT INTO @errors VALUES (N'T', NULL, 'TRAILER_DUPLICATE', CONCAT(N'File has ', @trailerCount, N' trailer (T) records; expected 1.'));
    ELSE
    BEGIN
        DECLARE @trailerCountText nvarchar(200), @trailerTotalText nvarchar(200);
        SELECT @trailerCountText = Field01, @trailerTotalText = Field02 FROM @raw WHERE RecordType = N'T';
        SET @trailerRowCount = CASE WHEN @trailerCountText NOT LIKE N'%[^0-9]%' THEN TRY_CONVERT(int, @trailerCountText) END;
        SET @trailerTotal = CASE WHEN @trailerTotalText NOT LIKE N'%[^0-9.]%' THEN TRY_CONVERT(decimal(19,2), @trailerTotalText) END;

        IF @trailerRowCount IS NULL
            INSERT INTO @errors VALUES (N'T', NULL, 'TRAILER_INVALID', N'Trailer record count is not a whole number.');
        ELSE IF @trailerRowCount <> @detailCount
            INSERT INTO @errors VALUES (N'T', NULL, 'TRAILER_COUNT_MISMATCH',
                CONCAT(N'Trailer record count ', @trailerRowCount, N' does not match ', @detailCount, N' detail records.'));

        IF @trailerTotal IS NULL
            INSERT INTO @errors VALUES (N'T', NULL, 'TRAILER_INVALID', N'Trailer amount total is not a valid amount.');
        ELSE IF NOT EXISTS (SELECT 1 FROM @errors WHERE ErrorCode = 'ROW_SCHEMA_INVALID')
             AND @trailerTotal <> ISNULL(@detailTotal, 0)
            INSERT INTO @errors VALUES (N'T', NULL, 'TRAILER_TOTAL_MISMATCH',
                CONCAT(N'Trailer amount total ', @trailerTotal, N' does not match detail total ', ISNULL(@detailTotal, 0), N'.'));
    END;

    DELETE FROM stg.CardSettlementValidationError WHERE BusinessDate = @BusinessDate AND SourceFile = @SourceFile;
    INSERT INTO stg.CardSettlementValidationError (BusinessDate, SourceFile, BatchRunId, RecordType, ProcessorRefNbr, ErrorCode, ErrorMessage)
    SELECT @BusinessDate, @SourceFile, @BatchRunId, RecordType, ProcessorRefNbr, ErrorCode, ErrorMessage FROM @errors;

    SELECT
        @SourceFile                                                         AS SourceFile,
        CAST(CASE WHEN EXISTS (SELECT 1 FROM @errors) THEN 0 ELSE 1 END AS bit) AS IsValid,
        @detailCount                                                        AS DetailRowCount,
        @trailerRowCount                                                    AS TrailerRowCount,
        @detailTotal                                                        AS DetailTotalAmount,
        @trailerTotal                                                       AS TrailerTotalAmount,
        (SELECT COUNT(*) FROM @errors)                                      AS ErrorCount,
        (SELECT LEFT(STRING_AGG(CONCAT(ErrorCode, N' x', ErrorTotal), N'; ') WITHIN GROUP (ORDER BY ErrorCode), 2000)
         FROM (SELECT ErrorCode, COUNT(*) AS ErrorTotal FROM @errors GROUP BY ErrorCode) AS e) AS ErrorSummary;
END;
GO
