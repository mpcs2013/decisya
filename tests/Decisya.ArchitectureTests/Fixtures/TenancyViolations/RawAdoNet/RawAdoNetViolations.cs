using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations.RawAdoNet;

/// <summary>#24 G4-24-03 red fixture: raw SQL through the transaction's own connection, inside an <c>async</c> method.</summary>
public static class TransactionConnectionCreateCommand
{
    public static async Task RunAsync(DbContext db)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await using var command = tx.GetDbTransaction().Connection!.CreateCommand();
        command.CommandText = "select 1";
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>#24 G4-24-03 red fixture: <c>DbTransaction.CommitAsync</c>, which would commit the caller's work early.</summary>
public static class DbTransactionCommit
{
    public static Task CommitAsync(DbTransaction transaction) => transaction.CommitAsync();
}

/// <summary>#24 G4-24-03 red fixture: <c>DbTransaction.RollbackAsync</c>.</summary>
public static class DbTransactionRollback
{
    public static Task RollbackAsync(DbTransaction transaction) => transaction.RollbackAsync();
}

/// <summary>#24 G4-24-03 red fixture: a new <c>NpgsqlCommand</c>.</summary>
public static class NewNpgsqlCommand
{
    public static NpgsqlCommand Make() => new("select 1");
}

/// <summary>#24 G4-24-03 red fixture: a new <c>NpgsqlConnection</c>.</summary>
public static class NewNpgsqlConnection
{
    public static NpgsqlConnection Make() => new("Host=fixture.invalid");
}

/// <summary>#24 G4-24-03 red fixture: <c>DbCommand.ExecuteScalarAsync</c> on a command made elsewhere.</summary>
public static class DbCommandExecute
{
    public static Task<object?> RunAsync(DbCommand command) => command.ExecuteScalarAsync();
}

/// <summary>#24 G4-24-03 red fixture: closes the caller's connection.</summary>
public static class DbConnectionClose
{
    public static Task CloseAsync(DbTransaction transaction) => transaction.Connection!.CloseAsync();
}

/// <summary>#24 G6-24-01 red fixture: the Npgsql COPY API reached by a cast of the transaction's connection, inside an <c>async</c> method.</summary>
public static class CopyExportThroughCast
{
    public static async Task<string?> RunAsync(DbTransaction transaction)
    {
        await Task.Yield();
        using var reader = ((NpgsqlConnection)transaction.Connection!).BeginTextExport("COPY (SELECT 1) TO STDOUT");
        return await reader.ReadLineAsync();
    }
}

/// <summary>#24 G6-24-01 red fixture: a data adapter runs its own <c>SelectCommand</c> on the connection.</summary>
public static class DataAdapterFill
{
    public static int Run(DbTransaction transaction)
    {
        using var adapter = new NpgsqlDataAdapter("select 1", (NpgsqlConnection)transaction.Connection!);
        using var table = new System.Data.DataTable();
        return adapter.Fill(table);
    }
}
