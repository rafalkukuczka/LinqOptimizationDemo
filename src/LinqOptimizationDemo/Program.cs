using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

Console.WriteLine("LINQ Optimization Demo - EF Core + SQLite");
Console.WriteLine("=========================================");

var dbPath = Path.Combine(AppContext.BaseDirectory, "linq-demo.db");
var connectionString = $"Data Source={dbPath}";

var slowQueryInterceptor = new SlowQueryInterceptor(thresholdMs: 100);

var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseSqlite(connectionString)
    .EnableDetailedErrors()
    // Enable this only in local/dev. It prints parameter values.
    .EnableSensitiveDataLogging()
    .LogTo(
        message =>
        {
            if (message.Contains("Executed DbCommand"))
                Console.WriteLine("[EF SQL LOG] " + message.Trim());
        },
        new[] { DbLoggerCategory.Database.Command.Name },
        LogLevel.Information)
    .AddInterceptors(slowQueryInterceptor)
    .Options;

await using var db = new AppDbContext(options);
await db.Database.EnsureCreatedAsync();

await SeedDatabaseAsync(db, customersCount: 20_000, ordersPerCustomer: 5);

Console.WriteLine();
Console.WriteLine("Database ready.");
Console.WriteLine($"Customers: {await db.Customers.CountAsync():N0}");
Console.WriteLine($"Orders:    {await db.Orders.CountAsync():N0}");
Console.WriteLine();

await DemoToQueryString(db);
await DemoIQueryableVsIEnumerable(db);
await DemoProjectionVsFullEntity(db);
await DemoAsNoTracking(db);
await DemoAnyVsCount(db);
await DemoNPlusOne(db);
await DemoIncludeVsProjection(db);
await DemoPagination(db);
await DemoDateFiltering(db);
await DemoIndexFriendlySearch(db);
await DemoAggregationInDatabase(db);
await DemoContainsInBatches(db);

Console.WriteLine();
Console.WriteLine("Done. Open Program.cs and change record counts or thresholds to experiment.");
Console.WriteLine("Tip: delete linq-demo.db from bin output folder to reseed from scratch.");

static async Task SeedDatabaseAsync(AppDbContext db, int customersCount, int ordersPerCustomer)
{
    if (await db.Customers.AnyAsync())
        return;

    Console.WriteLine("Seeding database. This can take a moment on first run...");

    var random = new Random(123);
    var cities = new[] { "Warsaw", "Berlin", "Munich", "Zurich", "Vienna", "Gdansk", "Poznan", "Hamburg" };
    var domains = new[] { "example.com", "factory.test", "pkey.info", "demo.local" };

    const int batchSize = 1000;
    var created = new DateTime(2020, 1, 1);

    for (var i = 1; i <= customersCount; i++)
    {
        var customer = new Customer
        {
            Name = i % 10 == 0 ? $"John Customer {i}" : $"Customer {i}",
            Email = $"customer{i}@{domains[i % domains.Length]}",
            NormalizedEmail = $"CUSTOMER{i}@{domains[i % domains.Length]}".ToUpperInvariant(),
            City = cities[i % cities.Length],
            IsActive = i % 3 != 0,
            CreatedAt = created.AddDays(i % 1500)
        };

        for (var j = 1; j <= ordersPerCustomer; j++)
        {
            customer.Orders.Add(new Order
            {
                OrderNumber = $"ORD-{i:D6}-{j:D2}",
                CreatedAt = created.AddDays(random.Next(0, 1500)),
                Total = Math.Round((decimal)(random.NextDouble() * 5000 + 50), 2),
                Status = j % 4 == 0 ? "Cancelled" : j % 3 == 0 ? "Pending" : "Completed"
            });
        }

        db.Customers.Add(customer);

        if (i % batchSize == 0)
        {
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            Console.WriteLine($"Seeded {i:N0} customers...");
        }
    }

    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
}

static async Task DemoToQueryString(AppDbContext db)
{
    PrintHeader("1. Inspect generated SQL with ToQueryString()");

    var query = db.Customers
        .Where(c => c.IsActive && c.City == "Warsaw")
        .OrderBy(c => c.Name)
        .Take(10)
        .Select(c => new CustomerDto(c.Id, c.Name, c.Email));

    Console.WriteLine(query.ToQueryString());

    var result = await MeasureAsync("Execute inspected query", () => query.ToListAsync());
    Console.WriteLine($"Rows: {result.Count}");
}

