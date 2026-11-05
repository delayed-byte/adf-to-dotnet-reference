-- Returns the distinct transaction codes posted on the business date that have no GL mapping.
-- Always returns exactly one row (UnmappedCount = 0 when everything is mapped).
CREATE OR ALTER PROCEDURE ref.usp_GetUnmappedTxnCodes
    @BusinessDate date
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        COUNT(*)                                                                          AS UnmappedCount,
        ISNULL(STRING_AGG(CAST(RTRIM(x.TxnCode) AS varchar(max)), ',') WITHIN GROUP (ORDER BY x.TxnCode), '') AS UnmappedCodes
    FROM (
        SELECT DISTINCT t.TxnCode
        FROM core.PostedTransaction AS t
        WHERE t.PostingDate = @BusinessDate
          AND NOT EXISTS (SELECT 1 FROM ref.TxnCodeGlMap AS m WHERE m.TxnCode = t.TxnCode)
    ) AS x;
END;
GO
