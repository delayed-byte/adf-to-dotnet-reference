-- Clears card settlement staging for a business date before files are processed.
CREATE OR ALTER PROCEDURE stg.usp_ResetCardSettlementStaging
    @BusinessDate date
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;
    DELETE FROM stg.CardSettlementRaw WHERE BusinessDate = @BusinessDate;
    DELETE FROM stg.CardSettlementValidationError WHERE BusinessDate = @BusinessDate;
    DELETE FROM stg.CardSettlement WHERE BusinessDate = @BusinessDate;
    COMMIT TRANSACTION;
END;
GO