static async Task DemoIQueryableVsIEnumerable(AppDbContext db)
{
    PrintHeader("2. IQueryable vs IEnumerable / AsEnumerable problem");

    IQueryable<Customer> goodQuery = db.Customers
        .Where(c => c.IsActive && c.City == "Berlin");

    var good = await MeasureAsync("GOOD IQueryable filter in SQL", () => goodQuery.Take(100).ToListAsync());

    var bad = await MeasureAsync("BAD AsEnumerable filter in memory", async () =>
    {
        // This intentionally switches from EF translation to LINQ-to-Objects.
        // The table is read first, then filtered in C# memory.
        return db.Customers
            .AsEnumerable()
            .Where(c => c.IsActive && c.City == "Berlin")
            .Take(100)
            .ToList();
    });

    Console.WriteLine($"Good rows: {good.Count}, bad rows: {bad.Count}");
}

static async Task DemoProjectionVsFullEntity(AppDbContext db)
{
    PrintHeader("3. Projection vs loading full entities");

    var full = await MeasureAsync("BAD load full entity", () => db.Customers
        .Where(c => c.IsActive)
        .Take(5000)
        .ToListAsync());

    var projected = await MeasureAsync("GOOD select only required columns", () => db.Customers
        .Where(c => c.IsActive)
        .Take(5000)
        .Select(c => new CustomerDto(c.Id, c.Name, c.Email))
        .ToListAsync());

    Console.WriteLine($"Full entities: {full.Count}, DTOs: {projected.Count}");
}

static async Task DemoAsNoTracking(AppDbContext db)
{
    PrintHeader("4. AsNoTracking for read-only queries");

    db.ChangeTracker.Clear();

    var tracked = await MeasureAsync("Tracked query", () => db.Customers
        .Where(c => c.IsActive)
        .Take(10000)
        .ToListAsync());

    Console.WriteLine($"ChangeTracker entries after tracked query: {db.ChangeTracker.Entries().Count():N0}");
    db.ChangeTracker.Clear();

    var notTracked = await MeasureAsync("AsNoTracking query", () => db.Customers
        .AsNoTracking()
        .Where(c => c.IsActive)
        .Take(10000)
        .ToListAsync());

    Console.WriteLine($"ChangeTracker entries after AsNoTracking: {db.ChangeTracker.Entries().Count():N0}");
    Console.WriteLine($"Rows: tracked={tracked.Count}, noTracking={notTracked.Count}");
}

static async Task DemoAnyVsCount(AppDbContext db)
{
    PrintHeader("5. Any() vs Count() > 0");

    var countResult = await MeasureAsync("BAD Count() > 0", async () =>
        await db.Orders.CountAsync(o => o.Status == "Completed") > 0);

    var anyResult = await MeasureAsync("GOOD Any()", () =>
        db.Orders.AnyAsync(o => o.Status == "Completed"));

    Console.WriteLine($"Count result: {countResult}, Any result: {anyResult}");
}

static async Task DemoNPlusOne(AppDbContext db)
{
    PrintHeader("6. N+1 query problem");

    var bad = await MeasureAsync("BAD N+1: query orders inside loop", async () =>
    {
        var customers = await db.Customers
            .AsNoTracking()
            .Where(c => c.IsActive)
            .Take(100)
            .ToListAsync();

        var totalOrders = 0;
        foreach (var customer in customers)
        {
            totalOrders += await db.Orders.CountAsync(o => o.CustomerId == customer.Id);
        }

        return totalOrders;
    });

    var good = await MeasureAsync("GOOD single grouped query", async () =>
    {
        var customerIds = await db.Customers
            .AsNoTracking()
            .Where(c => c.IsActive)
            .Take(100)
            .Select(c => c.Id)
            .ToListAsync();

        return await db.Orders
            .Where(o => customerIds.Contains(o.CustomerId))
            .CountAsync();
    });

    Console.WriteLine($"Total orders: bad={bad}, good={good}");
}

