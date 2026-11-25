using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.Persistence;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>Small key-value store backed by <c>formsAuditTrailState</c>, shared by every server in a load-balanced site.</summary>
public interface IAuditStateStore
{
    Task<string?> GetAsync(string key, CancellationToken ct);

    /// <summary>Inserts or replaces. Throws <see cref="ArgumentException"/> when <paramref name="json"/> exceeds the column width.</summary>
    Task SetAsync(string key, string json, CancellationToken ct);
}

/// <summary>Holds only the scope factory so it can be a singleton; the scoped DbContext is resolved per call (P2).</summary>
internal sealed class AuditStateStore(IServiceScopeFactory scopeFactory, TimeProvider time) : IAuditStateStore
{
    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        FormsAuditDbContext db = scope.ServiceProvider.GetRequiredService<FormsAuditDbContext>();
        return await db.State.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).SingleOrDefaultAsync(ct);
    }

    public async Task SetAsync(string key, string json, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (key.Length > AccessColumnLimits.StateKey)
        {
            throw new ArgumentException($"State keys are limited to {AccessColumnLimits.StateKey} characters.", nameof(key));
        }

        // Truncating would store invalid JSON that a reader then fails to parse, so refuse instead.
        if (json.Length > AccessColumnLimits.StateValue)
        {
            throw new ArgumentException($"State values are limited to {AccessColumnLimits.StateValue} characters.", nameof(json));
        }

        try
        {
            await UpsertAsync(key, json, ct);
        }
        catch (DbUpdateException)
        {
            // Two servers can both find the key missing and both insert; the loser hits the primary key. Retried once
            // in a fresh context, where the winner's row now exists, so this becomes an update. A second failure is
            // a real fault and escapes.
            await UpsertAsync(key, json, ct);
        }
    }

    private async Task UpsertAsync(string key, string json, CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        FormsAuditDbContext db = scope.ServiceProvider.GetRequiredService<FormsAuditDbContext>();
        AuditStateEntry? row = await db.State.SingleOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            db.State.Add(new AuditStateEntry { Key = key, Value = json, UpdatedUtc = time.GetUtcNow().UtcDateTime });
        }
        else
        {
            row.Value = json;
            row.UpdatedUtc = time.GetUtcNow().UtcDateTime;
        }

        await db.SaveChangesAsync(ct);
    }
}
