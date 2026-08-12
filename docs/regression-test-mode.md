# Regression-test mode

Save a baseline next to a checked-in `.sqlplan`:

```powershell
dotnet run --project src/SqlPlanViz.Baseline -- save samples/orders-actual.sqlplan samples/orders-actual.baseline.json
```

Check it in CI:

```powershell
dotnet run --project src/SqlPlanViz.Baseline -- check samples/orders-actual.sqlplan samples/orders-actual.baseline.json
```

The command exits non-zero when the operator/object shape fingerprint changes or an actual-plan duration exceeds the baseline tolerance (20% by default). Costs are deliberately excluded from the fingerprint.