static async Task DemoIncludeVsProjection(AppDbContext db)
{
    PrintHeader("7. Include full graph vs projection summary");

    var include = await MeasureAsync("BAD/expensive Include full Orders", () => db.Customers
        .AsNoTracking()
        .Include(c => c.Orders)
        .Where(c => c.IsActive)
        .Take(1000)
        .ToListAsync());

    var projection = await MeasureAsync("GOOD projection with aggregate", () => db.Customers
        .AsNoTracking()
        .Where(c => c.IsActive)
        .Take(1000)
        .Select(c => new CustomerOrderSummaryDto(
            c.Id,
            c.Name,
            c.Orders.Count,
            c.Orders.Sum(o => o.Total)))
        .ToListAsync());

    Console.WriteLine($"Included customers: {include.Count}, projected summaries: {projection.Count}");
}

static async Task DemoPagination(AppDbContext db)
{
    PrintHeader("8. Offset pagination vs keyset pagination");

    var offsetPage = await MeasureAsync("OFFSET Skip/Take", () => db.Customers
        .AsNoTracking()
        .OrderBy(c => c.Id)
        .Skip(15000)
        .Take(50)
        .Select(c => new CustomerDto(c.Id, c.Name, c.Email))
        .ToListAsync());

    var lastSeenId = 15000;
    var keysetPage = await MeasureAsync("KEYSET Id > lastSeenId", () => db.Customers
        .AsNoTracking()
        .Where(c => c.Id > lastSeenId)
        .OrderBy(c => c.Id)
        .Take(50)
        .Select(c => new CustomerDto(c.Id, c.Name, c.Email))
        .ToListAsync());

    Console.WriteLine($"Offset page: {offsetPage.Count}, keyset page: {keysetPage.Count}");
}

static async Task DemoDateFiltering(AppDbContext db)
{
    PrintHeader("9. Date filtering: function on column vs range");

    var date = new DateTime(2022, 5, 1);
    var next = date.AddDays(1);

    var bad = await MeasureAsync("BAD CreatedAt.Date == date", () => db.Orders
        .AsNoTracking()
        .Where(o => o.CreatedAt.Date == date)
        .Take(100)
        .ToListAsync());

    var good = await MeasureAsync("GOOD CreatedAt >= start && < end", () => db.Orders
        .AsNoTracking()
        .Where(o => o.CreatedAt >= date && o.CreatedAt < next)
        .Take(100)
        .ToListAsync());

    Console.WriteLine($"Rows: bad={bad.Count}, good={good.Count}");
}

static async Task DemoIndexFriendlySearch(AppDbContext db)
{
    PrintHeader("10. Index-friendly string search");

    var contains = await MeasureAsync("Usually worse: Contains", () => db.Customers
        .AsNoTracking()
        .Where(c => c.Name.Contains("John"))
        .Take(100)
        .ToListAsync());

    var startsWith = await MeasureAsync("Usually better: StartsWith", () => db.Customers
        .AsNoTracking()
        .Where(c => c.Name.StartsWith("John"))
        .Take(100)
        .ToListAsync());

    var email = "CUSTOMER100@EXAMPLE.COM";
    var normalized = await MeasureAsync("Best for exact email: indexed normalized column", () => db.Customers
        .AsNoTracking()
        .Where(c => c.NormalizedEmail == email)
        .ToListAsync());

    Console.WriteLine($"Rows: contains={contains.Count}, startsWith={startsWith.Count}, normalizedEmail={normalized.Count}");
}

static async Task DemoAggregationInDatabase(AppDbContext db)
{
    PrintHeader("11. Aggregation in database vs memory");

    var bad = await MeasureAsync("BAD load rows then Sum in memory", async () =>
    {
        var orders = await db.Orders
            .AsNoTracking()
            .Where(o => o.Status == "Completed")
            .Take(20000)
            .ToListAsync();

        return orders.Sum(o => Convert.ToDecimal(o.Total));
    });

    var good = await MeasureAsync("GOOD SumAsync in SQL", () => db.Orders
        .Where(o => o.Status == "Completed")
        .SumAsync(o => o.Total));

    Console.WriteLine($"Totals: memory={bad:N2}, sql={good:N2}");
}

