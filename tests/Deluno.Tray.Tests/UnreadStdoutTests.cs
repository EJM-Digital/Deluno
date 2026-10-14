using System.IO.Pipes;
using System.Runtime.InteropServices;
using Deluno.Infrastructure;
using Deluno.Tray;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Deluno.Tray.Tests;

/// <summary>Tests that change the process's own standard handles run one at a time, and put them back.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessConsoleCollection
{
    public const string Name = "The process's console";
}

/// <summary>
/// #1258: Weir's server hung inside the console because its standard output was a pipe nobody read. Deluno's tray and
/// service must be immune: whatever the launcher left as their standard output, logging never waits on it. These tests
/// use a real anonymous pipe that is never read, and every wait in them has a limit.
/// </summary>
[Collection(ProcessConsoleCollection.Name)]
public sealed class UnreadStdoutTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);
    private static readonly string Filler = new('x', 2000);
    private const int Lines = 3000; // about 6 MB; a pipe holds a few KB.

    [Fact]
    public async Task The_default_console_logger_does_block_on_an_unread_pipe_which_is_the_hazard()
    {
        using var pipe = new UnreadPipeStdout();
        using var factory = LoggerFactory.Create(logging => logging.AddConsole());
        var logger = factory.CreateLogger("hazard");

        var logging = Task.Run(() => LogMany(logger));

        // The caller is held up (the console logger's queue fills behind its stalled writer), so this does not finish.
        Assert.False(await FinishesWithin(logging, TimeSpan.FromSeconds(5)), "Logging finished, so the pipe was not full: the hazard is not shown.");
        pipe.Release();
    }

    [Fact]
    public async Task Logging_as_the_tray_and_the_service_configure_it_never_waits_on_an_unread_standard_output()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "deluno-1258-" + Guid.NewGuid().ToString("N"));
        using var pipe = new UnreadPipeStdout();
        var builder = TrayLogging.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration["Storage:DataRoot"] = dataRoot;
        builder.Services.AddDelunoInfrastructure(builder.Configuration);
        await using var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("tray");

        var logging = Task.Run(() =>
        {
            LogMany(logger);
            logger.LogWarning("the-last-line-1258");
        });

        Assert.True(await FinishesWithin(logging, Limit), "Logging waited on the unread standard output.");
        pipe.Release();
        await app.DisposeAsync();

        // The file logger is the record.
        var written = Directory.EnumerateFiles(Path.Combine(dataRoot, "logs"), "*", SearchOption.AllDirectories)
            .Any(file => File.ReadAllText(file).Contains("the-last-line-1258", StringComparison.Ordinal));
        Assert.True(written, "The line was not in the log file.");
        try { Directory.Delete(dataRoot, true); } catch (IOException) { }
    }

    [Fact]
    public void The_console_logger_is_not_registered_for_the_tray_or_the_service()
    {
        var builder = TrayLogging.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });

        var names = builder.Services
            .Where(descriptor => descriptor.ServiceType == typeof(ILoggerProvider))
            .Select(descriptor => descriptor.ImplementationType?.Name ?? descriptor.ImplementationInstance?.GetType().Name ?? "factory")
            .ToArray();

        Assert.DoesNotContain("ConsoleLoggerProvider", names);
    }

    [Fact]
    public async Task After_the_standard_handles_are_pointed_at_the_null_device_nothing_waits_on_an_inherited_pipe()
    {
        using var pipe = new UnreadPipeStdout();
        pipe.PointStandardHandlesAtPipe();
        var before = NativeMethods.GetStdHandle(NativeMethods.StdOutput);
        try
        {
            StandardHandles.DetachToNullDevice();

            var after = NativeMethods.GetStdHandle(NativeMethods.StdOutput);
            Assert.NotEqual(before, after);

            // What any code in the process does with standard output now: the raw streams and Console.Out and Error.
            var writing = Task.Run(() =>
            {
                var chunk = new byte[64 * 1024];
                using var raw = Console.OpenStandardOutput();
                for (var i = 0; i < 100; i++) { raw.Write(chunk); raw.Flush(); }
                using var error = Console.OpenStandardError();
                for (var i = 0; i < 100; i++) { error.Write(chunk); error.Flush(); }
                for (var i = 0; i < 100; i++) { Console.Out.Write(Filler); Console.Error.Write(Filler); }
            });
            Assert.True(await FinishesWithin(writing, Limit), "A write to standard output or error waited on the inherited pipe.");

            // Standard input is at its end at once; nothing waits for a keystroke or for the launcher.
            var reading = Task.Run(() => Console.In.ReadLine());
            Assert.True(await FinishesWithin(reading, Limit), "A read of standard input waited.");
            Assert.Null(await reading);
        }
        finally
        {
            pipe.Release();
        }
    }

    [Fact]
    public void The_null_device_handle_can_be_inherited_so_a_program_started_without_redirection_gets_it_and_not_the_pipe()
    {
        using var pipe = new UnreadPipeStdout();
        pipe.PointStandardHandlesAtPipe();
        try
        {
            StandardHandles.DetachToNullDevice();

            foreach (var which in new[] { NativeMethods.StdInput, NativeMethods.StdOutput, NativeMethods.StdError })
            {
                var handle = NativeMethods.GetStdHandle(which);
                Assert.NotEqual(pipe.WriteHandle, handle);
                Assert.True(NativeMethods.GetHandleInformation(handle, out var flags), $"Standard handle {which} is not open.");
                Assert.True((flags & NativeMethods.HandleFlagInherit) != 0, $"Standard handle {which} cannot be inherited.");
            }
        }
        finally
        {
            pipe.Release();
        }
    }

    private static void LogMany(ILogger logger)
    {
        for (var i = 0; i < Lines; i++)
        {
            logger.LogWarning("line {Number} {Filler}", i, Filler);
        }
    }

    private static async Task<bool> FinishesWithin(Task task, TimeSpan limit)
        => await Task.WhenAny(task, Task.Delay(limit)) == task; // reason: the limit is a ceiling that proves the task did not block, not a measured wait

    /// <summary>
    /// The process's standard output and error replaced by the write end of a real anonymous pipe that nobody reads,
    /// as they are when a launcher hands the process a pipe and never drains it. Release puts everything back.
    /// </summary>
    private sealed class UnreadPipeStdout : IDisposable
    {
        private readonly AnonymousPipeServerStream _reader = new(PipeDirection.In, HandleInheritability.Inheritable);
        private readonly AnonymousPipeClientStream _writer;
        private readonly nint _oldOut, _oldErr, _oldIn;
        private readonly TextWriter _oldConsoleOut, _oldConsoleError;
        private readonly TextReader _oldConsoleIn;
        private bool _released;

        public UnreadPipeStdout()
        {
            _oldOut = NativeMethods.GetStdHandle(NativeMethods.StdOutput);
            _oldErr = NativeMethods.GetStdHandle(NativeMethods.StdError);
            _oldIn = NativeMethods.GetStdHandle(NativeMethods.StdInput);
            _oldConsoleOut = Console.Out;
            _oldConsoleError = Console.Error;
            _oldConsoleIn = Console.In;
            _writer = new AnonymousPipeClientStream(PipeDirection.Out, _reader.ClientSafePipeHandle);

            // Console.Out is what the framework's console logger writes to.
            var text = new StreamWriter(_writer) { AutoFlush = true };
            Console.SetOut(text);
            Console.SetError(text);
        }

        public nint WriteHandle => _reader.ClientSafePipeHandle.DangerousGetHandle();

        public void PointStandardHandlesAtPipe()
        {
            var handle = _reader.ClientSafePipeHandle.DangerousGetHandle();
            NativeMethods.SetStdHandle(NativeMethods.StdOutput, handle);
            NativeMethods.SetStdHandle(NativeMethods.StdError, handle);
        }

        /// <summary>Puts the console back and closes the pipe, which frees anything still stuck writing to it.</summary>
        public void Release()
        {
            if (_released) { return; }
            _released = true;
            NativeMethods.SetStdHandle(NativeMethods.StdOutput, _oldOut);
            NativeMethods.SetStdHandle(NativeMethods.StdError, _oldErr);
            NativeMethods.SetStdHandle(NativeMethods.StdInput, _oldIn);
            Console.SetOut(_oldConsoleOut);
            Console.SetError(_oldConsoleError);
            Console.SetIn(_oldConsoleIn);
            _reader.Dispose();
        }

        public void Dispose() => Release();
    }

    private static class NativeMethods
    {
        public const int StdInput = -10;
        public const int StdOutput = -11;
        public const int StdError = -12;
        public const uint HandleFlagInherit = 1;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint GetStdHandle(int which);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetStdHandle(int which, nint handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetHandleInformation(nint handle, out uint flags);
    }
}
