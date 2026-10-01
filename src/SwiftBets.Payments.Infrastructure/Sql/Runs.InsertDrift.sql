INSERT INTO payments.ReconciliationDrifts (RunId, Kind, Reference, ProviderAmount, LedgerAmount, Currency, Detail)
VALUES (@RunId, @Kind, @Reference, @ProviderAmount, @LedgerAmount, @Currency, @Detail);
