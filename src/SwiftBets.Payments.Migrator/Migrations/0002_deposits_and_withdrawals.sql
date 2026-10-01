-- Deposits and withdrawals, each a forward-only state machine; Status is changed only by a conditional update.
CREATE TABLE payments.Deposits
(
    PaymentId         uniqueidentifier  NOT NULL CONSTRAINT PK_Deposits PRIMARY KEY,
    UserId            uniqueidentifier  NOT NULL,
    AccountId         uniqueidentifier  NOT NULL,
    Amount            bigint            NOT NULL CONSTRAINT CK_Deposits_Amount CHECK (Amount > 0),
    Currency          char(3)           NOT NULL,
    Provider          varchar(40)       NOT NULL,
    Status            tinyint           NOT NULL,
    ProviderReference nvarchar(200)     NULL,
    CheckoutUrl       nvarchar(1000)    NULL,
    FailureReason     nvarchar(400)     NULL,
    CreatedAt         datetimeoffset(3) NOT NULL,
    UpdatedAt         datetimeoffset(3) NOT NULL,
    CompletedAt       datetimeoffset(3) NULL
);
CREATE INDEX IX_Deposits_Open ON payments.Deposits (UpdatedAt) WHERE Status IN (1, 2);
CREATE INDEX IX_Deposits_User ON payments.Deposits (UserId, CreatedAt DESC);
CREATE INDEX IX_Deposits_Completed ON payments.Deposits (Provider, CompletedAt) WHERE Status = 3;

CREATE TABLE payments.Withdrawals
(
    WithdrawalId      uniqueidentifier  NOT NULL CONSTRAINT PK_Withdrawals PRIMARY KEY,
    UserId            uniqueidentifier  NOT NULL,
    AccountId         uniqueidentifier  NOT NULL,
    Amount            bigint            NOT NULL CONSTRAINT CK_Withdrawals_Amount CHECK (Amount > 0),
    Currency          char(3)           NOT NULL,
    Provider          varchar(40)       NOT NULL,
    Status            tinyint           NOT NULL,
    ReservationId     uniqueidentifier  NULL,
    RequiresApproval  bit               NOT NULL,
    HoldSettled       bit               NOT NULL CONSTRAINT DF_Withdrawals_HoldSettled DEFAULT (0),
    ProviderReference nvarchar(200)     NULL,
    DecidedBy         nvarchar(100)     NULL,
    Reason            nvarchar(400)     NULL,
    CreatedAt         datetimeoffset(3) NOT NULL,
    UpdatedAt         datetimeoffset(3) NOT NULL,
    CompletedAt       datetimeoffset(3) NULL
);
CREATE INDEX IX_Withdrawals_Unfinished ON payments.Withdrawals (HoldSettled, Status, UpdatedAt);
CREATE INDEX IX_Withdrawals_Status ON payments.Withdrawals (Status, CreatedAt);
CREATE INDEX IX_Withdrawals_User ON payments.Withdrawals (UserId, CreatedAt DESC);
CREATE INDEX IX_Withdrawals_Paid ON payments.Withdrawals (Provider, CompletedAt) WHERE Status = 5;

-- Every provider event seen, by the provider's own event id; a repeat delivery is acknowledged and not applied again.
CREATE TABLE payments.WebhookEvents
(
    Provider   varchar(40)       NOT NULL,
    EventId    nvarchar(200)     NOT NULL,
    Kind       tinyint           NOT NULL,
    Reference  nvarchar(200)     NOT NULL,
    ReceivedAt datetimeoffset(3) NOT NULL,
    CONSTRAINT PK_WebhookEvents PRIMARY KEY (Provider, EventId)
);
