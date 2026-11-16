using System.Data.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CheckoutReload.HiddenTests.Infrastructure;

public enum FaultMode
{
    None,
    /// <summary>Hold the first command that inserts into Orders (simulates a slow database / request that outlives the client timeout).</summary>
    DelayFirstOrderInsert,
    /// <summary>Fail the first command that inserts into Orders (failure before persistence).</summary>
    FailFirstOrderInsert,
    /// <summary>
    /// Let the first Orders insert execute, then fail the next database command of that request; if there is
    /// none, fail the HTTP response instead (failure after persistence, before the customer is told).
    /// If the implementation persists the order and its follow-up work atomically, the order rolls back.
    /// </summary>
    FailAfterFirstOrderInsert,
}

public sealed class InjectedFaultException(string message) : Exception(message);

/// <summary>Observes the implementation only at its database boundary and HTTP boundary.</summary>
public sealed class FaultInjector
{
    private int _orderInserts;
    private int _faultsFired;
    private int _inFlight;
    private int _afterPersistencePending;

    public FaultMode Mode { get; private set; }
    public TimeSpan Delay { get; private set; }
    public int OrderInsertsSeen => Volatile.Read(ref _orderInserts);
    public int FaultsFired => Volatile.Read(ref _faultsFired);
    public int InFlight => Volatile.Read(ref _inFlight);
    public Interceptor Interceptor { get; }

    public TransactionObserver Transactions { get; }

    public FaultInjector()
    {
        Interceptor = new Interceptor(this);
        Transactions = new TransactionObserver(this);
    }

    public void Arm(FaultMode mode, TimeSpan delay = default)
    {
        Mode = mode;
        Delay = delay;
        Interlocked.Exchange(ref _orderInserts, 0);
        Interlocked.Exchange(ref _faultsFired, 0);
        Interlocked.Exchange(ref _afterPersistencePending, 0);
        Volatile.Write(ref _orderTransaction, null);
    }

    public static bool IsOrderInsert(DbCommand command) =>
        command.CommandText.Contains("INSERT INTO [Orders]", StringComparison.OrdinalIgnoreCase);

    internal async Task BeforeAsync(DbCommand command)
    {
        var isInsert = IsOrderInsert(command);
        switch (Mode)
        {
            case FaultMode.DelayFirstOrderInsert when isInsert:
                if (Interlocked.Increment(ref _orderInserts) == 1) { Interlocked.Increment(ref _faultsFired); await Task.Delay(Delay); }
                break;
            case FaultMode.FailFirstOrderInsert when isInsert:
                if (Interlocked.Increment(ref _orderInserts) == 1)
                {
                    Interlocked.Increment(ref _faultsFired);
                    throw new InjectedFaultException("Injected failure before the order was persisted.");
                }
                break;
            case FaultMode.FailAfterFirstOrderInsert when !isInsert:
                if (Interlocked.CompareExchange(ref _afterPersistencePending, 0, 1) == 1)
                {
                    Interlocked.Increment(ref _faultsFired);
                    throw new InjectedFaultException("Injected failure after the order insert executed.");
                }
                break;
        }
    }

    private DbTransaction? _orderTransaction;

    internal void After(DbCommand command)
    {
        if (Mode == FaultMode.FailAfterFirstOrderInsert && IsOrderInsert(command) && Interlocked.Increment(ref _orderInserts) == 1)
        {
            // Persisted only once committed: immediately when no transaction is active, otherwise at commit.
            if (command.Transaction is null) Interlocked.Exchange(ref _afterPersistencePending, 1);
            else Volatile.Write(ref _orderTransaction, command.Transaction);
        }
        else if (Mode == FaultMode.None && IsOrderInsert(command))
            Interlocked.Increment(ref _orderInserts);
    }

    internal void Committed(DbTransaction transaction)
    {
        if (Mode == FaultMode.FailAfterFirstOrderInsert && ReferenceEquals(Interlocked.CompareExchange(ref _orderTransaction, null, transaction), transaction))
            Interlocked.Exchange(ref _afterPersistencePending, 1);
    }

    internal void RolledBack(DbTransaction transaction) => Interlocked.CompareExchange(ref _orderTransaction, null, transaction);

    internal bool ConsumePendingResponseFault()
    {
        if (Interlocked.CompareExchange(ref _afterPersistencePending, 0, 1) != 1) return false;
        Interlocked.Increment(ref _faultsFired);
        return true;
    }

    internal void Enter() => Interlocked.Increment(ref _inFlight);
    internal void Exit() => Interlocked.Decrement(ref _inFlight);

    /// <summary>Waits until no request is executing on the server (requests can outlive client timeouts).</summary>
    public async Task WaitForIdleAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (InFlight > 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
        await Task.Delay(200);
    }
}

public sealed class Interceptor(FaultInjector faults) : DbCommandInterceptor
{
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    { await faults.BeforeAsync(command); return result; }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    { faults.BeforeAsync(command).GetAwaiter().GetResult(); return result; }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { await faults.BeforeAsync(command); return result; }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    { faults.BeforeAsync(command).GetAwaiter().GetResult(); return result; }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    { await faults.BeforeAsync(command); return result; }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    { faults.BeforeAsync(command).GetAwaiter().GetResult(); return result; }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    { faults.After(command); return ValueTask.FromResult(result); }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    { faults.After(command); return result; }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    { faults.After(command); return ValueTask.FromResult(result); }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    { faults.After(command); return result; }
}

public sealed class TransactionObserver(FaultInjector faults) : DbTransactionInterceptor
{
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => faults.Committed(transaction);

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    { faults.Committed(transaction); return Task.CompletedTask; }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => faults.RolledBack(transaction);

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    { faults.RolledBack(transaction); return Task.CompletedTask; }
}

/// <summary>Outermost middleware: tracks in-flight requests and delivers a pending after-persistence fault.</summary>
public sealed class FaultMiddlewareStartupFilter(FaultInjector faults) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (HttpContext context, RequestDelegate nextDelegate) =>
        {
            faults.Enter();
            try
            {
                await nextDelegate(context);
                if (faults.ConsumePendingResponseFault())
                    throw new InjectedFaultException("Injected failure after the order was persisted, before the response reached the customer.");
            }
            finally { faults.Exit(); }
        });
        next(app);
    };
}
