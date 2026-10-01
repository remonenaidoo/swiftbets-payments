-- Daily provider-against-ledger runs and what each found.
CREATE TABLE payments.ReconciliationRuns
(
    RunId       uniqueidentifier  NOT NULL CONSTRAINT PK_ReconciliationRuns PRIMARY KEY,
    Provider    varchar(40)       NOT NULL,
    Day         date              NOT NULL,
    DriftCount  int               NOT NULL,
    CompletedAt datetimeoffset(3) NOT NULL
);
CREATE INDEX IX_ReconciliationRuns_Provider ON payments.ReconciliationRuns (Provider, CompletedAt DESC);

CREATE TABLE payments.ReconciliationDrifts
(
    DriftId        bigint IDENTITY(1, 1) NOT NULL CONSTRAINT PK_ReconciliationDrifts PRIMARY KEY,
    RunId          uniqueidentifier NOT NULL CONSTRAINT FK_ReconciliationDrifts_Runs REFERENCES payments.ReconciliationRuns (RunId),
    Kind           tinyint          NOT NULL,
    Reference      nvarchar(200)    NOT NULL,
    ProviderAmount bigint           NULL,
    LedgerAmount   bigint           NULL,
    Currency       char(3)          NOT NULL,
    Detail         nvarchar(400)    NOT NULL
);
CREATE INDEX IX_ReconciliationDrifts_Run ON payments.ReconciliationDrifts (RunId);
