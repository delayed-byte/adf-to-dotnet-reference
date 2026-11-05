-- Posted member transactions. AMOUNT is always positive; the debit or credit direction
-- per GL account comes from the KeyBridge transaction-code-to-GL mapping.
-- POSTING_DATE is the business date the core posted the transaction to (the extraction filter);
-- POSTED_TS is the timestamp recorded by the core. Its time-zone semantics are fixed by the
-- Db2 type-handling ADR (Phase 2).
-- CHANNEL is CHAR(3) and includes the space-padded value 'BR ' (branch).
-- CARD_PROCESSOR_REF links card transactions (CATEGORY 'CRD') to the processor settlement file.

CREATE TABLE KS.POSTED_TXN (
    TXN_ID              BIGINT         NOT NULL,
    ACCOUNT_NBR         CHAR(12)       NOT NULL,
    TXN_CODE            CHAR(4)        NOT NULL,
    POSTING_DATE        DATE           NOT NULL,
    EFFECTIVE_DATE      DATE           NOT NULL,
    POSTED_TS           TIMESTAMP(6)   NOT NULL,
    AMOUNT              DECIMAL(15,2)  NOT NULL,
    DESCRIPTION         VARCHAR(60),
    CHANNEL             CHAR(3)        NOT NULL,
    CARD_PROCESSOR_REF  VARCHAR(24),
    REVERSAL_OF_TXN_ID  BIGINT,
    CONSTRAINT PK_POSTED_TXN PRIMARY KEY (TXN_ID),
    CONSTRAINT FK_POSTED_TXN_ACCOUNT FOREIGN KEY (ACCOUNT_NBR) REFERENCES KS.ACCOUNT (ACCOUNT_NBR),
    CONSTRAINT FK_POSTED_TXN_CODE FOREIGN KEY (TXN_CODE) REFERENCES KS.TXN_CODE (TXN_CODE),
    CONSTRAINT CK_POSTED_TXN_AMOUNT CHECK (AMOUNT > 0),
    CONSTRAINT CK_POSTED_TXN_CHANNEL CHECK (CHANNEL IN ('BR ', 'ATM', 'ACH', 'CRD', 'WEB', 'SYS'))
);

CREATE INDEX KS.IX_POSTED_TXN_POSTING_DATE ON KS.POSTED_TXN (POSTING_DATE);
