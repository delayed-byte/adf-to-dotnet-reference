-- Reconciliation building blocks shared by the recon procedures and the evidence export.
-- Difference is always (compared side) - (KeyStone side).

-- Card check: settlement file totals vs KeyStone card transactions, signed by ops.ReconSignRule.
CREATE OR ALTER FUNCTION ops.fn_CardReconTotals (@BusinessDate date)
RETURNS TABLE
AS
RETURN
    SELECT
        x.SettlementTotal,
        x.KeyStoneTotal,
        CAST(x.SettlementTotal - x.KeyStoneTotal AS decimal(19,2)) AS Difference
    FROM (
        SELECT
            CAST(ISNULL((
                SELECT SUM(r.Sign * cs.Amount)
                FROM core.CardSettlement AS cs
                JOIN ops.ReconSignRule AS r ON r.RuleSource = 'Settlement' AND r.Code = cs.TxnType
                WHERE cs.SettlementDate = @BusinessDate), 0) AS decimal(19,2)) AS SettlementTotal,
            CAST(ISNULL((
                SELECT SUM(r.Sign * t.Amount)
                FROM core.PostedTransaction AS t
                JOIN ops.ReconSignRule AS r ON r.RuleSource = 'KeyStone' AND r.Code = t.TxnCode
                WHERE t.PostingDate = @BusinessDate), 0) AS decimal(19,2)) AS KeyStoneTotal
    ) AS x;
GO

-- GL check: derived core.DailyGlTotals vs KeyStone GL entries, per GL account and side.
CREATE OR ALTER FUNCTION ops.fn_GlReconDetail (@BusinessDate date)
RETURNS TABLE
AS
RETURN
    WITH ks AS (
        SELECT GlAccountNbr, SUM(DebitAmount) AS DebitTotal, SUM(CreditAmount) AS CreditTotal
        FROM core.GlEntry
        WHERE PostingDate = @BusinessDate
        GROUP BY GlAccountNbr),
    dg AS (
        SELECT GlAccountNbr, DebitTotal, CreditTotal
        FROM core.DailyGlTotals
        WHERE BusinessDate = @BusinessDate),
    j AS (
        SELECT COALESCE(ks.GlAccountNbr, dg.GlAccountNbr) AS GlAccountNbr,
               ISNULL(ks.DebitTotal, 0) AS KsDebit, ISNULL(ks.CreditTotal, 0) AS KsCredit,
               ISNULL(dg.DebitTotal, 0) AS DgDebit, ISNULL(dg.CreditTotal, 0) AS DgCredit
        FROM ks FULL OUTER JOIN dg ON dg.GlAccountNbr = ks.GlAccountNbr)
    SELECT
        j.GlAccountNbr,
        v.Side,
        CAST(v.KeyStoneAmount AS decimal(19,2)) AS KeyStoneAmount,
        CAST(v.ComparedAmount AS decimal(19,2)) AS ComparedAmount,
        CAST(v.ComparedAmount - v.KeyStoneAmount AS decimal(19,2)) AS Difference
    FROM j
    CROSS APPLY (VALUES ('DEBIT', j.KsDebit, j.DgDebit), ('CREDIT', j.KsCredit, j.DgCredit))
        AS v (Side, KeyStoneAmount, ComparedAmount);
GO
