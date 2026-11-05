-- Reference data that is stable across scenarios. Members, accounts, transactions and
-- GL entries are produced per scenario by the seeded generator (tests/TestData/generator).

INSERT INTO KS.GL_ACCOUNT (GL_ACCOUNT_NBR, GL_ACCOUNT_NAME, GL_ACCOUNT_TYPE) VALUES
    ('1010', 'Cash on hand - branch vault', 'A'),
    ('1020', 'Cash - ATM network', 'A'),
    ('1110', 'Due from Federal Reserve - ACH', 'A'),
    ('1300', 'Loans to members', 'A'),
    ('2100', 'Member share accounts', 'L'),
    ('2400', 'Card settlement payable', 'L'),
    ('4100', 'Loan interest income', 'I'),
    ('4300', 'Fee income - service charges', 'I'),
    ('4310', 'Fee income - card interchange', 'I'),
    ('5100', 'Dividend expense', 'E');

INSERT INTO KS.TXN_CODE (TXN_CODE, DESCRIPTION, CATEGORY) VALUES
    ('DEP1', 'Branch cash deposit', 'DEP'),
    ('ACHC', 'ACH credit (e.g. payroll)', 'DEP'),
    ('WDL1', 'Branch cash withdrawal', 'WDL'),
    ('ATMW', 'ATM cash withdrawal', 'WDL'),
    ('ACHD', 'ACH debit', 'WDL'),
    ('CPOS', 'Debit card purchase', 'CRD'),
    ('CRFD', 'Debit card refund', 'CRD'),
    ('LPMT', 'Loan payment from share', 'LON'),
    ('DIVP', 'Dividend posting', 'DIV'),
    ('FEE1', 'Service fee', 'FEE');
