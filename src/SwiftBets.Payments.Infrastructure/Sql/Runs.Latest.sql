DECLARE @RunId uniqueidentifier = (SELECT TOP (1) RunId FROM payments.ReconciliationRuns WHERE Provider = @Provider ORDER BY CompletedAt DESC);
SELECT RunId, Provider, Day, CompletedAt FROM payments.ReconciliationRuns WHERE RunId = @RunId;
SELECT Kind, Reference, ProviderAmount, LedgerAmount, Currency, Detail FROM payments.ReconciliationDrifts WHERE RunId = @RunId ORDER BY DriftId;
