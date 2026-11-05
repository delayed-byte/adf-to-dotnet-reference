-- General ledger chart of accounts (subset) and transaction code catalogue.
-- GL account numbers are CHAR(10) and stored space-padded (e.g. '2100      '),
-- which exercises Db2 CHAR padding during extraction.

CREATE TABLE KS.GL_ACCOUNT (
    GL_ACCOUNT_NBR   CHAR(10)     NOT NULL,
    GL_ACCOUNT_NAME  VARCHAR(60)  NOT NULL,
    GL_ACCOUNT_TYPE  CHAR(1)      NOT NULL,
    CONSTRAINT PK_GL_ACCOUNT PRIMARY KEY (GL_ACCOUNT_NBR),
    CONSTRAINT CK_GL_ACCOUNT_TYPE CHECK (GL_ACCOUNT_TYPE IN ('A', 'L', 'Q', 'I', 'E'))
);

CREATE TABLE KS.TXN_CODE (
    TXN_CODE     CHAR(4)      NOT NULL,
    DESCRIPTION  VARCHAR(60)  NOT NULL,
    CATEGORY     CHAR(3)      NOT NULL,
    CONSTRAINT PK_TXN_CODE PRIMARY KEY (TXN_CODE),
    CONSTRAINT CK_TXN_CODE_CATEGORY CHECK (CATEGORY IN ('DEP', 'WDL', 'CRD', 'LON', 'DIV', 'FEE'))
);
