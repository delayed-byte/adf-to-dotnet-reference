-- Members and their share (deposit) and loan accounts.
-- Synthetic data only: member and account numbers come from a reserved range
-- (member 9xxxxxxxxx, account 99xxxxxxxxxx); no SSNs or tax IDs are modelled at all.

CREATE TABLE KS.MEMBER (
    MEMBER_NBR     CHAR(10)      NOT NULL,
    FIRST_NAME     VARCHAR(40)   NOT NULL,
    LAST_NAME      VARCHAR(40)   NOT NULL,
    MEMBER_SINCE   DATE          NOT NULL,
    BRANCH_CODE    CHAR(4)       NOT NULL,
    STATUS         CHAR(1)       NOT NULL,
    LAST_MAINT_TS  TIMESTAMP(6)  NOT NULL,
    CONSTRAINT PK_MEMBER PRIMARY KEY (MEMBER_NBR),
    CONSTRAINT CK_MEMBER_STATUS CHECK (STATUS IN ('A', 'C'))
);

-- LAST_ACTIVITY_DATE is the extraction date column: an account is extracted for the
-- business date on which it last had activity or maintenance.
-- ACCRUED_INTEREST uses Db2's maximum DECIMAL precision (31) to exercise precision handling.
CREATE TABLE KS.ACCOUNT (
    ACCOUNT_NBR         CHAR(12)       NOT NULL,
    MEMBER_NBR          CHAR(10)       NOT NULL,
    ACCOUNT_CATEGORY    CHAR(1)        NOT NULL,
    PRODUCT_CODE        CHAR(6)        NOT NULL,
    DESCRIPTION         VARCHAR(60),
    OPEN_DATE           DATE           NOT NULL,
    CLOSE_DATE          DATE,
    CURRENT_BALANCE     DECIMAL(15,2)  NOT NULL,
    AVAILABLE_BALANCE   DECIMAL(15,2),
    INTEREST_RATE       DECIMAL(9,6)   NOT NULL,
    ACCRUED_INTEREST    DECIMAL(31,8)  NOT NULL,
    STATUS              CHAR(1)        NOT NULL,
    LAST_ACTIVITY_DATE  DATE           NOT NULL,
    LAST_MAINT_TS       TIMESTAMP(6)   NOT NULL,
    CONSTRAINT PK_ACCOUNT PRIMARY KEY (ACCOUNT_NBR),
    CONSTRAINT FK_ACCOUNT_MEMBER FOREIGN KEY (MEMBER_NBR) REFERENCES KS.MEMBER (MEMBER_NBR),
    CONSTRAINT CK_ACCOUNT_CATEGORY CHECK (ACCOUNT_CATEGORY IN ('S', 'L')),
    CONSTRAINT CK_ACCOUNT_STATUS CHECK (STATUS IN ('O', 'D', 'C'))
);

CREATE INDEX KS.IX_ACCOUNT_LAST_ACTIVITY ON KS.ACCOUNT (LAST_ACTIVITY_DATE);
