# LINQ Optimization Demo - C# / EF Core / SQLite

This repository is a practical demo for an article about optimizing LINQ queries in C#.

It creates a SQLite database with many records and runs slow and optimized LINQ query variants side by side.

## What it demonstrates

- `IQueryable` vs `IEnumerable`
- accidental in-memory filtering with `AsEnumerable()`
- generated SQL with `ToQueryString()`
- EF Core SQL logging
- slow query detection with `DbCommandInterceptor`
- projection vs loading full entities
- `AsNoTracking()` for read-only queries
- `Any()` vs `Count() > 0`
- N+1 query problem
- `Include()` vs projection
- offset pagination vs keyset pagination
- date filtering and index-friendly ranges
- `StartsWith()` vs `Contains()`
- normalized indexed columns
- aggregation in SQL vs aggregation in memory
- `Contains()` / SQL `IN` batching

## Requirements

- .NET 8 SDK
- Visual Studio 2022, Rider, or VS Code

## How to run

From repository root:

```bash
dotnet restore
dotnet run --project src/LinqOptimizationDemo/LinqOptimizationDemo.csproj -c Release
```

On first run the app creates a SQLite database in the output folder:

```text
bin/Release/net8.0/linq-demo.db
```

It seeds:

- 20,000 customers
- 100,000 orders

You can increase the numbers in `Program.cs`:

```csharp
await SeedDatabaseAsync(db, customersCount: 20_000, ordersPerCustomer: 5);
```

For stronger performance differences try:

```csharp
await SeedDatabaseAsync(db, customersCount: 100_000, ordersPerCustomer: 10);
```

Delete `linq-demo.db` if you want to regenerate the database.

## Important files

```text
LinqOptimizationDemo.sln
src/LinqOptimizationDemo/LinqOptimizationDemo.csproj
src/LinqOptimizationDemo/Program.cs
```

## Notes

This is an educational project. Some examples are intentionally bad so you can compare them with optimized versions.

Run in Release mode for more realistic timings:

```bash
dotnet run --project src/LinqOptimizationDemo/LinqOptimizationDemo.csproj -c Release
```

## Suggested article section

You can use this project as a downloadable companion for an article titled:

**How to Optimize LINQ Queries in C#: Complete Performance, Logging & Diagnostics Guide**
https://pkey.info/knowledge-base/optimize-linq-queries-csharp-performance/