static async Task DemoContainsInBatches(AppDbContext db)
{
    PrintHeader("12. Contains / SQL IN with batching");

    var ids = Enumerable.Range(1, 5000).Select(x => x * 2).ToArray();

    var singleIn = await MeasureAsync("Single large Contains/IN", () => db.Customers
        .AsNoTracking()
        .Where(c => ids.Contains(c.Id))
        .Select(c => new CustomerDto(c.Id, c.Name, c.Email))
        .ToListAsync());

    var batched = await MeasureAsync("Batched Contains/IN", async () =>
    {
        var list = new List<CustomerDto>();
        foreach (var batch in ids.Chunk(500))
        {
            var chunk = await db.Customers
                .AsNoTracking()
                .Where(c => batch.Contains(c.Id))
                .Select(c => new CustomerDto(c.Id, c.Name, c.Email))
                .ToListAsync();

            list.AddRange(chunk);
        }

        return list;
    });

    Console.WriteLine($"Rows: single={singleIn.Count}, batched={batched.Count}");
}

static async Task<T> MeasureAsync<T>(string title, Func<Task<T>> action)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var before = GC.GetAllocatedBytesForCurrentThread();
    var sw = Stopwatch.StartNew();
    var result = await action();
    sw.Stop();
    var after = GC.GetAllocatedBytesForCurrentThread();

    Console.WriteLine($"{title,-45} | {sw.ElapsedMilliseconds,6} ms | allocated approx: {(after - before) / 1024.0,10:N1} KB");
    return result;
}

static void PrintHeader(string title)
{
    Console.WriteLine();
    Console.WriteLine(new string('-', 100));
    Console.WriteLine(title);
    Console.WriteLine(new string('-', 100));
}

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Email).HasMaxLength(300);
            entity.Property(x => x.NormalizedEmail).HasMaxLength(300);
            entity.Property(x => x.City).HasMaxLength(100);

            entity.HasIndex(x => x.IsActive);
            entity.HasIndex(x => x.City);
            entity.HasIndex(x => x.Name);
            entity.HasIndex(x => x.NormalizedEmail).IsUnique();
            entity.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OrderNumber).HasMaxLength(50);
            entity.Property(x => x.Status).HasMaxLength(50);
            entity.Property(x => x.Total).HasPrecision(18, 2);

            entity.HasIndex(x => x.CustomerId);
            entity.HasIndex(x => x.CreatedAt);
            entity.HasIndex(x => x.Status);

            entity.HasOne(x => x.Customer)
                .WithMany(x => x.Orders)
                .HasForeignKey(x => x.CustomerId);
            
            entity.Property(o=>o.Total)
            .HasConversion<double>();

        });
    }
}

public sealed class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<Order> Orders { get; set; } = new();
}

public sealed class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string OrderNumber { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public decimal Total { get; set; }
    public string Status { get; set; } = string.Empty;
}

public sealed record CustomerDto(int Id, string Name, string Email);
public sealed record CustomerOrderSummaryDto(int Id, string Name, int OrdersCount, decimal TotalValue);

public sealed class SlowQueryInterceptor : DbCommandInterceptor
{
    private readonly long _thresholdMs;
    private readonly ConcurrentDictionary<Guid, Stopwatch> _stopwatches = new();

    public SlowQueryInterceptor(long thresholdMs)
    {
        _thresholdMs = thresholdMs;
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        _stopwatches[eventData.CommandId] = Stopwatch.StartNew();
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        _stopwatches[eventData.CommandId] = Stopwatch.StartNew();
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result)
    {
        LogIfSlow(command, eventData.CommandId);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        LogIfSlow(command, eventData.CommandId);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        _stopwatches[eventData.CommandId] = Stopwatch.StartNew();
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        _stopwatches[eventData.CommandId] = Stopwatch.StartNew();
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override int NonQueryExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result)
    {
        LogIfSlow(command, eventData.CommandId);
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        LogIfSlow(command, eventData.CommandId);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        _stopwatches[eventData.CommandId] = Stopwatch.StartNew();
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        _stopwatches[eventData.CommandId] = Stopwatch.StartNew();
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result)
    {
        LogIfSlow(command, eventData.CommandId);
        return base.ScalarExecuted(command, eventData, result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result,
        CancellationToken cancellationToken = default)
    {
        LogIfSlow(command, eventData.CommandId);
        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    private void LogIfSlow(DbCommand command, Guid commandId)
    {
        if (!_stopwatches.TryRemove(commandId, out var stopwatch))
            return;

        stopwatch.Stop();

        if (stopwatch.ElapsedMilliseconds >= _thresholdMs)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[SLOW QUERY WARNING] {stopwatch.ElapsedMilliseconds} ms");
            Console.ResetColor();
            Console.WriteLine(command.CommandText);
        }
    }
}
