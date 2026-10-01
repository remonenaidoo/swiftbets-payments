-- Rolls back 0003_reconciliation. Past reconciliation results are dropped; the next run starts from scratch.
DROP TABLE IF EXISTS payments.ReconciliationDrifts;
DROP TABLE IF EXISTS payments.ReconciliationRuns;
