-- Lookup sources for PL_Reconciliation. A difference is out of tolerance when
-- ABS(Difference) > @Tolerance (default tolerance 0.00, so any difference counts).
CREATE OR ALTER PROCEDURE ops.usp_GetCardReconTotals
    @BusinessDate  date,
    @Tolerance     decimal(19,2) = 0.00
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        SettlementTotal,
        KeyStoneTotal,
        Difference,
        ABS(Difference) AS AbsDifference,
        CAST(CASE WHEN ABS(Difference) > @Tolerance THEN 1 ELSE 0 END AS bit) AS IsOutOfTolerance
    FROM ops.fn_CardReconTotals(@BusinessDate);
END;
GO

CREATE OR ALTER PROCEDURE ops.usp_GetGlReconTotals
    @BusinessDate  date,
    @Tolerance     decimal(19,2) = 0.00
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        COUNT(*)                                                         AS ItemsCompared,
        SUM(CASE WHEN ABS(Difference) > @Tolerance THEN 1 ELSE 0 END)    AS ItemsOutOfTolerance,
        CAST(ISNULL(SUM(ABS(Difference)), 0) AS decimal(19,2))           AS TotalAbsDifference,
        CAST(CASE WHEN EXISTS (SELECT 1 FROM ops.fn_GlReconDetail(@BusinessDate) WHERE ABS(Difference) > @Tolerance)
                  THEN 1 ELSE 0 END AS bit)                              AS IsOutOfTolerance
    FROM ops.fn_GlReconDetail(@BusinessDate);
END;
GO
