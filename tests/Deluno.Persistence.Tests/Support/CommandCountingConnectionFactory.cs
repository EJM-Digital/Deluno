using System.Data;
using System.Data.Common;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Support;

/// <summary>
/// Wraps the real connection factory and counts every SQL command run through it.
///
/// <para>A test of "this call costs the same however big the library is" used to
/// time the call, and a busy runner failed it (#1506: one page took 303 ms). The
/// shape it defends is a number of statements, and statements do not depend on
/// how busy the machine is. Pass <see cref="Install"/> as the
/// <c>replaceServices</c> of <c>ApplicationTestHost.StartAsync</c>.</para>
/// </summary>
public sealed class CommandCountingConnectionFactory(IDelunoDatabaseConnectionFactory real, CommandCounter counter)
    : IDelunoDatabaseConnectionFactory
{
    public static Action<IServiceCollection> Install(CommandCounter counter)
        => services =>
        {
            var descriptor = services.Single(item => item.ServiceType == typeof(IDelunoDatabaseConnectionFactory));
            services.Remove(descriptor);
            services.Add(new ServiceDescriptor(
                typeof(IDelunoDatabaseConnectionFactory),
                provider => new CommandCountingConnectionFactory(
                    (IDelunoDatabaseConnectionFactory)(descriptor.ImplementationType is not null
                        ? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType)
                        : descriptor.ImplementationFactory!(provider)),
                    counter),
                descriptor.Lifetime));
        };

    public string GetDatabasePath(string databaseName) => real.GetDatabasePath(databaseName);

    public long GetWriteGeneration(string databaseName) => real.GetWriteGeneration(databaseName);

    public async ValueTask<DbConnection> OpenConnectionAsync(string databaseName, CancellationToken cancellationToken = default)
        => new CountingConnection(await real.OpenConnectionAsync(databaseName, cancellationToken), counter);

    public async ValueTask<DbConnection> OpenReadOnlyConnectionAsync(string databaseName, CancellationToken cancellationToken = default)
        => new CountingConnection(await real.OpenReadOnlyConnectionAsync(databaseName, cancellationToken), counter);

    private sealed class CountingConnection(DbConnection inner, CommandCounter counter) : DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get => inner.ConnectionString; set => inner.ConnectionString = value; }
        public override string Database => inner.Database;
        public override string DataSource => inner.DataSource;
        public override string ServerVersion => inner.ServerVersion;
        public override ConnectionState State => inner.State;
        public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);
        public override void Close() => inner.Close();
        public override void Open() => inner.Open();
        public override Task OpenAsync(CancellationToken cancellationToken) => inner.OpenAsync(cancellationToken);
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => inner.BeginTransaction(isolationLevel);
        protected override DbCommand CreateDbCommand() => new CountingCommand(inner.CreateCommand(), this, counter);
        public override ValueTask DisposeAsync() => inner.DisposeAsync();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class CountingCommand(DbCommand inner, DbConnection owner, CommandCounter counter) : DbCommand
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get => inner.CommandText; set => inner.CommandText = value; }
        public override int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
        public override CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
        public override bool DesignTimeVisible { get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value; }
        public override UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
        protected override DbConnection? DbConnection { get => owner; set { } }
        protected override DbParameterCollection DbParameterCollection => inner.Parameters;
        protected override DbTransaction? DbTransaction { get => inner.Transaction; set => inner.Transaction = value; }
        public override void Cancel() => inner.Cancel();
        public override void Prepare() => inner.Prepare();
        protected override DbParameter CreateDbParameter() => inner.CreateParameter();

        public override int ExecuteNonQuery() { counter.Record(inner.CommandText); return inner.ExecuteNonQuery(); }
        public override object? ExecuteScalar() { counter.Record(inner.CommandText); return inner.ExecuteScalar(); }
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) { counter.Record(inner.CommandText); return inner.ExecuteReader(behavior); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>The SQL commands a test's calls ran, in order.</summary>
public sealed class CommandCounter
{
    private readonly List<string> _commands = [];

    public int Count { get { lock (_commands) return _commands.Count; } }

    public IReadOnlyList<string> Commands { get { lock (_commands) return _commands.ToArray(); } }

    public void Record(string commandText) { lock (_commands) _commands.Add(commandText); }

    public void Reset() { lock (_commands) _commands.Clear(); }
}
