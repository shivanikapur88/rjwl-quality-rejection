using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quality.Core.Abstractions;
using Quality.Core.Dtos;
using Quality.Core.Entities;

namespace Quality.Tests.Integration;

/// <summary>Fifth full review (Sonnet 5.5): unreadable entry tables are visible in readiness and in the server log;
/// production-count rows must carry every field.</summary>
public sealed class Round5Tests(SqlFixture f) : SqlTest(f)
{
    private sealed class CollectingLogger : ILoggerProvider, ILogger
    {
        public readonly ConcurrentQueue<(LogLevel Level, string Category, string Message)> Entries = new();
        private string _category = "";
        public ILogger CreateLogger(string categoryName) => new CollectingLogger { _category = categoryName, Sink = this };
        private CollectingLogger? Sink { get; init; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            (Sink ?? this).Entries.Enqueue((logLevel, _category, formatter(state, exception)));
        public void Dispose() { }
    }

    /// <summary>The GDC store's reads fail with <paramref name="error"/> (the connection itself is fine).</summary>
    private sealed class UnreadableRouter(IEntryStoreRouter inner, string key, Func<Exception> error) : IEntryStoreRouter
    {
        public IEntryStore For(string k) => k == key ? new Store(inner.For(k), error) : inner.For(k);
        public IReadOnlyList<IEntryStore> All() => inner.All();
        public IReadOnlyCollection<string> Keys => inner.Keys;
        public Task<bool> CanConnectAsync(string k, CancellationToken ct) => inner.CanConnectAsync(k, ct);

        private sealed class Store(IEntryStore s, Func<Exception> error) : IEntryStore
        {
            public Task<QualityRejectionEntry?> FindBySubmissionIdAsync(Guid id) => s.FindBySubmissionIdAsync(id);
            public Task<QualityRejectionEntry> InsertAsync(QualityRejectionEntry e) => s.InsertAsync(e);
            public Task<QualityRejectionEntry?> GetByIdAsync(long id) => s.GetByIdAsync(id);
            public Task<IReadOnlyList<QualityRejectionEntry>> GetByIdsAsync(IReadOnlyCollection<long> ids) => s.GetByIdsAsync(ids);
            public Task<IReadOnlyList<QualityRejectionEntry>> ListAsync(EntryFilter f) => s.ListAsync(f);
            public Task<bool> IsPhenomenonUsedAsync(int id) => s.IsPhenomenonUsedAsync(id);
            public Task<bool> IsMachineUsedAsync(int id) => s.IsMachineUsedAsync(id);
            public Task<int> CountWithoutProductionDateAsync(CancellationToken ct) => throw error();
            public Task<IReadOnlyList<EntryAggregateRow>> AggregateAsync(int shopId, DateOnly from, DateOnly to, string? shift) => throw error();
            public Task<QualityRejectionEntry?> ApplyCorrectionAsync(int shopId, long entryId, long correctionId, DateTime nowUtc, QualityRejectionEntry? r) =>
                s.ApplyCorrectionAsync(shopId, entryId, correctionId, nowUtc, r);
            public Task<IReadOnlyList<QualityRejectionEntry>> ListForReportAsync(int shopId, DateOnly from, DateOnly to, string? shift, int? m, int? p, int limit) =>
                s.ListForReportAsync(shopId, from, to, shift, m, p, limit);
        }
    }

    private (Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory, CollectingLogger Log) Unreadable(Func<Exception> error)
    {
        var log = new CollectingLogger();
        var factory = F.CreateFactory(new Dictionary<string, string?>()).WithWebHostBuilder(b =>
        {
            b.ConfigureServices(s => s.AddSingleton<ILoggerProvider>(log));
            b.ConfigureTestServices(services =>
            {
                var original = services.Last(d => d.ServiceType == typeof(IEntryStoreRouter));
                services.Remove(original);
                services.AddScoped<IEntryStoreRouter>(sp => new UnreadableRouter((IEntryStoreRouter)original.ImplementationFactory!(sp), "GDC", error));
            });
        });
        return (factory, log);
    }

    [Fact]
    public async Task Readiness_reports_an_entries_table_that_cannot_be_read_and_reports_log_why_a_shop_is_unavailable()
    {
        // e.g. db/15 applied without SELECT on the entries table, or script 12 not run (missing column): the connection opens.
        var (factory, log) = Unreadable(() => new InvalidOperationException("simulated: SELECT permission denied"));
        await using (factory)
        {
            var ready = await factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") }).GetAsync("/api/health/ready");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            var dto = (await ready.Content.ReadFromJsonAsync<ReadinessDto>())!;
            Assert.Equal("unavailable", dto.Components["entries:GDC"]);
            Assert.Equal("ok", dto.Components["entries:CNC"]);
            Assert.Contains(dto.Warnings, w => w.StartsWith("entries:GDC: connected, but reading the entries table failed"));
            Assert.DoesNotContain(dto.Warnings, w => w.Contains("permission denied"));   // details only in the log (anonymous endpoint)
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("entries table of GDC"));

            var http = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
            http.DefaultRequestHeaders.Authorization = new("Bearer", await F.AdminTokenAsync());
            var r = (await http.GetFromJsonAsync<ReportDto>("/api/reports/totals?fromDate=2032-08-01&toDate=2032-08-02"))!;
            Assert.Contains("GDC", r.UnavailableShops);
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("shop GDC") && e.Category.EndsWith("ReportService"));
        }
    }

    [Fact]
    public async Task A_slow_count_in_readiness_is_a_warning_not_an_outage()
    {
        var (factory, _) = Unreadable(() => new OperationCanceledException("simulated: the 5 s probe timeout"));
        await using (factory)
        {
            var ready = await factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") }).GetAsync("/api/health/ready");
            var dto = (await ready.Content.ReadFromJsonAsync<ReadinessDto>())!;
            Assert.Equal("ok", dto.Components["entries:GDC"]);
            Assert.Contains(dto.Warnings, w => w.StartsWith("entries:GDC: the check for entries without a production day took too long"));
        }
    }

    [Fact]
    public async Task A_production_count_row_without_the_produced_quantity_is_refused_not_read_as_zero()
    {
        var admin = F.Client(await F.AdminTokenAsync());
        // A feed that names the column like the CSV ("produced") instead of the API's "producedQuantity".
        var body = """{"dryRun":false,"rows":[{"shopCode":"CNC","machineCode":"X","productionDate":"2032-08-01","shiftCode":"A","produced":500}]}""";
        var r = await admin.PostAsync("/api/admin/production-counts", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("producedQuantity", await r.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }
}
