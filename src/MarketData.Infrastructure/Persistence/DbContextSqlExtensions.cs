using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace MarketData.Infrastructure.Persistence;

internal static class DbContextSqlExtensions
{
    /// <summary>
    /// A raw Npgsql command on the context's connection, joined to its current transaction. That lets
    /// a caller commit repository writes together with <c>pg_notify</c> (ADR-007).
    /// </summary>
    /// <remarks>
    /// Hand-written SQL that EF can't express (upserts, <c>date_bin</c> rollups, gap scans) goes through
    /// here, with explicit column ordinals instead of name-based materialization.
    /// </remarks>
    public static async Task<NpgsqlCommand> CreateCommandAsync(this MarketDataDbContext db, string sql, CancellationToken cancellationToken)
    {
        if (db.Database.GetDbConnection().State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();
        return command;
    }

    public static NpgsqlCommand With<T>(this NpgsqlCommand command, string name, T value)
    {
        command.Parameters.Add(new NpgsqlParameter<T>(name, value));
        return command;
    }

    /// <summary>Npgsql writes <c>timestamptz</c> only from UTC values.</summary>
    public static DateTime Utc(this DateTimeOffset value) => value.UtcDateTime;

    public static DateTimeOffset GetUtcDateTimeOffset(this NpgsqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}
